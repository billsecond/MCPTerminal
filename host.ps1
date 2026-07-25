# =============================================================================
# MCPTerminal host.ps1 - runs INSIDE a terminal window and owns one shell session.
#
# Two input sources feed the SAME shell session:
#   * The human: live keystrokes in this window  ->  green  [YOU] >  prompt
#   * Assistant:    files dropped into inbox\        ->  bold bright-cyan [ASSISTANT] >
#
# Everything (both inputs + all output) is appended to transcript.log and the
# session is registered in the central index.json, keyed by GUID.
#
# Shells: pwsh (in-process, full PowerShell), cmd (persistent cmd.exe child),
#         bash (persistent Git Bash / bash child). cd + env state persists
#         within a session for all three.
#
# Protocol (plain text, UTF-8, no JSON parsing needed by hosts):
#   inbox\<ticks>_<cmdid>.cmd   line 1 = cmdid, remaining lines = the command
#   outbox\<cmdid>.done         line 1 = exit code, line 2 = duration ms,
#                               remaining lines = captured output (tail-capped)
# =============================================================================
param(
    [Parameter(Mandatory = $true)][string]$SessionId,
    [string]$Root = (Join-Path $env:LOCALAPPDATA 'MCPTerminal')
)

$ErrorActionPreference = 'Continue'
try { $PSStyle.OutputRendering = 'Ansi' } catch { }
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# --- session paths ------------------------------------------------------------
$SessionDir = Join-Path $Root "sessions\$SessionId"
$InboxDir   = Join-Path $SessionDir 'inbox'
$OutboxDir  = Join-Path $SessionDir 'outbox'
$Transcript = Join-Path $SessionDir 'transcript.log'
$Screen     = Join-Path $SessionDir 'screen.log'
$StateFile  = Join-Path $SessionDir 'state.json'
$IndexFile  = Join-Path $Root 'index.json'
foreach ($d in @($SessionDir, $InboxDir, $OutboxDir)) {
    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

# --- load state written by the CLI (shell type, name) -------------------------
$Shell = 'pwsh'; $Name = $SessionId.Substring(0, 8)
$script:StartupReadError = $null
try {
    $st = Get-Content $StateFile -Raw | ConvertFrom-Json
    if ($st.shell) { $Shell = [string]$st.shell }
    if ($st.name)  { $Name  = [string]$st.name }
} catch { $script:StartupReadError = ($_ | Out-String) }

# --- ANSI palette (raw escapes; ASCII-only glyphs) ----------------------------
$E = [char]27
$C_ASSIST = "$E[1;96m"   # bold bright cyan
$C_YOU    = "$E[1;92m"   # bold bright green
$C_DIM    = "$E[90m"
$C_ERR    = "$E[91m"
$C_RESET  = "$E[0m"

# --- logging ------------------------------------------------------------------
function Log-Text([string]$text) {
    try { [System.IO.File]::AppendAllText($Transcript, $text, [System.Text.UTF8Encoding]::new($false)) } catch { }
}
# Emit: print to this console AND mirror (ANSI included) to screen.log, which
# attached viewer windows tail to display the live session.
function Emit([string]$text) {
    Write-Host $text
    try { [System.IO.File]::AppendAllText($Screen, "$text`n", [System.Text.UTF8Encoding]::new($false)) } catch { }
}
function Now-Stamp { (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') }

# --- state/index updates (best effort, retried) -------------------------------
function Update-Json([string]$path, [scriptblock]$mutate) {
    $lastErr = $null
    for ($i = 0; $i -lt 5; $i++) {
        try {
            $obj = $null
            if (Test-Path $path) { $obj = Get-Content $path -Raw | ConvertFrom-Json }
            if ($null -eq $obj) { $obj = [pscustomobject]@{} }
            $obj = & $mutate $obj
            $json = $obj | ConvertTo-Json -Depth 8
            [System.IO.File]::WriteAllText($path, $json, [System.Text.UTF8Encoding]::new($false))
            return
        } catch { $lastErr = $_; Start-Sleep -Milliseconds (60 * ($i + 1)) }
    }
    if ($lastErr) { Log-Text "[dbg] Update-Json($path) FAILED: $($lastErr | Out-String)`n" }
}
function Set-SessionStatus([string]$status) {
    $sid = $SessionId; $shl = $Shell; $nm = $Name; $pid_ = $PID
    Update-Json $StateFile {
        param($o)
        $o | Add-Member -Force NoteProperty sessionId $sid
        $o | Add-Member -Force NoteProperty shell $shl
        $o | Add-Member -Force NoteProperty name $nm
        $o | Add-Member -Force NoteProperty hostPid $pid_
        $o | Add-Member -Force NoteProperty status $status
        $o | Add-Member -Force NoteProperty updatedAt (Now-Stamp)
        $o
    }
    Update-Json $IndexFile {
        param($o)
        $entry = [pscustomobject]@{
            name = $nm; shell = $shl; status = $status
            hostPid = $pid_; updatedAt = (Now-Stamp)
            transcript = $Transcript
        }
        $o | Add-Member -Force NoteProperty $sid $entry
        $o
    }
}

# --- child shell (cmd / bash) -------------------------------------------------
$Child = $null
function Start-ChildShell {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    if ($Shell -eq 'cmd') {
        $psi.FileName = "$env:SystemRoot\System32\cmd.exe"
        $psi.Arguments = '/Q /K prompt $G'
    } else {
        $bash = @(
            'C:\Program Files\Git\bin\bash.exe',
            'C:\Program Files\Git\usr\bin\bash.exe',
            '/usr/bin/bash', '/bin/bash'
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $bash) { throw 'No usable bash found (need Git Bash on Windows).' }
        $psi.FileName = $bash
        $psi.Arguments = '--noprofile --norc'
    }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8
    $p = [System.Diagnostics.Process]::Start($psi)
    # Drain stderr in the background so redirected buffers never deadlock.
    $null = Register-ObjectEvent -InputObject $p -EventName ErrorDataReceived -Action {
        if ($EventArgs.Data) { Write-Host $EventArgs.Data }
    }
    $p.BeginErrorReadLine()
    return $p
}

$script:CmdSeq = 0
function Invoke-InSession([string]$cmd) {
    # Returns @{ Output = <string>; Exit = <int> } after streaming output live.
    $sb = [System.Text.StringBuilder]::new()
    $exit = 0
    if ($Shell -eq 'pwsh') {
        # Session-lifetime PowerShell runspace: $vars, cd, and $env: persist
        # across commands AND stay isolated from this host script's internals.
        # The `. { } 2>&1` wrapper merges the error stream into the results
        # (a bare `IEX $cmd 2>&1` does NOT capture it - verified empirically).
        if ($null -eq $script:PS) { $script:PS = [powershell]::Create() }
        $hadError = $false
        try {
            $script:PS.Commands.Clear()
            $script:PS.Streams.ClearStreams()
            [void]$script:PS.AddScript(". { `$global:LASTEXITCODE = 0`n$cmd`n} 2>&1")
            $results = $script:PS.Invoke()
            # Format runs of normal objects TOGETHER (so tables render exactly
            # like a real console - one header, aligned rows), while error
            # records break the run and render in red.
            $run = [System.Collections.Generic.List[object]]::new()
            $flush = {
                if ($run.Count -gt 0) {
                    $text = ($run | Out-String).TrimEnd("`r", "`n")
                    if ($text.Length -gt 0) { Emit $text; [void]$sb.AppendLine($text) }
                    $run.Clear()
                }
            }
            foreach ($r in $results) {
                if ($r -is [System.Management.Automation.ErrorRecord]) {
                    & $flush
                    $hadError = $true
                    $line = ($r | Out-String).TrimEnd()
                    Emit "$C_ERR$line$C_RESET"
                    [void]$sb.AppendLine($line)
                } else {
                    [void]$run.Add($r)
                }
            }
            & $flush
            $script:PS.Commands.Clear()
            $ec = $script:PS.AddScript('$global:LASTEXITCODE').Invoke()
            $script:PS.Commands.Clear()
            if ($ec -and $ec[0]) { $exit = [int]$ec[0] }
            elseif ($hadError)   { $exit = 1 }
        } catch {
            $msg = ($_ | Out-String).TrimEnd()
            Emit "$C_ERR$msg$C_RESET"
            [void]$sb.AppendLine($msg)
            $exit = 1
        }
    } else {
        if ($null -eq $Child -or $Child.HasExited) { $script:Child = Start-ChildShell }
        $script:CmdSeq++
        $token = "__CT_DONE_$($script:CmdSeq)_"
        if ($Shell -eq 'cmd') {
            $Child.StandardInput.WriteLine("($cmd) 2>&1")
            $Child.StandardInput.WriteLine("echo $token%ERRORLEVEL%__")
        } else {
            $Child.StandardInput.WriteLine("{ $cmd`n} 2>&1")
            $Child.StandardInput.WriteLine("echo $token`$?__")
        }
        $Child.StandardInput.Flush()
        while ($true) {
            $task = $Child.StandardOutput.ReadLineAsync()
            while (-not $task.Wait(250)) {
                if ($Child.HasExited) { break }
            }
            if ($Child.HasExited -and -not $task.IsCompleted) { $exit = -1; break }
            $line = $task.Result
            if ($null -eq $line) { $exit = -1; break }          # child stdout closed
            if ($Shell -eq 'cmd') {
                # cmd's prompt ('>' via `prompt $G`) fuses onto the next output
                # line; drop bare prompts and strip a single fused leading '>'.
                if ($line -eq '>' -or $line -eq '') { continue }
                if ($line.StartsWith('>')) { $line = $line.Substring(1) }
            }
            if ($line -match [regex]::Escape($token) + '(-?\d+)__') {
                $exit = [int]$Matches[1]; break
            }
            Emit $line
            [void]$sb.AppendLine($line)
        }
    }
    return @{ Output = $sb.ToString(); Exit = $exit }
}

# --- run one command from a given actor, with rendering + transcript ----------
# Current working dir of the SESSION (for prompt rendering + activity status).
function Get-SessionCwd {
    if ($Shell -eq 'pwsh' -and $script:PS) {
        try {
            $script:PS.Commands.Clear()
            $p = $script:PS.AddScript('(Get-Location).Path').Invoke()[0]
            $script:PS.Commands.Clear()
            return [string]$p
        } catch { }
    }
    return (Get-Location).Path
}

# Prompt text that matches what a native console would show for this shell.
function Get-PromptText([string]$cwd) {
    switch ($Shell) {
        'pwsh' { return "PS $cwd> " }
        'cmd'  { return "$cwd>" }
        default { return '$ ' }
    }
}

function Set-Activity([string]$activity, [string]$detail) {
    $cwd = Get-SessionCwd
    Update-Json $StateFile {
        param($o)
        $o | Add-Member -Force NoteProperty activity $activity
        $o | Add-Member -Force NoteProperty activityDetail $detail
        $o | Add-Member -Force NoteProperty cwd $cwd
        $o | Add-Member -Force NoteProperty updatedAt (Now-Stamp)
        $o
    }
}

function Run-Command([string]$cmd, [string]$actor, [string]$cmdId) {
    # Render exactly like a native console: normal prompt + the typed command.
    # The ONLY visual difference: the assistant's typed text is bold bright-cyan.
    $promptTxt = Get-PromptText (Get-SessionCwd)
    if ($actor -eq 'ASSISTANT') { Emit "$promptTxt$C_ASSIST$cmd$C_RESET" }
    else                     { Emit "$promptTxt$cmd" }
    Log-Text "--- $(Now-Stamp) [$actor]`n> $cmd`n"
    Set-Activity 'running' $cmd
    $t0 = [DateTime]::UtcNow
    $res = Invoke-InSession $cmd
    $ms = [int]([DateTime]::UtcNow - $t0).TotalMilliseconds
    Log-Text ($res.Output)
    Log-Text "--- exit $($res.Exit) (${ms}ms)`n`n"
    Set-Activity 'idle' "last: $cmd (exit $($res.Exit), ${ms}ms)"
    if ($cmdId) {
        $out = $res.Output
        if ($out.Length -gt 60000) { $out = "...[truncated]...`n" + $out.Substring($out.Length - 60000) }
        $doneTmp = Join-Path $OutboxDir "$cmdId.tmp"
        $done    = Join-Path $OutboxDir "$cmdId.done"
        [System.IO.File]::WriteAllText($doneTmp, "$($res.Exit)`n$ms`n$out", [System.Text.UTF8Encoding]::new($false))
        Move-Item -Path $doneTmp -Destination $done -Force
    }
    return $res
}

# --- inbox --------------------------------------------------------------------
function Process-Inbox {
    $files = Get-ChildItem $InboxDir -Filter '*.cmd' -ErrorAction SilentlyContinue | Sort-Object Name
    foreach ($f in $files) {
        $raw = $null
        try { $raw = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8) } catch { continue }
        Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue
        if ([string]::IsNullOrWhiteSpace($raw)) { continue }
        $nl = $raw.IndexOf("`n")
        if ($nl -lt 1) { continue }
        $cmdId = $raw.Substring(0, $nl).Trim()
        $cmd = $raw.Substring($nl + 1).TrimEnd("`r", "`n")
        if ($cmd -eq '__CT_EXIT__') { $script:Quit = $true; return }
        # Viewer keystrokes arrive as *.you.cmd -> run as YOU, no outbox reply.
        $fromViewer = $f.Name -match '\.you\.cmd$'
        if ($fromViewer -and $cmd.Trim() -in @('exit', 'quit')) { $script:Quit = $true; return }
        Write-Host ''
        if ($fromViewer) { [void](Run-Command $cmd 'YOU' $null) }
        else             { [void](Run-Command $cmd 'ASSISTANT' $cmdId) }
        Show-Prompt
    }
}

# --- console input ------------------------------------------------------------
$script:LineBuffer = ''
$script:History = [System.Collections.Generic.List[string]]::new()
$script:HistPos = -1
$script:InputAvailable = $true
$script:Quit = $false

function Show-Prompt {
    $loc = ''
    if ($Shell -eq 'pwsh' -and $script:PS) {
        try {
            $script:PS.Commands.Clear()
            $loc = ' ' + $script:PS.AddScript('(Get-Location).Path').Invoke()[0]
            $script:PS.Commands.Clear()
        } catch { $loc = '' }
    }
    Write-Host -NoNewline "$C_YOU[YOU]$loc >$C_RESET $($script:LineBuffer)"
}

function Process-Keys {
    if (-not $script:InputAvailable) { return }
    try { $avail = [Console]::KeyAvailable } catch { $script:InputAvailable = $false; return }
    while ($avail) {
        $k = [Console]::ReadKey($true)
        switch ($k.Key) {
            'Enter' {
                Write-Host ''
                $line = $script:LineBuffer
                $script:LineBuffer = ''
                $script:HistPos = -1
                if ($line.Trim()) {
                    if ($line.Trim() -in @('exit', 'quit')) { $script:Quit = $true; return }
                    $script:History.Add($line)
                    [void](Run-Command $line 'YOU' $null)
                }
                Show-Prompt
            }
            'Backspace' {
                if ($script:LineBuffer.Length -gt 0) {
                    $script:LineBuffer = $script:LineBuffer.Substring(0, $script:LineBuffer.Length - 1)
                    Write-Host -NoNewline "`b `b"
                }
            }
            'UpArrow' {
                if ($script:History.Count -gt 0) {
                    if ($script:HistPos -lt 0) { $script:HistPos = $script:History.Count }
                    if ($script:HistPos -gt 0) { $script:HistPos-- }
                    # clear current line render
                    Write-Host -NoNewline ("`b `b" * $script:LineBuffer.Length)
                    $script:LineBuffer = $script:History[$script:HistPos]
                    Write-Host -NoNewline $script:LineBuffer
                }
            }
            'DownArrow' {
                if ($script:HistPos -ge 0) {
                    Write-Host -NoNewline ("`b `b" * $script:LineBuffer.Length)
                    $script:HistPos++
                    if ($script:HistPos -ge $script:History.Count) { $script:HistPos = -1; $script:LineBuffer = '' }
                    else { $script:LineBuffer = $script:History[$script:HistPos] }
                    Write-Host -NoNewline $script:LineBuffer
                }
            }
            default {
                if ($k.KeyChar -and [int]$k.KeyChar -ge 32) {
                    $script:LineBuffer += $k.KeyChar
                    Write-Host -NoNewline $k.KeyChar
                }
            }
        }
        try { $avail = [Console]::KeyAvailable } catch { $avail = $false }
    }
}

# --- banner + main loop -------------------------------------------------------
try { $Host.UI.RawUI.WindowTitle = "MCPTerminal [$Name] $Shell $($SessionId.Substring(0,8))" } catch { }
Write-Host "$C_DIM=============================================================$C_RESET"
Write-Host "$C_DIM MCPTerminal session$C_RESET $C_YOU$Name$C_RESET  $C_DIM($Shell)$C_RESET"
Write-Host "$C_DIM guid: $SessionId$C_RESET"
Write-Host "$C_DIM log : $Transcript$C_RESET"
Write-Host "$C_DIM Type commands normally. $C_RESET$C_ASSIST[ASSISTANT]$C_RESET$C_DIM lines are the assistant typing."
Write-Host "$C_DIM Type 'exit' to close the session.$C_RESET"
Write-Host "$C_DIM=============================================================$C_RESET"
Log-Text "=== MCPTerminal session $SessionId ($Name, $Shell) started $(Now-Stamp) ===`n"
Set-SessionStatus 'running'
Set-Activity 'idle' 'session started'
try { [Console]::TreatControlCAsInput = $true } catch { }
$RequestsDir = Join-Path $Root 'requests'
if (-not (Test-Path $RequestsDir)) { New-Item -ItemType Directory -Path $RequestsDir -Force | Out-Null }

# Attach requests: a desktop shortcut (or anything else) can drop
# "<name-or-guid>.req" into requests\; the host spawns a viewer in ITS OWN
# process world (required for a coherent filesystem view on Windows).
function Process-Requests {
    foreach ($f in (Get-ChildItem $RequestsDir -Filter '*.req' -ErrorAction SilentlyContinue)) {
        $want = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
        if ($want -eq $Name -or $want -eq $SessionId -or $SessionId.StartsWith($want)) {
            Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue
            $viewer = Join-Path $PSScriptRoot 'viewer.ps1'
            Start-Process pwsh -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass',
                '-File', ('"{0}"' -f $viewer), '-SessionId', $SessionId) | Out-Null
        }
    }
}

if ($Shell -ne 'pwsh') {
    try { $script:Child = Start-ChildShell } catch {
        Write-Host "$C_ERR$($_.Exception.Message)$C_RESET"
    }
}

Show-Prompt
$reqTick = 0
try {
    while (-not $script:Quit) {
        Process-Inbox
        if ($script:Quit) { break }
        Process-Keys
        if ((++$reqTick % 5) -eq 0) { Process-Requests }   # ~every 600ms
        Start-Sleep -Milliseconds 120
    }
} finally {
    if ($Child -and -not $Child.HasExited) { try { $Child.Kill() } catch { } }
    if ($script:PS) { try { $script:PS.Dispose() } catch { } }
    Log-Text "=== session closed $(Now-Stamp) ===`n"
    Set-SessionStatus 'closed'
}
Write-Host "$C_DIM Session closed.$C_RESET"
