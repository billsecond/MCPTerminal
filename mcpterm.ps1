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
function Resolve-Session([string]$idOrName) {
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
        if ($hit.Count -eq 1) { return $hit[0] }
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
            $state = [pscustomobject]@{
                sessionId = $sid; shell = $Shell; name = $Name
                status = 'starting'; createdAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
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
        } else {
            # NATIVE session. If MCPTerminal Studio is running, the terminal
            # opens integrated inside the app (the exe hands itself over via
            # the requests folder); otherwise a standalone window opens.
            if (-not $AppExe) { throw 'MCPTerminal.exe not found - build or install it first.' }
            $studioLock = Join-Path $Root 'studio.lock'
            $inStudio = $false
            if (Test-Path $studioLock) {
                try {
                    $spid = [int](Get-Content $studioLock -Raw).Trim()
                    if (Get-Process -Id $spid -ErrorAction SilentlyContinue) { $inStudio = $true }
                } catch { }
            }
            $exeArgs = @('--shell', $Shell)
            if ($Name) { $exeArgs += @('--name', $Name) }
            if ($Cwd) { $exeArgs += @('--cwd', ('"{0}"' -f $Cwd)) }
            if ($WslDistro) { $exeArgs += @('--wsl-distro', $WslDistro) }
            if ($Controller) { $exeArgs += @('--controller', ('"{0}"' -f $Controller)) }
            Start-Process $AppExe -ArgumentList $exeArgs | Out-Null
            if ($inStudio) { Write-Output "terminal opening inside MCPTerminal Studio (shell=$Shell)" }
            else { Write-Output "window launched (shell=$Shell) - the session code appears in its header/title" }
        }
    }

    'list' {
        $idx = Read-Index
        $rows = @($idx.PSObject.Properties | ForEach-Object {
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
            [pscustomobject]@{
                Guid = $_.Name.Substring(0, 8); Name = $_.Value.name
                Mode = $_.Value.mode ?? 'headless'; Shell = $_.Value.shell; Status = $status
                Controller = if ($ctrl) { $ctrl } else { '(unclaimed)' }
                Updated = $_.Value.updatedAt ?? $_.Value.createdAt
            }
        })
        if ($rows.Count -eq 0) { Write-Output '(no sessions yet - mcpterm new)' }
        else { $rows | Sort-Object Status, Name | Format-Table -AutoSize | Out-String | Write-Output }
    }

    'connect' {
        # Connecting shouldn't DO anything - it just announces itself: types
        # `info` so the terminal shows the CONNECTED status + controlling chat.
        & $PSCommandPath exec -Id $Id -Command 'info' -Controller $Controller -TimeoutSec $TimeoutSec
        return
    }

    'exec' {
        if (-not $Command) { throw 'Missing -Command.' }
        $s = Resolve-Session $Id
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
            while (-not (Test-Path $ack) -and [DateTime]::UtcNow -lt $ackDeadline) { Start-Sleep -Milliseconds 100 }
            if (-not (Test-Path $ack)) { throw "session window did not accept the command (no ack)." }
            Remove-Item $ack -Force -ErrorAction SilentlyContinue
            $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
            $lastLen = $pre; $lastChange = [DateTime]::UtcNow
            while ([DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 150
                $len = if (Test-Path $t) { (Get-Item $t).Length } else { 0 }
                if ($len -ne $lastLen) { $lastLen = $len; $lastChange = [DateTime]::UtcNow }
                elseif ($len -gt $pre -and ([DateTime]::UtcNow - $lastChange).TotalMilliseconds -gt 500) { break }
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
        $s = Resolve-Session $Id
        $t = Join-Path $Root "sessions\$($s.Sid)\transcript.log"
        if (Test-Path $t) { Get-Content $t -Tail $Tail } else { Write-Output '(no transcript yet)' }
    }

    'keys' {
        # Raw keystrokes for interactive prompts / TUI apps. Tokens:
        #   {ENTER} {ESC} {TAB} {SPACE} {BKSP} {UP} {DOWN} {LEFT} {RIGHT}
        #   {CTRL+C} {CTRL+D} {CTRL+U}   - everything else is literal text.
        if (-not $Keys) { throw 'Missing -Keys (e.g. "Y{ENTER}" or "{DOWN}{DOWN}{ENTER}").' }
        $s = Resolve-Session $Id
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
        $s = Resolve-Session $Id
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
        $s = Resolve-Session $Id
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
                 [-Cwd dir] [-WslDistro d] [-Hidden]
  mcpterm list
  mcpterm connect -Id <code> [-Controller "<chat>"]     (announce + show status)
  mcpterm exec   -Id <code> -Command "<cmd>" [-Controller "<chat>"] [-TimeoutSec n]
  mcpterm keys   -Id <code> -Keys "Y{ENTER}"      (raw keys: prompts, TUI apps)
  mcpterm read   -Id <code> [-Tail n]
  mcpterm rename -Id <code> -Name "<purpose>"     (name by what it's doing)
  mcpterm kill   -Id <code>

Sessions + logs + index: %LOCALAPPDATA%\MCPTerminal
'@ | Write-Output
    }
}
