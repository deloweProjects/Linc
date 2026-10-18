using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace Linc.Desktop.Services;

/// <summary>
/// A terminal on the PC for the phone (v27, D-097): PowerShell, Command Prompt or WSL running in
/// a real Windows pseudo-console (ConPTY), so colours, prompts, progress bars and full-screen
/// programs behave exactly as in Windows Terminal. What the console draws goes to the phone as
/// VT text; what the phone types comes back as keystrokes.
///
/// <para>Channel 9, both ways: <c>[1 byte type][4 bytes big-endian length][payload]</c>. Type 1
/// is text (UTF-8), 2 is a resize (cols, rows as two big-endian 16-bit numbers), and 3, PC → phone
/// only, says the shell exited (its exit code, 32-bit).</para>
///
/// <para>It runs as the signed-in user, unelevated — the same as opening Terminal yourself.</para>
/// </summary>
public sealed class PcTerminal : IDisposable
{
    private const byte TypeText = 1, TypeResize = 2, TypeExit = 3;

    private readonly Stream _channel;
    private readonly ILogService _log;
    private readonly object _write = new();
    private IntPtr _console;
    private readonly SafeFileHandle _inputWrite;
    private readonly SafeFileHandle _outputRead;
    private readonly IntPtr _process;
    private readonly FileStream _toShell;
    private bool _disposed;

    public string Shell { get; }

    /// <summary>The shells this PC has: always PowerShell and Command Prompt; WSL when installed.</summary>
    public static JsonObject State(PcTerminal? running)
    {
        var shells = new JsonArray { "powershell", "cmd" };
        if (File.Exists(Path.Combine(Environment.SystemDirectory, "wsl.exe"))) shells.Add("wsl");
        return new JsonObject { ["shells"] = shells, ["running"] = running?.Shell };
    }

    private static string CommandLine(string shell) => shell switch
    {
        "cmd" => "cmd.exe",
        "wsl" => "wsl.exe",
        // PowerShell 7 if it is installed (it is what Windows Terminal opens), else Windows PowerShell.
        _ => FindOnPath("pwsh.exe") is { } pwsh ? $"\"{pwsh}\" -NoLogo" : "powershell.exe -NoLogo",
    };

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(dir => Path.Combine(dir.Trim(), exe)).FirstOrDefault(File.Exists);

    public static PcTerminal Start(string shell, int cols, int rows, Stream channel, ILogService log) =>
        new(shell is "cmd" or "wsl" ? shell : "powershell", cols, rows, channel, log);

    private PcTerminal(string shell, int cols, int rows, Stream channel, ILogService log)
    {
        Shell = shell;
        _channel = channel;
        _log = log;
        if (!CreatePipe(out var inputRead, out _inputWrite, IntPtr.Zero, 0) ||
            !CreatePipe(out _outputRead, out var outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception();
        int hr = CreatePseudoConsole(new COORD { X = (short)cols, Y = (short)rows }, inputRead, outputWrite, 0, out _console);
        // The console holds its own references now.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0) throw new LincException("This version of Windows has no pseudo-console (Windows 10 1809 or later is needed).");

        var startup = new STARTUPINFOEX();
        startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        startup.lpAttributeList = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(startup.lpAttributeList, 1, 0, ref size) ||
                !UpdateProcThreadAttribute(startup.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!CreateProcess(null, CommandLine(shell), IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero, home, ref startup, out var info))
                throw new LincException($"{shell} couldn't start on the PC ({new Win32Exception().Message}).");
            _process = info.hProcess;
            CloseHandle(info.hThread);
        }
        finally
        {
            DeleteProcThreadAttributeList(startup.lpAttributeList);
            Marshal.FreeHGlobal(startup.lpAttributeList);
        }
        _toShell = new FileStream(_inputWrite, FileAccess.Write, 1);
        new Thread(PumpOutput) { IsBackground = true, Name = "linc-term-out" }.Start();
        new Thread(PumpInput) { IsBackground = true, Name = "linc-term-in" }.Start();
        new Thread(WaitForExit) { IsBackground = true, Name = "linc-term-wait" }.Start();
        log.Log(LogLevel.Info, $"PC tools: a {shell} terminal opened from the phone ({cols}×{rows})");
    }

    /// <summary>The console's VT output → the phone.</summary>
    private void PumpOutput()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            using var fromShell = new FileStream(_outputRead, FileAccess.Read, 1);
            while (true)
            {
                int n = fromShell.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                Send(TypeText, buffer.AsSpan(0, n));
            }
        }
        catch (Exception) when (_disposed) { }
        catch (Exception ex) { _log.Log(LogLevel.Info, $"PC tools: terminal output ended ({ex.Message})"); }
    }

    /// <summary>The phone's keystrokes and resizes → the console.</summary>
    private void PumpInput()
    {
        var header = new byte[5];
        try
        {
            while (true)
            {
                ReadExactly(header);
                int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
                if (length is < 0 or > 1 << 20) break;
                var payload = new byte[length];
                ReadExactly(payload);
                if (header[0] == TypeText) { _toShell.Write(payload); _toShell.Flush(); }
                else if (header[0] == TypeResize && payload.Length >= 4)
                {
                    short cols = System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(payload);
                    short rows = System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(2));
                    if (cols > 0 && rows > 0) ResizePseudoConsole(_console, new COORD { X = cols, Y = rows });
                }
            }
        }
        catch (Exception) { /* the phone closed the terminal */ }
        Dispose();
    }

    private void WaitForExit()
    {
        WaitForSingleObject(_process, uint.MaxValue);
        GetExitCodeProcess(_process, out uint code);
        var payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(payload, (int)code);
        try { Send(TypeExit, payload); } catch { }
        _log.Log(LogLevel.Info, $"PC tools: the phone's {Shell} terminal closed (exit code {code})");
        Dispose();
    }

    private void Send(byte type, ReadOnlySpan<byte> payload)
    {
        var header = new byte[5];
        header[0] = type;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);
        lock (_write)
        {
            _channel.Write(header);
            _channel.Write(payload);
            _channel.Flush();
        }
    }

    private void ReadExactly(byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = _channel.Read(buffer, read, buffer.Length - read);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    public void Dispose()
    {
        lock (_write)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { TerminateProcess(_process, 0); } catch { }
        if (_console != IntPtr.Zero) { ClosePseudoConsole(_console); _console = IntPtr.Zero; }
        try { _toShell.Dispose(); } catch { }
        try { _channel.Dispose(); } catch { }
        CloseHandle(_process);
    }

    // ---- interop ------------------------------------------------------------------------

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = new(0x00020016);

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr console, COORD size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? directory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION info);

    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
