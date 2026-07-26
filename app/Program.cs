// =============================================================================
// MCPTerminal - a shared terminal: a real shell that both a human and an AI
// assistant (via the companion CLI / MCP server) can type into.
//
// Cross-platform:
//   * Windows : hosts the shell through ConPTY (the Windows Terminal mechanism)
//   * Linux/macOS : hosts the shell through a PTY allocated by `script(1)`
//
// Shells (--shell):
//   Windows : pwsh (default) | powershell | cmd | bash (Git Bash) | bash-wsl
//   Unix    : bash (default) | sh
//
// The terminal below the startup header is 100% native - line editing, colors,
// completion, scrollback, TUI apps - because it IS the real shell on a real
// PTY. Status lives in the window/tab title ([DISCONNECTED]/[CONTROLLED]/
// [IDLE]) and in the prompt (a cyan * while the assistant is connected).
//
// Sessions are registered by GUID in <root>/index.json, with per-session logs:
//   screen.log         raw byte mirror of everything on screen
//   transcript.log     plain-text transcript (ANSI stripped)
//   assistant-cmds.log exactly which commands came from the assistant
//
// Root: %LOCALAPPDATA%\MCPTerminal on Windows, ~/.local/share/mcpterminal on
// Unix, or the MCPTERMINAL_ROOT environment variable.
//
// Zero arguments needed: run it, it names itself and shows a session code.
// Optional: --name <n> --cwd <dir> --root <dir> --shell <sh> --wsl-distro <d>
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace MCPTerminal;

internal static class Program
{

    // ------------------------------------------------------------------ state
    static readonly object OutLock = new();
    static string Root, SessionId, Name, Shell, WslDistro, Controller, SessionDir, InboxDir, OutboxDir;
    static string ScreenLog, TranscriptLog, AssistantLog, StateFile, IndexFile;
    static string ShortId => SessionId[..8];

    static Stream _stdout;
    static Stream _ptyIn;                    // writes = keystrokes into the shell
    static volatile bool _running = true;
    static DateTime _lastControlUtc = DateTime.MinValue;
    static string _lastTitle = "";
    static bool IsWin => OperatingSystem.IsWindows();

    static int Main(string[] args)
    {
        string cwd = null, name = null, shell = null;
        Root = Environment.GetEnvironmentVariable("MCPTERMINAL_ROOT");
        WslDistro = Environment.GetEnvironmentVariable("MCPTERMINAL_WSL_DISTRO");
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--name": name = args[i + 1]; break;
                case "--cwd": cwd = args[i + 1]; break;
                case "--root": Root = args[i + 1]; break;
                case "--shell": shell = args[i + 1]; break;
                case "--wsl-distro": WslDistro = args[i + 1]; break;
                case "--controller": Controller = args[i + 1]; break;
            }
        }
        Root ??= IsWin
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCPTerminal")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "mcpterminal");
        cwd ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Shell = (shell ?? "").ToLowerInvariant();
        if (Shell == "") Shell = IsWin ? "pwsh" : (File.Exists("/bin/bash") || File.Exists("/usr/bin/bash") ? "bash" : "sh");

        // If MCPTerminal Studio is running, integrate: hand this launch to the
        // app so the terminal opens inside it (unless --standalone is given).
        if (IsWin && !args.Contains("--standalone") &&
            StudioBridge.TryRedirect(Root, Shell, name, cwd, WslDistro, Controller))
            return 0;

        SessionId = Guid.NewGuid().ToString();
        Name = ShellSupport.SanitizeName(name, ShellSupport.AutoName(Root, Shell));
        WslDistro = ShellSupport.SanitizeName(WslDistro, null);
        SessionDir = Path.Combine(Root, "sessions", SessionId);
        InboxDir = Path.Combine(SessionDir, "inbox");
        OutboxDir = Path.Combine(SessionDir, "outbox");
        Directory.CreateDirectory(InboxDir);
        Directory.CreateDirectory(OutboxDir);
        // Session logs contain everything printed in the terminal - keep them
        // owner-only on Unix (Windows inherits the restricted %LOCALAPPDATA% ACL).
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                const UnixFileMode ownerOnlyDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                foreach (var d in new[] { Root, Path.Combine(Root, "sessions"), SessionDir, InboxDir, OutboxDir })
                    File.SetUnixFileMode(d, ownerOnlyDir);
            }
            catch { }
        }
        ScreenLog = Path.Combine(SessionDir, "screen.log");
        TranscriptLog = Path.Combine(SessionDir, "transcript.log");
        AssistantLog = Path.Combine(SessionDir, "assistant-cmds.log");
        StateFile = Path.Combine(SessionDir, "state.json");
        IndexFile = Path.Combine(Root, "index.json");

        _stdout = Console.OpenStandardOutput();
        TerminalSetup.Enter();
        RegisterSession();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { MarkClosed(); TerminalSetup.Exit(); };
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; };   // ^C belongs to the shell

        WriteShellInit();
        IPtySession pty;
        try
        {
            string cmd = BuildShellCommand();
            pty = IsWin ? WindowsPty.Spawn(cmd, cwd) : UnixPty.Spawn(cmd, cwd, Shell == "sh" ? InitPath : null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MCPTerminal: failed to start shell '{Shell}': {ex.Message}");
            MarkClosed();
            return 1;
        }
        _ptyIn = pty.Input;

        PaintTitle();
        new Thread(() => OutputPump(pty.Output)) { IsBackground = true }.Start();
        new Thread(StdinPump) { IsBackground = true }.Start();
        new Thread(InboxPump) { IsBackground = true }.Start();
        new Thread(() => Housekeeping(pty)) { IsBackground = true }.Start();

        pty.WaitForExit();
        _running = false;
        MarkClosed();
        lock (OutLock) { WriteOut($"\r\n\x1b[90m Session '{Name}' ended - transcript saved.\x1b[0m\r\n"); }
        pty.Dispose();
        TerminalSetup.Exit();
        return 0;
    }

    // ---------------------------------------------------------------- session
    static string AutoName()
    {
        string[] words = { "amber", "basil", "cedar", "delta", "ember", "flint",
                           "gale", "harbor", "iris", "juniper", "koa", "lunar",
                           "mesa", "nova", "onyx", "pine", "quartz", "ridge",
                           "slate", "topaz", "umber", "vale", "willow", "zephyr" };
        var rng = new Random();
        return $"{words[rng.Next(words.Length)]}-{rng.Next(10, 99)}";
    }

    static void RegisterSession()
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var state = new JsonObject
        {
            ["sessionId"] = SessionId, ["name"] = Name, ["shell"] = Shell,
            ["mode"] = "native", ["status"] = "running",
            ["windowPid"] = Environment.ProcessId, ["createdAt"] = now,
        };
        // Owning conversation, stamped at creation so a session is grouped
        // (and claimable) from its very first moment.
        if (!string.IsNullOrWhiteSpace(Controller)) state["controller"] = Controller;
        WriteJson(StateFile, state);
        MutateIndex(idx => idx[SessionId] = new JsonObject
        {
            ["name"] = Name, ["shell"] = Shell, ["mode"] = "native",
            ["status"] = "running", ["windowPid"] = Environment.ProcessId,
            ["createdAt"] = now, ["transcript"] = TranscriptLog,
        });
    }

    static void MarkClosed()
    {
        try
        {
            var st = JsonNode.Parse(File.ReadAllText(StateFile)) as JsonObject ?? new JsonObject();
            st["status"] = "closed";
            WriteJson(StateFile, st);
            MutateIndex(idx => { if (idx[SessionId] is JsonObject e) e["status"] = "closed"; });
        }
        catch { }
    }

    static void WriteJson(string path, JsonObject obj) =>
        File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    static void MutateIndex(Action<JsonObject> mutate)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                JsonObject idx = File.Exists(IndexFile)
                    ? JsonNode.Parse(File.ReadAllText(IndexFile)) as JsonObject ?? new JsonObject()
                    : new JsonObject();
                mutate(idx);
                WriteJson(IndexFile, idx);
                return;
            }
            catch { Thread.Sleep(60 * (i + 1)); }
        }
    }

    // ------------------------------------------------------------------ title
    static void PaintTitle()
    {
        bool everJoined = _lastControlUtc != DateTime.MinValue;
        double idleSec = everJoined ? (DateTime.UtcNow - _lastControlUtc).TotalSeconds : double.MaxValue;
        string title =
            !everJoined ? $"[DISCONNECTED] MCPTerminal {Name}"
            : idleSec < 120 ? $"[CONTROLLED] MCPTerminal {Name}"
            : $"[IDLE] MCPTerminal {Name}";
        if (title == _lastTitle) return;
        _lastTitle = title;
        if (IsWin) { try { Console.Title = title; } catch { } }
        else lock (OutLock) { WriteOut($"\x1b]0;{title}\x07"); }
    }

    // -------------------------------------------------------------- the shell
    static bool IsBashLike => Shell is "bash" or "bash-wsl" or "sh";
    static string InitPath => Path.Combine(SessionDir, ShellSupport.InitFileName(Shell));
    static string BuildShellCommand() => ShellSupport.BuildShellCommand(Shell, InitPath, WslDistro, IsWin);
    static void WriteShellInit()
    {
        ShellSupport.WriteInitScript(Shell, InitPath, Name, SessionId, SessionDir, StateFile);
        if (Shell == "bash-wsl") ShellSupport.PushInitToWsl(InitPath, WslDistro);
    }
    // ------------------------------------------------------------------ pumps
    static void OutputPump(Stream ptyOut)
    {
        var buf = new byte[8192];
        var carry = Array.Empty<byte>();
        while (_running)
        {
            int n;
            try { n = ptyOut.Read(buf, 0, buf.Length); } catch { break; }
            if (n <= 0) break;
            var translated = VtFilter.Process(buf, n, ref carry);
            lock (OutLock) { _stdout.Write(translated, 0, translated.Length); _stdout.Flush(); }
            try
            {
                using var fs = new FileStream(ScreenLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(buf, 0, n);
            }
            catch { }
            try
            {
                string text = Encoding.UTF8.GetString(buf, 0, n);
                File.AppendAllText(TranscriptLog, VtFilter.StripAnsi(text));
            }
            catch { }
        }
    }

    static void StdinPump()
    {
        using var stdin = Console.OpenStandardInput();
        var buf = new byte[1024];
        while (_running)
        {
            int n;
            try { n = stdin.Read(buf, 0, buf.Length); } catch { break; }
            if (n <= 0) break;
            try { _ptyIn.Write(buf, 0, n); _ptyIn.Flush(); } catch { break; }
        }
    }

    static void InboxPump()
    {
        Thread.Sleep(1500);                       // let the shell finish booting
        while (_running)
        {
            try
            {
                foreach (var f in Directory.GetFiles(InboxDir, "*.cmd").OrderBy(x => x))
                {
                    string raw;
                    try { raw = File.ReadAllText(f, Encoding.UTF8); } catch { continue; }
                    try { File.Delete(f); } catch { }
                    int nl = raw.IndexOf('\n');
                    if (nl < 1) continue;
                    string id = raw[..nl].Trim();
                    string cmd = raw[(nl + 1)..].TrimEnd('\r', '\n');
                    if (cmd == "__CT_EXIT__") { TypeIntoShell("exit", clearFirst: true); return; }

                    // Raw keystrokes: verbatim, no line-clear, no auto Enter.
                    if (cmd.StartsWith("__CT_KEYS__"))
                    {
                        try
                        {
                            var keyBytes = Convert.FromBase64String(cmd["__CT_KEYS__".Length..]);
                            _ptyIn.Write(keyBytes, 0, keyBytes.Length);
                            _ptyIn.Flush();
                        }
                        catch { }
                        _lastControlUtc = DateTime.UtcNow;
                        try { File.WriteAllText(Path.Combine(OutboxDir, id + ".done"), "0\n0\n(keys sent)"); } catch { }
                        PaintTitle();
                        continue;
                    }

                    TypeIntoShell(cmd, clearFirst: true);
                    _lastControlUtc = DateTime.UtcNow;
                    try
                    {
                        var st = JsonNode.Parse(File.ReadAllText(StateFile)) as JsonObject ?? new JsonObject();
                        st["lastControlUnix"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        WriteJson(StateFile, st);
                    }
                    catch { }
                    try { File.AppendAllText(AssistantLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {cmd}\n"); } catch { }
                    try { File.WriteAllText(Path.Combine(OutboxDir, id + ".done"), "0\n0\n(typed into session)"); } catch { }
                    PaintTitle();
                }
            }
            catch { }
            Thread.Sleep(200);
        }
    }

    static void TypeIntoShell(string cmd, bool clearFirst)
    {
        try
        {
            if (clearFirst)
            {
                // Clear anything half-typed at the prompt first.
                // PowerShell/cmd line editing: ESC clears the line.
                // bash/sh readline: Ctrl+U (a bare ESC would arm a Meta- prefix).
                _ptyIn.WriteByte(IsBashLike ? (byte)0x15 : (byte)0x1B);
                _ptyIn.Flush();
                Thread.Sleep(80);
            }
            var bytes = Encoding.UTF8.GetBytes(cmd + (IsBashLike && !IsWin ? "\n" : "\r"));
            _ptyIn.Write(bytes, 0, bytes.Length);
            _ptyIn.Flush();
        }
        catch { }
    }

    static void Housekeeping(IPtySession pty)
    {
        int lastW = SafeWidth(), lastH = SafeHeight();
        while (_running)
        {
            Thread.Sleep(1000);
            try
            {
                int w = SafeWidth(), h = SafeHeight();
                if (w != lastW || h != lastH) { lastW = w; lastH = h; pty.Resize(w, h); }
                PaintTitle();
            }
            catch { }
        }
    }

    static int SafeWidth() { try { return Math.Max(40, Console.WindowWidth); } catch { return 120; } }
    static int SafeHeight() { try { return Math.Max(10, Console.WindowHeight); } catch { return 30; } }
    static void WriteOut(string s) { var b = Encoding.UTF8.GetBytes(s); _stdout.Write(b, 0, b.Length); _stdout.Flush(); }
}
