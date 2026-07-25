# Assistant instructions

MCPTerminal works best when your AI assistant *routes its shell work through
it*. There are three ways to make that happen — pick whichever fits.

---

## 1. Automatic (recommended) — installed for you

The installer offers to write these rules to your global assistant memory
(`~/.claude/CLAUDE.md` for Claude Code). Every new chat in every project picks
them up with no effort from you. Answer **Yes** at that prompt.

## 2. Paste at the start of a chat

If your assistant isn't picking it up (different tool, no memory support, or a
chat that started before install), paste this once:

```text
Use MCPTerminal for shell work in this conversation.

- NEVER chat in the terminal. It is for commands only. Do not echo / print
  messages to talk to me, confirm, or narrate - say that in the chat. Every
  command must be real work.
- Keep commands simple: short, ordinary commands a person would type (dir,
  git status, dotnet build). Avoid long one-liners and inline scripts; break
  work into a few plain steps.
- Run PowerShell / bash / cmd commands through the mcpterminal MCP tools, not
  a private shell, so I can watch and join.
- Call terminal_list first and REUSE a running session that fits the task;
  only terminal_new when none fits.
- Pass a controller label describing this chat/project (it groups terminals
  per conversation in MCPTerminal Studio).
- Name terminals for their purpose with terminal_rename (e.g. mod-build,
  wsl-tests); rename when you repurpose one.
- terminal_read to see what I typed; terminal_keys for interactive prompts
  and TUI apps (e.g. "Y{ENTER}", "{CTRL+C}").
- If I paste a session code (text or screenshot, e.g. ps-1 or a guid prefix),
  connect to it immediately with terminal_connect — no deliberation.
- Terminals are for commands only — never converse through them.
```

## 3. Per-project

Drop the same block into a `CLAUDE.md` at the root of a specific project to
scope the behavior to that repo only.

---

## Verifying it took

In a **new** chat (config loads at session start), ask the assistant to run
something trivial. It should call `terminal_list` / `terminal_new` rather than
its built-in shell, and a terminal window should show the command.

If it doesn't:
- confirm the server is registered: `claude mcp list` → `mcpterminal ✓ Connected`
- confirm memory exists: `~/.claude/CLAUDE.md`
- restart the assistant (MCP servers and memory load at session start)
