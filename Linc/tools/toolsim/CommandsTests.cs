using Linc.Desktop.Services.Tools;

namespace Linc.ToolSim;

/// <summary>Tools › Commands: the catalogue, its search, and how parameters become commands.</summary>
internal static class CommandsTests
{
    public static void Run(Action<bool, string> check)
    {
        var all = CommandCatalog.All;
        check(all.Count >= 290, $"the catalogue has {all.Count} commands (at least 290)");
        check(all.Select(e => e.Title).Distinct().Count() == all.Count, "every command title is unique");
        check(all.Select(e => e.Id).Distinct().Count() == all.Count, "every command id is unique");
        check(all.All(e => e.Title.Length > 0 && e.Description.Length > 0 && e.Keywords.Length > 0
                           && CommandCatalog.Categories.Contains(e.Category)), "every command has a title, description, keywords and a known category");
        foreach (var cat in CommandCatalog.Categories)
        {
            check(all.Any(e => e.Category == cat), $"category {cat} has commands");
        }

        // Every {placeholder} has a parameter and every parameter is used.
        var badParams = all.Where(e =>
        {
            var used = CommandCatalog.Placeholder().Matches(e.Template).Select(m => m.Groups[1].Value).ToHashSet();
            var defined = e.Params.Select(p => p.Name).ToHashSet();
            return !used.SetEquals(defined) || defined.Count != e.Params.Count;
        }).Select(e => e.Id).ToList();
        check(badParams.Count == 0, "placeholders and parameters match" + (badParams.Count > 0 ? ": " + string.Join(", ", badParams) : ""));

        // Choices are plain words (they go into commands unquoted), and defaults are valid.
        check(all.SelectMany(e => e.Params).Where(p => p.Kind == ParamKind.Choice)
                 .All(p => p.Choices is { Count: > 0 } && p.Choices.All(c => c.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_'))),
            "every choice is a plain word");
        var badDefaults = all.SelectMany(e => e.Params.Select(p => (e.Id, p)))
            .Where(x => x.p.Default.Length > 0 && x.p.Kind is not (ParamKind.PcFile or ParamKind.PcFolder)
                        && CommandCatalog.Validate(x.p, x.p.Default) is not null)
            .Select(x => $"{x.Id}.{x.p.Name}").ToList();
        check(badDefaults.Count == 0, "every default value is valid" + (badDefaults.Count > 0 ? ": " + string.Join(", ", badDefaults) : ""));

        // Nothing from TOOLS.md §13 (weakens security, or bricks the phone).
        string[] forbidden =
        [
            "package_verifier", "verifier_verify_adb_installs", "locksettings", "set-device-owner", "set-profile-owner",
            "dpm ", "hidden_api_policy", "WRITE_SECURE_SETTINGS", "SYSTEM_ALERT_WINDOW", "BIND_ACCESSIBILITY",
            "enabled_accessibility_services", "enabled_notification_listeners", "BIND_DEVICE_ADMIN", "REQUEST_INSTALL_PACKAGES",
            "fastboot", "flashing", "oem unlock", "oem_unlock", "rm -rf /", "reboot fastboot", "sideload", "--remove-all",
            "appops set", "setprop",
        ];
        var hits = all.Where(e => forbidden.Any(f => (e.Template + " " + string.Join(' ', e.Params.SelectMany(p => p.Choices ?? []))).Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(e => e.Id).ToList();
        check(hits.Count == 0, "no command weakens the phone's security or bricks it" + (hits.Count > 0 ? ": " + string.Join(", ", hits) : ""));
        check(all.First(e => e.Id == "tcpip").Tier == CommandTier.Confirm && all.Any(e => e.Id == "usb-only"), "port 5555 asks first and has its undo");

        // Bounded forms only: nothing here runs forever.
        var unbounded = all.Where(e => !e.Adb && (
            (e.Template.Contains("logcat") && !e.Template.Contains("-d") && !e.Template.Contains("logcat -c")) ||
            (System.Text.RegularExpressions.Regex.IsMatch(e.Template, @"(^|[ ;&|])top ") && !e.Template.Contains("-n ")) ||
            (e.Template.Contains("screenrecord") && !e.Template.Contains("--time-limit")) ||
            (e.Template.Contains("ping ") && !e.Template.Contains("-c "))))
            .Select(e => e.Id).ToList();
        check(unbounded.Count == 0, "every command ends on its own" + (unbounded.Count > 0 ? ": " + string.Join(", ", unbounded) : ""));
        check(all.First(e => e.Id == "screenrecord").Params[0].Max <= 180, "screen recording is capped at 3 minutes");

        // Tiers where they matter.
        foreach (var id in new[] { "uninstall", "uninstall-keep", "clear-data", "delete-file", "delete-folder" })
        {
            check(all.First(e => e.Id == id).Tier == CommandTier.Destructive, $"{id} is marked can't-be-undone");
        }
        foreach (var id in new[] { "reboot", "reboot-recovery", "reboot-bootloader", "power-off", "wifi-onoff", "airplane", "setting-put", "install", "log-clear" })
        {
            check(all.First(e => e.Id == id).Tier != CommandTier.Safe, $"{id} asks first");
        }

        // Paths that delete or move must stay inside the user's storage.
        var deletePath = all.First(e => e.Id == "delete-folder").Params[0];
        foreach (var bad in new[] { "/", "/sdcard", "/sdcard/", "/storage/emulated/0", "/storage/emulated/0/", "/system/app", "/data",
                                    "/sdcard/../system", "sdcard/x", "/storage/self/primary", "/storage/emulated/x/y" })
        {
            check(CommandCatalog.Validate(deletePath, bad) is not null, $"delete refuses {bad}");
        }
        foreach (var ok in new[] { "/sdcard/Download/old", "/storage/emulated/0/DCIM/x", "/storage/1234-ABCD/Music", "/storage/self/primary/Download" })
        {
            check(CommandCatalog.Validate(deletePath, ok) is null, $"delete accepts {ok}");
        }

        // Package names, numbers, hosts, settings keys.
        var pkg = new CommandParam("package", "App", ParamKind.Package);
        check(CommandCatalog.Validate(pkg, "com.spotify.music") is null, "a package name is accepted");
        foreach (var bad in new[] { "spotify", "com.x; rm -rf /sdcard", "com.x'", "$(id)", "-com.x", "com..x", "com.x\nreboot" })
        {
            check(CommandCatalog.Validate(pkg, bad) is not null, $"package {bad.Replace("\n", "\\n")} is refused");
        }
        var num = new CommandParam("n", "N", ParamKind.Number, Min: 1, Max: 180);
        check(CommandCatalog.Validate(num, "30") is null && CommandCatalog.Validate(num, "0") is not null
              && CommandCatalog.Validate(num, "181") is not null && CommandCatalog.Validate(num, "3;reboot") is not null
              && CommandCatalog.Validate(num, "-5") is not null, "numbers are checked against their range");
        var key = all.First(e => e.Id == "setting-put").Params[1];
        check(CommandCatalog.Validate(key, "screen_off_timeout") is null, "an everyday setting can be changed");
        check(CommandCatalog.Validate(key, "package_verifier_enable") is not null && CommandCatalog.Validate(key, "adb_enabled") is not null
              && CommandCatalog.Validate(key, "enabled_accessibility_services") is not null, "settings that weaken security or cut Linc's link are refused");
        var host = new CommandParam("host", "Host", ParamKind.Host);
        check(CommandCatalog.Validate(host, "192.168.1.20") is null && CommandCatalog.Validate(host, "8.8.8.8;reboot") is not null
              && CommandCatalog.Validate(host, "-f") is not null, "host names are checked");
        var url = new CommandParam("url", "Url", ParamKind.Url);
        check(CommandCatalog.Validate(url, "https://example.com/a?b=1&c=2") is null && CommandCatalog.Validate(url, "javascript:alert(1)") is not null
              && CommandCatalog.Validate(url, "https://x.com/'$(reboot)'") is not null, "web addresses are checked");

        // Protected apps can't be removed, disabled or wiped.
        var uninstall = all.First(e => e.Id == "uninstall");
        check(CommandCatalog.Fill(uninstall, new Dictionary<string, string> { ["package"] = "com.android.systemui" }) is null
              && CommandCatalog.ValidateEntry(uninstall, new Dictionary<string, string> { ["package"] = "app.linc.android" }) is not null,
            "the phone's own core apps and Linc can't be uninstalled from here");
        check(CommandCatalog.Fill(all.First(e => e.Id == "open-app"), new Dictionary<string, string> { ["package"] = "com.android.settings" }) is not null,
            "opening a core app is still fine");

        // Filling: plain values stay plain, anything else becomes one quoted word.
        var filled = CommandCatalog.Fill(uninstall, new Dictionary<string, string> { ["package"] = "com.example.app" });
        check(filled?.Shell == "pm uninstall com.example.app" && filled.Display == "adb shell pm uninstall com.example.app", "uninstall fills to a plain command");
        var find = all.First(e => e.Id == "find-files");
        string[] nasty = ["it's", "$(reboot)", "a; reboot", "`reboot`", "a && reboot", "a | sh", "*", "~", "a b", "\"x\"", "#x", "a>b", "!x", "-delete"];
        foreach (var v in nasty)
        {
            var f = CommandCatalog.Fill(find, new Dictionary<string, string> { ["path"] = "/sdcard", ["pattern"] = v });
            check(f?.Shell is { } s && s.Contains(" -iname " + AdbQuote.Arg(v) + " "), $"typed text {v} goes in as one quoted word");
        }
        var weirdPath = CommandCatalog.Fill(all.First(e => e.Id == "delete-file"),
            new Dictionary<string, string> { ["path"] = "/sdcard/Download/a'b $(reboot).pdf" });
        check(weirdPath?.Shell == "rm " + AdbQuote.Arg("/sdcard/Download/a'b $(reboot).pdf"), "a path with odd characters is quoted");
        check(CommandCatalog.Fill(find, new Dictionary<string, string> { ["path"] = "/sdcard", ["pattern"] = "a\nreboot" }) is null, "a second line is refused");
        var typed = CommandCatalog.Fill(all.First(e => e.Id == "type-text"), new Dictionary<string, string> { ["text"] = "hi there" });
        check(typed?.Shell == "input text 'hi%sthere'", "typed text uses input text's own spaces");
        var sms = CommandCatalog.Fill(all.First(e => e.Id == "text-message"), new Dictionary<string, string> { ["number"] = "+15551234567", ["message"] = "On my way; ok?" });
        check(sms?.Shell == "am start -a android.intent.action.SENDTO -d smsto:+15551234567 --es sms_body 'On my way; ok?'", "a text message is filled safely");
        var size = CommandCatalog.Fill(all.First(e => e.Id == "set-screen-size"), new Dictionary<string, string> { ["width"] = "1080", ["height"] = "2400" });
        check(size?.Shell == "wm size 1080x2400", "two numbers fill one word");
        check(CommandCatalog.Fill(all.First(e => e.Id == "set-screen-size"), new Dictionary<string, string> { ["width"] = "1080" }) is null, "a missing value leaves nothing to run");

        // adb-level commands: each value one argument.
        var dir = Path.Combine(Path.GetTempPath(), "linc-toolsim-cmd " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var apk = Path.Combine(dir, "my app.apk");
            File.WriteAllText(apk, "x");
            var install = CommandCatalog.Fill(all.First(e => e.Id == "install"), new Dictionary<string, string> { ["apk"] = apk });
            check(install?.AdbArgs is { Count: 3 } a && a[0] == "install" && a[1] == "-r" && a[2] == apk, "install passes the APK as one argument");
            check(install?.Display == $"adb install -r \"{apk}\"", "a PC path with a space is shown in quotes");
            var txt = Path.Combine(dir, "notes.txt");
            File.WriteAllText(txt, "x");
            check(CommandCatalog.Fill(all.First(e => e.Id == "install"), new Dictionary<string, string> { ["apk"] = txt }) is null, "install wants an .apk");
            check(CommandCatalog.Fill(all.First(e => e.Id == "install"), new Dictionary<string, string> { ["apk"] = Path.Combine(dir, "missing.apk") }) is null,
                "install wants a file that exists");
            var pull = CommandCatalog.Fill(all.First(e => e.Id == "pull"), new Dictionary<string, string> { ["path"] = "/sdcard/DCIM/Camera", ["folder"] = dir });
            check(pull?.AdbArgs is { Count: 3 } p && p[1] == "/sdcard/DCIM/Camera" && p[2] == dir, "pull passes both paths as arguments");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
        var connect = CommandCatalog.Fill(all.First(e => e.Id == "wireless-connect"), new Dictionary<string, string> { ["host"] = "192.168.1.20", ["port"] = "5555" });
        check(connect?.AdbArgs is { Count: 2 } c && c[1] == "192.168.1.20:5555" && connect.Display == "adb connect 192.168.1.20:5555", "connect joins host and port");
        var reboot = CommandCatalog.Fill(all.First(e => e.Id == "reboot"), new Dictionary<string, string>());
        check(reboot?.Display == "adb reboot", "a command with no parameters fills as is");

        // Search.
        string First(string q, string? cat = null) => CommandCatalog.Search(q, cat).FirstOrDefault()?.Id ?? "(none)";
        check(First("install app") == "install", $"\"install app\" finds Install first ({First("install app")})");
        check(First("install") == "install", $"\"install\" finds Install first ({First("install")})");
        check(First("reboot") == "reboot", $"\"reboot\" finds Reboot first ({First("reboot")})");
        check(First("screen size") == "screen-size", $"\"screen size\" finds the screen size first ({First("screen size")})");
        check(First("wifi").StartsWith("wifi-", StringComparison.Ordinal) || First("wifi") == "settings-wifi", $"\"wifi\" finds a Wi-Fi command first ({First("wifi")})");
        check(First("Wi-Fi") == First("wifi"), "Wi-Fi and wifi search the same");
        check(First("battery").StartsWith("battery", StringComparison.Ordinal), $"\"battery\" finds a battery command first ({First("battery")})");
        check(First("uninstall") == "uninstall", $"\"uninstall\" finds Uninstall first ({First("uninstall")})");
        check(First("dark") == "dark-mode", $"\"dark\" finds Dark mode first ({First("dark")})");
        check(First("screenshot") == "screenshot", $"\"screenshot\" finds the screenshot first ({First("screenshot")})");
        check(First("ip") == "ip-address", $"\"ip\" finds the IP address first ({First("ip")})");
        check(CommandCatalog.Search("pm uninstall").Any(e => e.Id == "uninstall"), "the command itself is searchable");
        // The second half of the catalogue (1.0.0.19) is just as findable.
        (string Query, string[] Ids)[] more =
        [
            ("nfc", ["nfc"]), ("dns", ["dns-show"]), ("signal", ["wifi-signal", "mobile-signal"]), ("cpu", ["cpu-speed", "chip"]),
            ("refresh rate", ["refresh-rate"]), ("doze", ["doze-state", "doze-now"]), ("md5", ["file-md5"]),
            ("default browser", ["set-default-browser"]), ("keyboard", ["keyboard-list"]), ("selinux", ["selinux"]),
            ("kernel", ["kernel"]), ("hotspot", ["hotspot-status"]), ("checksum", ["file-md5", "file-sha256"]),
            ("bold", ["bold-text"]), ("scroll down", ["scroll-down"]), ("battery health", ["battery-health"]),
        ];
        foreach (var (query, ids) in more)
        {
            check(ids.Contains(First(query)), $"\"{query}\" finds {string.Join(" or ", ids)} first ({First(query)})");
        }
        // Commands that change things for an app refuse the phone's own core apps.
        check(CommandCatalog.Fill(all.First(e => e.Id == "app-bucket-set"), new Dictionary<string, string> { ["package"] = "com.android.systemui", ["bucket"] = "restricted" }) is null,
            "restricting a core app is refused");
        check(CommandCatalog.Fill(all.First(e => e.Id == "app-cache-folder"), new Dictionary<string, string> { ["package"] = "com.x/../../" }) is null,
            "the cache folder only takes a real package name");
        var dns = CommandCatalog.Fill(all.First(e => e.Id == "dns-set"), new Dictionary<string, string> { ["host"] = "dns.google" });
        check(dns?.Shell == "settings put global private_dns_mode hostname && settings put global private_dns_specifier dns.google", "private DNS takes one host name");
        check(CommandCatalog.Fill(all.First(e => e.Id == "dns-set"), new Dictionary<string, string> { ["host"] = "x; reboot" }) is null, "a DNS host can't carry more command");
        var level = CommandCatalog.Fill(all.First(e => e.Id == "log-level"), new Dictionary<string, string> { ["level"] = "E" });
        check(level?.Shell == "logcat -d -t 300 '*:'E", "the log level goes in after the quoted *:");
        var grep = CommandCatalog.Fill(all.First(e => e.Id == "log-search"), new Dictionary<string, string> { ["text"] = "-v" });
        check(grep?.Shell is { } g && g.Contains("-e '-v'"), "a search word that looks like an option stays a word");

        check(CommandCatalog.Search("zzzz nothing").Count == 0, "a search with no match is empty");
        check(CommandCatalog.Search("", CommandCatalog.Power).All(e => e.Category == CommandCatalog.Power)
              && CommandCatalog.Search("", null).Count == all.Count, "an empty search lists the category, or everything");
        check(CommandCatalog.Search("reboot", CommandCatalog.Apps).Count == 0, "a category filters the search");

        Console.WriteLine("    commands per category: " + string.Join(", ", CommandCatalog.Categories.Select(c => $"{c} {all.Count(e => e.Category == c)}")));
    }
}
