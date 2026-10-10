using System.IO;
using System.IO.Pipes;
using Microsoft.Win32;

namespace Linc.Desktop.QuickShare;

/// <summary>
/// "Send with Quick Share" in Explorer's right-click menu, and the hand-off that makes it work
/// with a single-instance app: Explorer starts a second Linc with <c>--quickshare &lt;file&gt;</c>,
/// that copy passes the path to the running one over a named pipe and exits at once.
/// <para>
/// The menu is a per-user HKCU verb (no admin, nothing machine-wide), only written when the
/// user turns it on from the Share page, and removed when they turn it off.
/// </para>
/// </summary>
public static class QsShellIntegration
{
    public const string Argument = "--quickshare";

    private const string VerbKey = @"Software\Classes\*\shell\LincQuickShare";

    /// <summary>Per user and per session, so two people signed in never cross wires.</summary>
    private static string PipeName => $"LincQuickShare-{Environment.UserName}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(VerbKey);
        return key is not null;
    }

    public static void Register(string exePath)
    {
        using var verb = Registry.CurrentUser.CreateSubKey(VerbKey);
        verb.SetValue("", "Send with Quick Share");
        verb.SetValue("Icon", $"\"{exePath}\",0");
        // Player: one process per selection batch rather than a refusal past 15 files.
        verb.SetValue("MultiSelectModel", "Player");
        using var command = verb.CreateSubKey("command");
        command.SetValue("", $"\"{exePath}\" {Argument} \"%1\"");
    }

    public static void Unregister() => Registry.CurrentUser.DeleteSubKeyTree(VerbKey, throwOnMissingSubKey: false);

    /// <summary>The file paths passed after <see cref="Argument"/>, or empty.</summary>
    public static List<string> PathsFrom(string[] args)
    {
        var paths = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], Argument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            for (var j = i + 1; j < args.Length && !args[j].StartsWith("--", StringComparison.Ordinal); j++)
            {
                paths.Add(args[j]);
            }
        }
        return paths;
    }

    /// <summary>Second instance: hand the paths to the running Linc. False when nobody is listening.</summary>
    public static bool TryForward(IReadOnlyList<string> paths)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(3000);
            using var writer = new StreamWriter(pipe);
            foreach (var path in paths)
            {
                writer.WriteLine(path);
            }
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>First instance: accept hand-offs for the life of the process.</summary>
    public static void Listen(Action<IReadOnlyList<string>> onPaths, Action<string> log, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(pipe);
                    var paths = new List<string>();
                    while (await reader.ReadLineAsync(ct) is { } line)
                    {
                        if (line.Length > 0)
                        {
                            paths.Add(line);
                        }
                    }
                    if (paths.Count > 0)
                    {
                        onPaths(paths);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException ex)
                {
                    log($"Quick Share hand-off from Explorer failed: {ex.Message}");
                }
            }
        }, ct);
    }
}
