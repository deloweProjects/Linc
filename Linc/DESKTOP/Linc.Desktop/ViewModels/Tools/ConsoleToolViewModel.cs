using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services.Tools;

namespace Linc.Desktop.ViewModels.Tools;

/// <summary>
/// Tools › Console (TOOLS.md §10): <c>adb shell</c> typed by hand, with ↑/↓ history. Verbs that
/// change or wipe things are flagged while typing, and the first one in a session asks first.
/// </summary>
public partial class ConsoleToolViewModel(ToolsContext ctx) : ToolSection(ctx)
{
    private static readonly TimeSpan CommandLimit = TimeSpan.FromSeconds(30);

    private readonly CommandHistory _history = new();
    private readonly ConsoleBuffer _buffer = new();
    private bool _riskAllowed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RiskHint), nameof(HasRiskHint))]
    public partial string Input { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput), nameof(NoOutput))]
    public partial string Output { get; set; } = "";

    public bool HasOutput => Output.Length > 0;

    public bool NoOutput => !HasOutput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotRunning))]
    public partial bool Running { get; set; }

    public bool NotRunning => !Running;

    public string RiskHint => ConsoleRisk.Describe(Input) is { } why ? $"Careful: this {why}." : "";

    public bool HasRiskHint => RiskHint.Length > 0;

    [RelayCommand]
    private async Task RunAsync()
    {
        var command = Input.Trim();
        if (command.Length == 0 || Running) return;
        if (ConsoleRisk.Describe(command) is { } why && !_riskAllowed)
        {
            var go = await Ctx.Confirm(new ConfirmRequest(
                "Run this command?",
                $"\"{command}\" {why}. Commands run on the phone as they are, with nothing to undo them.",
                "Run"));
            if (!go) return;
            _riskAllowed = true; // asked once per session
        }
        _history.Add(command);
        Input = "";
        Append($"$ {command}\n");
        Running = true;
        try
        {
            var result = await Ctx.RunAsync(command, CommandLimit);
            var text = result.Text.Length > 0 ? result.Text.TrimEnd('\r', '\n') + "\n" : "";
            Append(result.Ok ? text : $"({result.Text})\n");
        }
        finally
        {
            Running = false;
        }
    }

    /// <summary>↑ in the box.</summary>
    public void HistoryUp()
    {
        if (_history.Previous(Input) is { } previous) Input = previous;
    }

    /// <summary>↓ in the box.</summary>
    public void HistoryDown()
    {
        if (_history.Next() is { } next) Input = next;
    }

    [RelayCommand]
    private void Clear()
    {
        _buffer.Clear();
        Output = "";
    }

    private void Append(string text)
    {
        _buffer.Append(text);
        Output = _buffer.Text;
    }
}
