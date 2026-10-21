using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services.Tools;

namespace Linc.Desktop.ViewModels.Tools;

/// <summary>One search result in Tools › Commands.</summary>
public sealed class CommandItem(CommandEntry entry)
{
    public CommandEntry Entry => entry;
    public string Title => entry.Title;
    public string Description => entry.Description;
    public string Glyph => CommandCatalog.GlyphFor(entry.Category);

    /// <summary>Its UI Automation name in the results list.</summary>
    public override string ToString() => entry.Title;
}

/// <summary>One parameter box of the selected command.</summary>
public sealed partial class CommandParamItem : ObservableObject
{
    public CommandParamItem(CommandParam param)
    {
        Param = param;
        Value = param.Default;
    }

    public CommandParam Param { get; }

    public string Label => Param.Required ? Param.Label : Param.Label + " (optional)";

    public string Placeholder => Param.Example.Length > 0 ? "e.g. " + Param.Example : "";

    public bool IsChoice => Param.Kind == ParamKind.Choice;

    public bool IsTextInput => !IsChoice;

    /// <summary>A file or folder on this PC: shows Browse….</summary>
    public bool CanBrowse => Param.Kind is ParamKind.PcFile or ParamKind.PcFolder;

    public bool IsFolder => Param.Kind == ParamKind.PcFolder;

    public IReadOnlyList<string> Choices => Param.Choices ?? [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedChoice))]
    public partial string Value { get; set; } = "";

    /// <summary>The choice box's selection (x:Bind's two-way SelectedItem needs an object).</summary>
    public object? SelectedChoice
    {
        get => Choices.Contains(Value) ? Value : null;
        set { if (value is string s && s != Value) Value = s; }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; } = "";

    public bool HasError => Error.Length > 0;

    public event Action? Changed;

    partial void OnValueChanged(string value) => Changed?.Invoke();
}

/// <summary>
/// Tools › Commands: search everyday adb commands by what they do, fill in their parameters, then
/// run or copy them (<see cref="CommandCatalog"/>). Runs through the Tools shell, so every command
/// shows in the caption, asks first when it changes the phone, and never splices raw text.
/// </summary>
public partial class CommandsToolViewModel(ToolsContext ctx) : ToolSection(ctx)
{
    public const string AllName = "All";
    private const int MaxOutput = 200_000;

    public ObservableCollection<CommandItem> Results { get; } = [];

    public ObservableCollection<CommandParamItem> Params { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    partial void OnQueryChanged(string value) => Refilter();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAll), nameof(IsApps), nameof(IsFiles), nameof(IsScreen), nameof(IsPower),
        nameof(IsNetwork), nameof(IsSettings), nameof(IsInfo), nameof(IsDebug))]
    public partial string Category { get; set; } = AllName;

    public bool IsAll => Category == AllName;
    public bool IsApps => Category == CommandCatalog.Apps;
    public bool IsFiles => Category == CommandCatalog.Files;
    public bool IsScreen => Category == CommandCatalog.Screen;
    public bool IsPower => Category == CommandCatalog.Power;
    public bool IsNetwork => Category == CommandCatalog.Network;
    public bool IsSettings => Category == CommandCatalog.Settings;
    public bool IsInfo => Category == CommandCatalog.Info;
    public bool IsDebug => Category == CommandCatalog.Debug;

    [ObservableProperty]
    public partial string ResultCountText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoResults))]
    public partial bool HasResults { get; set; }

    public bool NoResults => !HasResults;

    [RelayCommand]
    private void SelectCategory(string name)
    {
        if (name == Category) return;
        Category = name;
        Refilter();
    }

    // ---- the selected command -----------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(NoSelection), nameof(SelectedResult), nameof(SelectedTitle),
        nameof(SelectedDescription), nameof(TierText), nameof(IsSafe), nameof(IsCaution), nameof(IsDanger), nameof(HasParams))]
    public partial CommandItem? Selected { get; set; }

    /// <summary>The results list's selection (x:Bind's two-way SelectedItem needs an object).</summary>
    public object? SelectedResult
    {
        get => Selected;
        set { if (value is CommandItem item && item != Selected) Selected = item; }
    }

    public bool HasSelection => Selected is not null;
    public bool NoSelection => Selected is null;
    public string SelectedTitle => Selected?.Title ?? "";
    public string SelectedDescription => Selected?.Description ?? "";
    public string TierText => Selected is null ? "" : CommandCatalog.TierLabel(Selected.Entry.Tier);
    public bool IsSafe => Selected?.Entry.Tier == CommandTier.Safe;
    public bool IsCaution => Selected?.Entry.Tier == CommandTier.Confirm;
    public bool IsDanger => Selected?.Entry.Tier == CommandTier.Destructive;
    public bool HasParams => Params.Count > 0;

    [ObservableProperty]
    public partial string Preview { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlockReason))]
    public partial string BlockReason { get; set; } = "";

    public bool HasBlockReason => BlockReason.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    public partial string Output { get; set; } = "";

    public bool HasOutput => Output.Length > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool Running { get; set; }

    private FilledCommand? _filled;

    /// <summary>What Copy puts on the clipboard: the filled command, or its template while
    /// parameters are missing.</summary>
    public string CopyText => _filled?.Display ?? Preview;

    public bool CanRun => _filled is not null && BlockReason.Length == 0 && !Running;

    partial void OnSelectedChanged(CommandItem? value)
    {
        foreach (var p in Params) p.Changed -= Recompute;
        Params.Clear();
        if (value is not null)
        {
            foreach (var p in value.Entry.Params)
            {
                var item = new CommandParamItem(p);
                item.Changed += Recompute;
                Params.Add(item);
            }
        }
        OnPropertyChanged(nameof(HasParams));
        Output = "";
        Recompute();
    }

    public override Task ShownAsync()
    {
        // Opens on the first command, so the page never looks empty.
        if (Results.Count == 0) Refilter();
        Recompute(); // the phone may have changed: Android version, USB or Wi-Fi
        return Task.CompletedTask;
    }

    // One item per command for the page's life, so the list can re-select the open one.
    private readonly Dictionary<string, CommandItem> _items = [];

    private void Refilter()
    {
        var found = CommandCatalog.Search(Query, Category == AllName ? null : Category);
        Results.Clear();
        foreach (var e in found)
        {
            if (!_items.TryGetValue(e.Id, out var item)) _items[e.Id] = item = new CommandItem(e);
            Results.Add(item);
        }
        HasResults = Results.Count > 0;
        ResultCountText = Results.Count == 1 ? "1 command" : $"{Results.Count} commands";
        // A new search opens its best match.
        if (Results.Count > 0 && Selected != Results[0]) Selected = Results[0];
        OnPropertyChanged(nameof(SelectedResult));
    }

    private Dictionary<string, string> Values() => Params.ToDictionary(p => p.Param.Name, p => p.Value.Trim());

    private void Recompute()
    {
        var entry = Selected?.Entry;
        if (entry is null)
        {
            _filled = null;
            Preview = "";
            BlockReason = "";
            OnPropertyChanged(nameof(CopyText));
            RunCommand.NotifyCanExecuteChanged();
            return;
        }
        var values = Values();
        foreach (var p in Params)
        {
            var v = values[p.Param.Name];
            p.Error = v.Length == 0 ? "" : CommandCatalog.Validate(p.Param, v) ?? "";
        }
        _filled = CommandCatalog.Fill(entry, values);
        Preview = _filled?.Display ?? TemplatePreview(entry);
        BlockReason = Block(entry, values) ?? "";
        OnPropertyChanged(nameof(CopyText));
        RunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The command with each missing value shown as &lt;its label&gt;.</summary>
    private static string TemplatePreview(CommandEntry entry)
    {
        var labels = entry.Params.ToDictionary(p => p.Name, p => "<" + p.Label.ToLowerInvariant() + ">");
        var text = CommandCatalog.Placeholder().Replace(entry.Template, m => labels[m.Groups[1].Value]);
        return (entry.Adb ? "adb " : "adb shell ") + text;
    }

    private string? Block(CommandEntry entry, Dictionary<string, string> values)
    {
        if (!Ctx.HasDevice) return "Connect a phone first.";
        if (entry.MinSdk > 0 && Ctx.Sdk > 0 && Ctx.Sdk < entry.MinSdk) return $"Needs {AndroidName(entry.MinSdk)} or newer.";
        if (entry.UsbOnly && Ctx.Device is { Usb: false }) return "Needs the USB cable: over Wi-Fi this would cut the connection.";
        return CommandCatalog.ValidateEntry(entry, values);
    }

    private static string AndroidName(int sdk) => sdk switch
    {
        30 => "Android 11",
        31 or 32 => "Android 12",
        33 => "Android 13",
        34 => "Android 14",
        35 => "Android 15",
        _ => $"API level {sdk}",
    };

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var entry = Selected?.Entry;
        var filled = _filled;
        if (entry is null || filled is null) return;
        if (entry.Tier != CommandTier.Safe)
        {
            var go = await Ctx.Confirm(new ConfirmRequest(entry.Title + "?",
                entry.Description + "\n\n" + filled.Display, "Run", Destructive: entry.Tier == CommandTier.Destructive));
            if (!go) return;
        }
        Running = true;
        Output = "Running…";
        try
        {
            var timeout = TimeSpan.FromSeconds(entry.TimeoutSeconds);
            ShellResult result;
            if (filled.Shell is { } shell)
            {
                var secret = entry.Params.Any(p => p.Kind == ParamKind.TypedText);
                result = await Ctx.RunAsync(shell, timeout, secret);
            }
            else
            {
                Ctx.ShowCommand(filled.Display);
                result = await Ctx.Shell.AdbAsync(Ctx.Serial, [.. filled.AdbArgs!], timeout);
            }
            var text = result.Text.Trim();
            if (text.Length > MaxOutput) text = text[^MaxOutput..];
            Output = text.Length > 0 ? text : result.Ok ? "Done. (No output.)" : "It didn't work.";
        }
        finally
        {
            Running = false;
            RunCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Called by the view after it put <see cref="CopyText"/> on the clipboard.</summary>
    public void Copied() => Ctx.Say("Copied: " + CopyText);

    /// <summary>A file or folder picked with Browse….</summary>
    public void Picked(CommandParamItem item, string path) => item.Value = path;
}
