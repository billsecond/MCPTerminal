# =============================================================================
# MCPTerminal installer (Windows)
#
#   pwsh -File install.ps1              interactive install
#   pwsh -File install.ps1 -Yes         accept every prompt (unattended)
#   pwsh -File install.ps1 -Uninstall
#
# Installs to %LOCALAPPDATA%\Programs\MCPTerminal and optionally registers:
#   * a Desktop shortcut (pin to taskbar via right-click)
#   * Windows Terminal profiles per shell
#   * MCPTerminal Studio + its shortcut
#   * the mcpterminal MCP server (global, for AI assistants)
#   * assistant routing rules in ~/.claude/CLAUDE.md
# =============================================================================
param([switch]$Uninstall, [switch]$Yes)
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\MCPTerminal'
$exePath = Join-Path $installDir 'MCPTerminal.exe'
$desktop = [Environment]::GetFolderPath('Desktop')
$lnkPath = Join-Path $desktop 'MCPTerminal.lnk'
$studioLnk = Join-Path $desktop 'MCPTerminal Studio.lnk'
$wtSettings = "$env:LOCALAPPDATA\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json"
$profileNames = @('MCPTerminal', 'MCPTerminal CMD', 'MCPTerminal PS5', 'MCPTerminal Git Bash', 'MCPTerminal WSL')
$ws = New-Object -ComObject WScript.Shell

function Ask([string]$question, [string]$detail) {
    if ($Yes) { return $true }
    Write-Host ''
    Write-Host "  $question" -ForegroundColor Cyan
    if ($detail) { Write-Host "  $detail" -ForegroundColor DarkGray }
    $a = Read-Host '  [Y/n]'
    return ($a -eq '' -or $a -match '^[Yy]')
}

# Merge mcpServers.mcpterminal into a JSON-config MCP client (Claude Desktop,
# Cursor, Windsurf). Creates the file if missing, preserves everything else.
# $serverPath is set during install; -Remove never uses it.
function Register-JsonClient([string]$clientName, [string]$configPath, [switch]$Remove) {
    try {
        $cfg = if (Test-Path $configPath) { Get-Content $configPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
        if (-not $cfg) { $cfg = [pscustomobject]@{} }
        if (-not ($cfg.PSObject.Properties.Name -contains 'mcpServers')) {
            $cfg | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{})
        }
        if ($Remove) {
            if ($cfg.mcpServers.PSObject.Properties.Name -contains 'mcpterminal') {
                $cfg.mcpServers.PSObject.Properties.Remove('mcpterminal')
                $cfg | ConvertTo-Json -Depth 32 | Set-Content $configPath -Encoding UTF8
                Write-Host "  $clientName - mcpterminal removed." -ForegroundColor Green
            }
            return
        }
        $entry = [pscustomobject]@{ command = 'node'; args = @($serverPath) }
        if ($cfg.mcpServers.PSObject.Properties.Name -contains 'mcpterminal') {
            $cfg.mcpServers.mcpterminal = $entry
        } else {
            $cfg.mcpServers | Add-Member -NotePropertyName mcpterminal -NotePropertyValue $entry
        }
        New-Item -ItemType Directory -Path (Split-Path $configPath) -Force | Out-Null
        if (Test-Path $configPath) { Copy-Item $configPath "$configPath.mcpterminal-backup" -Force }
        $cfg | ConvertTo-Json -Depth 32 | Set-Content $configPath -Encoding UTF8
        Write-Host "  $clientName registered ($configPath)." -ForegroundColor Green
    } catch {
        Write-Host "  $clientName registration failed: $_" -ForegroundColor Yellow
    }
}

# PowerShell 7 is the DEFAULT shell for new terminals and the interpreter the
# `mcpterm` CLI runs under, so a machine without it gets a broken default. Not
# fatal - cmd / PS5 / Git Bash / WSL all still work - so this offers to install
# it rather than refusing to continue.
function Ensure-Pwsh7 {
    $pwshCmd = Get-Command pwsh -ErrorAction SilentlyContinue
    if ($pwshCmd) {
        $ver = try { & pwsh -NoProfile -NoLogo -Command '$PSVersionTable.PSVersion.ToString()' 2>$null } catch { $null }
        if ($ver -and [version]($ver -split '-')[0] -ge [version]'7.0') {
            Write-Host "  PowerShell $ver found." -ForegroundColor Green
            return
        }
        Write-Host "  Found pwsh but could not confirm it is 7+ (reported '$ver')." -ForegroundColor Yellow
    }

    Write-Host ''
    Write-Host '  PowerShell 7 (pwsh) is NOT installed.' -ForegroundColor Yellow
    Write-Host '  It is the default shell for new terminals and the interpreter the' -ForegroundColor DarkGray
    Write-Host '  mcpterm CLI runs under. Without it, terminals opened as "pwsh" will' -ForegroundColor DarkGray
    Write-Host '  fail - cmd, Windows PowerShell, Git Bash and WSL still work.' -ForegroundColor DarkGray

    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        Write-Host ''
        Write-Host '  winget is not available on this machine. Install PowerShell 7 manually:' -ForegroundColor Yellow
        Write-Host '    https://aka.ms/powershell-release' -ForegroundColor DarkGray
        return
    }

    if (-not (Ask 'Install PowerShell 7 now?' 'Runs: winget install --id Microsoft.PowerShell --exact')) {
        Write-Host '  Skipped. Install it later with:' -ForegroundColor DarkGray
        Write-Host '    winget install --id Microsoft.PowerShell --exact' -ForegroundColor DarkGray
        return
    }

    Write-Host '  Installing PowerShell 7 (winget may prompt for agreements)...'
    # --exact so the id cannot resolve to Preview or a different package.
    winget install --id Microsoft.PowerShell --exact --source winget `
        --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  winget exited with $LASTEXITCODE - PowerShell 7 was not installed." -ForegroundColor Yellow
        Write-Host '    Install it manually: https://aka.ms/powershell-release' -ForegroundColor DarkGray
        return
    }
    # winget put pwsh.exe on PATH for NEW processes; this one inherited the old
    # PATH, so look for it where winget installs it before giving up.
    if (-not (Get-Command pwsh -ErrorAction SilentlyContinue)) {
        $guess = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
        if (Test-Path $guess) { $env:Path = "$(Split-Path $guess);$env:Path" }
    }
    if (Get-Command pwsh -ErrorAction SilentlyContinue) {
        Write-Host '  PowerShell 7 installed.' -ForegroundColor Green
    } else {
        Write-Host '  PowerShell 7 installed - open a NEW terminal for `pwsh` to be on your PATH.' -ForegroundColor Green
    }
}

function Show-Disclaimer {
    Write-Host ''
    Write-Host '  ============================================================' -ForegroundColor Yellow
    Write-Host '   MCPTerminal - PLEASE READ BEFORE INSTALLING' -ForegroundColor Yellow
    Write-Host '  ============================================================' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '   This tool lets an AI assistant type commands into a real' -ForegroundColor White
    Write-Host '   shell on this machine, running as YOU, with YOUR access.' -ForegroundColor White
    Write-Host ''
    Write-Host '   * An assistant you connect can run ANY command you could run:' -ForegroundColor Gray
    Write-Host '     read, modify or delete your files, install software, reach' -ForegroundColor Gray
    Write-Host '     the network. Only share a session code with an assistant' -ForegroundColor Gray
    Write-Host '     you trust - it is like handing over your keyboard.' -ForegroundColor Gray
    Write-Host '   * Everything in a session is LOGGED IN PLAIN TEXT and kept' -ForegroundColor Gray
    Write-Host '     indefinitely (%LOCALAPPDATA%\MCPTerminal). Anything echoed to' -ForegroundColor Gray
    Write-Host '     the screen - including secrets on command lines - is captured.' -ForegroundColor Gray
    Write-Host '   * Anyone able to write to your user profile can inject commands' -ForegroundColor Gray
    Write-Host '     into your sessions. Do not use on a machine you do not trust.' -ForegroundColor Gray
    Write-Host '   * Provided AS IS, WITHOUT WARRANTY OF ANY KIND (MIT). The' -ForegroundColor Gray
    Write-Host '     author(s) will NOT be held responsible or liable for any' -ForegroundColor Gray
    Write-Host '     damage, data loss, or other consequence of using this tool.' -ForegroundColor Gray
    Write-Host '     By continuing you assume ALL responsibility for what' -ForegroundColor Gray
    Write-Host '     connected clients do on this system.' -ForegroundColor Gray
    Write-Host ''
    if (-not (Ask 'Do you understand and want to continue?' 'Answer n to abort the installation.')) {
        Write-Host '  Aborted - nothing was installed.' -ForegroundColor Yellow
        exit 1
    }
}

function Update-WtProfiles([switch]$Remove) {
    if (-not (Test-Path $wtSettings)) { Write-Host '  (Windows Terminal not found - skipping profiles)'; return }
    Copy-Item $wtSettings "$wtSettings.mcpterminal-backup" -Force
    $json = Get-Content $wtSettings -Raw | ConvertFrom-Json
    $list = @($json.profiles.list | Where-Object { $_.name -notin $profileNames })
    if (-not $Remove) {
        function New-Profile([string]$name, [string]$cmdline) {
            [pscustomobject]@{
                name = $name; commandline = $cmdline
                startingDirectory = '%USERPROFILE%'
                guid = '{' + ([guid]::NewGuid().ToString()) + '}'
            }
        }
        $list += New-Profile 'MCPTerminal' "$exePath"
        $list += New-Profile 'MCPTerminal CMD' "$exePath --shell cmd"
        $list += New-Profile 'MCPTerminal PS5' "$exePath --shell powershell"
        if (Test-Path 'C:\Program Files\Git\bin\bash.exe') {
            $list += New-Profile 'MCPTerminal Git Bash' "$exePath --shell bash"
        }
        $distro = (wsl.exe -l -q 2>$null) -replace "`0", '' |
            Where-Object { $_ -and $_ -notmatch 'docker' } | Select-Object -First 1
        if ($distro) {
            $list += New-Profile 'MCPTerminal WSL' "$exePath --shell bash-wsl --wsl-distro $($distro.Trim())"
        }
    }
    $json.profiles.list = $list
    $json | ConvertTo-Json -Depth 32 | Set-Content $wtSettings -Encoding UTF8
    Write-Host "  Windows Terminal profiles $(if ($Remove) { 'removed' } else { 'registered' })." -ForegroundColor Green
}

# ----------------------------------------------------------------- uninstall
if ($Uninstall) {
    Get-Process MCPTerminal, MCPTerminalStudio -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
    if (Test-Path $lnkPath) { Remove-Item $lnkPath -Force }
    if (Test-Path $studioLnk) { Remove-Item $studioLnk -Force }
    Update-WtProfiles -Remove
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        claude mcp remove --scope user mcpterminal 2>$null | Out-Null
        Write-Host '  MCP server unregistered.' -ForegroundColor Green
    }
    foreach ($c in @(
        @{ n = 'Claude Desktop'; p = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json' },
        @{ n = 'Cursor'; p = Join-Path $env:USERPROFILE '.cursor\mcp.json' },
        @{ n = 'Windsurf'; p = Join-Path $env:USERPROFILE '.codeium\windsurf\mcp_config.json' })) {
        if (Test-Path $c.p) { Register-JsonClient $c.n $c.p -Remove }
    }
    Write-Host ''
    Write-Host '  MCPTerminal uninstalled.' -ForegroundColor Cyan
    Write-Host '  Session logs were KEPT at %LOCALAPPDATA%\MCPTerminal (delete manually if you want them gone).'
    Write-Host '  Assistant rules in ~\.claude\CLAUDE.md were left untouched.'
    return
}

# ------------------------------------------------------------------- install
Show-Disclaimer

Write-Host ''
Write-Host '  Checking prerequisites...' -ForegroundColor Cyan
Ensure-Pwsh7

$source = Join-Path $PSScriptRoot '..\releases\win-x64\MCPTerminal.exe'
if (-not (Test-Path $source)) { throw "Release binary not found: $source (build it first, or use setup\get.ps1)" }

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item $source $exePath -Force
Copy-Item (Join-Path $PSScriptRoot '..\mcpterm.ps1') (Join-Path $installDir 'mcpterm.ps1') -Force
foreach ($doc in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    $p = Join-Path $PSScriptRoot "..\$doc"
    if (Test-Path $p) { Copy-Item $p (Join-Path $installDir $doc) -Force }
}
Write-Host "  Installed to $installDir" -ForegroundColor Green

if (Ask 'Create a Desktop shortcut?' 'You can right-click it afterwards to pin it to the taskbar.') {
    $lnk = $ws.CreateShortcut($lnkPath)
    $lnk.TargetPath = $exePath
    $lnk.WorkingDirectory = $env:USERPROFILE
    $lnk.Description = 'MCPTerminal - shared terminal (paste the session code into your assistant)'
    $lnk.IconLocation = "$env:SystemRoot\System32\cmd.exe,0"
    $lnk.Save()
    Write-Host '  Desktop shortcut created.' -ForegroundColor Green
}

if (Ask 'Add MCPTerminal profiles to Windows Terminal?' 'Adds PowerShell / CMD / PS5 / Git Bash / WSL entries to the new-tab menu.') {
    Update-WtProfiles
}

# Studio (optional app)
$studioBin = Join-Path $PSScriptRoot '..\studio\bin\Release\net10.0-windows'
if (Test-Path (Join-Path $studioBin 'MCPTerminalStudio.exe')) {
    if (Ask 'Install MCPTerminal Studio (terminal manager app)?' 'Tabs per conversation, terminal list with activity, history search. Optional - terminals work without it.') {
        $studioDir = Join-Path $installDir 'Studio'
        New-Item -ItemType Directory -Path $studioDir -Force | Out-Null
        Copy-Item "$studioBin\*" $studioDir -Recurse -Force
        $slnk = $ws.CreateShortcut($studioLnk)
        $slnk.TargetPath = Join-Path $studioDir 'MCPTerminalStudio.exe'
        $slnk.WorkingDirectory = $env:USERPROFILE
        $slnk.Description = 'MCPTerminal Studio - terminal manager'
        $slnk.Save()
        Write-Host '  Studio installed (Desktop shortcut: MCPTerminal Studio).' -ForegroundColor Green
    }
}

# MCP server registration (this is what lets an assistant drive terminals).
# Each client is opt-in; pick only the ones you actually use.
$serverPath = (Resolve-Path (Join-Path $PSScriptRoot '..\mcp\server.mjs')).Path

$claudeDesktopCfg = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
$cursorCfg = Join-Path $env:USERPROFILE '.cursor\mcp.json'
$windsurfCfg = Join-Path $env:USERPROFILE '.codeium\windsurf\mcp_config.json'

Write-Host ''
Write-Host '  MCP client registration - choose which assistants may use these terminals.' -ForegroundColor Cyan
Write-Host '  (Each one you register gets the full-control access described above.)' -ForegroundColor DarkGray

# Claude Code CLI (separate config from Claude Desktop - registering both is fine)
if (Get-Command claude -ErrorAction SilentlyContinue) {
    if (Ask 'Register with Claude Code (CLI)?' "Runs: claude mcp add --scope user mcpterminal -- node `"$serverPath`"") {
        claude mcp add --scope user mcpterminal -- node $serverPath 2>&1 | Out-Null
        Write-Host '  Claude Code registered (verify with: claude mcp list).' -ForegroundColor Green
    }
} else {
    Write-Host '  (Claude Code CLI not found - skipping. Manual command:)' -ForegroundColor DarkGray
    Write-Host "    claude mcp add --scope user mcpterminal -- node `"$serverPath`"" -ForegroundColor DarkGray
}

if (Test-Path (Join-Path $env:APPDATA 'Claude')) {
    if (Ask 'Register with Claude Desktop?' "Adds mcpterminal to $claudeDesktopCfg") {
        Register-JsonClient 'Claude Desktop' $claudeDesktopCfg
    }
} else { Write-Host '  (Claude Desktop not found - skipping)' -ForegroundColor DarkGray }

if (Test-Path (Join-Path $env:USERPROFILE '.cursor')) {
    if (Ask 'Register with Cursor?' "Adds mcpterminal to $cursorCfg") {
        Register-JsonClient 'Cursor' $cursorCfg
    }
} else { Write-Host '  (Cursor not found - skipping)' -ForegroundColor DarkGray }

if (Test-Path (Join-Path $env:USERPROFILE '.codeium')) {
    if (Ask 'Register with Windsurf?' "Adds mcpterminal to $windsurfCfg") {
        Register-JsonClient 'Windsurf' $windsurfCfg
    }
} else { Write-Host '  (Windsurf not found - skipping)' -ForegroundColor DarkGray }

# Assistant routing rules
$claudeMd = Join-Path $env:USERPROFILE '.claude\CLAUDE.md'
if (Ask 'Add assistant routing rules to ~\.claude\CLAUDE.md?' 'Tells assistants in EVERY new chat to run shell commands through MCPTerminal. Appends - existing content is preserved.') {
    $rules = @'

# Shell commands: use MCPTerminal

MCPTerminal is installed on this machine and registered as a global MCP server
(`mcpterminal`). It provides shared terminals the user can watch and type into.

**Never chat in the terminal - it is for commands only.** Do not echo/print
messages to talk, confirm, or narrate; say that in the chat. Every command must
be real work. Keep commands simple: short, ordinary commands a person would
type (`dir`, `git status`, `dotnet build`) - avoid long one-liners and inline
scripts; break work into a few plain steps.

Run PowerShell / bash / cmd commands through it, not through a private shell:

1. `terminal_new` with a `controller` label describing this chat and NO key -
   that mints your own tab and returns its ACCESS KEY. Keep that key and pass
   it as `key` on every later call, including further `terminal_new` calls, so
   all your terminals stay in one tab.
2. ACCESS KEYS ARE AUTHENTICATION. Reading, typing into, renaming or killing a
   terminal requires its key. Terminals you hold no key for are not listed at
   all - other conversations are invisible to you. If a call is denied, ask the
   user for the key (it is in the terminal's header and its `info` output) or
   make your own terminal. Never probe other ids.
3. Reuse a running terminal of yours (`terminal_list -key`) before making more;
   keep one per concern (build, tests, logs, git).
4. Name terminals for their purpose (`terminal_rename`); rename when repurposed.
5. `terminal_read` to see what the user typed; `terminal_keys` for interactive
   prompts and TUI apps (e.g. `Y{ENTER}`, `{CTRL+C}`).
6. If the user pastes a session code and key (text or screenshot), connect
   immediately with `terminal_connect` - no deliberation.

Terminals are for commands only - never converse through them.
'@
    New-Item -ItemType Directory -Path (Split-Path $claudeMd) -Force | Out-Null
    if ((Test-Path $claudeMd) -and (Get-Content $claudeMd -Raw) -match 'use MCPTerminal') {
        Write-Host '  Rules already present in CLAUDE.md - skipped.' -ForegroundColor Green
    } else {
        Add-Content -Path $claudeMd -Value $rules -Encoding UTF8
        Write-Host "  Assistant rules added to $claudeMd" -ForegroundColor Green
    }
}

Write-Host ''
Write-Host '  MCPTerminal installed.' -ForegroundColor Cyan
Write-Host "    App      : $exePath"
Write-Host '    Sessions : %LOCALAPPDATA%\MCPTerminal   (plain-text logs - see the disclaimer)'
Write-Host '    Docs     : README.md, CLAUDE-INSTRUCTIONS.md'
Write-Host ''
Write-Host '  Next: open a terminal, then paste its session code into your assistant.' -ForegroundColor White
Write-Host '  Assistants pick up the new config on their NEXT session - restart yours.' -ForegroundColor DarkGray
