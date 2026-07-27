# =============================================================================
# MCPTerminal one-command installer (Windows)
#
#   powershell -c "irm https://raw.githubusercontent.com/billsecond/MCPTerminal/main/setup/get.ps1 | iex"
#
# Clones (or updates) the source, builds the terminal + Studio, and installs:
# desktop shortcuts, Windows Terminal profiles, %LOCALAPPDATA% app folder.
#
# Prerequisites: git, .NET 10 SDK  (winget install Git.Git Microsoft.DotNet.SDK.10)
# =============================================================================
$ErrorActionPreference = 'Stop'
$repo = 'https://github.com/billsecond/MCPTerminal.git'
$src = Join-Path $env:LOCALAPPDATA 'MCPTerminal\source'

function Need([string]$cmd, [string]$hint) {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        throw "Missing prerequisite: $cmd  ->  install with: $hint"
    }
}
Need git 'winget install Git.Git'
Need dotnet 'winget install Microsoft.DotNet.SDK.10'

Write-Host 'MCPTerminal installer' -ForegroundColor Cyan
# git is a native exe, so $ErrorActionPreference does NOT stop the script when it
# fails - check the exit code explicitly. Without this a failed pull just printed
# a message and then happily rebuilt the OLD source, which looks exactly like
# "I installed the latest and nothing changed".
if (Test-Path (Join-Path $src '.git')) {
    Write-Host "Updating source in $src"
    git -C $src pull --ff-only
    if ($LASTEXITCODE -ne 0) {
        Write-Host ''
        Write-Host '  Could not fast-forward the installer''s copy of the source.' -ForegroundColor Yellow
        Write-Host "  It lives in $src and exists only to build from." -ForegroundColor DarkGray
        $a = Read-Host '  Reset it to match the latest published version? [Y/n]'
        if ($a -ne '' -and $a -notmatch '^[Yy]') { throw 'Aborted - source is not up to date, nothing was built.' }
        git -C $src fetch origin
        if ($LASTEXITCODE -ne 0) { throw 'git fetch failed - check your network or GitHub access.' }
        git -C $src reset --hard origin/main
        if ($LASTEXITCODE -ne 0) { throw 'git reset failed - delete the folder above and run this again.' }
    }
} else {
    Write-Host "Cloning into $src"
    New-Item -ItemType Directory -Path (Split-Path $src) -Force | Out-Null
    git clone $repo $src
    if ($LASTEXITCODE -ne 0) { throw 'git clone failed - check your network or GitHub access.' }
}

# Say exactly what is about to be built, so "did I get the latest?" is answerable
# by looking at the screen rather than by guessing.
$head = (git -C $src log --oneline -1) -join ''
Write-Host "Building: $head" -ForegroundColor Cyan

Write-Host 'Building terminal (self-contained win-x64)...'
Push-Location (Join-Path $src 'app')
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=none -o ..\releases\win-x64 --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'terminal build failed' }
Pop-Location

Write-Host 'Building Studio...'
Push-Location (Join-Path $src 'studio')
dotnet build -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'Studio build failed' }
Pop-Location

& (Join-Path $src 'setup\install.ps1')
