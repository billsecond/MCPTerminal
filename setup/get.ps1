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
if (Test-Path (Join-Path $src '.git')) {
    Write-Host "Updating source in $src"
    git -C $src pull --ff-only
} else {
    Write-Host "Cloning into $src"
    New-Item -ItemType Directory -Path (Split-Path $src) -Force | Out-Null
    git clone $repo $src
}

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
