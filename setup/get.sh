#!/bin/sh
# =============================================================================
# MCPTerminal one-command installer (Linux / macOS)
#
#   curl -fsSL https://raw.githubusercontent.com/billsecond/MCPTerminal/main/setup/get.sh | sh
#
# Clones (or updates) the source, builds a self-contained binary for this
# platform, and installs it to ~/.local/bin/mcpterm.
#
# Prerequisites: git, .NET 10 SDK, script(1)
# =============================================================================
set -e
REPO="https://github.com/billsecond/MCPTerminal.git"
SRC="$HOME/.local/share/mcpterminal/source"

command -v git >/dev/null || { echo "missing prerequisite: git"; exit 1; }
command -v dotnet >/dev/null || { echo "missing prerequisite: dotnet (.NET 10 SDK)"; exit 1; }

echo "MCPTerminal installer"
if [ -d "$SRC/.git" ]; then
    echo "Updating source in $SRC"
    git -C "$SRC" pull --ff-only
else
    echo "Cloning into $SRC"
    mkdir -p "$(dirname "$SRC")"
    git clone "$REPO" "$SRC"
fi

OS=$(uname -s); ARCH=$(uname -m)
case "$OS" in
    Linux)  if ldd --version 2>&1 | grep -qi musl; then RID=linux-musl-x64; else RID=linux-x64; fi ;;
    Darwin) if [ "$ARCH" = "arm64" ]; then RID=osx-arm64; else RID=osx-x64; fi ;;
    *) echo "Unsupported OS: $OS"; exit 1 ;;
esac

echo "Building ($RID)..."
cd "$SRC/app"
dotnet publish -c Release -r "$RID" --self-contained -p:PublishSingleFile=true -p:DebugType=none -o "../releases/$RID" --nologo -v quiet

sh "$SRC/setup/install.sh"
