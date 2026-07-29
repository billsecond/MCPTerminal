# =============================================================================
# mcpterm.ps1 - CLI for MCPTerminal shared terminal sessions.
#
#   new    [-Shell pwsh|powershell|cmd|bash|bash-wsl] [-Name x] [-Cwd dir]
#          [-WslDistro d] [-Hidden]
#   list
#   exec   -Id <code> -Command "<cmd>" [-Controller "<chat label>"] [-TimeoutSec n]
#   read   -Id <code> [-Tail n]
#   kill   -Id <code>
#
# Sessions live under %LOCALAPPDATA%\MCPTerminal (override: MCPTERMINAL_ROOT).
# `exec` types the command into the live terminal window (the human watches it
# run), waits for the acknowledgement, and prints the transcript delta.
# =============================================================================
param(
    [Parameter(Position = 0)][ValidateSet('new', 'list', 'connect', 'exec', 'keys', 'read', 'rename', 'kill', 'help')]
    [string]$Action = 'help',
    [ValidateSet('pwsh', 'powershell', 'cmd', 'bash', 'bash-wsl', 'sh')][string]$Shell = 'pwsh',
    [string]$Name,
    [string]$Id,
    [string]$Command,
    [string]$Keys,
    [string]$Controller,
    [string]$Key,
    [string]$WslDistro,
    [int]$TimeoutSec = 120,
    [int]$Tail = 80,
    [string]$Cwd,
    [switch]$Hidden
)

$ErrorActionPreference = 'Stop'
$Root = if ($env:MCPTERMINAL_ROOT) { $env:MCPTERMINAL_ROOT } else { Join-Path $env:LOCALAPPDATA 'MCPTerminal' }
$IndexFile = Join-Path $Root 'index.json'
$HostScript = Join-Path $PSScriptRoot 'host.ps1'
$AppExe = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\MCPTerminal\MCPTerminal.exe'),
    (Join-Path $PSScriptRoot 'app\bin\Release\net10.0\MCPTerminal.exe'),
    (Join-Path $PSScriptRoot 'releases\win-x64\MCPTerminal.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not (Test-Path $Root)) { New-Item -ItemType Directory -Path $Root -Force | Out-Null }

function Read-Index {
    if (Test-Path $IndexFile) {
        try { return Get-Content $IndexFile -Raw | ConvertFrom-Json } catch { }
    }
    return [pscustomobject]@{}
}
function Write-IndexEntry([string]$sid, $entry) {
    for ($i = 0; $i -lt 5; $i++) {
        try {
            $idx = Read-Index
            $idx | Add-Member -Force NoteProperty $sid $entry
            [System.IO.File]::WriteAllText($IndexFile, ($idx | ConvertTo-Json -Depth 8),
                [System.Text.UTF8Encoding]::new($false))
            return
        } catch { Start-Sleep -Milliseconds (60 * ($i + 1)) }
    }
}
# ---------------------------------------------------------------- access keys
# A terminal belongs to a tab, every tab has one random access key, and each
# session stores a copy of it. Nothing may read or drive a session without
# presenting that key, so one conversation can never touch another's terminals.
function Get-SessionKey([string]$sid) {
    try {
        $sp = Join-Path $Root "sessions\$sid\state.json"
        if (Test-Path $sp) { return (Get-Content $sp -Raw | ConvertFrom-Json).accessKey }
    } catch { }
    return $null
}

# The Local tab is the user's own. It is never given a key, so there is nothing
# to present and nothing to leak - and we check the label as well as the key so
# a forged or empty key still cannot reach it.
function Test-IsLocal([string]$sid) {
    try {
        $sp = Join-Path $Root "sessions\$sid\state.json"
        if (Test-Path $sp) {
            $st = Get-Content $sp -Raw | ConvertFrom-Json
            $tab = $st.tabLabel
            if (-not $tab) { return (-not $st.controller) }   # no tab recorded = ungrouped = local
            return ($tab -ieq 'Local')
        }
    } catch { }
    return $false
}

# tabs.json: tab label -> access key. Mirrors AccessKeys.ClaimTab in the app so
# the CLI and the app agree on who owns which tab.
function Get-OrCreateTab([string]$label, [string]$suppliedKey) {
    $file = Join-Path $Root 'tabs.json'
    $tabs = if (Test-Path $file) {
        try { Get-Content $file -Raw | ConvertFrom-Json } catch { [pscustomobject]@{} }
    } else { [pscustomobject]@{} }
    if (-not $tabs) { $tabs = [pscustomobject]@{} }

    # A key is a credential for one specific TAB: if it opens a tab, the caller
    # joins THAT tab whatever it calls itself. This is what lets a second chat
    # share a tab the user invited it into (see Add-TabGuest).
    if ($suppliedKey) {
        foreach ($p in $tabs.PSObject.Properties) {
            if ($p.Value.key -eq $suppliedKey) { return @{ Label = $p.Name; Key = $p.Value.key } }
        }
    }

    $existing = $tabs.PSObject.Properties[$label]
    if ($existing) {
        if ($suppliedKey -and $existing.Value.key -eq $suppliedKey) {
            return @{ Label = $label; Key = $existing.Value.key }
        }
        # taken by someone else and no valid key - branch off a new tab
        $n = 2
        while ($tabs.PSObject.Properties["$label #$n"]) { $n++ }
        $label = "$label #$n"
    }
    $key = New-AccessKey
    $tabs | Add-Member -Force NoteProperty $label ([pscustomobject]@{
        key = $key; createdAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    })
    [System.IO.File]::WriteAllText($file, ($tabs | ConvertTo-Json -Depth 8),
        [System.Text.UTF8Encoding]::new($false))
    return @{ Label = $label; Key = $key }
}

# Take over an unclaimed Local terminal: it MOVES out of Local into the calling
# conversation's tab and is issued that tab's key. Local therefore always means
# "no assistant has touched this" - a takeover is visible the moment it happens,
# because the terminal jumps tabs in Studio.
function Claim-LocalSession($s, [string]$controllerLabel, [string]$suppliedKey) {
    $tab = Get-OrCreateTab $controllerLabel $suppliedKey
    $spath = Join-Path $Root "sessions\$($s.Sid)\state.json"
    $st = Get-Content $spath -Raw | ConvertFrom-Json
    $st | Add-Member -Force NoteProperty accessKey $tab.Key
    $st | Add-Member -Force NoteProperty tabLabel $tab.Label
    $st | Add-Member -Force NoteProperty controller $tab.Label
    [System.IO.File]::WriteAllText($spath, ($st | ConvertTo-Json -Depth 8),
        [System.Text.UTF8Encoding]::new($false))
    return $tab
}

# SHARING: a tab can be worked by more than one conversation. The user hands a
# second chat the tab's key (Studio's Share button on the tab), and that chat
# arrives holding the key but calling itself something else - it is a GUEST.
# Access is decided by the key alone; recording the guest here is what makes the
# sharing visible in Studio's tabstrip and in `mcpterm tabs`.
function Add-TabGuest([string]$sid, [string]$label) {
    if (-not $label) { return }
    try {
        $sp = Join-Path $Root "sessions\$sid\state.json"
        if (-not (Test-Path $sp)) { return }
        $tab = (Get-Content $sp -Raw | ConvertFrom-Json).tabLabel
        if (-not $tab -or $tab -ieq $label) { return }      # the owner, not a guest
        $file = Join-Path $Root 'tabs.json'
        if (-not (Test-Path $file)) { return }
        $tabs = Get-Content $file -Raw | ConvertFrom-Json
        $entry = $tabs.PSObject.Properties[$tab]
        if (-not $entry) { return }
        $guests = @(@($entry.Value.guests) | Where-Object { $_ })
        if ($guests -contains $label) { return }
        $entry.Value | Add-Member -Force NoteProperty guests ([string[]]($guests + $label))
        [System.IO.File]::WriteAllText($file, ($tabs | ConvertTo-Json -Depth 8),
            [System.Text.UTF8Encoding]::new($false))
    } catch { }
}

function Test-KeyMatch([string]$sid, [string]$supplied) {
    if (Test-IsLocal $sid) { return $false }     # must be claimed out of Local first
    $have = Get-SessionKey $sid
    # A session with no key at all predates this feature. Every session created
    # now gets one, so the only keyless sessions left are stale index entries
    # from before the upgrade - lock them rather than leaving a hole.
    if (-not $have) { return $false }
    return ($supplied -and $have -eq $supplied)
}

function New-AccessKey {
    $b = [byte[]]::new(6)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($b)
    return 'mt_' + ([System.BitConverter]::ToString($b) -replace '-', '').ToLowerInvariant()
}

function Assert-Access($s, [string]$supplied) {
    if (Test-KeyMatch $s.Sid $supplied) {
        Add-TabGuest $s.Sid $Controller      # a second chat working a shared tab
        return
    }
    # A plain message, not a PowerShell exception: this text is what an
    # assistant reads back, and a stack trace only invites a retry.
    if (Test-IsLocal $s.Sid) {
        # Unclaimed and local: takeable, but only INTO your own tab. Name the
        # conversation and the terminal moves there; Local keeps its meaning.
        if (-not $Controller) {
            Write-Output "'$($s.Info.name)' is a LOCAL terminal - the user's own, unclaimed."
            Write-Output '  To take it over, pass -Controller "<your chat label>": the terminal MOVES'
            Write-Output '  out of Local into your tab and is issued that tab''s access key. Local'
            Write-Output '  terminals themselves have no key, so there is nothing to ask the user for.'
            exit 4
        }
        $tab = Claim-LocalSession $s $Controller $Key
        Write-Output "[claimed '$($s.Info.name)' out of Local into tab '$($tab.Label)']"
        Write-Output "[ACCESS KEY: $($tab.Key)  <- pass as -Key on every later call for this tab]"
        $script:Key = $tab.Key
        return
    }
    Write-Output "ACCESS DENIED - '$($s.Info.name)' belongs to another conversation."
    if ($supplied) { Write-Output '  The key you supplied does not open it.' }
    else { Write-Output '  You supplied no access key.' }
    Write-Output '  Ask the user for this terminal''s access key - it is shown in the terminal'
    Write-Output '  window header, by running `info` in it, and on the pane header in Studio.'
    Write-Output '  Or run `new` to get your own terminal with its own key. Do not retry blindly.'
    exit 4
}

function Resolve-Session([string]$idOrName, [string]$suppliedKey) {
    if (-not $idOrName) { throw 'Missing -Id (session code: name or guid prefix).' }
    $idx = Read-Index
    $all = @($idx.PSObject.Properties | ForEach-Object {
        [pscustomobject]@{ Sid = $_.Name; Info = $_.Value }
    })
    $running = @($all | Where-Object { $_.Info.status -eq 'running' })
    foreach ($pool in @($running, $all)) {
        $hit = @($pool | Where-Object {
            $_.Sid -eq $idOrName -or $_.Sid.StartsWith($idOrName) -or $_.Info.name -eq $idOrName
        })
        # Names like ps-1 repeat across tabs. When a key is supplied, only the
        # sessions it unlocks are candidates - that both disambiguates and keeps
        # other conversations' terminals invisible.
        if ($hit.Count -gt 1 -and $suppliedKey) {
            $mine = @($hit | Where-Object { (Get-SessionKey $_.Sid) -eq $suppliedKey })
            if ($mine.Count -ge 1) { $hit = $mine }
        }
        if ($hit.Count -eq 1) {
            # index.json is a cache and can go stale under concurrent writers -
            # state.json is authoritative for status/name.
            try {
                $sp = Join-Path $Root "sessions\$($hit[0].Sid)\state.json"
                if (Test-Path $sp) {
                    $live = Get-Content $sp -Raw | ConvertFrom-Json
                    if ($live.status) { $hit[0].Info.status = $live.status }
                    if ($live.name) { $hit[0].Info.name = $live.name }
                }
            } catch { }
            return $hit[0]
        }
        if ($hit.Count -gt 1) {
            $names = ($hit | ForEach-Object { "$($_.Sid.Substring(0,8)) [$($_.Info.name)]" }) -join ', '
            throw "Ambiguous id '$idOrName' - matches: $names"
        }
    }
    # Index-independent fallback: scan session state files directly (the index
    # is a convenience cache, not the source of truth) and heal the index.
    $sessRoot = Join-Path $Root 'sessions'
    if (Test-Path $sessRoot) {
        foreach ($d in (Get-ChildItem $sessRoot -Directory | Sort-Object CreationTime -Descending)) {
            $stPath = Join-Path $d.FullName 'state.json'
            if (-not (Test-Path $stPath)) { continue }
            try { $st = Get-Content $stPath -Raw | ConvertFrom-Json } catch { continue }
            if ($st.status -ne 'running') { continue }
            if ($st.name -eq $idOrName -or $st.sessionId -eq $idOrName -or $st.sessionId.StartsWith($idOrName)) {
                $entry = [pscustomobject]@{
                    name = $st.name; shell = $st.shell; mode = $st.mode; status = $st.status
                    windowPid = $st.windowPid; createdAt = $st.createdAt
                    transcript = (Join-Path $d.FullName 'transcript.log')
                }
                Write-IndexEntry $st.sessionId $entry
                return [pscustomobject]@{ Sid = $st.sessionId; Info = $entry }
            }
        }
    }
    throw "No session matches '$idOrName'. Run: mcpterm list"
}

switch ($Action) {
    'new' {
        if ($Hidden) {
            # HEADLESS protocol session (host.ps1): structured exec with
            # captured output + exact exit codes; no window.
            $sid = [guid]::NewGuid().ToString()
            if (-not $Name) { $Name = $sid.Substring(0, 8) }
            $sessionDir = Join-Path $Root "sessions\$sid"
            foreach ($d in @($sessionDir, (Join-Path $sessionDir 'inbox'), (Join-Path $sessionDir 'outbox'))) {
                New-Item -ItemType Directory -Path $d -Force | Out-Null
            }
            # Headless sessions have no window to display a key in, so the key
            # is printed here - it is the only time it is shown.
            $hkey = if ($Key) { $Key } else { New-AccessKey }
            $state = [pscustomobject]@{
                sessionId = $sid; shell = $Shell; name = $Name
                status = 'starting'; createdAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
                accessKey = $hkey; tabLabel = if ($Controller) { $Controller } else { 'Local' }
            }
            [System.IO.File]::WriteAllText((Join-Path $sessionDir 'state.json'),
                ($state | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))
            Write-IndexEntry $sid ([pscustomobject]@{
                name = $Name; shell = $Shell; status = 'starting'
                createdAt = $state.createdAt
                transcript = (Join-Path $sessionDir 'transcript.log')
            })
            $hostArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass',
                          '-File', ('"{0}"' -f $HostScript), '-SessionId', $sid,
                          '-Root', ('"{0}"' -f $Root))
            $spawnOpts = @{ FilePath = 'pwsh'; ArgumentList = $hostArgs; WindowStyle = 'Hidden' }
            if ($Cwd) { $spawnOpts.WorkingDirectory = $Cwd }
            Start-Process @spawnOpts | Out-Null
            Write-Output "created $sid (headless, name=$Name)"
            Write-Output "ACCESS KEY: $hkey  <- pass this as -Key on every call for this session"
        } else {
            # NATIVE session. If MCPTerminal Studio is running, the terminal
            # opens integrated inside the app (the exe hands itself over via
            # the requests folder); otherwise a standalone window opens.
            if (-not $AppExe) { throw 'MCPTerminal.exe not found - build or install it first.' }
            # Is Studio up? The lock file can go stale (e.g. Studio was killed
            # rather than closed), so fall back to looking for the process.
            $studioLock = Join-Path $Root 'studio.lock'
            $inStudio = $false
            if (Test-Path $studioLock) {
                try {
                    $spid = [int](Get-Content $studioLock -Raw).Trim()
                    if (Get-Process -Id $spid -ErrorAction SilentlyContinue) { $inStudio = $true }
                } catch { }
            }
            if (-not $inStudio -and (Get-Process MCPTerminalStudio -ErrorAction SilentlyContinue)) {
                $inStudio = $true
            }
            if ($inStudio) {
                # Studio is running: ask it directly. Writing the request here
                # (instead of launching the app just so IT can write the same
                # file) removes a process hop, and works in environments where
                # spawning a console app is restricted.
                $reqDir = Join-Path $Root 'requests'
                New-Item -ItemType Directory -Path $reqDir -Force | Out-Null
                $req = [pscustomobject]@{
                    shell = $Shell
                    name = if ($Name) { $Name } else { '' }
                    cwd = if ($Cwd) { $Cwd } else { '' }
                    wslDistro = if ($WslDistro) { $WslDistro } else { '' }
                    controller = if ($Controller) { $Controller } else { '' }
                    accessKey = if ($Key) { $Key } else { '' }
                    trusted = $false
                }
                $known = @(Get-ChildItem (Join-Path $Root 'sessions') -Directory -ErrorAction SilentlyContinue |
                           Select-Object -ExpandProperty Name)
                $tmp = Join-Path $reqDir ((New-Guid).ToString('N') + '.tmp')
                [System.IO.File]::WriteAllText($tmp, ($req | ConvertTo-Json -Compress),
                    [System.Text.UTF8Encoding]::new($false))
                Move-Item $tmp ([System.IO.Path]::ChangeExtension($tmp, '.newterm')) -Force

                # wait for Studio to create it, then report the real code
                $deadline = [DateTime]::UtcNow.AddSeconds(15)
                while ([DateTime]::UtcNow -lt $deadline) {
                    Start-Sleep -Milliseconds 250
                    $new = Get-ChildItem (Join-Path $Root 'sessions') -Directory -ErrorAction SilentlyContinue |
                           Where-Object { $known -notcontains $_.Name }
                    if ($new) {
                        foreach ($d in $new) {
                            try {
                                $st = Get-Content (Join-Path $d.FullName 'state.json') -Raw | ConvertFrom-Json
                                Write-Output "created $($st.name) ($($st.sessionId.Substring(0,8))) shell=$($st.shell) in MCPTerminal Studio"
                                if ($st.accessKey) {
                                    Write-Output "tab: $($st.tabLabel)"
                                    Write-Output "ACCESS KEY: $($st.accessKey)  <- pass this as -Key on every call for this tab"
                                }
                            } catch { }
                        }
                        return
                    }
                }
                Write-Output "requested a $Shell terminal from Studio, but it did not appear within 15s."
                exit 3
            }

            $exeArgs = @('--shell', $Shell)
            if ($Name) { $exeArgs += @('--name', $Name) }
            if ($Cwd) { $exeArgs += @('--cwd', ('"{0}"' -f $Cwd)) }
            if ($WslDistro) { $exeArgs += @('--wsl-distro', $WslDistro) }
            if ($Controller) { $exeArgs += @('--controller', ('"{0}"' -f $Controller)) }
            if ($Key) { $exeArgs += @('--key', $Key) }
            Start-Process $AppExe -ArgumentList $exeArgs | Out-Null
            Write-Output "window launched (shell=$Shell) - the session code and access key appear in its header"
        }
    }

    'list' {
        $idx = Read-Index
        # Sessions the supplied key does not unlock are not listed at all - a
        # caller must not learn that another conversation's terminals exist,
        # let alone their names. Only the count is reported.
        # Shown: your own tab's terminals, plus the user's unclaimed Local ones
        # (which you may take over - doing so moves them into your tab).
        # Hidden: everything belonging to another conversation.
        $lockedCount = 0
        $rows = @($idx.PSObject.Properties | Where-Object {
            if ((Test-KeyMatch $_.Name $Key) -or (Test-IsLocal $_.Name)) { $true }
            else { $script:lockedCount++; $false }
        } | ForEach-Object {
            $status = $_.Value.status
            if ($_.Value.mode -eq 'native' -and $status -eq 'running' -and $_.Value.windowPid) {
                if (-not (Get-Process -Id $_.Value.windowPid -ErrorAction SilentlyContinue)) {
                    $status = 'closed'
                    $entry = $_.Value; $entry.status = 'closed'; Write-IndexEntry $_.Name $entry
                }
            }
            # controller = the conversation that owns this session. Read from
            # state.json (authoritative) so assistants can tell whose it is and
            # avoid hijacking another chat's terminal.
            $ctrl = ''
            try {
                $sp = Join-Path $Root "sessions\$($_.Name)\state.json"
                if (Test-Path $sp) { $ctrl = (Get-Content $sp -Raw | ConvertFrom-Json).controller }
            } catch { }
            # what is this terminal doing? last command the assistant sent, else
            # "(user)" when the human has been driving it directly.
            $last = ''
            try {
                $lp = Join-Path $Root "sessions\$($_.Name)\assistant-cmds.log"
                if (Test-Path $lp) {
                    $ll = (Get-Content $lp -Tail 1 -ErrorAction SilentlyContinue)
                    if ($ll) { $last = ($ll -replace '^\S+ \S+\s+', '') }
                }
            } catch { }
            if (-not $last) { $last = '(user-driven)' }
            if ($last.Length -gt 44) { $last = $last.Substring(0, 44) + '...' }
            [pscustomobject]@{
                Guid = $_.Name.Substring(0, 8); Name = $_.Value.name
                Shell = $_.Value.shell; Status = $status
                Controller = if ($ctrl) { $ctrl } else { '(local - take over with -Controller)' }
                Doing = $last
            }
        })
        if ($rows.Count -eq 0) {
            if ($lockedCount -gt 0) {
                Write-Output "(no terminals you can access. $lockedCount belong to other conversations - locked."
                Write-Output ' Ask the user for a terminal''s access key, or run `new` to get your own.)'
            } else {
                Write-Output '(no sessions yet - mcpterm new)'
            }
        } else {
            $rows | Sort-Object Status, Name | Format-Table -AutoSize | Out-String | Write-Output
            if ($lockedCount -gt 0) {
                Write-Output "($lockedCount more terminal(s) belong to other conversations - locked, not shown.)"
            }
        }
    }

    'connect' {
        # Connecting shouldn't DO anything - it just announces itself: types
        # `info` so the terminal shows the CONNECTED status + controlling chat.
        & $PSCommandPath exec -Id $Id -Command 'info' -Controller $Controller -Key $Key -TimeoutSec $TimeoutSec
        return
    }

    'exec' {
        if (-not $Command) { throw 'Missing -Command.' }
        $s = Resolve-Session $Id $Key
        Assert-Access $s $Key
        if ($s.Info.status -ne 'running') {
            throw "Session $($s.Sid.Substring(0,8)) [$($s.Info.name)] is '$($s.Info.status)', not running."
        }
        $sessionDir = Join-Path $Root "sessions\$($s.Sid)"

        if ($s.Info.mode -eq 'native') {
            $st = Get-Content (Join-Path $sessionDir 'state.json') -Raw | ConvertFrom-Json
            $winPid = [int]$st.windowPid
            if (-not (Get-Process -Id $winPid -ErrorAction SilentlyContinue)) {
                throw "Session window (pid $winPid) is gone. mcpterm list to see live sessions."
            }
            $t = Join-Path $sessionDir 'transcript.log'
            $pre = if (Test-Path $t) { (Get-Item $t).Length } else { 0 }
            # stamp the controlling-chat label BEFORE the command runs, so even
            # a first-command `info` can display it
            try {
                $spath = Join-Path $sessionDir 'state.json'
                $sobj = Get-Content $spath -Raw | ConvertFrom-Json
                $label = if ($Controller) { $Controller }
                         elseif ($env:MCPTERMINAL_CONTROLLER) { $env:MCPTERMINAL_CONTROLLER }
                         else {
                             $proj = $env:MCPTERMINAL_PROJECT_DIR
                             if (-not $proj) { $proj = (Get-Location).Path }
                             "MCP client - $(Split-Path $proj -Leaf) ($proj)"
                         }
                $sobj | Add-Member -Force NoteProperty controller $label
                [System.IO.File]::WriteAllText($spath, ($sobj | ConvertTo-Json -Depth 8),
                    [System.Text.UTF8Encoding]::new($false))
            } catch { }
            $cmdId = [guid]::NewGuid().ToString('N').Substring(0, 12)
            $tmp = Join-Path $sessionDir "inbox\$cmdId.tmp"
            [System.IO.File]::WriteAllText($tmp, "$cmdId`n$Command", [System.Text.UTF8Encoding]::new($false))
            Move-Item $tmp (Join-Path $sessionDir ("inbox\{0}_{1}.cmd" -f (Get-Date).Ticks, $cmdId)) -Force
            $ack = Join-Path $sessionDir "outbox\$cmdId.done"
            $ackDeadline = [DateTime]::UtcNow.AddSeconds(10)
            while (-not (Test-Path $ack) -and [DateTime]::UtcNow -lt $ackDeadline) { Start-Sleep -Milliseconds 25 }
            if (-not (Test-Path $ack)) { throw "session window did not accept the command (no ack)." }
            Remove-Item $ack -Force -ErrorAction SilentlyContinue
            # Return as soon as output stops growing - tight polling keeps short
            # commands feeling immediate instead of paying a fixed penalty.
            $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
            $lastLen = $pre; $lastChange = [DateTime]::UtcNow
            while ([DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 40
                $len = if (Test-Path $t) { (Get-Item $t).Length } else { 0 }
                if ($len -ne $lastLen) { $lastLen = $len; $lastChange = [DateTime]::UtcNow }
                elseif ($len -gt $pre -and ([DateTime]::UtcNow - $lastChange).TotalMilliseconds -gt 220) { break }
            }
            if ($lastLen -gt $pre) {
                $fs = [System.IO.File]::Open($t, 'Open', 'Read', 'ReadWrite')
                try {
                    $fs.Position = $pre
                    $buf = [byte[]]::new($lastLen - $pre)
                    [void]$fs.Read($buf, 0, $buf.Length)
                    Write-Output ([System.Text.Encoding]::UTF8.GetString($buf).TrimEnd())
                } finally { $fs.Dispose() }
            } else {
                Write-Output '(typed into session; no transcript output captured yet)'
            }
            return
        }

        # headless protocol session
        $cmdId = [guid]::NewGuid().ToString('N').Substring(0, 12)
        $tmp = Join-Path $sessionDir "inbox\$cmdId.tmp"
        $dst = Join-Path $sessionDir ("inbox\{0}_{1}.cmd" -f (Get-Date).Ticks, $cmdId)
        [System.IO.File]::WriteAllText($tmp, "$cmdId`n$Command", [System.Text.UTF8Encoding]::new($false))
        Move-Item $tmp $dst -Force
        $done = Join-Path $sessionDir "outbox\$cmdId.done"
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
        while (-not (Test-Path $done)) {
            if ([DateTime]::UtcNow -gt $deadline) {
                Write-Output "TIMEOUT after ${TimeoutSec}s - command may still be running."
                exit 3
            }
            Start-Sleep -Milliseconds 120
        }
        Start-Sleep -Milliseconds 60
        $raw = [System.IO.File]::ReadAllText($done, [System.Text.Encoding]::UTF8)
        Remove-Item $done -Force -ErrorAction SilentlyContinue
        $lines = $raw -split "`n", 3
        $exit = 0; [void][int]::TryParse($lines[0].Trim(), [ref]$exit)
        $body = if ($lines.Count -gt 2) { $lines[2] } else { '' }
        Write-Output $body.TrimEnd()
        Write-Output "[mcpterm: exit=$exit session=$($s.Info.name)]"
        if ($exit -ne 0) { exit 1 }
    }

    'read' {
        $s = Resolve-Session $Id $Key
        Assert-Access $s $Key
        $t = Join-Path $Root "sessions\$($s.Sid)\transcript.log"
        if (Test-Path $t) { Get-Content $t -Tail $Tail } else { Write-Output '(no transcript yet)' }
    }

    'keys' {
        # Raw keystrokes for interactive prompts / TUI apps. Tokens:
        #   {ENTER} {ESC} {TAB} {SPACE} {BKSP} {UP} {DOWN} {LEFT} {RIGHT}
        #   {CTRL+C} {CTRL+D} {CTRL+U}   - everything else is literal text.
        if (-not $Keys) { throw 'Missing -Keys (e.g. "Y{ENTER}" or "{DOWN}{DOWN}{ENTER}").' }
        $s = Resolve-Session $Id $Key
        Assert-Access $s $Key
        if ($s.Info.status -ne 'running') { throw "Session [$($s.Info.name)] is not running." }
        $map = @{
            '{ENTER}' = "`r"; '{ESC}' = [char]27; '{TAB}' = "`t"; '{SPACE}' = ' '
            '{BKSP}' = [char]8; '{CTRL+C}' = [char]3; '{CTRL+D}' = [char]4; '{CTRL+U}' = [char]21
            '{UP}' = "$([char]27)[A"; '{DOWN}' = "$([char]27)[B"
            '{RIGHT}' = "$([char]27)[C"; '{LEFT}' = "$([char]27)[D"
        }
        $out = $Keys
        foreach ($k in $map.Keys) { $out = $out.Replace($k, $map[$k]) }
        $b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($out))

        $sessionDir = Join-Path $Root "sessions\$($s.Sid)"
        $cmdId = [guid]::NewGuid().ToString('N').Substring(0, 12)
        $tmp = Join-Path $sessionDir "inbox\$cmdId.tmp"
        [System.IO.File]::WriteAllText($tmp, "$cmdId`n__CT_KEYS__$b64", [System.Text.UTF8Encoding]::new($false))
        Move-Item $tmp (Join-Path $sessionDir ("inbox\{0}_{1}.cmd" -f (Get-Date).Ticks, $cmdId)) -Force
        $ack = Join-Path $sessionDir "outbox\$cmdId.done"
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        while (-not (Test-Path $ack) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        Remove-Item $ack -Force -ErrorAction SilentlyContinue
        Write-Output "keys sent to [$($s.Info.name)]"
    }

    'rename' {
        if (-not $Name) { throw 'Missing -Name (the new, purpose-describing name).' }
        $s = Resolve-Session $Id $Key
        Assert-Access $s $Key
        $sessionDir = Join-Path $Root "sessions\$($s.Sid)"
        try {
            $spath = Join-Path $sessionDir 'state.json'
            $sobj = Get-Content $spath -Raw | ConvertFrom-Json
            $sobj.name = $Name
            [System.IO.File]::WriteAllText($spath, ($sobj | ConvertTo-Json -Depth 8),
                [System.Text.UTF8Encoding]::new($false))
        } catch { }
        $entry = $s.Info; $entry.name = $Name
        Write-IndexEntry $s.Sid $entry
        Write-Output "renamed $($s.Sid.Substring(0,8)) -> '$Name'"
    }

    'kill' {
        $s = Resolve-Session $Id $Key
        Assert-Access $s $Key
        if ($s.Info.mode -eq 'native') {
            if ($s.Info.windowPid) { Stop-Process -Id $s.Info.windowPid -Force -ErrorAction SilentlyContinue }
        } else {
            $sessionDir = Join-Path $Root "sessions\$($s.Sid)"
            $tmp = Join-Path $sessionDir 'inbox\kill.tmp'
            [System.IO.File]::WriteAllText($tmp, "kill`n__CT_EXIT__", [System.Text.UTF8Encoding]::new($false))
            Move-Item $tmp (Join-Path $sessionDir ("inbox\{0}_kill.cmd" -f (Get-Date).Ticks)) -Force
            Start-Sleep -Milliseconds 700
            try {
                $st = Get-Content (Join-Path $sessionDir 'state.json') -Raw | ConvertFrom-Json
                if ($st.status -ne 'closed' -and $st.hostPid) {
                    Stop-Process -Id $st.hostPid -Force -ErrorAction SilentlyContinue
                }
            } catch { }
        }
        $entry = $s.Info; $entry.status = 'closed'
        Write-IndexEntry $s.Sid $entry
        Write-Output "closed $($s.Sid.Substring(0,8)) [$($s.Info.name)]"
    }

    default {
        @'
MCPTerminal - shared live terminals for you + an AI assistant

  mcpterm new    [-Shell pwsh|powershell|cmd|bash|bash-wsl] [-Name x]
                 [-Cwd dir] [-WslDistro d] [-Key <key>] [-Hidden]
  mcpterm list   [-Key <key>]
  mcpterm connect -Id <code> -Key <key> [-Controller "<chat>"]   (announce + status)
  mcpterm exec   -Id <code> -Key <key> -Command "<cmd>" [-Controller "<chat>"] [-TimeoutSec n]
  mcpterm keys   -Id <code> -Key <key> -Keys "Y{ENTER}"   (raw keys: prompts, TUI apps)
  mcpterm read   -Id <code> -Key <key> [-Tail n]
  mcpterm rename -Id <code> -Key <key> -Name "<purpose>"  (name by what it's doing)
  mcpterm kill   -Id <code> -Key <key>

ACCESS KEYS: each terminal belongs to a tab, and every tab has one access key.
Reading or driving a terminal requires its key (-Key); `new` without a key mints
a fresh tab and prints the key it created. The key is shown in the terminal
window's own header and by running `info` inside it, so the person at the
keyboard decides who gets in. Terminals you hold no key for are not listed.

LOCAL IS PRIVATE: terminals in the "Local" tab are the user's own. They are
never issued a key, cannot be listed, read, typed into or killed by an
assistant, and an assistant cannot create one - `new` puts it in its own tab.

Sessions + logs + index: %LOCALAPPDATA%\MCPTerminal
'@ | Write-Output
    }
}
