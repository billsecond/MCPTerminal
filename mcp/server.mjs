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
        controller: { type: 'string', description: 'Label for THIS chat/project - claims the session for this conversation and gives it its own tab in Studio. Always pass it.' },
        key: { type: 'string', description: "Access key of an existing tab to add this terminal to. Omit to get a NEW tab with a freshly minted key (returned in the output - keep it, every later call needs it)." },
      },
    },
  },
  {
    name: 'terminal_list',
    description: 'List MCPTerminal sessions (guid, name, shell, status).',
    inputSchema: {
      type: 'object',
      properties: {
        key: { type: 'string', description: 'Access key. Lists EVERY terminal in that tab - including ones the USER added to your tab after you connected, which you already have full access to. Terminals in other tabs are not shown at all. Call this whenever the user mentions terminals you have not seen; do not ask for another key.' },
      },
    },
  },
  {
    name: 'terminal_connect',
    description:
      'Connect to a session by its code: announces the connection by running the info command (shows CONNECTED + which chat). Use this FIRST when the user shares a session code. ' +
      'If the terminal is LOCAL/unclaimed (its info says "no key exists yet"), there is no key to ask for: call this with `controller` and NO `key`, and the terminal is moved into a new tab of your own, which returns the access key to use from then on.',
    inputSchema: {
      type: 'object',
      properties: {
        id: { type: 'string', description: 'Session code: name (word-NN) or guid prefix' },
        key: { type: 'string', description: "Access key for this terminal's tab. Needed for a terminal that already belongs to a tab. OMIT for a LOCAL/unclaimed terminal - those have no key, and none can be given to you." },
        controller: { type: 'string', description: 'Label describing this chat/project. For a LOCAL terminal this is what claims it: the terminal moves out of Local into your own tab and you get that tab\'s key back.' },
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
        key: { type: 'string', description: "Access key for this terminal's tab. Needed for a terminal that already belongs to a tab. OMIT for a LOCAL/unclaimed terminal - those have no key; pass `controller` instead and it is claimed into your own tab." },
        controller: { type: 'string', description: 'Label describing this chat/project (shown by the info command). Claims a LOCAL terminal into your own tab.' },
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
        key: { type: 'string', description: "Access key for this terminal's tab. Needed for a terminal that already belongs to a tab; a LOCAL/unclaimed terminal has none - claim it first with terminal_connect + `controller`." },
      },
      required: ['id', 'keys'],
    },
  },
  {
    name: 'terminal_read',
    description: "Read the tail of a session's transcript (includes what the user typed).",
    inputSchema: {
      type: 'object',
      properties: {
        id: { type: 'string' },
        tail: { type: 'number' },
        key: { type: 'string', description: "Access key for this terminal's tab. Needed for a terminal that already belongs to a tab; a LOCAL/unclaimed terminal has none - claim it first with terminal_connect + `controller`." },
      },
      required: ['id'],
    },
  },
  {
    name: 'terminal_rename',
    description:
      'Rename a session to describe its PURPOSE (e.g. "mod-build", "wsl-tests"). Keep terminal names meaningful: rename when you repurpose one.',
    inputSchema: {
      type: 'object',
      properties: {
        id: { type: 'string' },
        name: { type: 'string' },
        key: { type: 'string', description: "Access key for this terminal's tab. Needed for a terminal that already belongs to a tab; a LOCAL/unclaimed terminal has none - claim it first with terminal_connect + `controller`." },
      },
      required: ['id', 'name'],
    },
  },
  {
    name: 'terminal_kill',
    description: 'End a session (its transcript is preserved).',
    inputSchema: {
      type: 'object',
      properties: {
        id: { type: 'string' },
        key: { type: 'string', description: "Access key for this terminal's tab. Needed for a terminal that already belongs to a tab; a LOCAL/unclaimed terminal has none - claim it first with terminal_connect + `controller`." },
      },
      required: ['id'],
    },
  },
];

// Every mutating call returns the current terminal roster, so the model always
// knows what exists, who owns it, and what each one is doing - without having
// to remember or call terminal_list again.
async function withState(res, key) {
  try {
    const list = await runCli(key ? ['list', '-Key', key] : ['list']);
    return { ...res, text: `${res.text}\n\n--- terminals now ---\n${list.text}` };
  } catch {
    return res;
  }
}

async function callTool(name, args = {}) {
  switch (name) {
    case 'terminal_new': {
      const a = ['new'];
      if (args.shell) a.push('-Shell', args.shell);
      if (args.name) a.push('-Name', args.name);
      if (args.cwd) a.push('-Cwd', args.cwd);
      if (args.wslDistro) a.push('-WslDistro', args.wslDistro);
      if (args.controller) a.push('-Controller', args.controller);
      if (args.key) a.push('-Key', args.key);
      return withState(await runCli(a), args.key);
    }
    case 'terminal_list':
      return runCli(args.key ? ['list', '-Key', args.key] : ['list']);
    case 'terminal_connect': {
      const a = ['connect', '-Id', args.id, '-Key', args.key ?? ''];
      if (args.controller) a.push('-Controller', args.controller);
      return withState(await runCli(a), args.key);
    }
    case 'terminal_exec': {
      const a = ['exec', '-Id', args.id, '-Command', args.command, '-Key', args.key ?? ''];
      if (args.controller) a.push('-Controller', args.controller);
      if (args.timeoutSec) a.push('-TimeoutSec', String(args.timeoutSec));
      return withState(await runCli(a), args.key);
    }
    case 'terminal_keys':
      return runCli(['keys', '-Id', args.id, '-Keys', args.keys, '-Key', args.key ?? '']);
    case 'terminal_read': {
      const a = ['read', '-Id', args.id, '-Key', args.key ?? ''];
      if (args.tail) a.push('-Tail', String(args.tail));
      return runCli(a);
    }
    case 'terminal_rename':
      return runCli(['rename', '-Id', args.id, '-Name', args.name, '-Key', args.key ?? '']);
    case 'terminal_kill':
      return runCli(['kill', '-Id', args.id, '-Key', args.key ?? '']);
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
          'IDENTIFY YOURSELF - do this once, before any other terminal call:',
          'compose a controller id of the form "<chat title> - <4 hex chars you',
          'generate>", e.g. "Payments API refactor - 9c2f". Use the conversation title',
          'the user sees in their chat list. Generate the suffix yourself, once, and',
          'then pass that EXACT SAME string as `controller` on EVERY terminal_new,',
          'terminal_connect and terminal_exec call for the rest of the conversation.',
          'It is this conversation\'s identity: Studio groups all terminals sharing a',
          'controller under one top tab, so a stable id keeps your terminals together',
          'and stops other chats from landing in your tab.',
          '',
          'ACCESS KEYS - THIS IS AUTHENTICATION, TREAT IT AS SUCH.',
          'Every terminal belongs to a tab, and every tab has one random access key',
          '(looks like mt_a1b2c3d4e5f6). You cannot read, type into, rename or kill a',
          'terminal without passing its key as `key`. Terminals you hold no key for',
          'are not even listed - other conversations are invisible to you, and yours',
          'are invisible to them.',
          '- Your FIRST call should be terminal_new with your controller id and NO key.',
          '  That mints a new tab and returns its ACCESS KEY. Remember that key and',
          '  pass it on every subsequent call, including later terminal_new calls, so',
          '  all your terminals land in the same tab.',
          '- ONE KEY UNLOCKS YOUR WHOLE TAB, NOT ONE TERMINAL. It covers every',
          '  terminal in that tab - including ones the USER creates in it after you',
          '  connected. You already have full access to those: read them, type in',
          '  them, rename them. If the user says "see my new terminals" or names one',
          '  you do not recognise, call terminal_list with the key you already hold -',
          '  it will be there. Asking for a second key is always wrong: a tab has',
          '  exactly one key, and you have it.',
          '- If you lose the key you cannot get it back: the user must read it off the',
          '  terminal window (it is in the header and in `info` output) and paste it.',
          '  Ask them for it rather than guessing.',
          '- If a call is denied, do NOT retry or probe other ids. Either ask the user',
          '  for the key, or make your own terminal with terminal_new.',
          '',
          'LOCAL TERMINALS HAVE NO KEY - DO NOT ASK FOR ONE.',
          'A terminal the user opened themselves starts in the "Local" tab, unclaimed.',
          'Its `info` says "LOCAL - unclaimed; no key exists yet" and terminal_list',
          'shows it as "(local - take over with -Controller)". No key exists for it,',
          'the user cannot produce one, and asking for it strands you both.',
          'To use such a terminal, TAKE IT OVER - this is expected and always allowed:',
          '  terminal_connect { id: "<code>", controller: "<your chat id>" }   // no key',
          'That moves the terminal OUT of Local into a new tab of your own (it visibly',
          'jumps tabs in Studio) and RETURNS that tab\'s access key in the response.',
          'Read the key out of that response and pass it on every later call. You can',
          'also claim directly with terminal_exec by passing `controller` and no key.',
          'So: key missing + terminal is Local => claim it. Key missing + terminal',
          'belongs to another chat => ask the user, or make your own with terminal_new.',
          '',
          'ONE CONVERSATION = ITS OWN TERMINALS. Keep one terminal per concern (build,',
          'tests, logs, git) within your tab and rotate between them by id. Name each',
          'for its purpose with terminal_rename.',
          '',
          'CONNECTING: if the user pastes session info - text or a screenshot showing',
          '"session code: <name>" - do not deliberate, connect immediately. Codes look',
          'like ps-1 / cedar-10 or an 8+ char guid prefix.',
          '  * pasted an "access key: mt_..." too -> terminal_connect with code AND key.',
          '  * says LOCAL / unclaimed / "no key exists yet" -> terminal_connect with the',
          '    code and your `controller`, NO key. It becomes yours and the response',
          '    carries the new key. Never ask the user for a Local terminal\'s key.',
          '  * no key and not Local -> ask the user for the key, or use terminal_new.',
          '',
          'Use terminal_read to see what the user typed; terminal_keys for interactive',
          'prompts and TUI apps (e.g. "Y{ENTER}", "{CTRL+C}").',
          '',
          'ACT, DO NOT DELIBERATE. These calls are cheap and local. Every mutating',
          'call returns the current terminal roster (name, shell, owner, and what',
          'each one is doing), so you always know the state without re-checking:',
          'just make the call. Terminals marked "(unclaimed / user)" are the user\'s',
          'own - you may take one over at any time by passing your controller id,',
          'and you can read what they have been doing there with terminal_read.',
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
