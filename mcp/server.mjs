#!/usr/bin/env node
// =============================================================================
// MCPTerminal MCP server - exposes the mcpterm CLI as typed MCP tools over
// stdio (JSON-RPC 2.0). Zero dependencies.
// =============================================================================
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const CLI = join(dirname(dirname(fileURLToPath(import.meta.url))), 'mcpterm.ps1');

function runCli(args) {
  return new Promise((resolve) => {
    const p = spawn('pwsh', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', CLI, ...args], {
      windowsHide: true,
    });
    let out = '', err = '';
    p.stdout.on('data', (d) => (out += d));
    p.stderr.on('data', (d) => (err += d));
    p.on('close', (code) => resolve({ code, text: (out + (err ? `\n${err}` : '')).trim() }));
    p.on('error', (e) => resolve({ code: -1, text: `spawn failed: ${e.message}` }));
  });
}

const TOOLS = [
  {
    name: 'terminal_new',
    description:
      'Open a new MCPTerminal shared terminal window (the user can type into it too). The session code appears in its header/title.',
    inputSchema: {
      type: 'object',
      properties: {
        shell: { type: 'string', enum: ['pwsh', 'powershell', 'cmd', 'bash', 'bash-wsl'], description: 'Shell (default pwsh)' },
        name: { type: 'string', description: 'Session name (auto-generated if omitted)' },
        cwd: { type: 'string', description: 'Starting directory' },
        wslDistro: { type: 'string', description: 'WSL distro for bash-wsl (e.g. Ubuntu)' },
      },
    },
  },
  {
    name: 'terminal_list',
    description: 'List MCPTerminal sessions (guid, name, shell, status).',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'terminal_connect',
    description:
      'Connect to a session by its code: announces the connection by running the info command (shows CONNECTED + which chat). Use this FIRST when the user shares a session code.',
    inputSchema: {
      type: 'object',
      properties: {
        id: { type: 'string', description: 'Session code: name (word-NN) or guid prefix' },
        controller: { type: 'string', description: 'Label describing this chat/project' },
      },
      required: ['id'],
    },
  },
  {
    name: 'terminal_exec',
    description:
      "Type a command into a shared session (the user watches it appear and run). Returns transcript output.",
    inputSchema: {
      type: 'object',
      properties: {
        id: { type: 'string', description: 'Session code: name (word-NN) or guid prefix' },
        command: { type: 'string' },
        controller: { type: 'string', description: 'Label describing this chat/project (shown by the info command)' },
        timeoutSec: { type: 'number' },
      },
      required: ['id', 'command'],
    },
  },
  {
    name: 'terminal_keys',
    description:
      'Send RAW keystrokes to a session - no line clearing, no automatic Enter. Use this to answer interactive prompts (y/n, menus, wizards) and to drive full-screen TUI apps. Tokens: {ENTER} {ESC} {TAB} {SPACE} {BKSP} {UP} {DOWN} {LEFT} {RIGHT} {CTRL+C} {CTRL+D} {CTRL+U}; anything else is literal text. Example: "Y{ENTER}".',
    inputSchema: {
      type: 'object',
      properties: {
        id: { type: 'string', description: 'Session code' },
        keys: { type: 'string', description: 'Keys to send, e.g. "Y{ENTER}" or "{DOWN}{DOWN}{ENTER}"' },
      },
      required: ['id', 'keys'],
    },
  },
  {
    name: 'terminal_read',
    description: "Read the tail of a session's transcript (includes what the user typed).",
    inputSchema: {
      type: 'object',
      properties: { id: { type: 'string' }, tail: { type: 'number' } },
      required: ['id'],
    },
  },
  {
    name: 'terminal_rename',
    description:
      'Rename a session to describe its PURPOSE (e.g. "mod-build", "wsl-tests"). Keep terminal names meaningful: rename when you repurpose one.',
    inputSchema: {
      type: 'object',
      properties: { id: { type: 'string' }, name: { type: 'string' } },
      required: ['id', 'name'],
    },
  },
  {
    name: 'terminal_kill',
    description: 'End a session (its transcript is preserved).',
    inputSchema: { type: 'object', properties: { id: { type: 'string' } }, required: ['id'] },
  },
];

async function callTool(name, args = {}) {
  switch (name) {
    case 'terminal_new': {
      const a = ['new'];
      if (args.shell) a.push('-Shell', args.shell);
      if (args.name) a.push('-Name', args.name);
      if (args.cwd) a.push('-Cwd', args.cwd);
      if (args.wslDistro) a.push('-WslDistro', args.wslDistro);
      return runCli(a);
    }
    case 'terminal_list':
      return runCli(['list']);
    case 'terminal_connect': {
      const a = ['connect', '-Id', args.id];
      if (args.controller) a.push('-Controller', args.controller);
      return runCli(a);
    }
    case 'terminal_exec': {
      const a = ['exec', '-Id', args.id, '-Command', args.command];
      if (args.controller) a.push('-Controller', args.controller);
      if (args.timeoutSec) a.push('-TimeoutSec', String(args.timeoutSec));
      return runCli(a);
    }
    case 'terminal_keys':
      return runCli(['keys', '-Id', args.id, '-Keys', args.keys]);
    case 'terminal_read': {
      const a = ['read', '-Id', args.id];
      if (args.tail) a.push('-Tail', String(args.tail));
      return runCli(a);
    }
    case 'terminal_rename':
      return runCli(['rename', '-Id', args.id, '-Name', args.name]);
    case 'terminal_kill':
      return runCli(['kill', '-Id', args.id]);
    default:
      return { code: -1, text: `unknown tool: ${name}` };
  }
}

// ---- minimal JSON-RPC over stdio -------------------------------------------
let buffer = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => {
  buffer += chunk;
  let nl;
  while ((nl = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, nl).trim();
    buffer = buffer.slice(nl + 1);
    if (line) handleLine(line);
  }
});

function send(obj) {
  process.stdout.write(JSON.stringify(obj) + '\n');
}

async function handleLine(line) {
  let msg;
  try { msg = JSON.parse(line); } catch { return; }
  const { id, method, params } = msg;
  if (method === 'initialize') {
    send({
      jsonrpc: '2.0', id,
      result: {
        protocolVersion: params?.protocolVersion ?? '2024-11-05',
        capabilities: { tools: {} },
        serverInfo: { name: 'mcpterminal', version: '2.1.0' },
        instructions: [
          "MCPTerminal provides the user's visible, shared shell. They watch it and type in it too.",
          '',
          'NEVER CHAT IN THE TERMINAL. It is for commands only.',
          '- Do NOT send echo / Write-Output / printf just to talk, narrate, confirm, or',
          '  say things like "OK" or "done". Say those things in the chat instead.',
          '- Every command you send must be real work the user asked for or that you',
          '  genuinely need. If you want to verify something, run the actual check',
          '  (e.g. `git status`), not a message about it.',
          '',
          'KEEP COMMANDS SIMPLE.',
          '- Prefer short, ordinary commands a person would type: `dir`, `git status`,',
          '  `dotnet build`, `ls -la`.',
          '- Avoid long one-liners, chained pipelines, and inline scripts. Break work',
          '  into a few plain steps so the user can read and reuse them.',
          '- No decorative output, banners, or formatting flourishes.',
          '',
          'ROUTING: run ALL PowerShell, bash and cmd commands through these tools',
          '(terminal_exec) rather than any private shell, so the user can see them.',
          '',
          'ORGANIZE + REUSE: call terminal_list first and reuse a running session that',
          'fits; only terminal_new when none does. Pass a controller label describing',
          'this chat/project (it groups terminals per conversation in Studio). Name',
          'sessions for their purpose with terminal_rename.',
          '',
          'CONNECTING: if the user pastes session info - text or a screenshot showing',
          '"session code: <name>" - do not deliberate, immediately call terminal_connect',
          'with that code. Codes look like ps-1 / cedar-10 or an 8+ char guid prefix.',
          '',
          'Use terminal_read to see what the user typed; terminal_keys for interactive',
          'prompts and TUI apps (e.g. "Y{ENTER}", "{CTRL+C}").',
        ].join('\n'),
      },
    });
  } else if (method === 'notifications/initialized') {
    // no response for notifications
  } else if (method === 'tools/list') {
    send({ jsonrpc: '2.0', id, result: { tools: TOOLS } });
  } else if (method === 'tools/call') {
    const { name, arguments: args } = params ?? {};
    const res = await callTool(name, args);
    send({
      jsonrpc: '2.0', id,
      result: {
        content: [{ type: 'text', text: res.text || '(no output)' }],
        isError: res.code !== 0,
      },
    });
  } else if (id !== undefined) {
    send({ jsonrpc: '2.0', id, error: { code: -32601, message: `method not found: ${method}` } });
  }
}
