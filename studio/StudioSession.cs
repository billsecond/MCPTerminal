// =============================================================================
// StudioSession - one in-app terminal inside MCPTerminal Studio.
//
// Speaks the exact same session protocol as a standalone terminal window
// (state.json / index.json / inbox / outbox / logs), so the CLI and MCP tools
// drive Studio terminals identically. mode:"studio" and windowPid = the Studio
// process, so `list`/`exec` liveness checks track the app.
// =============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace MCPTerminal.Studio;

public sealed class StudioSession
{
    public string Id { get; private set; }
    public string Name { get; private set; }
    public string Shell { get; private set; }
    public string SessionDir { get; private set; }
    public DateTime LastOutputUtc { get; private set; } = DateTime.MinValue;

    string _stateFile, _indexFile, _inboxDir, _outboxDir;
    string _screenLog, _transcriptLog, _assistantLog;
    WindowsPty _pty;
    volatile bool _running = true;

    public event Action<StudioSession, byte[]> Output;
    public event Action<StudioSession> Exited;
    public string ClosedReason { get; private set; } = "exited";   // exited | assistant | user

    static readonly string[] Words = { "amber", "basil", "cedar", "delta", "ember", "flint",
        "gale", "harbor", "iris", "juniper", "koa", "lunar", "mesa", "nova", "onyx", "pine",
        "quartz", "ridge", "slate", "topaz", "umber", "vale", "willow", "zephyr" };

    public static StudioSession Create(string root, string shell, string name, string cwd, string wslDistro,
        string controller = null)
    {
        var s = new StudioSession
        {
            Id = Guid.NewGuid().ToString(),
            Shell = string.IsNullOrWhiteSpace(shell) ? "pwsh" : shell.ToLowerInvariant(),
        };
        s.Name = ShellSupport.SanitizeName(name, ShellSupport.AutoName(root, s.Shell));
        wslDistro = ShellSupport.SanitizeName(wslDistro, null);
        cwd = string.IsNullOrWhiteSpace(cwd)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : cwd;

        s.SessionDir = Path.Combine(root, "sessions", s.Id);
        s._inboxDir = Path.Combine(s.SessionDir, "inbox");
        s._outboxDir = Path.Combine(s.SessionDir, "outbox");
        Directory.CreateDirectory(s._inboxDir);
        Directory.CreateDirectory(s._outboxDir);
        s._screenLog = Path.Combine(s.SessionDir, "screen.log");
        s._transcriptLog = Path.Combine(s.SessionDir, "transcript.log");
        s._assistantLog = Path.Combine(s.SessionDir, "assistant-cmds.log");
        s._stateFile = Path.Combine(s.SessionDir, "state.json");
        s._indexFile = Path.Combine(root, "index.json");

        string initPath = Path.Combine(s.SessionDir, ShellSupport.InitFileName(s.Shell));
        ShellSupport.WriteInitScript(s.Shell, initPath, s.Name, s.Id, s.SessionDir, s._stateFile);
        s.Register(controller);

        string cmdline = ShellSupport.BuildShellCommand(s.Shell, initPath, wslDistro, isWindows: true);
        s._pty = WindowsPty.Spawn(cmdline, cwd, 120, 30);

        new Thread(s.OutputPump) { IsBackground = true }.Start();
        new Thread(s.InboxPump) { IsBackground = true }.Start();
        new Thread(() => { s._pty.WaitForExit(); s._running = false; s.MarkClosed(); s.Exited?.Invoke(s); })
            { IsBackground = true }.Start();
        return s;
    }

    // ------------------------------------------------------------- lifecycle
    void Register(string controller = null)
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var state = new JsonObject
        {
            ["sessionId"] = Id, ["name"] = Name, ["shell"] = Shell,
            ["mode"] = "native", ["host"] = "studio", ["status"] = "running",
            ["windowPid"] = Environment.ProcessId, ["createdAt"] = now,
        };
        // owning conversation, stamped at creation so the tab exists immediately
        if (!string.IsNullOrWhiteSpace(controller)) state["controller"] = controller;
        WriteJson(_stateFile, state);
        MutateIndex(idx => idx[Id] = new JsonObject
        {
            ["name"] = Name, ["shell"] = Shell, ["mode"] = "native", ["host"] = "studio",
            ["status"] = "running", ["windowPid"] = Environment.ProcessId,
            ["createdAt"] = now, ["transcript"] = _transcriptLog,
        });
    }

    public void MarkClosed()
    {
        try
        {
            var st = JsonNode.Parse(File.ReadAllText(_stateFile)) as JsonObject ?? new JsonObject();
            st["status"] = "closed";
            WriteJson(_stateFile, st);
            MutateIndex(idx => { if (idx[Id] is JsonObject e) e["status"] = "closed"; });
        }
        catch { }
    }

    public void Close(string reason = "user")
    {
        ClosedReason = reason;
        _running = false;
        try { _pty.Kill(); } catch { }
        try { _pty.Dispose(); } catch { }
        MarkClosed();
    }

    public void Resize(int cols, int rows) { try { _pty.Resize(cols, rows); } catch { } }

    public void WriteInput(byte[] data)
    {
        try { _pty.Input.Write(data, 0, data.Length); _pty.Input.Flush(); } catch { }
    }

    // Reads live status for the UI: (controller, secondsSinceAssistant or -1,
    // current name - renames flow through state.json).
    public (string controller, long controlAgo, string name) ReadLive()
    {
        try
        {
            var st = JsonNode.Parse(File.ReadAllText(_stateFile)) as JsonObject;
            string ctrl = st?["controller"]?.GetValue<string>();
            long ago = -1;
            if (st?["lastControlUnix"] != null)
                ago = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - st["lastControlUnix"].GetValue<long>();
            string nm = st?["name"]?.GetValue<string>() ?? Name;
            if (nm != Name) Name = nm;
            return (ctrl, ago, nm);
        }
        catch { return (null, -1, Name); }
    }

    public void Rename(string newName)
    {
        newName = ShellSupport.SanitizeName(newName, null);
        if (newName == null) return;
        Name = newName;
        try
        {
            var st = JsonNode.Parse(File.ReadAllText(_stateFile)) as JsonObject ?? new JsonObject();
            st["name"] = Name;
            WriteJson(_stateFile, st);
            MutateIndex(idx => { if (idx[Id] is JsonObject e) e["name"] = Name; });
        }
        catch { }
    }

    // ----------------------------------------------------------------- pumps
    void OutputPump()
    {
        var buf = new byte[8192];
        while (_running)
        {
            int n;
            try { n = _pty.Output.Read(buf, 0, buf.Length); } catch { break; }
            if (n <= 0) break;
            LastOutputUtc = DateTime.UtcNow;
            var chunk = new byte[n];
            Buffer.BlockCopy(buf, 0, chunk, 0, n);
            Output?.Invoke(this, chunk);
            try
            {
                using var fs = new FileStream(_screenLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(buf, 0, n);
            }
            catch { }
            try { File.AppendAllText(_transcriptLog, VtFilter.StripAnsi(Encoding.UTF8.GetString(buf, 0, n))); }
            catch { }
        }
    }

    void InboxPump()
    {
        Thread.Sleep(600);          // brief grace while the shell boots
        bool bashLike = Shell is "bash" or "bash-wsl" or "sh";
        while (_running)
        {
            try
            {
                foreach (var f in Directory.GetFiles(_inboxDir, "*.cmd").OrderBy(x => x))
                {
                    string raw;
                    try { raw = File.ReadAllText(f, Encoding.UTF8); } catch { continue; }
                    try { File.Delete(f); } catch { }
                    int nl = raw.IndexOf('\n');
                    if (nl < 1) continue;
                    string id = raw[..nl].Trim();
                    string cmd = raw[(nl + 1)..].TrimEnd('\r', '\n');
                    if (cmd == "__CT_EXIT__") { Close("assistant"); return; }

                    // Raw keystrokes: sent verbatim - no line-clear, no auto
                    // Enter. This is what answers interactive prompts (y/n,
                    // menus) and drives full-screen TUI apps.
                    if (cmd.StartsWith("__CT_KEYS__"))
                    {
                        try
                        {
                            var keyBytes = Convert.FromBase64String(cmd["__CT_KEYS__".Length..]);
                            _pty.Input.Write(keyBytes, 0, keyBytes.Length);
                            _pty.Input.Flush();
                        }
                        catch { }
                        try { File.WriteAllText(Path.Combine(_outboxDir, id + ".done"), "0\n0\n(keys sent)"); } catch { }
                        continue;
                    }

                    try
                    {
                        _pty.Input.WriteByte(bashLike ? (byte)0x15 : (byte)0x1B);   // clear half-typed line
                        _pty.Input.Flush();
                        Thread.Sleep(30);
                        var bytes = Encoding.UTF8.GetBytes(cmd + "\r");
                        _pty.Input.Write(bytes, 0, bytes.Length);
                        _pty.Input.Flush();
                    }
                    catch { }

                    try
                    {
                        var st = JsonNode.Parse(File.ReadAllText(_stateFile)) as JsonObject ?? new JsonObject();
                        st["lastControlUnix"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        WriteJson(_stateFile, st);
                    }
                    catch { }
                    try { File.AppendAllText(_assistantLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {cmd}\n"); } catch { }
                    try { File.WriteAllText(Path.Combine(_outboxDir, id + ".done"), "0\n0\n(typed into session)"); } catch { }
                }
            }
            catch { }
            Thread.Sleep(50);       // responsive pickup of assistant commands
        }
    }

    // ----------------------------------------------------------------- utils
    static void WriteJson(string path, JsonObject obj) =>
        File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    void MutateIndex(Action<JsonObject> mutate)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                JsonObject idx = File.Exists(_indexFile)
                    ? JsonNode.Parse(File.ReadAllText(_indexFile)) as JsonObject ?? new JsonObject()
                    : new JsonObject();
                mutate(idx);
                WriteJson(_indexFile, idx);
                return;
            }
            catch { Thread.Sleep(60 * (i + 1)); }
        }
    }
}
