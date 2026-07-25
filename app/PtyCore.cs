// =============================================================================
// MCPTerminal PtyCore - shared PTY plumbing used by both the standalone
// terminal app and MCPTerminal Studio.
//   * WindowsPty : ConPTY host (the Windows Terminal mechanism)
//   * UnixPty    : PTY via script(1) on Linux/macOS
//   * VtFilter   : passes VT through, suppressing only the shell's title-sets
//   * TerminalSetup : console mode setup for a standalone console window
//   * StudioBridge  : lets a standalone launch hand itself to a running Studio
// =============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MCPTerminal;

public interface IPtySession : IDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    void Resize(int cols, int rows);
    void WaitForExit();
}

// ----------------------------------------------------------------- Windows
public sealed class WindowsPty : IPtySession
{
    public Stream Input { get; private set; }
    public Stream Output { get; private set; }
    IntPtr _hPC, _hProcess;

    public void Resize(int cols, int rows) =>
        ResizePseudoConsole(_hPC, new COORD { X = (short)cols, Y = (short)rows });
    public void WaitForExit() => WaitForSingleObject(_hProcess, INFINITE);
    public bool HasExited => WaitForSingleObject(_hProcess, 0) == 0;
    public void Dispose() { if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; } }
    public void Kill() { try { TerminateProcess(_hProcess, 0); } catch { } }

    public static WindowsPty Spawn(string cmdline, string cwd, int w = 120, int h = 30)
    {
        CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0);
        CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0);
        int hr = CreatePseudoConsole(new COORD { X = (short)w, Y = (short)h }, inRead, outWrite, 0, out var hPC);
        if (hr != 0) throw new InvalidOperationException($"CreatePseudoConsole: 0x{hr:x}");
        CloseHandle(inRead);
        CloseHandle(outWrite);

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attrs = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size))
            throw new InvalidOperationException("InitializeProcThreadAttributeList failed");
        if (!UpdateProcThreadAttribute(attrs, 0, (IntPtr)0x00020016, hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("UpdateProcThreadAttribute failed");

        var siex = new STARTUPINFOEX();
        siex.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        siex.lpAttributeList = attrs;
        if (!CreateProcessW(null, cmdline, IntPtr.Zero, IntPtr.Zero, false,
                0x00080000, IntPtr.Zero, cwd, ref siex, out var pi))
            throw new InvalidOperationException($"CreateProcess: {Marshal.GetLastWin32Error()}");

        return new WindowsPty
        {
            _hPC = hPC,
            _hProcess = pi.hProcess,
            Input = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(inWrite, true), FileAccess.Write),
            Output = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(outRead, true), FileAccess.Read),
        };
    }

    [StructLayout(LayoutKind.Sequential)] struct COORD { public short X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOW
    {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOEX { public STARTUPINFOW StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    const uint INFINITE = 0xFFFFFFFF;
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CreatePipe(out IntPtr r, out IntPtr w, IntPtr a, int s);
    [DllImport("kernel32.dll", SetLastError = true)] static extern int CreatePseudoConsole(COORD size, IntPtr hIn, IntPtr hOut, uint flags, out IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)] static extern int ResizePseudoConsole(IntPtr hPC, COORD size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern void ClosePseudoConsole(IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(IntPtr l, int c, int f, ref IntPtr s);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(IntPtr l, uint f, IntPtr a, IntPtr v, IntPtr cb, IntPtr p, IntPtr r);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFOEX siex, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr h, uint code);
}

// ------------------------------------------------------------------- Unix
public sealed class UnixPty : IPtySession
{
    Process _proc;
    public Stream Input => _proc.StandardInput.BaseStream;
    public Stream Output => _proc.StandardOutput.BaseStream;
    public void Resize(int cols, int rows) { /* fixed size in this version */ }
    public void WaitForExit() => _proc.WaitForExit();
    public void Dispose() { try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { } }

    public static UnixPty Spawn(string shellCmd, string cwd, string shInitPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "script",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
        };
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(shellCmd);
        psi.ArgumentList.Add("/dev/null");
        psi.Environment["TERM"] = Environment.GetEnvironmentVariable("TERM") ?? "xterm-256color";
        if (shInitPath != null) psi.Environment["ENV"] = shInitPath;
        var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start script(1)");
        return new UnixPty { _proc = p };
    }
}

// =============================================================================
public static class VtFilter
{
    public static byte[] Process(byte[] buf, int n, ref byte[] carry)
    {
        var data = new byte[carry.Length + n];
        Buffer.BlockCopy(carry, 0, data, 0, carry.Length);
        Buffer.BlockCopy(buf, 0, data, carry.Length, n);
        carry = Array.Empty<byte>();

        var outBuf = new MemoryStream(data.Length + 16);
        int i = 0;
        while (i < data.Length)
        {
            byte b = data[i];
            if (b != 0x1B) { outBuf.WriteByte(b); i++; continue; }
            if (i + 1 >= data.Length) { carry = data[i..]; break; }
            byte kind = data[i + 1];
            if (kind == (byte)'[')
            {
                int j = i + 2;
                while (j < data.Length && !(data[j] >= 0x40 && data[j] <= 0x7E)) j++;
                if (j >= data.Length) { carry = data[i..]; break; }
                outBuf.Write(data, i, j + 1 - i);
                i = j + 1;
            }
            else if (kind == (byte)']' || kind == (byte)'P' || kind == (byte)'X' ||
                     kind == (byte)'^' || kind == (byte)'_')
            {
                int j = i + 2, end = -1;
                while (j < data.Length)
                {
                    if (data[j] == 0x07) { end = j + 1; break; }
                    if (data[j] == 0x1B && j + 1 < data.Length && data[j + 1] == (byte)'\\') { end = j + 2; break; }
                    j++;
                }
                if (end < 0)
                {
                    if (data.Length - i > 8192) { outBuf.Write(data, i, data.Length - i); i = data.Length; }
                    else { carry = data[i..]; }
                    break;
                }
                bool isTitleSet = kind == (byte)']' && end > i + 3 &&
                    (data[i + 2] == (byte)'0' || data[i + 2] == (byte)'1' || data[i + 2] == (byte)'2') &&
                    data[i + 3] == (byte)';';
                if (!isTitleSet) outBuf.Write(data, i, end - i);
                i = end;
            }
            else
            {
                outBuf.WriteByte(data[i]); outBuf.WriteByte(kind); i += 2;
            }
        }
        return outBuf.ToArray();
    }

    static readonly Regex AnsiRx = new(
        "\x1b\\[[0-9;?]*[A-Za-z]|\x1b\\][^\x07\x1b]*(\x07|\x1b\\\\)|\x1b[=>()][0-9A-Za-z]?|[\x00-\x08\x0b\x0c\x0e-\x1a\x1c-\x1f]",
        RegexOptions.Compiled);
    public static string StripAnsi(string text) => AnsiRx.Replace(text, "");
}

// =============================================================================
public static class TerminalSetup
{
    public static void Enter()
    {
        if (OperatingSystem.IsWindows())
        {
            SetConsoleOutputCP(65001);
            SetConsoleCP(65001);
            var hOut = GetStdHandle(-11);
            GetConsoleMode(hOut, out uint om);
            SetConsoleMode(hOut, om | 0x0004 | 0x0008);
            var hIn = GetStdHandle(-10);
            GetConsoleMode(hIn, out uint im);
            im &= ~(uint)(0x0001 | 0x0002 | 0x0004);
            SetConsoleMode(hIn, im | 0x0200 | 0x0080);
        }
        else if (!Console.IsInputRedirected)
        {
            Run("stty", "raw -echo");
        }
    }

    public static void Exit()
    {
        if (!OperatingSystem.IsWindows() && !Console.IsInputRedirected)
            Run("stty", "sane");
    }

    static void Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false });
            p?.WaitForExit(2000);
        }
        catch { }
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetConsoleMode(IntPtr h, out uint m);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleMode(IntPtr h, uint m);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleOutputCP(uint cp);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleCP(uint cp);
}

// =============================================================================
//  StudioBridge - integration between standalone launches and a running
//  MCPTerminal Studio. Studio maintains <root>/studio.lock (its pid) and
//  watches <root>/requests/*.newterm; a standalone launch that finds a live
//  Studio hands its arguments over and exits, so the terminal opens INSIDE
//  the app. Studio is never required: no lock, no redirect.
// =============================================================================
public static class StudioBridge
{
    public static bool TryRedirect(string root, string shell, string name, string cwd, string wslDistro,
        string controller = null)
    {
        try
        {
            string lockPath = Path.Combine(root, "studio.lock");
            if (!File.Exists(lockPath)) return false;
            if (!int.TryParse(File.ReadAllText(lockPath).Trim(), out int pid)) return false;
            try { Process.GetProcessById(pid); } catch { return false; }

            var req = new JsonObject
            {
                ["shell"] = shell, ["name"] = name ?? "", ["cwd"] = cwd ?? "",
                ["wslDistro"] = wslDistro ?? "", ["controller"] = controller ?? "",
            };
            string reqDir = Path.Combine(root, "requests");
            Directory.CreateDirectory(reqDir);
            string tmp = Path.Combine(reqDir, Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(tmp, req.ToJsonString());
            File.Move(tmp, Path.ChangeExtension(tmp, ".newterm"));
            return true;
        }
        catch { return false; }
    }
}
