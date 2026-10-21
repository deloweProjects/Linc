using Linc.Desktop.Services.Tools;
using P = Linc.Desktop.Services.Tools.HealthSystemParsers;

namespace Linc.ToolSim;

/// <summary>Tools › Health and System parsers against output captured in the shape Android 11–14
/// prints it.</summary>
internal static class HealthSystemTests
{
    private const string Battery14 = """
        Current Battery Service state:
          AC powered: false
          USB powered: true
          Wireless powered: false
          Dock powered: false
          Max charging current: 500000
          Max charging voltage: 5000000
          Charge counter: 3612000
          status: 2
          health: 2
          present: true
          level: 82
          scale: 100
          voltage: 4123
          temperature: 312
          technology: Li-ion
          Charging state: 1
          Charging policy: 1
          Capacity level: 3
        """;

    private const string Battery11Unplugged = """
        Current Battery Service state:
          AC powered: false
          USB powered: false
          Wireless powered: false
          Max charging current: 0
          status: 3
          health: 2
          present: true
          level: 47
          scale: 100
          voltage: 3810
          temperature: 285
          technology: Li-poly
        """;

    private const string Thermal14 = """
        IsStatusOverride: false
        ThermalEventListeners:
        	callbacks: 1
        	killed: false
        	broadcasts count: -1
        ThermalStatusListeners:
        	callbacks: 1
        	killed: false
        	broadcasts count: -1
        Thermal Status: 0
        Cached temperatures:
        	Temperature{mValue=52.0, mType=3, mName=skin, mStatus=2}
        HAL Ready: true
        HAL connection:
        	ThermalHAL AIDL 1  connected: yes
        Current temperatures from HAL:
        	Temperature{mValue=44.7, mType=0, mName=BIG, mStatus=0}
        	Temperature{mValue=31.2, mType=2, mName=battery, mStatus=0}
        	Temperature{mValue=36.4, mType=3, mName=VIRTUAL-SKIN, mStatus=0}
        	Temperature{mValue=NaN, mType=1, mName=G3D, mStatus=0}
        Current cooling devices from HAL:
        	CoolingDevice{mValue=0, mType=2, mName=battery}
        """;

    private const string Thermal11NoSkin = """
        IsStatusOverride: false
        Thermal Status: 0
        Cached temperatures:
        HAL Ready: true
        HAL connection:
        	ThermalHAL 2.0 connected: yes
        Current temperatures from HAL:
        	Temperature{mValue=38.799999, mType=0, mName=cpu0-silver-usr, mStatus=0}
        	Temperature{mValue=40.1, mType=0, mName=cpu4-gold-usr, mStatus=0}
        	Temperature{mValue=29.0, mType=2, mName=battery, mStatus=0}
        Current cooling devices from HAL:
        """;

    private const string Df = """
        Filesystem       1K-blocks     Used Available Use% Mounted on
        /dev/block/dm-48 115005644 51046400  63959244  45% /data
        """;

    private const string Stat1 = """
        cpu  1000000 20000 300000 5000000 40000 0 10000 0 0 0
        cpu0 250000 5000 75000 1250000 10000 0 2500 0 0 0
        intr 123
        """;

    private const string Stat2 = """
        cpu  1001500 20000 300800 5007000 40100 0 10000 0 0 0
        """;

    private const string MemInfo = """
        MemTotal:        7810000 kB
        MemFree:          271128 kB
        MemAvailable:    2462000 kB
        Buffers:            4980 kB
        """;

    private const string Top = "Tasks: 812 total,   1 running, 811 sleeping,   0 stopped,   0 zombie\n" +
        "  Mem:  7621588K total,  7350460K used,   271128K free,     4980K buffers\n" +
        " Swap:  4194300K total,  1386204K used,  2808096K free,  3123332K cached\n" +
        "800%cpu  42%user   0%nice  31%sys 724%idle   0%iow   3%irq   0%sirq   0%host\n" +
        "\u001b[7m  PID %CPU   RES NAME                                                       \u001b[0m\n" +
        " 1234 11.0 300M com.instagram.android\n" +
        " 4321  9.0 5.0M top\n" +
        "  567  6.2 250M com.android.chrome:sandboxed_process0\n" +
        "  568  1.0 150M com.android.chrome\n" +
        "  890  3.1 180M com.spotify.music\n" +
        "   77  2.5   0  kworker/u16:3\n" +
        "  901  1.5 120M system_server\n" +
        "  902  1.2  90M com.google.android.gms.persistent\n";

    public static void Run(Action<bool, string> check)
    {
        // Battery.
        var b = P.ParseBattery(Battery14);
        check(b is { Level: 82, Charging: true, Full: false, Source: "USB", VoltageMv: 4123 } && b.TemperatureC == 31.2, "dumpsys battery (14): 82 %, charging over USB, 31.2°");
        var u = P.ParseBattery(Battery11Unplugged);
        check(u is { Level: 47, Charging: false, Source: null }, "dumpsys battery (11): unplugged");
        check(P.ParseBattery("Can't find service: battery") is null, "no battery service gives nothing");
        check(P.BatteryStatus(u!, null) == "On battery", "unplugged status");

        var w = P.ChargeWatts("-4366000", 4123);
        check(w is > 17.9 and < 18.1, "current_now in µA, negative on this maker → 18 W");
        check(P.ChargeWatts("1500", 4000) is > 5.9 and < 6.1, "current_now in mA → 6 W");
        check(P.ChargeWatts("cat: /sys/class/power_supply/battery/current_now: Permission denied", 4000) is null, "unreadable current gives no watts");
        check(P.ChargeWatts("0", 4000) is null && P.ChargeWatts("100", null) is null, "zero current or no voltage gives no watts");
        check(P.BatteryStatus(b!, w) == "Charging · USB · 18 W", "status line: Charging · USB · 18 W");

        // Temperature.
        var t14 = P.PhoneTemperature(Thermal14);
        check(t14 is { } a && Math.Abs(a.Celsius - 36.4) < 0.01 && a.Name == "VIRTUAL-SKIN", "thermalservice (14): the skin sensor from the HAL block, not the cached one");
        var t11 = P.PhoneTemperature(Thermal11NoSkin);
        check(t11 is { } c && Math.Abs(c.Celsius - 40.1) < 0.01, "thermalservice (11) without a skin sensor: the hottest");
        check(P.PhoneTemperature("Can't find service: thermalservice") is null, "no thermal service gives nothing");
        check(P.TemperatureState(36) == "Cool" && P.TemperatureState(40) == "Warm" && P.TemperatureState(45) == "Hot", "cool / warm / hot");

        // Storage.
        var d = P.ParseDf(Df);
        check(d is { TotalKb: 115005644, UsedKb: 51046400, AvailKb: 63959244 }, "df /data");
        check(P.FormatKb(63959244) == "61 GB" && P.FormatKb(115005644) == "110 GB", "sizes over 10 GB round to whole GB");
        check(P.FormatKb(5_347_738) == "5.1 GB" && P.FormatKb(839_680) == "820 MB", "smaller sizes keep a decimal, or show MB");
        check(P.ParseDf("df: /data: Permission denied") is null, "an unreadable df gives nothing");

        // CPU and memory.
        var s1 = P.ParseProcStat(Stat1);
        var s2 = P.ParseProcStat(Stat2);
        check(s1 is { Idle: 5040000 } && s1.Total == 6370000, "/proc/stat first line: idle + iowait, and the total");
        var pct = s1 is not null && s2 is not null ? P.CpuPercent(s1, s2) : null;
        check(pct == 24.5, "CPU % between two /proc/stat samples: 2300 of 9400 jiffies busy = 24.5 %");
        var a1 = new CpuSample(1000, 2000);
        var a2 = new CpuSample(1750, 3000);
        check(P.CpuPercent(a1, a2) == 25, "CPU %: 250 of 1000 jiffies busy = 25 %");
        check(P.CpuPercent(a2, a1) is null, "CPU % from samples out of order is refused");
        var m = P.ParseMemInfo(MemInfo);
        check(m is { TotalKb: 7810000, AvailableKb: 2462000 }, "/proc/meminfo total and available");
        check(P.FormatKb(7810000 - 2462000) == "5.1 GB", "memory in use: 5.1 GB");

        // Busy apps.
        var apps = P.ParseTop(Top);
        check(apps.Count == 4, "top: four app processes (not top, kworker or system_server)");
        check(apps.Count > 0 && apps[0] is { Package: "com.instagram.android", Pid: 1234 } && apps[0].Cpu == 11.0, "top: the busiest first");
        check(apps.Count(x => x.Package == "com.android.chrome") == 1 && apps.First(x => x.Package == "com.android.chrome").Cpu == 6.2,
            "top: chrome once, with its busiest process");
        check(apps.Any(x => x.Package == "com.google.android.gms.persistent"), "top: a dotted process name counts as a package");
        check(P.ParseTop("").Count == 0, "no top output gives no apps");
        check(P.IsPackageName("com.spotify.music") && !P.IsPackageName("kworker/u16:3") && !P.IsPackageName("top") && !P.IsPackageName("a;b.c"),
            "package names only");

        // Details.
        check(P.ParseWmSize("Physical size: 1080x2400") == "1080 × 2400", "wm size");
        check(P.ParseWmSize("Physical size: 1080x2400\nOverride size: 1440x3200") == "1440 × 3200", "wm size: the override wins");
        check(P.FormatUptime("273600.12 1000.00") == "3 days 4 h" && P.FormatUptime("3720.5 1") == "1 h 2 min" && P.FormatUptime("30 1") == "1 min", "uptime");
        check(P.Prop("Pixel 7\n") == "Pixel 7" && P.Prop("") is null, "getprop value");

        // System settings.
        check(P.Setting("null") is null && P.Setting("") is null && P.Setting("600000") == "600000", "settings get: null and empty are no value");
        check(P.SettingNumber("1.0") == 1.0 && P.SettingNumber("0.5") == 0.5 && P.SettingNumber("null") is null, "settings numbers");
        check(P.SettingOn("7") == true && P.SettingOn("0") == false && P.SettingOn("null") is null, "settings on/off");
        check(P.ParseNightMode("Night mode: yes") == true && P.ParseNightMode("Night mode: auto") == false && P.ParseNightMode("Unknown command") is null, "cmd uimode night");
        check(P.ParseWifi("Wifi is enabled\nWifi scanning is always available\n") == true && P.ParseWifi("Wifi is disabled") == false && P.ParseWifi("") is null, "cmd wifi status");
        check(P.ParseTrueFalse("true\n") == true && P.ParseTrueFalse("false") == false && P.ParseTrueFalse("Unknown") is null, "cmd location is-location-enabled");
    }
}
