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
    public const string Credits =
        "Designed & built by William Daugherty - william@daugherty.info - https://www.linkedin.com/in/wdaugherty/";

    // Shell-appropriate session names: ps-1, cmd-2, wsl-1... next free number
    // among currently-running sessions of the same kind.
    public static string AutoName(string root, string shell)
    {
        string tag = shell switch
        {
            "pwsh" => "ps", "powershell" => "ps5", "cmd" => "cmd",
            "bash-wsl" => "wsl", "sh" => "sh",
            "bash" => OperatingSystem.IsWindows() ? "git" : "bash",
            _ => "term",
        };
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
                    if (n.StartsWith(tag + "-") && int.TryParse(n[(tag.Length + 1)..], out int v))
                        used.Add(v);
                }
            }
        }
        catch { }
        int i = 1;
        while (used.Contains(i)) i++;
        return $"{tag}-{i}";
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
        // The session path lives under LOCALAPPDATA and contains no spaces.
        "bash-wsl" => $"wsl.exe {(string.IsNullOrEmpty(wslDistro) ? "" : $"-d {wslDistro} ")}" +
                      $"-- bash --rcfile {ToWslPath(initPath)} -i",
        "bash" => $"bash --rcfile '{initPath}' -i",
        "sh" => "sh -i",
        _ => throw new InvalidOperationException($"unknown shell '{shell}'"),
    };

    public static void WriteInitScript(string shell, string initPath, string name, string sessionId,
        string sessionDir, string stateFile)
    {
        string shortId = sessionId[..8];
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
Write-Host 'Paste the code into your assistant to let it type here. ''info'' = details; * in prompt = connected.' -ForegroundColor DarkGray
Write-Host ''
function global:info {{
    Write-Host ''
    Write-Host '  MCPTerminal session code: ' -NoNewline
    Write-Host '{name}' -ForegroundColor Cyan
    Write-Host '  guid   : {sessionId}'
    Write-Host '  shell  : {shell}'
    Write-Host '  logs   : {sessionDir}'
    $j = $null
    try {{ $j = Get-Content -LiteralPath '{stateFile}' -Raw | ConvertFrom-Json }} catch {{ }}
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
                $"echo {e}[90mPaste the code into your assistant to let it type here. 'info' = details.{e}[0m\r\n" +
                "echo.\r\n" +
                $"doskey info=echo. $T echo   MCPTerminal session code: {name} $T echo   guid: {sessionId} $T " +
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
printf ""\033[90mPaste the code into your assistant to let it type here. 'info' = details; * in prompt = connected.\033[0m\n\n""
info() {{
    printf '\n  MCPTerminal session code: \033[1;96m{name}\033[0m\n'
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
