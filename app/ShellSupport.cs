// =============================================================================
// MCPTerminal ShellSupport - shared shell definitions used by the standalone
// terminal and MCPTerminal Studio: per-shell init scripts (header, `info`,
// connected-star prompt) and the command line that launches each shell.
// =============================================================================
using System;
using System.IO;
using System.Linq;

namespace MCPTerminal;

public static class ShellSupport
{
    public const string Credits = "Designed & built by William Daugherty";

    // Short, silly, memorable session names: smug-owl-1, zippy-newt-2. With
    // ~1500 word pairs a clash is unlikely, and the trailing number makes it
    // impossible among sessions that are running at the same time. The shell is
    // shown as its own chip in the UI, so the name doesn't need to encode it.
    static readonly string[] Adjectives =
    {
        "wry", "smug", "spry", "brisk", "zesty", "dizzy", "jolly", "sassy", "moody",
        "nifty", "perky", "quirky", "snappy", "wonky", "zippy", "bouncy", "chunky",
        "fuzzy", "grumpy", "cranky", "plucky", "spicy", "salty", "giddy", "hasty",
        "husky", "lofty", "mushy", "nutty", "peppy", "punchy", "silly", "snazzy",
        "sneaky", "soggy", "sturdy", "swanky", "tipsy", "wacky", "weepy", "witty",
        "woozy", "yappy", "zany", "cheeky", "chirpy", "clumsy", "drowsy", "feisty",
    };
    static readonly string[] Critters =
    {
        "yak", "owl", "newt", "mole", "crab", "toad", "wasp", "moth", "lynx", "mule",
        "ibex", "kiwi", "wren", "vole", "slug", "clam", "hare", "mink", "seal", "swan",
        "gnu", "emu", "ram", "bat", "cod", "elk", "fox", "hen", "jay", "koi", "pug",
        "eel", "carp", "dodo", "finch", "gecko", "heron", "llama", "otter", "quail",
    };

    public static string AutoName(string root, string shell)
    {
        var rng = Random.Shared;
        string combo = $"{Adjectives[rng.Next(Adjectives.Length)]}-{Critters[rng.Next(Critters.Length)]}";

        // Numbers already taken by live sessions sharing this combo.
        var used = new System.Collections.Generic.HashSet<int>();
        try
        {
            string ix = Path.Combine(root, "index.json");
            if (File.Exists(ix))
            {
                var idx = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ix)) as System.Text.Json.Nodes.JsonObject;
                foreach (var kv in idx)
                {
                    var e = kv.Value as System.Text.Json.Nodes.JsonObject;
                    if (e?["status"]?.GetValue<string>() != "running") continue;
                    string n = e?["name"]?.GetValue<string>() ?? "";
                    if (n.StartsWith(combo + "-", StringComparison.Ordinal) &&
                        int.TryParse(n[(combo.Length + 1)..], out int v))
                        used.Add(v);
                }
            }
        }
        catch { }
        int i = 1;
        while (used.Contains(i)) i++;
        return $"{combo}-{i}";
    }

    // Session names and distro names are interpolated into generated shell
    // scripts, command lines and terminal titles - constrain them to a safe
    // character set so nothing can break out of a quote or inject arguments.
    public static string SanitizeName(string name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var clean = new string(name.Where(c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.').ToArray());
        if (clean.Length > 48) clean = clean[..48];
        return clean.Length == 0 ? fallback : clean;
    }

    public static string InitFileName(string shell) => shell switch
    {
        "pwsh" or "powershell" => "init.ps1",
        "cmd" => "init.cmd",
        "sh" => "init.sh",
        _ => "init.bash",
    };

    public static string FindGitBash()
    {
        string[] candidates =
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
            @"C:\Program Files\Git\usr\bin\bash.exe",
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("Git Bash not found - install Git for Windows or use --shell bash-wsl");
    }

    // Convert C:\foo\bar -> /mnt/c/foo/bar for use inside WSL.
    public static string ToWslPath(string winPath)
    {
        var full = Path.GetFullPath(winPath);
        return "/mnt/" + char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');
    }

    public static string BuildShellCommand(string shell, string initPath, string wslDistro, bool isWindows) => shell switch
    {
        "pwsh" => $"pwsh.exe -NoLogo -NoExit -Command \". '{initPath}'\"",
        "powershell" => $"powershell.exe -NoLogo -NoExit -Command \". '{initPath}'\"",
        "cmd" => $"cmd.exe /Q /K \"{initPath}\"",
        "bash" when isWindows => $"\"{FindGitBash()}\" --rcfile \"{initPath.Replace('\\', '/')}\" -i",
        // Pass bash's arguments straight through with `--` instead of wrapping
        // them in `bash -c "..."`: the nested quoting was being mangled before
        // it reached bash, so --rcfile was dropped and the session silently
        // started without our init (no info command, no connected-star prompt).
        // The rcfile lives at a WSL-native /tmp path (see PushInitToWsl): the
        // 9p /mnt/c mount doesn't reliably see a directory created on the
        // Windows side milliseconds earlier, so referencing the session dir
        // via /mnt/c made bash silently skip the init.
        "bash-wsl" => $"wsl.exe {(string.IsNullOrEmpty(wslDistro) ? "" : $"-d {wslDistro} ")}" +
                      $"-- bash --rcfile {WslInitTarget(initPath)} -i",
        "bash" => $"bash --rcfile '{initPath}' -i",
        "sh" => "sh -i",
        _ => throw new InvalidOperationException($"unknown shell '{shell}'"),
    };

    // WSL-native location for a bash-wsl session's init script. /tmp is inside
    // the distro (tmpfs), so it is visible immediately - unlike the session dir
    // under /mnt/c. Derived from the session guid in initPath's directory name.
    public static string WslInitTarget(string initPath)
    {
        string guid = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(initPath))) ?? "session";
        return "/tmp/mcpterminal-" + SanitizeName(guid, "session") + ".bash";
    }

    // Copy the just-written init script into the distro via wsl.exe stdin so
    // bash can load it from a native path. Call after WriteInitScript for
    // shell == "bash-wsl".
    public static void PushInitToWsl(string initPath, string wslDistro)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("wsl.exe")
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
        };
        if (!string.IsNullOrEmpty(wslDistro)) { psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(wslDistro); }
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        string target = WslInitTarget(initPath);
        psi.ArgumentList.Add($"umask 077; cat > {target}");
        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start wsl.exe");
        p.StandardInput.Write(File.ReadAllText(initPath).Replace("\r\n", "\n"));
        p.StandardInput.Close();
        if (!p.WaitForExit(15000))
        {
            try { p.Kill(); } catch { }
            throw new InvalidOperationException("timed out writing WSL init script");
        }
    }

    public static void WriteInitScript(string shell, string initPath, string name, string sessionId,
        string sessionDir, string stateFile, string accessKey = "")
    {
        string shortId = sessionId[..8];
        // The key is shown here on purpose: this window belongs to the person at
        // the keyboard, and handing the key to an assistant is how they grant it
        // access. Nothing can drive this terminal without it.
        //
        // A Local terminal has no key: it is unclaimed. An assistant can take it
        // over, but doing so MOVES it out of Local into that assistant's own tab
        // and issues it that tab's key - so the banner says which it is, and
        // `info` re-reads the key from state.json so a takeover shows up there.
        accessKey = SanitizeName(accessKey, "");
        bool local = accessKey.Length == 0;
        string keyLinePs = local
            ? "Write-Host 'local terminal - yours until an assistant claims it (it then moves to that chat''s tab).' -ForegroundColor DarkGray"
            : "Write-Host 'access key ' -ForegroundColor DarkGray -NoNewline\n" +
              $"Write-Host '{accessKey}' -ForegroundColor Yellow -NoNewline\n" +
              "Write-Host ' - give the code AND key to an assistant to let it type here.' -ForegroundColor DarkGray";
        bool isPs = shell is "pwsh" or "powershell";
        if (isPs)
        {
            File.WriteAllText(initPath, $@"
# Always emit ANSI colour. PowerShell drops to PlainText whenever it thinks
# stdout is not a terminal, which is what strips colour inside a pseudoconsole.
try {{ $PSStyle.OutputRendering = 'Ansi' }} catch {{ }}
Write-Host 'MCPTerminal ' -ForegroundColor Cyan -NoNewline
Write-Host 'shared terminal - session code ' -NoNewline
Write-Host '{name}' -ForegroundColor Cyan -NoNewline
Write-Host ' ({shortId})'
{keyLinePs}
Write-Host '''info'' = details; * in prompt = connected.' -ForegroundColor DarkGray
Write-Host ''
function global:info {{
    Write-Host ''
    Write-Host '  MCPTerminal session code: ' -NoNewline
    Write-Host '{name}' -ForegroundColor Cyan
    $j = $null
    try {{ $j = Get-Content -LiteralPath '{stateFile}' -Raw | ConvertFrom-Json }} catch {{ }}
    if ($j -and $j.accessKey) {{
        Write-Host '  access key: ' -NoNewline
        Write-Host $j.accessKey -ForegroundColor Yellow
        Write-Host '  tab    : ' -NoNewline
        Write-Host ($(if ($j.tabLabel) {{ $j.tabLabel }} else {{ '(none)' }})) -ForegroundColor Cyan
    }} else {{
        Write-Host '  access : ' -NoNewline
        Write-Host 'LOCAL - unclaimed; no key exists yet' -ForegroundColor Green
    }}
    Write-Host '  guid   : {sessionId}'
    Write-Host '  shell  : {shell}'
    Write-Host '  logs   : {sessionDir}'
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    if ($j -and $j.lastControlUnix) {{
        $ago = $now - $j.lastControlUnix
        if ($ago -lt 120) {{
            Write-Host '  status : ' -NoNewline
            Write-Host ('CONNECTED - controlled by the assistant (last command ' + $ago + 's ago)') -ForegroundColor Cyan
        }} else {{
            Write-Host '  status : ' -NoNewline
            Write-Host ('idle - assistant last active ' + [int]($ago / 60) + 'm ago') -ForegroundColor Green
        }}
        if ($j.controller) {{ Write-Host ('  chat   : ' + $j.controller) }}
    }} else {{
        Write-Host '  status : ' -NoNewline
        Write-Host 'DISCONNECTED - paste the code into your assistant to connect it' -ForegroundColor DarkGray
    }}
    Write-Host '  {Credits}' -ForegroundColor DarkGray
    Write-Host ''
}}
function global:prompt {{
    $ct = ''
    try {{
        $j = Get-Content -LiteralPath '{stateFile}' -Raw | ConvertFrom-Json
        if ($j.lastControlUnix -and (([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) - $j.lastControlUnix) -lt 120) {{
            $e = [char]27
            $ct = $e + '[1;96m*' + $e + '[0m '
        }}
    }} catch {{ }}
    $ct + 'PS ' + $executionContext.SessionState.Path.CurrentLocation + ('>' * ($nestedPromptLevel + 1)) + ' '
}}
");
        }
        else if (shell == "cmd")
        {
            string e = "\x1b";
            File.WriteAllText(initPath,
                "@echo off\r\n" +
                $"echo {e}[1;96mMCPTerminal{e}[0m shared terminal - session code {e}[1;96m{name}{e}[0m ({shortId})\r\n" +
                (local
                    ? $"echo {e}[90mprivate terminal - assistants cannot read or use this one.{e}[0m\r\n"
                    : $"echo {e}[90maccess key {e}[93m{accessKey}{e}[90m - give the code AND key to an assistant.{e}[0m\r\n") +
                $"echo {e}[90m'info' = details.{e}[0m\r\n" +
                "echo.\r\n" +
                $"doskey info=echo. $T echo   MCPTerminal session code: {name} $T " +
                (local
                    ? "echo   access: PRIVATE - no key exists; assistants cannot use this terminal $T "
                    : $"echo   access key: {accessKey} $T ") +
                $"echo   guid: {sessionId} $T " +
                $"echo   shell: cmd $T echo   logs: {sessionDir} $T echo   status: see window title $T " +
                $"echo   {Credits} $T echo.\r\n" +
                "prompt $P$G\r\n");
        }
        else
        {
            string stateForShell = shell == "bash-wsl" ? ToWslPath(stateFile) : stateFile.Replace('\\', '/');
            string dirForShell = shell == "bash-wsl" ? ToWslPath(sessionDir) : sessionDir.Replace('\\', '/');
            File.WriteAllText(initPath, $@"
[ -f ~/.bashrc ] && . ~/.bashrc 2>/dev/null
printf '\033[1;96mMCPTerminal\033[0m shared terminal - session code \033[1;96m{name}\033[0m ({shortId})\n'
{(local
    ? @"printf ""\033[90mlocal terminal - yours until an assistant claims it (it then moves to that chat's tab).\033[0m\n"""
    : $@"printf ""\033[90maccess key \033[93m{accessKey}\033[90m - give the code AND key to an assistant to let it type here.\033[0m\n""")}
printf ""\033[90m'info' = details; * in prompt = connected.\033[0m\n\n""
info() {{
    printf '\n  MCPTerminal session code: \033[1;96m{name}\033[0m\n'
    _key=$(grep -o '""accessKey"": *""[^""]*""' '{stateForShell}' 2>/dev/null | sed 's/.*: *""//;s/""$//')
    if [ -n ""$_key"" ]; then
        printf '  access key: \033[93m%s\033[0m\n' ""$_key""
    else
        printf '  access : \033[32mLOCAL - unclaimed; no key exists yet\033[0m\n'
    fi
    printf '  guid   : {sessionId}\n'
    printf '  shell  : {shell}\n'
    printf '  logs   : {dirForShell}\n'
    _now=$(date +%s)
    _lc=$(grep -o '""lastControlUnix"": *[0-9]*' '{stateForShell}' 2>/dev/null | grep -o '[0-9]*$')
    if [ -n ""$_lc"" ]; then
        _ago=$((_now - _lc))
        if [ ""$_ago"" -lt 120 ]; then
            printf '  status : \033[1;96mCONNECTED - controlled by the assistant (last command %ss ago)\033[0m\n' ""$_ago""
        else
            printf '  status : \033[32midle - assistant last active %sm ago\033[0m\n' ""$((_ago / 60))""
        fi
        _ctrl=$(grep -o '""controller"": *""[^""]*""' '{stateForShell}' 2>/dev/null | sed 's/.*: *""//;s/""$//')
        [ -n ""$_ctrl"" ] && printf '  chat   : %s\n' ""$_ctrl""
    else
        printf '  status : \033[90mDISCONNECTED - paste the code into your assistant to connect it\033[0m\n'
    fi
    printf '  \033[90m{Credits}\033[0m\n\n'
}}
_mt_star() {{
    _lc=$(grep -o '""lastControlUnix"": *[0-9]*' '{stateForShell}' 2>/dev/null | grep -o '[0-9]*$')
    if [ -n ""$_lc"" ] && [ $(( $(date +%s) - _lc )) -lt 120 ]; then printf '\033[1;96m*\033[0m '; fi
}}
PS1='$(_mt_star)\u@\h:\w\$ '
export PS1
");
        }
    }
}
