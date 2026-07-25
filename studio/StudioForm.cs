// =============================================================================
// StudioForm - the MCPTerminal Studio main window (WinForms + WebView2).
//
// The UI (conversation tabstrip, Cursor-style vertical terminal list with
// activity indicators, xterm.js panes, history search) lives in ui.html;
// this form hosts it, bridges messages to StudioSession PTYs, watches the
// requests folder so external launches integrate, and owns the lifecycle:
// closing the app terminates its terminals.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MCPTerminal.Studio;

public sealed class StudioForm : Form
{
    readonly string _root;
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly Dictionary<string, StudioSession> _sessions = new();
    readonly System.Windows.Forms.Timer _requestTimer = new() { Interval = 500 };
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };
    bool _uiReady;

    public StudioForm(string root)
    {
        _root = root;
        Text = "MCPTerminal Studio";
        BackColor = Color.FromArgb(24, 24, 27);
        Width = 1400;
        Height = 860;
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_web);
        Load += OnLoad;
        FormClosing += OnClosing;
        // Persist size/position as they change - relying on FormClosing alone
        // loses the layout whenever the process is killed rather than closed.
        ResizeEnd += (_, _) => SaveWindowState();
        Move += (_, _) => _saveDirty = true;
        Resize += (_, _) => _saveDirty = true;
    }

    string UiStateFile => Path.Combine(_root, "studio-ui.json");
    bool _saveDirty;

    void RestoreWindowState()
    {
        try
        {
            if (!File.Exists(UiStateFile)) return;
            var st = JsonNode.Parse(File.ReadAllText(UiStateFile)) as JsonObject;
            if (st == null) return;
            var b = new Rectangle(
                st["x"]?.GetValue<int>() ?? Left, st["y"]?.GetValue<int>() ?? Top,
                st["w"]?.GetValue<int>() ?? Width, st["h"]?.GetValue<int>() ?? Height);
            // only restore if it lands on a visible screen
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(b)) && b.Width >= 400 && b.Height >= 300)
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = b;
            }
            if (st["max"]?.GetValue<bool>() == true) WindowState = FormWindowState.Maximized;
        }
        catch { }
    }

    void SaveWindowState()
    {
        try
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var st = new JsonObject
            {
                ["x"] = b.X, ["y"] = b.Y, ["w"] = b.Width, ["h"] = b.Height,
                ["max"] = WindowState == FormWindowState.Maximized,
            };
            File.WriteAllText(UiStateFile, st.ToJsonString());
        }
        catch { }
    }

    async void OnLoad(object sender, EventArgs e)
    {
        RestoreWindowState();
        File.WriteAllText(Path.Combine(_root, "studio.lock"), Environment.ProcessId.ToString());

        var env = await CoreWebView2Environment.CreateAsync(null,
            Path.Combine(_root, "studio-data"));
        await _web.EnsureCoreWebView2Async(env);
        _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        // the terminal implements its own copy/paste - allow clipboard access
        _web.CoreWebView2.PermissionRequested += (_, pe) =>
        {
            if (pe.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
                pe.State = CoreWebView2PermissionState.Allow;
        };
        _web.CoreWebView2.WebMessageReceived += OnWebMessage;
        // Serve the UI from a virtual host instead of file:// so it has a real
        // origin: CSP 'self' then works and the page can load its vendored
        // assets while remote code stays blocked.
        _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "mcpterminal.studio", AppContext.BaseDirectory,
            CoreWebView2HostResourceAccessKind.Allow);
        _web.CoreWebView2.Navigate("https://mcpterminal.studio/ui.html");

        _requestTimer.Tick += (_, _) => DrainRequests();
        _statusTimer.Tick += (_, _) =>
        {
            PushStatus();
            if (_saveDirty) { _saveDirty = false; SaveWindowState(); }
        };
        // make sure the terminal actually has keyboard focus on startup
        _web.Focus();
        _requestTimer.Start();
        _statusTimer.Start();
    }

    void OnClosing(object sender, FormClosingEventArgs e)
    {
        SaveWindowState();
        // Closing the app terminates its terminals (sessions stay in history).
        foreach (var s in _sessions.Values.ToList()) { try { s.Close(); } catch { } }
        _sessions.Clear();
        try { File.Delete(Path.Combine(_root, "studio.lock")); } catch { }
    }

    // ------------------------------------------------------------- messaging
    void Post(object obj) =>
        _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(obj));

    void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonObject m;
        try { m = JsonNode.Parse(e.WebMessageAsJson) as JsonObject; } catch { return; }
        string type = m?["type"]?.GetValue<string>();
        switch (type)
        {
            case "ready":
                _uiReady = true;
                if (_sessions.Count == 0) CreateTerm("pwsh", null, null, null);
                else foreach (var s in _sessions.Values) AnnounceTerm(s);
                break;
            case "newTerm":
                CreateTerm(m["shell"]?.GetValue<string>(), m["name"]?.GetValue<string>(),
                           m["cwd"]?.GetValue<string>(), m["wslDistro"]?.GetValue<string>());
                break;
            case "input":
                if (_sessions.TryGetValue(m["id"]?.GetValue<string>() ?? "", out var si))
                    si.WriteInput(Convert.FromBase64String(m["dataB64"].GetValue<string>()));
                break;
            case "resize":
                if (_sessions.TryGetValue(m["id"]?.GetValue<string>() ?? "", out var sr))
                    sr.Resize(m["cols"].GetValue<int>(), m["rows"].GetValue<int>());
                break;
            case "closeTerm":
                if (_sessions.TryGetValue(m["id"]?.GetValue<string>() ?? "", out var sc))
                    sc.Close("user");
                break;
            case "renameTerm":
                if (_sessions.TryGetValue(m["id"]?.GetValue<string>() ?? "", out var sn))
                    sn.Rename(m["name"]?.GetValue<string>());
                break;
            case "search":
                RunSearch(m["q"]?.GetValue<string>() ?? "");
                break;
            case "openHistory":
                OpenHistory(m["sid"]?.GetValue<string>() ?? "");
                break;
        }
    }

    // -------------------------------------------------------------- terminals
    void CreateTerm(string shell, string name, string cwd, string wslDistro, string controller = null)
    {
        StudioSession s;
        try { s = StudioSession.Create(_root, shell, name, cwd, wslDistro, controller); }
        catch (Exception ex)
        {
            Post(new { type = "error", text = $"failed to start {shell}: {ex.Message}" });
            return;
        }
        _sessions[s.Id] = s;
        s.Output += (sess, chunk) =>
        {
            try { BeginInvoke(() => Post(new { type = "output", id = sess.Id, b64 = Convert.ToBase64String(chunk) })); }
            catch { }
        };
        s.Exited += sess =>
        {
            // keep a tombstone row in the UI (italic red) so the user can go
            // back to the scrollback and see what happened + who closed it
            try
            {
                BeginInvoke(() =>
                {
                    _sessions.Remove(sess.Id);
                    Post(new { type = "termClosed", id = sess.Id, reason = sess.ClosedReason, sid = sess.Id });
                });
            }
            catch { }
        };
        AnnounceTerm(s);
    }

    void AnnounceTerm(StudioSession s)
    {
        if (_uiReady)
            Post(new { type = "termAdded", id = s.Id, name = s.Name, shell = s.Shell, shortId = s.Id[..8] });
    }

    void PushStatus()
    {
        if (!_uiReady) return;
        foreach (var s in _sessions.Values)
        {
            var (controller, controlAgo, name) = s.ReadLive();
            double activeAgo = s.LastOutputUtc == DateTime.MinValue
                ? -1 : (DateTime.UtcNow - s.LastOutputUtc).TotalSeconds;
            Post(new
            {
                type = "status", id = s.Id,
                controller = controller ?? "", controlAgo, activeAgo, name,
            });
        }
    }

    // ---------------------------------------------------- external launches
    void DrainRequests()
    {
        string reqDir = Path.Combine(_root, "requests");
        if (!Directory.Exists(reqDir)) return;
        foreach (var f in Directory.GetFiles(reqDir, "*.newterm"))
        {
            JsonObject req = null;
            try { req = JsonNode.Parse(File.ReadAllText(f)) as JsonObject; } catch { }
            try { File.Delete(f); } catch { }
            if (req == null) continue;
            CreateTerm(req["shell"]?.GetValue<string>(), Null(req["name"]),
                       Null(req["cwd"]), Null(req["wslDistro"]), Null(req["controller"]));
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }
        static string Null(JsonNode n) { var v = n?.GetValue<string>(); return string.IsNullOrWhiteSpace(v) ? null : v; }
    }

    // ----------------------------------------------------- history + search
    void RunSearch(string q)
    {
        var results = new List<object>();
        if (!string.IsNullOrWhiteSpace(q))
        {
            try
            {
                var index = File.Exists(Path.Combine(_root, "index.json"))
                    ? JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "index.json"))) as JsonObject
                    : new JsonObject();
                foreach (var dir in Directory.GetDirectories(Path.Combine(_root, "sessions"))
                             .OrderByDescending(Directory.GetCreationTime))
                {
                    string sid = Path.GetFileName(dir);
                    string t = Path.Combine(dir, "transcript.log");
                    if (!File.Exists(t)) continue;
                    string name = (index?[sid] as JsonObject)?["name"]?.GetValue<string>() ?? sid[..8];
                    string created = (index?[sid] as JsonObject)?["createdAt"]?.GetValue<string>() ?? "";
                    int ln = 0;
                    foreach (var line in File.ReadLines(t))
                    {
                        ln++;
                        if (line.Contains(q, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(new { sid, name, created, ln, text = line.Trim() });
                            if (results.Count >= 200) break;
                        }
                    }
                    if (results.Count >= 200) break;
                }
            }
            catch { }
        }
        Post(new { type = "searchResults", q, results });
    }

    // Session ids come from the renderer; only ever accept a bare GUID so a
    // crafted value can never escape the sessions folder via Path.Combine.
    static bool IsSafeSessionId(string sid) => Guid.TryParse(sid, out _);

    void OpenHistory(string sid)
    {
        if (!IsSafeSessionId(sid)) { Post(new { type = "historyDoc", sid, text = "(invalid session id)" }); return; }
        try
        {
            string t = Path.Combine(_root, "sessions", sid, "transcript.log");
            if (!File.Exists(t)) { Post(new { type = "historyDoc", sid, text = "(no transcript)" }); return; }
            var fi = new FileInfo(t);
            string text;
            const long cap = 512 * 1024;
            if (fi.Length <= cap) text = File.ReadAllText(t);
            else
            {
                using var fs = fi.OpenRead();
                fs.Position = fi.Length - cap;
                using var r = new StreamReader(fs, Encoding.UTF8);
                text = "...[truncated]...\n" + r.ReadToEnd();
            }
            Post(new { type = "historyDoc", sid, text });
        }
        catch (Exception ex) { Post(new { type = "historyDoc", sid, text = $"(error: {ex.Message})" }); }
    }
}

// =============================================================================
internal static class StudioProgram
{
    [STAThread]
    static void Main()
    {
        string root = Environment.GetEnvironmentVariable("MCPTERMINAL_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCPTerminal");
        Directory.CreateDirectory(root);

        // single instance: if a live Studio already owns the lock, focus it and go
        string lockPath = Path.Combine(root, "studio.lock");
        if (File.Exists(lockPath) && int.TryParse(File.ReadAllText(lockPath).Trim(), out int pid))
        {
            try { Process.GetProcessById(pid); return; } catch { }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new StudioForm(root));
    }
}
