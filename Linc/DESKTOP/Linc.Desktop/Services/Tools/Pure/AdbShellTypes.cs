using System.IO;
using System.Text.Json;

namespace Linc.Desktop.Services.Tools;

/// <summary>What one command gave back. <see cref="Ok"/> is false only when it couldn't run (no
/// phone, timeout, adb gone); a command that ran and printed an error is Ok with that text.</summary>
public sealed record ShellResult(bool Ok, string Text, string Command);

/// <summary>A phone adb can see, whether or not the Linc app runs on it.</summary>
public sealed record ToolsDevice(string Serial, string Model, bool Usb, bool Authorized)
{
    public string Label => Authorized
        ? $"{Model} · {(Usb ? "USB" : "Wi-Fi")}"
        : $"{Model} · Not allowed yet";
}

/// <summary>
/// Every Tools command goes through this (PLAN.md §1.1): one place for timeouts, the per-phone
/// limit on commands at once, logging, and the fake used by the screenshot test.
/// </summary>
public interface IAdbShell
{
    /// <summary>True for the screenshot test's pretend phone (<c>--fake-adb</c>).</summary>
    bool IsFake { get; }

    /// <summary>Phones adb sees right now, authorised or not. Never throws.</summary>
    Task<IReadOnlyList<ToolsDevice>> DevicesAsync(CancellationToken ct);

    /// <summary><c>adb shell &lt;command&gt;</c>. Never throws for phone errors. With
    /// <paramref name="secret"/> the log says only that a command ran (typed text, PINs).</summary>
    Task<ShellResult> RunAsync(string serial, string command, TimeSpan? timeout = null, bool secret = false, CancellationToken ct = default);

    /// <summary><c>adb exec-out &lt;command&gt;</c>, binary-safe (screencap -p). Null when it failed.</summary>
    Task<byte[]?> ExecOutAsync(string serial, string command, TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>Copies one file off the phone. False when it failed; a partial file is removed.</summary>
    Task<bool> PullAsync(string serial, string remote, string local, IProgress<double>? progress, CancellationToken ct);

    /// <summary><c>adb -s &lt;serial&gt; &lt;args&gt;</c> (reboot, install, bugreport).</summary>
    Task<ShellResult> AdbAsync(string serial, string[] args, TimeSpan? timeout = null, CancellationToken ct = default);
}

/// <summary>
/// The pretend phone for tools/uishots (<c>--fake-adb &lt;folder&gt;</c>): every <c>*.json</c> in
/// the folder is an object of <c>"command prefix": "output"</c>; the longest matching prefix
/// answers. A command nothing matches gives empty output. Dependency-free, so tools/toolsim can
/// check it too.
/// </summary>
public sealed class FakeAdbShell : IAdbShell
{
    public const string Serial = "fake-pixel";
    private readonly List<KeyValuePair<string, string>> _answers = [];

    public FakeAdbShell(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    _answers.Add(new(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString()));
                }
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        _answers.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
    }

    public bool IsFake => true;

    /// <summary>Commands run so far, newest last, so a test can see what a button sent.</summary>
    public List<string> History { get; } = [];

    public string Answer(string command) =>
        _answers.FirstOrDefault(a => command.StartsWith(a.Key, StringComparison.Ordinal)).Value ?? "";

    public Task<IReadOnlyList<ToolsDevice>> DevicesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ToolsDevice>>([new ToolsDevice(Serial, "Pixel 7", Usb: true, Authorized: true)]);

    public Task<ShellResult> RunAsync(string serial, string command, TimeSpan? timeout = null, bool secret = false, CancellationToken ct = default)
    {
        History.Add(command);
        return Task.FromResult(new ShellResult(true, Answer(command), command));
    }

    public Task<byte[]?> ExecOutAsync(string serial, string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        History.Add(command);
        return Task.FromResult<byte[]?>(null);
    }

    public Task<bool> PullAsync(string serial, string remote, string local, IProgress<double>? progress, CancellationToken ct)
    {
        History.Add("pull " + remote);
        progress?.Report(100);
        return Task.FromResult(false);
    }

    public Task<ShellResult> AdbAsync(string serial, string[] args, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var command = string.Join(' ', args);
        History.Add("adb " + command);
        return Task.FromResult(new ShellResult(true, Answer("adb " + command), "adb " + command));
    }
}
