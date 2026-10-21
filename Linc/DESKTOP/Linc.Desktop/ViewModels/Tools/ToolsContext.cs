using CommunityToolkit.Mvvm.ComponentModel;
using Linc.Desktop.Services.Tools;

namespace Linc.Desktop.ViewModels.Tools;

/// <summary>A question before a step that changes the phone (PLAN.md §2 rule 4). A
/// <see cref="Destructive"/> one gets a red button that only wakes after a moment.</summary>
public sealed record ConfirmRequest(string Title, string Body, string Primary, bool Destructive = false);

/// <summary>
/// What every Tools section shares: the phone in use, the shell, the command caption, the
/// confirm dialog and the notice line. <see cref="ToolsViewModel"/> owns it and keeps
/// <see cref="Device"/> current; the page supplies <see cref="Confirm"/>.
/// </summary>
public sealed class ToolsContext(IAdbShell shell)
{
    public IAdbShell Shell { get; } = shell;

    /// <summary>The phone Tools works on, or null when none is connected and allowed.</summary>
    public ToolsDevice? Device { get; internal set; }

    /// <summary>The phone's Android API level (30 = Android 11), or 0 while unknown.</summary>
    public int Sdk { get; internal set; }

    public bool HasDevice => Device is { Authorized: true };

    public string Serial => Device?.Serial ?? "";

    /// <summary>Shows the question and says whether the user went ahead. Set by ToolsPage.</summary>
    public Func<ConfirmRequest, Task<bool>> Confirm { get; set; } = _ => Task.FromResult(false);

    /// <summary>The "⌘ adb shell …" caption under the last action (rule 3).</summary>
    public event Action<string>? CommandShown;

    /// <summary>A short line for the user: done, failed, or why something can't run.</summary>
    public event Action<string>? Notice;

    public void ShowCommand(string command) => CommandShown?.Invoke(command);

    public void Say(string text) => Notice?.Invoke(text);

    /// <summary>Runs one shell command on the phone and shows it in the caption. With
    /// <paramref name="secret"/> the caption and the log hide the text (typed text, PINs).</summary>
    public async Task<ShellResult> RunAsync(string command, TimeSpan? timeout = null, bool secret = false, CancellationToken ct = default)
    {
        if (!HasDevice)
        {
            return new ShellResult(false, "No phone is connected.", command);
        }
        ShowCommand(secret ? "adb shell input text ••••" : "adb shell " + command);
        return await Shell.RunAsync(Serial, command, timeout, secret, ct);
    }

    /// <summary>Runs a command and reports a failure in the notice line. True when it ran.</summary>
    public async Task<bool> DoAsync(string command, string? done = null, bool secret = false)
    {
        var result = await RunAsync(command, secret: secret);
        if (!result.Ok)
        {
            Say(result.Text);
            return false;
        }
        if (done is not null) Say(done);
        return true;
    }
}

/// <summary>
/// One Tools section (Type, Apps, Health…). Nothing runs until it is shown (PLAN.md §1.3), and
/// anything live stops when it is hidden.
/// </summary>
public abstract partial class ToolSection(ToolsContext ctx) : ObservableObject
{
    protected ToolsContext Ctx { get; } = ctx;

    /// <summary>The section was opened, or the phone changed while it was open. Only called with
    /// a phone connected and allowed.</summary>
    public virtual Task ShownAsync() => Task.CompletedTask;

    /// <summary>The section was closed, the page was left, or the phone went away.</summary>
    public virtual void Hidden() { }
}
