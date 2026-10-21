using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linc.Desktop.Services.Tools;

namespace Linc.Desktop.ViewModels.Tools;

/// <summary>One app in the Apps list.</summary>
public sealed partial class AppItem(PackageEntry entry, bool isSystem, bool isDisabled, AppSizes? sizes) : ObservableObject
{
    public string Package { get; } = entry.Package;
    public string Name { get; } = AppsParsers.PrettyName(entry.Package);
    public long VersionCode { get; } = entry.VersionCode;
    public bool IsSystem { get; } = isSystem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle), nameof(StateText))]
    public partial bool IsDisabled { get; set; } = isDisabled;

    public string SizeText { get; } = sizes is { } s ? AppsParsers.FormatSize(s.Total) : "";

    /// <summary>Line two of the row: the package, its size, and "Off" when disabled.</summary>
    public string Subtitle => string.Join(" · ", new[] { Package, SizeText, IsDisabled ? "Off" : "" }.Where(p => p.Length > 0));

    public string StateText => IsDisabled ? "Turned off" : IsSystem ? "Came with the phone" : "Installed by you";

    /// <summary>The tile's letter.</summary>
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    public override string ToString() => Name;
}

/// <summary>One runtime permission switch in the detail panel. Flipping it grants or revokes on
/// the phone; flipping it back is the undo.</summary>
public sealed partial class PermissionItem(AppPermission permission, Func<PermissionItem, bool, Task<bool>> apply) : ObservableObject
{
    private bool _settingQuietly;

    public string Name { get; } = permission.Name;
    public string Label { get; } = permission.Label;

    [ObservableProperty]
    public partial bool Granted { get; set; } = permission.Granted;

    partial void OnGrantedChanged(bool value)
    {
        if (_settingQuietly) return;
        _ = ApplyAsync(value);
    }

    private async Task ApplyAsync(bool value)
    {
        if (await apply(this, value)) return;
        // The phone said no: put the switch back to the truth.
        _settingQuietly = true;
        Granted = !value;
        _settingQuietly = false;
    }
}

/// <summary>
/// Tools › Apps (Vibe/claude-notes/linc-tools/TOOLS.md §3): every app on the phone from
/// <c>pm list packages</c>, filters Yours · System · Off, and a detail panel to open, stop,
/// remove, turn off, clear, save the APK and set permissions. Works with only ADB: names come
/// from the package (no labels or icons without the Linc app).
/// </summary>
public partial class AppsToolViewModel(ToolsContext ctx) : ToolSection(ctx)
{
    public const string YoursFilter = "Yours", SystemFilter = "System", DisabledFilter = "Disabled";

    private List<AppItem> _all = [];
    private string _loadedSerial = "";
    private string _launcher = "";
    private int _detailsRequest;

    public ObservableCollection<AppItem> Visible { get; } = [];

    public ObservableCollection<PermissionItem> Permissions { get; } = [];

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    partial void OnSearchChanged(string value) => ApplyFilter();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowYours), nameof(ShowSystem), nameof(ShowDisabled))]
    public partial string Filter { get; set; } = YoursFilter;

    public bool ShowYours => Filter == YoursFilter;
    public bool ShowSystem => Filter == SystemFilter;
    public bool ShowDisabled => Filter == DisabledFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool Loading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial string EmptyText { get; set; } = "";

    public bool ShowEmpty => !Loading && EmptyText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct), nameof(CanRemove))]
    public partial bool Busy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(NoSelection), nameof(SelectedItem), nameof(CanAct),
        nameof(CanRemove), nameof(ProtectedReason), nameof(HasProtectedReason), nameof(UninstallLabel),
        nameof(DisableLabel), nameof(DisableGlyph), nameof(SelectedName), nameof(SelectedPackage), nameof(SelectedInitial),
        nameof(SelectedState))]
    public partial AppItem? Selected { get; set; }

    /// <summary>The list's selection (x:Bind's two-way SelectedItem needs an object).</summary>
    public object? SelectedItem
    {
        get => Selected;
        set { if (value is AppItem app && app != Selected) Selected = app; else if (value is null && Selected is not null && !Visible.Contains(Selected)) Selected = null; }
    }

    public bool HasSelection => Selected is not null;
    public bool NoSelection => Selected is null;
    public string SelectedName => Selected?.Name ?? "";
    public string SelectedPackage => Selected?.Package ?? "";
    public string SelectedInitial => Selected?.Initial ?? "";
    public string SelectedState => Selected?.StateText ?? "";

    /// <summary>Open and Stop: anything but Linc itself and the parts the phone needs.</summary>
    public bool CanAct => Selected is not null && !Busy;

    /// <summary>Remove, turn off and clear: not for protected apps (TOOLS.md §3, §13).</summary>
    public bool CanRemove => CanAct && ProtectedReason is null;

    public string? ProtectedReason => Selected is null ? null : AppsParsers.ProtectedReason(Selected.Package, _launcher);
    public bool HasProtectedReason => ProtectedReason is not null;
    public string ProtectedText => ProtectedReason ?? "";

    public string UninstallLabel => Selected?.IsSystem == true ? "Remove" : "Uninstall";
    public string DisableLabel => Selected?.IsDisabled == true ? "Turn on" : "Turn off";
    public string DisableGlyph => Selected?.IsDisabled == true ? "" : "";

    [ObservableProperty] public partial string DetailVersion { get; set; } = "";
    [ObservableProperty] public partial string DetailInfo { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPermissions))]
    public partial bool HasPermissions { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPermissions))]
    public partial bool DetailsLoaded { get; set; }

    public bool HasNoPermissions => DetailsLoaded && !HasPermissions;

    [ObservableProperty] public partial bool LoadingDetails { get; set; }

    /// <summary>A system app removed this session, so it can be put back (install-existing).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPutBack), nameof(PutBackLabel))]
    public partial AppItem? LastRemovedSystem { get; set; }

    public bool CanPutBack => LastRemovedSystem is not null;
    public string PutBackLabel => LastRemovedSystem is { } app ? $"Put {app.Name} back" : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Saving))]
    public partial string SaveProgress { get; set; } = "";

    public bool Saving => SaveProgress.Length > 0;

    public override async Task ShownAsync()
    {
        if (_loadedSerial == Ctx.Serial && _all.Count > 0) return;
        await LoadAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private void SetFilter(string filter)
    {
        Filter = filter;
        ApplyFilter();
    }

    private async Task LoadAsync()
    {
        if (!Ctx.HasDevice || Loading) return;
        Loading = true;
        EmptyText = "";
        try
        {
            var serial = Ctx.Serial;
            var all = await Ctx.RunAsync("pm list packages -f -U --show-versioncode", TimeSpan.FromSeconds(30));
            if (all.Ok && (all.Text.Contains("Unknown option", StringComparison.OrdinalIgnoreCase) || !all.Text.Contains("package:", StringComparison.Ordinal)))
            {
                all = await Ctx.RunAsync("pm list packages -f", TimeSpan.FromSeconds(30));
            }
            if (!all.Ok)
            {
                _all = [];
                Visible.Clear();
                Selected = null;
                EmptyText = all.Text;
                return;
            }
            var third = await Ctx.RunAsync("pm list packages -3", TimeSpan.FromSeconds(20));
            var disabled = await Ctx.RunAsync("pm list packages -d", TimeSpan.FromSeconds(20));
            var home = await Ctx.RunAsync("cmd package resolve-activity --brief -c android.intent.category.HOME -a android.intent.action.MAIN");
            // Sizes are a nice extra: diskstats can be slow or missing, so give it a short leash.
            var disk = await Ctx.RunAsync("dumpsys diskstats", TimeSpan.FromSeconds(15));
            if (serial != Ctx.Serial) return; // the phone changed while this ran

            var yours = AppsParsers.PackageNames(third.Text);
            var off = AppsParsers.PackageNames(disabled.Text);
            var sizes = disk.Ok ? AppsParsers.ParseDiskStats(disk.Text) : [];
            _launcher = home.Ok ? AppsParsers.ParseLauncher(home.Text) : "";
            _all = AppsParsers.ParsePackageList(all.Text)
                .Select(p => new AppItem(p, isSystem: third.Ok && !yours.Contains(p.Package), off.Contains(p.Package),
                    sizes.TryGetValue(p.Package, out var s) ? s : null))
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            _loadedSerial = serial;
            var keep = Selected?.Package;
            ApplyFilter();
            Selected = keep is null ? null : _all.FirstOrDefault(a => a.Package == keep);
            if (Selected is not null) await LoadDetailsAsync();
        }
        finally
        {
            Loading = false;
        }
    }

    private void ApplyFilter()
    {
        var query = Search.Trim();
        var shown = _all.Where(a => Filter switch
            {
                SystemFilter => a.IsSystem,
                DisabledFilter => a.IsDisabled,
                _ => !a.IsSystem,
            })
            .Where(a => query.Length == 0 ||
                        a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                        a.Package.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!shown.SequenceEqual(Visible))
        {
            Visible.Clear();
            foreach (var a in shown) Visible.Add(a);
        }
        if (shown.Count > 0) EmptyText = "";
        else if (_all.Count == 0) EmptyText = "The phone didn't list any apps. Try Refresh.";
        else if (query.Length > 0) EmptyText = $"No app matches \"{query}\".";
        else EmptyText = Filter == DisabledFilter ? "Nothing is turned off." : "No apps here.";
        OnPropertyChanged(nameof(SelectedItem));
    }

    partial void OnSelectedChanged(AppItem? value)
    {
        Permissions.Clear();
        HasPermissions = false;
        DetailsLoaded = false;
        LoadingDetails = false;
        DetailVersion = "";
        DetailInfo = "";
        OnPropertyChanged(nameof(ProtectedText));
        if (value is not null) _ = LoadDetailsAsync();
    }

    private async Task LoadDetailsAsync()
    {
        if (Selected is not { } app || !AppsParsers.IsValidPackage(app.Package)) return;
        var request = ++_detailsRequest;
        LoadingDetails = true;
        var result = await Ctx.RunAsync("dumpsys package " + AdbQuote.Arg(app.Package), TimeSpan.FromSeconds(15));
        if (request != _detailsRequest || Selected != app) return; // another app was picked meanwhile
        LoadingDetails = false;
        var details = result.Ok ? AppsParsers.ParseDetails(result.Text) : null;
        DetailVersion = details?.VersionName is { Length: > 0 } v ? "Version " + v : "";
        var info = new List<string>();
        if (app.SizeText.Length > 0) info.Add(app.SizeText);
        if (details is { TargetSdk: > 0 }) info.Add($"made for Android API {details.TargetSdk}");
        if (details?.LastUpdate is { Length: >= 10 } updated) info.Add("updated " + updated[..10]);
        DetailInfo = string.Join(" · ", info);
        Permissions.Clear();
        foreach (var p in details?.Permissions ?? []) Permissions.Add(new PermissionItem(p, SetPermissionAsync));
        HasPermissions = Permissions.Count > 0;
        DetailsLoaded = true;
    }

    private async Task<bool> SetPermissionAsync(PermissionItem permission, bool grant)
    {
        if (Selected is not { } app) return false;
        var command = $"pm {(grant ? "grant" : "revoke")} {AdbQuote.Arg(app.Package)} {AdbQuote.Arg(permission.Name)}";
        var result = await Ctx.RunAsync(command);
        // grant/revoke print nothing when they worked.
        if (result.Ok && result.Text.Trim().Length == 0)
        {
            Ctx.Say($"{permission.Label} {(grant ? "allowed" : "taken away")} for {app.Name}.");
            return true;
        }
        Ctx.Say($"The phone didn't change {permission.Label} for {app.Name}. Some permissions can only be changed on the phone.");
        return false;
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (!Pick(out var app)) return;
        var result = await RunBusyAsync($"monkey -p {AdbQuote.Arg(app.Package)} -c android.intent.category.LAUNCHER 1");
        Ctx.Say(!result.Ok ? result.Text
            : result.Text.Contains("No activities found", StringComparison.OrdinalIgnoreCase) ? $"{app.Name} has nothing to open."
            : $"Opened {app.Name} on the phone.");
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!Pick(out var app)) return;
        if (ProtectedReason is { } why)
        {
            Ctx.Say(why);
            return;
        }
        var result = await RunBusyAsync("am force-stop " + AdbQuote.Arg(app.Package));
        Ctx.Say(result.Ok ? $"Stopped {app.Name}." : result.Text);
    }

    [RelayCommand]
    private async Task UninstallAsync()
    {
        if (!Pick(out var app) || !CanRemove) return;
        bool ok;
        if (app.IsSystem)
        {
            // System apps are only removed for this user: the phone keeps its copy (undo below).
            if (!await Ctx.Confirm(new ConfirmRequest($"Remove {app.Name}?",
                    $"{app.Name} came with the phone. Linc removes it for you only; the phone keeps a copy, so \"Put {app.Name} back\" here brings it back (or a factory reset).",
                    "Remove"))) return;
            ok = await PmAsync($"pm uninstall -k --user 0 {AdbQuote.Arg(app.Package)}", "Success");
            if (ok) LastRemovedSystem = app;
        }
        else
        {
            if (!await Ctx.Confirm(new ConfirmRequest($"Uninstall {app.Name}?",
                    $"{app.Name} and everything it saved are removed from the phone. This can't be undone. To keep a copy, use Save APK first.",
                    "Uninstall", Destructive: true))) return;
            ok = await PmAsync("pm uninstall " + AdbQuote.Arg(app.Package), "Success");
        }
        if (!ok) return;
        Ctx.Say(app.IsSystem ? $"Removed {app.Name}." : $"Uninstalled {app.Name}.");
        _all.Remove(app);
        Selected = null;
        ApplyFilter();
    }

    [RelayCommand]
    private async Task PutBackAsync()
    {
        if (LastRemovedSystem is not { } app) return;
        if (!await PmAsync("cmd package install-existing " + AdbQuote.Arg(app.Package), "installed")) return;
        Ctx.Say($"{app.Name} is back.");
        LastRemovedSystem = null;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ToggleDisabledAsync()
    {
        if (!Pick(out var app) || !CanRemove) return;
        if (app.IsDisabled)
        {
            if (!await PmAsync("pm enable " + AdbQuote.Arg(app.Package), "new state")) return;
            app.IsDisabled = false;
            Ctx.Say($"{app.Name} is on again.");
        }
        else
        {
            if (!await Ctx.Confirm(new ConfirmRequest($"Turn off {app.Name}?",
                    $"{app.Name} stops running and its icon goes away, but nothing is deleted. Turn it back on here any time (Off filter).",
                    "Turn off"))) return;
            if (!await PmAsync("pm disable-user --user 0 " + AdbQuote.Arg(app.Package), "new state")) return;
            app.IsDisabled = true;
            Ctx.Say($"{app.Name} is turned off.");
        }
        OnPropertyChanged(nameof(DisableLabel));
        OnPropertyChanged(nameof(DisableGlyph));
        OnPropertyChanged(nameof(SelectedState));
        ApplyFilter();
    }

    [RelayCommand]
    private async Task ClearDataAsync()
    {
        if (!Pick(out var app) || !CanRemove) return;
        if (!await Ctx.Confirm(new ConfirmRequest($"Clear {app.Name}'s data?",
                $"Everything {app.Name} saved on the phone (sign-ins, settings, downloads inside it) is deleted, as if it was just installed. This can't be undone.",
                "Clear data", Destructive: true))) return;
        if (await PmAsync("pm clear " + AdbQuote.Arg(app.Package), "Success")) Ctx.Say($"Cleared {app.Name}'s data.");
    }

    [RelayCommand]
    private async Task SaveApkAsync()
    {
        if (!Pick(out var app) || Saving) return;
        var paths = await RunBusyAsync("pm path " + AdbQuote.Arg(app.Package));
        var apks = paths.Ok ? AppsParsers.ParseApkPaths(paths.Text) : [];
        if (apks.Count == 0)
        {
            Ctx.Say(paths.Ok ? $"The phone didn't say where {app.Name}'s APK is." : paths.Text);
            return;
        }
        var version = DetailVersion.StartsWith("Version ", StringComparison.Ordinal) ? DetailVersion["Version ".Length..] : app.VersionCode.ToString();
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads",
            AppsParsers.SafeFolderName($"{app.Name} {version}"));
        var saved = 0;
        try
        {
            for (var i = 0; i < apks.Count; i++)
            {
                var index = i;
                SaveProgress = $"Saving {app.Name}… {index + 1} of {apks.Count}";
                var progress = new Progress<double>(p => SaveProgress = $"Saving {app.Name}… {(apks.Count > 1 ? $"{index + 1} of {apks.Count}, " : "")}{p:0}%");
                Ctx.ShowCommand($"adb pull {apks[i]}");
                var local = Path.Combine(folder, AppsParsers.SafeFolderName(Path.GetFileName(apks[i])));
                if (await Ctx.Shell.PullAsync(Ctx.Serial, apks[i], local, progress, CancellationToken.None)) saved++;
            }
        }
        finally
        {
            SaveProgress = "";
        }
        if (saved == apks.Count)
        {
            Ctx.Say(apks.Count > 1
                ? $"Saved {app.Name} ({apks.Count} parts) to Downloads\\{Path.GetFileName(folder)}. Install it again with adb install-multiple."
                : $"Saved {app.Name} to Downloads\\{Path.GetFileName(folder)}.");
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); } catch { }
        }
        else
        {
            Ctx.Say($"Couldn't copy all of {app.Name} off the phone ({saved} of {apks.Count} parts).");
        }
    }

    [RelayCommand]
    private async Task AppInfoAsync()
    {
        if (!Pick(out var app)) return;
        var result = await RunBusyAsync($"am start -a android.settings.APPLICATION_DETAILS_SETTINGS -d {AdbQuote.Arg("package:" + app.Package)}");
        Ctx.Say(result.Ok ? $"{app.Name}'s info is open on the phone." : result.Text);
    }

    private bool Pick(out AppItem app)
    {
        app = Selected!;
        return Selected is not null && Ctx.HasDevice && !Busy && AppsParsers.IsValidPackage(Selected.Package);
    }

    private async Task<ShellResult> RunBusyAsync(string command)
    {
        Busy = true;
        try { return await Ctx.RunAsync(command, TimeSpan.FromSeconds(20)); }
        finally { Busy = false; }
    }

    /// <summary>A pm command, which reports in words: true when its output has <paramref name="success"/>.</summary>
    private async Task<bool> PmAsync(string command, string success)
    {
        var result = await RunBusyAsync(command);
        if (result.Ok && result.Text.Contains(success, StringComparison.OrdinalIgnoreCase)) return true;
        var why = result.Text.Trim();
        Ctx.Say(why.Length == 0 ? "The phone didn't do it." : "The phone said: " + (why.Length > 160 ? why[..160] + "…" : why));
        return false;
    }
}
