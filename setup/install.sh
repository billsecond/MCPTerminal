#!/bin/sh
# =============================================================================
# MCPTerminal installer (Linux / macOS)
#
#   ./install.sh              interactive install
#   ./install.sh --yes        accept every prompt (unattended)
#   ./install.sh --uninstall
#
# Installs the binary to ~/.local/bin/mcpterm and optionally registers the
# MCP server + assistant routing rules.
# =============================================================================
set -e
BIN_DIR="$HOME/.local/bin"
TARGET="$BIN_DIR/mcpterm"
HERE=$(cd "$(dirname "$0")" && pwd)
ASSUME_YES=0
[ "$1" = "--yes" ] && ASSUME_YES=1

ask() {  # ask "question" "detail"
    [ "$ASSUME_YES" = "1" ] && return 0
    printf '\n  \033[1;36m%s\033[0m\n' "$1"
    [ -n "$2" ] && printf '  \033[90m%s\033[0m\n' "$2"
    printf '  [Y/n] '
    read -r a </dev/tty || a=""
    case "$a" in ''|y|Y) return 0 ;; *) return 1 ;; esac
}

if [ "$1" = "--uninstall" ]; then
    rm -f "$TARGET"
    command -v claude >/dev/null 2>&1 && claude mcp remove --scope user mcpterminal >/dev/null 2>&1 || true
    echo "MCPTerminal uninstalled."
    echo "Session logs were KEPT at ~/.local/share/mcpterminal (delete manually if you want them gone)."
    exit 0
fi

# --------------------------------------------------------------- disclaimer
printf '\n  \033[1;33m============================================================\033[0m\n'
printf '  \033[1;33m MCPTerminal - PLEASE READ BEFORE INSTALLING\033[0m\n'
printf '  \033[1;33m============================================================\033[0m\n\n'
printf '   This tool lets an AI assistant type commands into a real shell on\n'
printf '   this machine, running as YOU, with YOUR access.\n\n'
printf '   \033[90m* An assistant you connect can run ANY command you could run:\n'
printf '     read, modify or delete files, install software, reach the network.\n'
printf '     Only share a session code with an assistant you trust - it is like\n'
printf '     handing over your keyboard.\n'
printf '   * Everything in a session is LOGGED IN PLAIN TEXT and kept\n'
printf '     indefinitely (~/.local/share/mcpterminal). Anything echoed to the\n'
printf '     screen - including secrets on command lines - is captured.\n'
printf '   * On shared machines, check the permissions of that directory.\n'
printf '   * Provided AS IS, without warranty (MIT). You are responsible for\n'
printf '     what gets run.\033[0m\n'
ask "Do you understand and want to continue?" "Answer n to abort the installation." || {
    echo "  Aborted - nothing was installed."; exit 1; }

# ------------------------------------------------------------------ install
OS=$(uname -s)
ARCH=$(uname -m)
case "$OS" in
    Linux)
        if ldd --version 2>&1 | grep -qi musl; then RID=linux-musl-x64; else RID=linux-x64; fi ;;
    Darwin)
        if [ "$ARCH" = "arm64" ]; then RID=osx-arm64; else RID=osx-x64; fi ;;
    *) echo "Unsupported OS: $OS"; exit 1 ;;
esac

SRC="$HERE/../releases/$RID/MCPTerminal"
[ -f "$SRC" ] || { echo "Release binary not found: $SRC"; exit 1; }

command -v script >/dev/null 2>&1 || echo "  WARNING: script(1) not found - MCPTerminal needs it to allocate a PTY."

mkdir -p "$BIN_DIR"
cp "$SRC" "$TARGET"
chmod +x "$TARGET"
printf '  \033[32mInstalled: %s  (rid: %s)\033[0m\n' "$TARGET" "$RID"

# MCP server registration
SERVER="$HERE/../mcp/server.mjs"
if command -v claude >/dev/null 2>&1; then
    if ask "Register the MCP server so AI assistants can use these terminals?" \
           "Runs: claude mcp add --scope user mcpterminal -- node \"$SERVER\""; then
        claude mcp add --scope user mcpterminal -- node "$SERVER" >/dev/null 2>&1 || true
        printf '  \033[32mMCP server registered globally (verify: claude mcp list).\033[0m\n'
    fi
else
    printf '\n  \033[90m(Claude Code CLI not found - register manually:)\n'
    printf '    claude mcp add --scope user mcpterminal -- node "%s"\033[0m\n' "$SERVER"
fi

# Assistant routing rules
CLAUDE_MD="$HOME/.claude/CLAUDE.md"
if ask "Add assistant routing rules to ~/.claude/CLAUDE.md?" \
       "Tells assistants in EVERY new chat to run shell commands through MCPTerminal. Appends - existing content is preserved."; then
    mkdir -p "$(dirname "$CLAUDE_MD")"
    if [ -f "$CLAUDE_MD" ] && grep -q "use MCPTerminal" "$CLAUDE_MD" 2>/dev/null; then
        printf '  \033[32mRules already present - skipped.\033[0m\n'
    else
        cat >> "$CLAUDE_MD" <<'EOF'

# Shell commands: use MCPTerminal

MCPTerminal is installed on this machine and registered as a global MCP server
(`mcpterminal`). It provides shared terminals the user can watch and type into.

**Never chat in the terminal - it is for commands only.** Do not echo/print
messages to talk, confirm, or narrate; say that in the chat. Every command must
be real work. Keep commands simple: short, ordinary commands a person would
type (`dir`, `git status`) - avoid long one-liners and inline scripts.

Run PowerShell / bash / cmd commands through it, not through a private shell:

1. `terminal_list` first - reuse a running session that fits the task.
2. No suitable session? `terminal_new`, then keep using it for that work.
3. Always pass a `controller` label describing the chat/project.
4. Name terminals for their purpose (`terminal_rename`).
5. `terminal_read` to see what the user typed; `terminal_keys` for interactive
   prompts and TUI apps.
6. If the user pastes a session code, connect immediately with
   `terminal_connect` - no deliberation.

Terminals are for commands only - never converse through them.
EOF
        printf '  \033[32mAssistant rules added to %s\033[0m\n' "$CLAUDE_MD"
    fi
fi

printf '\n  \033[1;36mMCPTerminal installed.\033[0m\n'
printf '    Run: mcpterm   (ensure ~/.local/bin is on your PATH)\n'
printf '    Sessions: ~/.local/share/mcpterminal   (plain-text logs - see the disclaimer)\n'
printf '    Assistants pick up the new config on their NEXT session - restart yours.\n'
