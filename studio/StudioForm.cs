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
        var ic = DisclaimerForm.LoadAppIcon();
        if (ic != null) Icon = ic;
        BackColor = Color.FromArgb(24, 24, 27);
        Width = 1400;
        Height = 860;
        // Restore geometry BEFORE the form is shown. Doing it in Load is too
        // late: WinForms applies StartPosition during Show and overwrites
        // whatever Load set, which is why the saved size/position never stuck.
        StartPosition = RestoreWindowState() ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
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

    // Returns true if saved geometry was applied (caller then uses Manual
    // positioning so WinForms does not re-centre the window on show).
    bool RestoreWindowState()
    {
        try
        {
            if (!File.Exists(UiStateFile)) return false;
            var st = JsonNode.Parse(File.ReadAllText(UiStateFile)) as JsonObject;
            if (st == null) return false;
            var b = new Rectangle(
                st["x"]?.GetValue<int>() ?? Left, st["y"]?.GetValue<int>() ?? Top,
                st["w"]?.GetValue<int>() ?? Width, st["h"]?.GetValue<int>() ?? Height);
            // only restore if it still lands on a connected screen
            if (b.Width < 400 || b.Height < 300) return false;
            if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(b))) return false;
            Bounds = b;
            if (st["max"]?.GetValue<bool>() == true) WindowState = FormWindowState.Maximized;
            return true;
        }
        catch { return false; }
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
        File.WriteAllText(Path.Combine(_root, "studio.lock"), Environment.ProcessId.ToString());
        ReapOrphans();

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
                           m["cwd"]?.GetValue<string>(), m["wslDistro"]?.GetValue<string>(),
                           m["controller"]?.GetValue<string>());
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
            case "historyList":
                ListHistory();
                break;
        }
    }

    // -------------------------------------------------------------- terminals
    void CreateTerm(string shell, string name, string cwd, string wslDistro, string controller = null,
        string accessKey = null, bool trusted = true)
    {
        StudioSession s;
        try { s = StudioSession.Create(_root, shell, name, cwd, wslDistro, controller, accessKey, trusted); }
        catch (Exception ex)
        {
            // Surface failures somewhere the user (and I) can actually read -
            // a WebView console message is invisible in a shipped app.
            try
            {
                File.AppendAllText(Path.Combine(_root, "studio-errors.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  CreateTerm(shell={shell}, name={name}, cwd={cwd}) failed:\n{ex}\n\n");
            }
            catch { }
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
            Post(new
            {
                type = "termAdded", id = s.Id, name = s.Name, shell = s.Shell, shortId = s.Id[..8],
                // shown in the pane header so the user can hand it to an assistant
                tab = s.TabLabel, key = s.AccessKey,
            });
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
            // Requests come from the CLI / an MCP client: untrusted, so the
            // access key decides which tab they may join.
            bool trusted = false;
            try { trusted = req["trusted"]?.GetValue<bool>() ?? false; } catch { }
            CreateTerm(req["shell"]?.GetValue<string>(), Null(req["name"]),
                       Null(req["cwd"]), Null(req["wslDistro"]), Null(req["controller"]),
                       Null(req["accessKey"]), trusted);
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }
        static string Null(JsonNode n) { var v = n?.GetValue<string>(); return string.IsNullOrWhiteSpace(v) ? null : v; }
    }

    // ----------------------------------------------------- history + search
    // Every past session, newest first, grouped by the conversation that owned
    // it. This is what History shows before you type anything: browsing beats
    // having to guess a search term to see what you did yesterday.
    void ListHistory()
    {
        var sessions = new List<object>();
        try
        {
            string root = Path.Combine(_root, "sessions");
            if (Directory.Exists(root))
            {
                JsonObject index = null;
                try
                {
                    string ip = Path.Combine(_root, "index.json");
                    if (File.Exists(ip)) index = JsonNode.Parse(File.ReadAllText(ip)) as JsonObject;
                }
                catch { }

                foreach (var d in new DirectoryInfo(root).GetDirectories()
                             .OrderByDescending(d => d.CreationTimeUtc))
                {
                    string t = Path.Combine(d.FullName, "transcript.log");
                    if (!File.Exists(t)) continue;
                    long size = 0;
                    try { size = new FileInfo(t).Length; } catch { }
                    if (size == 0) continue;                 // nothing to read

                    JsonObject st = null;
                    try
                    {
                        string sp = Path.Combine(d.FullName, "state.json");
                        if (File.Exists(sp)) st = JsonNode.Parse(File.ReadAllText(sp)) as JsonObject;
                    }
                    catch { }
                    var e = index?[d.Name] as JsonObject;
                    string tab = st?["tabLabel"]?.GetValue<string>()
                        ?? st?["controller"]?.GetValue<string>() ?? "Local";
                    // "running" in state.json only means nobody got to write
                    // "closed" - a killed host leaves it behind. Trust the pid.
                    string status = st?["status"]?.GetValue<string>() ?? "closed";
                    if (status == "running" && !OwnerAlive(st)) status = "closed";
                    sessions.Add(new
                    {
                        sid = d.Name,
                        name = st?["name"]?.GetValue<string>() ?? e?["name"]?.GetValue<string>()
                               ?? SessionNameFromState(d.FullName, d.Name),
                        shell = st?["shell"]?.GetValue<string>() ?? e?["shell"]?.GetValue<string>() ?? "",
                        status,
                        created = st?["createdAt"]?.GetValue<string>()
                                  ?? d.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        tab,
                        kb = Math.Max(1, size / 1024),
                    });
                }
            }
        }
        catch (Exception ex) { LogError("ListHistory", ex); }
        Post(new { type = "historySessions", sessions });
    }

    // Full-text search across every session transcript. One unreadable session
    // must not sink the whole search, and nothing here may fail silently: a
    // swallowed exception here looks exactly like "history is broken".
    void RunSearch(string q)
    {
        var results = new List<object>();
        int scanned = 0, skipped = 0;
        string note = null;

        if (string.IsNullOrWhiteSpace(q))
        {
            Post(new { type = "searchResults", q, results, note = (string)null });
            return;
        }

        JsonObject index = null;
        try
        {
            string ip = Path.Combine(_root, "index.json");
            if (File.Exists(ip)) index = JsonNode.Parse(File.ReadAllText(ip)) as JsonObject;
        }
        catch { }                    // names fall back to the guid prefix

        try
        {
            string sessions = Path.Combine(_root, "sessions");
            if (!Directory.Exists(sessions))
            {
                Post(new { type = "searchResults", q, results, note = "no sessions folder yet" });
                return;
            }
            var dirs = new DirectoryInfo(sessions).GetDirectories()
                .OrderByDescending(d => d.CreationTimeUtc);
            foreach (var d in dirs)
            {
                if (results.Count >= 300) { note = "showing the first 300 matches"; break; }
                string t = Path.Combine(d.FullName, "transcript.log");
                if (!File.Exists(t)) continue;
                string sid = d.Name;
                var entry = index?[sid] as JsonObject;
                string name = entry?["name"]?.GetValue<string>() ?? SessionNameFromState(d.FullName, sid);
                string created = entry?["createdAt"]?.GetValue<string>()
                    ?? d.CreationTime.ToString("yyyy-MM-dd HH:mm:ss");
                try
                {
                    scanned++;
                    int ln = 0;
                    // Share-read: the session writing this file right now still
                    // has it open, and a plain ReadLines would throw.
                    using var fs = new FileStream(t, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var r = new StreamReader(fs, Encoding.UTF8);
                    string line;
                    while ((line = r.ReadLine()) != null)
                    {
                        ln++;
                        if (line.Contains(q, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(new { sid, name, created, ln, text = Clean(line) });
                            if (results.Count >= 300) break;
                        }
                    }
                }
                catch { skipped++; }
            }
        }
        catch (Exception ex)
        {
            LogError($"RunSearch(q={q})", ex);
            Post(new { type = "searchResults", q, results, note = "search failed: " + ex.Message });
            return;
        }

        if (note == null && skipped > 0) note = $"{skipped} session(s) could not be read";
        if (note == null && results.Count == 0) note = $"no matches in {scanned} session transcript(s)";
        Post(new { type = "searchResults", q, results, note });
    }

    // Is the process that owns this session still alive?
    static bool OwnerAlive(JsonObject st)
    {
        try
        {
            var pidNode = st?["windowPid"] ?? st?["hostPid"];
            if (pidNode == null) return false;
            int pid = pidNode.GetValue<int>();
            if (pid <= 0) return false;
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    // A Studio that was killed rather than closed leaves its sessions marked
    // "running" forever, which makes them look live in History and in
    // `mcpterm list`. Reap those once at startup.
    void ReapOrphans()
    {
        try
        {
            string sessions = Path.Combine(_root, "sessions");
            if (!Directory.Exists(sessions)) return;
            int fixedUp = 0;
            foreach (var d in Directory.GetDirectories(sessions))
            {
                string sp = Path.Combine(d, "state.json");
                if (!File.Exists(sp)) continue;
                try
                {
                    var st = JsonNode.Parse(File.ReadAllText(sp)) as JsonObject;
                    if (st?["status"]?.GetValue<string>() != "running") continue;
                    if (OwnerAlive(st)) continue;
                    st["status"] = "closed";
                    File.WriteAllText(sp, st.ToJsonString());
                    fixedUp++;
                    string ip = Path.Combine(_root, "index.json");
                    if (File.Exists(ip) &&
                        JsonNode.Parse(File.ReadAllText(ip)) as JsonObject is JsonObject idx &&
                        idx[Path.GetFileName(d)] is JsonObject entry)
                    {
                        entry["status"] = "closed";
                        File.WriteAllText(ip, idx.ToJsonString());
                    }
                }
                catch { }
            }
            if (fixedUp > 0) LogInfo($"reaped {fixedUp} orphaned session(s) left as running");
        }
        catch (Exception ex) { LogError("ReapOrphans", ex); }
    }

    void LogInfo(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(_root, "studio-errors.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}\n");
        }
        catch { }
    }

    static string SessionNameFromState(string dir, string sid)
    {
        try
        {
            string sp = Path.Combine(dir, "state.json");
            if (File.Exists(sp))
            {
                var st = JsonNode.Parse(File.ReadAllText(sp)) as JsonObject;
                string n = st?["name"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(n)) return n;
            }
        }
        catch { }
        return sid.Length >= 8 ? sid[..8] : sid;
    }

    // Transcript lines still carry the odd escape sequence; strip them so a
    // result row shows the command, not ANSI noise.
    static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\x1b')
            {
                int j = i + 1;
                if (j < s.Length && s[j] == '[')
                {
                    j++;
                    while (j < s.Length && !char.IsLetter(s[j])) j++;
                }
                i = j;
                continue;
            }
            if (c == '\r' || c == '\a') continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    void LogError(string what, Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(_root, "studio-errors.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {what} failed:\n{ex}\n\n");
        }
        catch { }
    }

    // Session ids come from the renderer; only ever accept a bare GUID so a
    // crafted value can never escape the sessions folder via Path.Combine.
    static bool IsSafeSessionId(string sid) => Guid.TryParse(sid, out _);

    void OpenHistory(string sid)
    {
        if (!IsSafeSessionId(sid)) { Post(new { type = "historyDoc", sid, text = "(invalid session id)" }); return; }
        try
        {
            // Prefer screen.log: it still has the ANSI, so replaying it through
            // xterm reproduces the session's real colours and layout. The
            // stripped transcript is the fallback and reads as flat, gappy text.
            string dir = Path.Combine(_root, "sessions", sid);
            string t = Path.Combine(dir, "screen.log");
            bool raw = File.Exists(t) && new FileInfo(t).Length > 0;
            if (!raw) t = Path.Combine(dir, "transcript.log");
            if (!File.Exists(t)) { Post(new { type = "historyDoc", sid, text = "(no transcript)" }); return; }

            // FileShare.ReadWrite is essential: a live session appends to this
            // file, and a plain read loses the race with it.
            using var fs = new FileStream(t, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const long cap = 1024 * 1024;
            bool truncated = fs.Length > cap;
            if (truncated) fs.Position = fs.Length - cap;
            var ms = new MemoryStream();
            fs.CopyTo(ms);
            var bytes = ms.ToArray();
            Post(new
            {
                type = "historyDoc", sid, raw, truncated,
                b64 = Convert.ToBase64String(bytes),
            });
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
        // Shown on every launch by design - connected MCP clients get full
        // control of this system - unless the user ticked "don't show again".
        if (!DisclaimerForm.ShowAndConfirm(root)) return;
        Application.Run(new StudioForm(root));
    }
}
