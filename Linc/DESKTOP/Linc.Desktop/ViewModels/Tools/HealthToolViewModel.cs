using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Linc.Desktop.Services.Tools;
using P = Linc.Desktop.Services.Tools.HealthSystemParsers;

namespace Linc.Desktop.ViewModels.Tools;

/// <summary>A busy app in Tools › Health, with what Stop sends.</summary>
public sealed record BusyAppItem(string Package, string Name, string CpuText)
{
    public string StopName => "Stop " + Name;
}

/// <summary>One line of Tools › Health › Phone details.</summary>
public sealed record DetailRow(string Label, string Value);

/// <summary>
/// Tools › Health (TOOLS.md §5): battery, temperature, storage, CPU and memory graphs and the
/// busiest apps, read again every 3 s while the section is open. Anything the phone doesn't
/// share stays hidden.
/// </summary>
public partial class HealthToolViewModel(ToolsContext ctx) : ToolSection(ctx)
{
    public const int GraphSamples = 60;
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(3);

    private CancellationTokenSource? _loop;
    private CpuSample? _lastCpu;
    private readonly List<double> _cpu = [];
    private readonly List<double> _mem = [];

    // ---- battery -----------------------------------------------------------------------------
    [ObservableProperty] public partial bool HasBattery { get; set; }
    [ObservableProperty] public partial double BatteryLevel { get; set; }
    [ObservableProperty] public partial string BatteryText { get; set; } = "";
    [ObservableProperty] public partial string BatteryStatus { get; set; } = "";
    [ObservableProperty] public partial string BatteryTemperature { get; set; } = "";

    // ---- temperature -------------------------------------------------------------------------
    [ObservableProperty] public partial bool HasTemperature { get; set; }
    [ObservableProperty] public partial string TemperatureText { get; set; } = "";
    [ObservableProperty] public partial string TemperatureState { get; set; } = "";
    [ObservableProperty] public partial bool IsCool { get; set; }
    [ObservableProperty] public partial bool IsWarm { get; set; }
    [ObservableProperty] public partial bool IsHot { get; set; }

    // ---- storage -----------------------------------------------------------------------------
    [ObservableProperty] public partial bool HasStorage { get; set; }
    [ObservableProperty] public partial string StorageFree { get; set; } = "";
    [ObservableProperty] public partial string StorageTotal { get; set; } = "";
    [ObservableProperty] public partial double StorageUsedPercent { get; set; }

    // ---- CPU and memory ----------------------------------------------------------------------
    [ObservableProperty] public partial string CpuText { get; set; } = "CPU";
    [ObservableProperty] public partial string MemoryText { get; set; } = "Memory";
    [ObservableProperty] public partial bool HasMemory { get; set; }

    /// <summary>CPU use, 0–100, oldest first (up to <see cref="GraphSamples"/>).</summary>
    public IReadOnlyList<double> CpuSamples => _cpu;

    /// <summary>Memory in use, 0–100, oldest first.</summary>
    public IReadOnlyList<double> MemorySamples => _mem;

    /// <summary>Raised after each refresh that added graph samples; the view redraws.</summary>
    public event Action? SamplesChanged;

    // ---- busy apps and details ---------------------------------------------------------------
    public ObservableCollection<BusyAppItem> BusyApps { get; } = [];
    [ObservableProperty] public partial bool HasBusyApps { get; set; }

    public ObservableCollection<DetailRow> Details { get; } = [];

    public string DetailsText => string.Join(Environment.NewLine, Details.Select(d => $"{d.Label}: {d.Value}"));

    public override Task ShownAsync()
    {
        _loop?.Cancel();
        var cts = _loop = new CancellationTokenSource();
        _lastCpu = null;
        _cpu.Clear();
        _mem.Clear();
        SamplesChanged?.Invoke();
        _ = LoadDetailsAsync(cts.Token);
        _ = RefreshLoopAsync(cts.Token);
        return Task.CompletedTask;
    }

    public override void Hidden()
    {
        _loop?.Cancel();
        _loop = null;
    }

    // One refresh at a time: the next waits for the last to finish, then 3 s.
    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // One bad answer mustn't stop the refresh; the next round tries again.
            }
            try { await Task.Delay(Every, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private Task<ShellResult> Read(string command, CancellationToken ct, int seconds = 6) =>
        Ctx.Shell.RunAsync(Ctx.Serial, command, TimeSpan.FromSeconds(seconds), ct: ct);

    private async Task RefreshAsync(CancellationToken ct)
    {
        var battery = Read("dumpsys battery", ct);
        var current = Read("cat /sys/class/power_supply/battery/current_now", ct);
        var thermal = Read("dumpsys thermalservice", ct);
        var df = Read("df /data", ct);
        var stat = Read("cat /proc/stat", ct);
        var mem = Read("cat /proc/meminfo", ct);
        var top = Read("top -b -n 1 -m 10 -o PID,%CPU,RES,NAME -s 2", ct, seconds: 10);
        await Task.WhenAll(battery, current, thermal, df, stat, mem, top);
        ct.ThrowIfCancellationRequested();

        // Battery.
        var b = battery.Result.Ok ? P.ParseBattery(battery.Result.Text) : null;
        HasBattery = b is not null;
        if (b is not null)
        {
            BatteryLevel = b.Level;
            BatteryText = $"{b.Level}%";
            var watts = current.Result.Ok ? P.ChargeWatts(current.Result.Text, b.VoltageMv) : null;
            BatteryStatus = P.BatteryStatus(b, watts);
            BatteryTemperature = b.TemperatureC is { } bt ? $"Battery {Math.Round(bt)}°" : "";
        }

        // Temperature.
        var t = thermal.Result.Ok ? P.PhoneTemperature(thermal.Result.Text) : null;
        if (t is null && b?.TemperatureC is { } fallback) t = (fallback, "battery");
        HasTemperature = t is not null;
        if (t is { } temp)
        {
            TemperatureText = $"{Math.Round(temp.Celsius)}°";
            TemperatureState = P.TemperatureState(temp.Celsius);
            IsCool = TemperatureState == "Cool";
            IsWarm = TemperatureState == "Warm";
            IsHot = TemperatureState == "Hot";
        }

        // Storage.
        var d = df.Result.Ok ? P.ParseDf(df.Result.Text) : null;
        HasStorage = d is not null;
        if (d is { } disk)
        {
            StorageFree = P.FormatKb(disk.AvailKb) + " free";
            StorageTotal = "of " + P.FormatKb(disk.TotalKb);
            StorageUsedPercent = Math.Clamp(100.0 * (disk.TotalKb - disk.AvailKb) / disk.TotalKb, 0, 100);
        }

        // CPU, against the last refresh's sample.
        var sample = stat.Result.Ok ? P.ParseProcStat(stat.Result.Text) : null;
        if (sample is not null)
        {
            if (_lastCpu is not null && P.CpuPercent(_lastCpu, sample) is { } pct)
            {
                Push(_cpu, pct);
                CpuText = $"CPU · {Math.Round(pct)}%";
            }
            _lastCpu = sample;
        }

        // Memory.
        var m = mem.Result.Ok ? P.ParseMemInfo(mem.Result.Text) : null;
        HasMemory = m is not null;
        if (m is { } ram)
        {
            var used = ram.TotalKb - ram.AvailableKb;
            Push(_mem, 100.0 * used / ram.TotalKb);
            MemoryText = $"Memory · {P.FormatKb(used).Replace(" GB", "")} of {P.FormatKb(ram.TotalKb)}";
        }
        SamplesChanged?.Invoke();

        // Busy apps.
        var apps = top.Result.Ok ? P.ParseTop(top.Result.Text) : [];
        var items = apps.Select(a => new BusyAppItem(a.Package, Pretty(a.Package), $"{Math.Round(a.Cpu)}%")).ToList();
        if (!items.SequenceEqual(BusyApps))
        {
            BusyApps.Clear();
            foreach (var item in items) BusyApps.Add(item);
        }
        HasBusyApps = BusyApps.Count > 0;
    }

    private static void Push(List<double> samples, double value)
    {
        samples.Add(Math.Clamp(value, 0, 100));
        if (samples.Count > GraphSamples) samples.RemoveAt(0);
    }

    /// <summary>"com.spotify.music" → "Spotify"; good enough without the Linc app's labels.</summary>
    public static string Pretty(string package)
    {
        var parts = package.Split('.');
        var skip = new HashSet<string> { "com", "org", "net", "android", "app", "apps", "google", "mobile", "music", "client", "katana", "orca" };
        var pick = parts.Skip(1).FirstOrDefault(p => !skip.Contains(p)) ?? parts[^1];
        return pick.Length == 0 ? package : char.ToUpperInvariant(pick[0]) + pick[1..];
    }

    /// <summary>Stop on a busy app's row.</summary>
    public async Task StopAppAsync(string package)
    {
        if (!P.IsPackageName(package)) return;
        if (await Ctx.DoAsync("am force-stop " + AdbQuote.Arg(package), $"Stopped {Pretty(package)}."))
        {
            var row = BusyApps.FirstOrDefault(a => a.Package == package);
            if (row is not null) BusyApps.Remove(row);
            HasBusyApps = BusyApps.Count > 0;
        }
    }

    private async Task LoadDetailsAsync(CancellationToken ct)
    {
        try
        {
            async Task<string?> Text(string command)
            {
                var r = await Read(command, ct);
                return r.Ok ? r.Text : null;
            }
            async Task<string?> Prop(string name) => await Text("getprop " + name) is { } v ? P.Prop(v) : null;
            var maker = await Prop("ro.product.manufacturer");
            var model = await Prop("ro.product.model");
            var release = await Prop("ro.build.version.release");
            var patch = await Prop("ro.build.version.security_patch");
            var soc = await Prop("ro.soc.model");
            var size = await Text("wm size") is { } wm ? P.ParseWmSize(wm) : null;
            var uptime = await Text("cat /proc/uptime") is { } up ? P.FormatUptime(up) : null;
            ct.ThrowIfCancellationRequested();
            Details.Clear();
            void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) Details.Add(new DetailRow(label, value)); }
            Add("Phone", string.Join(' ', new[] { maker, model }.Where(s => s is not null)));
            Add("Android", release is null ? null : $"{release}{(Ctx.Sdk > 0 ? $" (API {Ctx.Sdk})" : "")}");
            Add("Security update", patch);
            Add("Chip", soc);
            Add("Screen", size);
            Add("Up for", uptime);
            Add("Serial", Ctx.Serial);
            OnPropertyChanged(nameof(DetailsText));
        }
        catch (OperationCanceledException) { }
    }
}
