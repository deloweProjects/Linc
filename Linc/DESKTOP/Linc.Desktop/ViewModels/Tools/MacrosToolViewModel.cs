using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services;
using Linc.Desktop.Services.Tools;
using Microsoft.UI.Dispatching;

namespace Linc.Desktop.ViewModels.Tools;

/// <summary>
/// Tools › Macros (TOOLS.md §9; the owner, 2026-11-03: "add a macros section in Tools on PC and
/// phone"): named lists of steps run with one click. Steps are what Linc already does to the
/// phone (over ADB, or over its own link: ring, DND, media, mirror, clipboard) and a few on this
/// PC. Kept in <c>macros.json</c> beside <c>settings.json</c>. Unlike the other sections it opens
/// without ADB, since a macro of link steps doesn't need it.
/// </summary>
public partial class MacrosToolViewModel : ToolSection
{
    private const string FileName = "macros.json";

    private readonly IConnectionManager _connection;
    private readonly IConnectionSupervisor _link;
    private readonly MirrorViewModel _mirror;
    private readonly ILogService _log;
    private readonly string _path;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _run;

    public MacrosToolViewModel(ToolsContext ctx, IConnectionManager connection, IConnectionSupervisor link, MirrorViewModel mirror,
        IDeviceRegistry registry, ILogService log) : base(ctx)
    {
        _connection = connection;
        _link = link;
        _mirror = mirror;
        _log = log;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _path = Path.Combine(registry.RootPath, FileName);
        Load();
        AddKind = MacroSteps.Kinds[0];
    }

    public ObservableCollection<MacroCard> Macros { get; } = [];

    public ObservableCollection<MacroStepRow> Steps { get; } = [];

    public IReadOnlyList<MacroStepInfo> Kinds => MacroSteps.Kinds;

    public IReadOnlyList<Macro> Templates => MacroSteps.Templates;

    /// <summary>The PIN for a "Type the PIN" step, asked each run and never kept. Set by the section.</summary>
    public Func<Task<string?>> AskPin { get; set; } = () => Task.FromResult<string?>(null);

    // ---- the list ----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Editing), nameof(NotEditing))]
    public partial MacroCard? Selected { get; set; }

    public bool Editing => Selected is not null;

    public bool NotEditing => Selected is null;

    /// <summary>The list's selection (x:Bind's two-way SelectedItem needs an object).</summary>
    public object? SelectedItem
    {
        get => Selected;
        set { if (value is MacroCard card && card != Selected) Selected = card; }
    }

    partial void OnSelectedChanged(MacroCard? value)
    {
        OnPropertyChanged(nameof(SelectedItem));
        Steps.Clear();
        if (value is null) return;
        _name = value.Macro.Name;
        OnPropertyChanged(nameof(Name));
        for (var i = 0; i < value.Macro.Steps.Count; i++) Steps.Add(new MacroStepRow(i, value.Macro.Steps[i]));
    }

    [RelayCommand]
    private void New()
    {
        var card = new MacroCard(new Macro(Guid.NewGuid().ToString("N")[..8], "New macro", "", []));
        Macros.Add(card);
        Selected = card;
        Save();
    }

    [RelayCommand]
    private void AddTemplate(Macro template)
    {
        var card = new MacroCard(template with { Id = Guid.NewGuid().ToString("N")[..8] });
        Macros.Add(card);
        Selected = card;
        Save();
    }

    [RelayCommand]
    private void CloseEditor() => Selected = null;

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } card) return;
        if (!await Ctx.Confirm(new ConfirmRequest($"Delete \"{card.Macro.Name}\"?", "Its steps go too.", "Delete", Destructive: true))) return;
        Macros.Remove(card);
        Selected = null;
        Save();
    }

    // ---- the editor --------------------------------------------------------------------------

    private string _name = "";

    public string Name
    {
        get => _name;
        set
        {
            if (value == _name || Selected is null) return;
            _name = value;
            OnPropertyChanged();
            Change(Selected.Macro with { Name = value.Trim().Length > 0 ? value.Trim() : "Macro" }, rebuildSteps: false);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddHasValue), nameof(AddLabel), nameof(AddExample))]
    public partial MacroStepInfo AddKind { get; set; }

    public bool AddHasValue => AddKind.HasValue;

    public string AddLabel => AddKind.ValueLabel;

    public string AddExample => AddKind.Example;

    [ObservableProperty]
    public partial string AddValue { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddProblem))]
    public partial string AddProblem { get; set; } = "";

    public bool HasAddProblem => AddProblem.Length > 0;

    /// <summary>The "Add step" picker's selection (x:Bind's two-way SelectedItem needs an object).</summary>
    public object? AddKindItem
    {
        get => AddKind;
        set { if (value is MacroStepInfo info && info != AddKind) { AddKind = info; AddProblem = ""; OnPropertyChanged(); } }
    }

    [RelayCommand]
    private void AddStep()
    {
        if (Selected is null) return;
        var step = MacroSteps.FromInput(AddKind.Kind, AddValue);
        if (MacroSteps.Problem(step) is { } problem)
        {
            AddProblem = problem;
            return;
        }
        AddProblem = "";
        AddValue = "";
        Change(Selected.Macro with { Steps = [.. Selected.Macro.Steps, step] });
    }

    /// <summary>The catalogue commands a macro can run: shell ones that delete or reset nothing.</summary>
    public IReadOnlyList<CommandEntry> SearchCommands(string query) =>
        CommandCatalog.Search(query).Where(e => !e.Adb && e.Tier != CommandTier.Destructive).Take(12).ToList();

    /// <summary>A catalogue command picked: one with nothing to fill in is added as it is; one with
    /// values goes into the Shell box with its {placeholders}, to fill in there.</summary>
    public void PickCommand(CommandEntry entry)
    {
        AddKindItem = MacroSteps.Kinds.First(k => k.Kind == MacroStepKind.Shell);
        if (entry.Params.Count == 0 && CommandCatalog.Fill(entry, new Dictionary<string, string>()) is { Shell: { } shell })
        {
            AddValue = shell;
            AddStep();
            return;
        }
        var defaults = entry.Params.Where(p => p.Default.Length > 0).ToDictionary(p => p.Name, p => p.Default);
        AddValue = CommandCatalog.Placeholder().Replace(entry.Template,
            m => defaults.TryGetValue(m.Groups[1].Value, out var d) ? CommandCatalog.ShellValue(ParamKind.Text, d) : m.Value);
        AddProblem = "Fill in the {values}, then Add";
    }

    [RelayCommand]
    private void MoveUp(MacroStepRow row) => Move(row.Index, row.Index - 1);

    [RelayCommand]
    private void MoveDown(MacroStepRow row) => Move(row.Index, row.Index + 1);

    private void Move(int from, int to)
    {
        if (Selected is null) return;
        Change(Selected.Macro with { Steps = MacroSteps.Moved(Selected.Macro.Steps, from, to) });
    }

    [RelayCommand]
    private void RemoveStep(MacroStepRow row)
    {
        if (Selected is null) return;
        var steps = Selected.Macro.Steps.ToList();
        if (row.Index < 0 || row.Index >= steps.Count) return;
        steps.RemoveAt(row.Index);
        Change(Selected.Macro with { Steps = steps });
    }

    /// <summary>The list was dragged into a new order (the ListView reorders <see cref="Steps"/> itself).</summary>
    public void StepsReordered()
    {
        if (Selected is null) return;
        Change(Selected.Macro with { Steps = Steps.Select(r => r.Step).ToList() });
    }

    private void Change(Macro macro, bool rebuildSteps = true)
    {
        if (Selected is null) return;
        Selected.Macro = macro;
        if (rebuildSteps)
        {
            Steps.Clear();
            for (var i = 0; i < macro.Steps.Count; i++) Steps.Add(new MacroStepRow(i, macro.Steps[i]));
        }
        Save();
    }

    // ---- running -----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotRunning))]
    public partial bool Running { get; set; }

    public bool NotRunning => !Running;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunText))]
    public partial string RunText { get; set; } = "";

    public bool HasRunText => RunText.Length > 0;

    [ObservableProperty]
    public partial double RunProgress { get; set; }

    [ObservableProperty]
    public partial bool RunFailed { get; set; }

    [RelayCommand]
    private Task RunSelectedAsync() => Selected is { } card ? RunAsync(card) : Task.CompletedTask;

    [RelayCommand]
    private async Task RunAsync(MacroCard card)
    {
        if (Running) return;
        var macro = card.Macro;
        if (macro.Steps.Count == 0)
        {
            Show($"\"{macro.Name}\" has no steps yet.", failed: true);
            return;
        }
        for (var i = 0; i < macro.Steps.Count; i++)
        {
            if (MacroSteps.Problem(macro.Steps[i]) is { } problem)
            {
                Show(MacroSteps.FailureText(i, macro.Steps[i], problem), failed: true);
                return;
            }
        }
        var risky = macro.Steps.Where(MacroSteps.IsRisky).ToList();
        if (risky.Count > 0 && !await Ctx.Confirm(new ConfirmRequest($"Run \"{macro.Name}\"?",
                "It " + string.Join("; ", risky.Select(s => ConsoleRisk.Describe(s.Value)).Distinct()) + ".", "Run")))
        {
            return;
        }

        Running = true;
        RunFailed = false;
        _run = new CancellationTokenSource();
        var ct = _run.Token;
        try
        {
            for (var i = 0; i < macro.Steps.Count; i++)
            {
                var step = macro.Steps[i];
                RunProgress = (double)i / macro.Steps.Count;
                RunText = $"{macro.Name} · step {i + 1} of {macro.Steps.Count}: {MacroSteps.Describe(step)}";
                string? failure;
                try
                {
                    failure = await RunStepAsync(step, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                }
                if (failure is not null)
                {
                    var text = MacroSteps.FailureText(i, step, failure);
                    _log.Log(LogLevel.Warn, "Macro \"" + macro.Name + "\": " + text);
                    Show(text, failed: true);
                    return;
                }
            }
            RunProgress = 1;
            Show($"\"{macro.Name}\" ran all {macro.Steps.Count} step{(macro.Steps.Count == 1 ? "" : "s")}.", failed: false);
        }
        catch (OperationCanceledException)
        {
            Show($"Stopped \"{macro.Name}\".", failed: false);
        }
        finally
        {
            Running = false;
            _run = null;
        }
    }

    [RelayCommand]
    private void StopRun() => _run?.Cancel();

    private void Show(string text, bool failed)
    {
        RunText = text;
        RunFailed = failed;
        Ctx.Say(text);
    }

    /// <summary>Runs one step; the reason it failed, or null when it ran.</summary>
    private async Task<string?> RunStepAsync(MacroStep step, CancellationToken ct)
    {
        switch (MacroSteps.Need(step.Kind))
        {
            case MacroNeed.Adb when !Ctx.HasDevice:
                return "this step reaches the phone over ADB, and ADB can't see it. Plug in the cable or turn on wireless debugging.";
            case MacroNeed.Link when _link.State != LinkState.Connected || _connection.Current is null:
                return "the phone isn't connected to Linc.";
        }

        switch (step.Kind)
        {
            case MacroStepKind.Wait:
                await Task.Delay(TimeSpan.FromSeconds(step.Seconds), ct);
                return null;
            case MacroStepKind.Pin:
                var pin = await AskPin();
                if (string.IsNullOrEmpty(pin)) return "no PIN was given.";
                return await ShellAsync(TypeCommands.LineCommand(pin, enter: false, out _)!, ct, secret: true);
            case MacroStepKind.TapElement:
                var dump = await Ctx.RunAsync(UiDump.Command, TimeSpan.FromSeconds(20), ct: ct);
                if (!dump.Ok) return dump.Text;
                if (UiDump.Find(UiDump.Parse(dump.Text), step.Value) is not { } node) return "it isn't on the screen.";
                return await ShellAsync($"input tap {node.CenterX} {node.CenterY}", ct);
            case MacroStepKind.WaitApp or MacroStepKind.WaitText or MacroStepKind.WaitScreenOn:
                return await WaitUntilAsync(step, ct);
            case MacroStepKind.Screenshot:
                return await ScreenshotAsync(ct);
            case MacroStepKind.SendClipboard:
                var text = step.Value.Length > 0 ? step.Value : await PcClipboardAsync();
                if (string.IsNullOrEmpty(text)) return "this PC's clipboard has no text.";
                await _connection.SendClipboardAsync(text, ct);
                return null;
            case MacroStepKind.Ring:
                await _connection.LocateDeviceAsync(ct);
                return null;
            case MacroStepKind.Dnd:
                await _connection.SetDndAsync(step.Value == "on", ct);
                return null;
            case MacroStepKind.Media:
                await _connection.SendMediaControlAsync(step.Value, null, ct);
                return null;
            case MacroStepKind.Mirror:
                if (_mirror.IsRunning) return null;
                await _mirror.ToggleCommand.ExecuteAsync(null);
                return _mirror.ErrorMessage;
            case MacroStepKind.PcOpen:
                Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(step.Value.Trim())) { UseShellExecute = true })?.Dispose();
                return null;
            default:
                return MacroSteps.Command(step) is { } command ? await ShellAsync(command, ct) : "this step isn't filled in.";
        }
    }

    private async Task<string?> ShellAsync(string command, CancellationToken ct, bool secret = false)
    {
        var result = await Ctx.RunAsync(command, secret: secret, ct: ct);
        return result.Ok ? null : result.Text;
    }

    private async Task<string?> WaitUntilAsync(MacroStep step, CancellationToken ct)
    {
        var seconds = step.Seconds > 0 ? step.Seconds : MacroSteps.DefaultWaitSeconds;
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            var done = step.Kind switch
            {
                MacroStepKind.WaitApp => MacroSteps.AppOnScreen((await Ctx.RunAsync(MacroSteps.ForegroundCommand, ct: ct)).Text, step.Value),
                MacroStepKind.WaitScreenOn => MacroSteps.ScreenOn((await Ctx.RunAsync(MacroSteps.ScreenCommand, ct: ct)).Text),
                _ => UiDump.Shows(UiDump.Parse((await Ctx.RunAsync(UiDump.Command, TimeSpan.FromSeconds(20), ct: ct)).Text), step.Value),
            };
            if (done) return null;
            await Task.Delay(700, ct);
        }
        return $"it didn't happen within {seconds:0.#} seconds.";
    }

    private async Task<string?> ScreenshotAsync(CancellationToken ct)
    {
        Ctx.ShowCommand("adb exec-out screencap -p");
        var png = await Ctx.Shell.ExecOutAsync(Ctx.Serial, "screencap -p", TimeSpan.FromSeconds(20), ct);
        if (png is null) return "the phone didn't send a screenshot.";
        Directory.CreateDirectory(CaptureToolViewModel.ScreenshotFolder);
        await File.WriteAllBytesAsync(Path.Combine(CaptureToolViewModel.ScreenshotFolder, CaptureCommands.FileName(DateTime.Now, "png")), png, ct);
        return null;
    }

    private static async Task<string?> PcClipboardAsync()
    {
        var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
        return content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text) ? await content.GetTextAsync() : null;
    }

    // ---- the file ----------------------------------------------------------------------------

    private void Load()
    {
        IReadOnlyList<Macro>? macros = null;
        try
        {
            if (File.Exists(_path))
            {
                macros = MacroSteps.TryDeserialize(File.ReadAllText(_path));
                if (macros is null)
                {
                    // Not a macros file: keep it beside, rather than writing over what someone made.
                    File.Copy(_path, _path + ".unreadable", overwrite: true);
                    _log.Log(LogLevel.Warn, "macros.json couldn't be read; kept it as macros.json.unreadable");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Log(LogLevel.Warn, "Couldn't read macros.json: " + ex.Message);
        }
        foreach (var m in macros ?? MacroSteps.Templates) Macros.Add(new MacroCard(m));
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, MacroSteps.Serialize(Macros.Select(c => c.Macro).ToList()));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ctx.Say("Couldn't save the macros: " + ex.Message);
        }
    }
}

/// <summary>One macro in the list.</summary>
public partial class MacroCard(Macro macro) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Summary))]
    public partial Macro Macro { get; set; } = macro;

    public string Name => Macro.Name;

    public string Glyph => Macro.Glyph;

    public string Summary => Macro.Steps.Count switch
    {
        0 => "No steps yet",
        1 => MacroSteps.Describe(Macro.Steps[0]),
        var n => $"{n} steps · {MacroSteps.Describe(Macro.Steps[0])}, …",
    };

    public override string ToString() => "Macro: " + Name;
}

/// <summary>One step in the editor's list.</summary>
public sealed class MacroStepRow(int index, MacroStep step)
{
    public int Index { get; } = index;

    public MacroStep Step { get; } = step;

    public string Number => (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string Text => MacroSteps.Describe(Step);

    public string Glyph => MacroSteps.Kinds.FirstOrDefault(k => k.Kind == Step.Kind)?.Glyph ?? "";

    public string Needs => MacroSteps.Need(Step.Kind) switch
    {
        MacroNeed.Adb => "ADB",
        MacroNeed.Link => "Linc",
        _ => "PC",
    };

    public override string ToString() => "Step " + Number + ": " + Text;
}
