using Linc.Desktop.Services.Tools;

namespace Linc.ToolSim;

/// <summary>Tools › Apps: the parsers, against output captured in the formats of Android 11 and
/// Android 14 (TOOLS.md §3).</summary>
internal static class AppsTests
{
    // Android 14: base64 folders with '=' in the apk path, versionCode and uid.
    private const string List14 = """
        package:/data/app/~~Gq3uY0bQ1x2t9vZ5aR4Kkw==/com.spotify.music-dP5dVb8w6l0GzA_xN3S4cA==/base.apk=com.spotify.music versionCode:115800717 uid:10234
        package:/data/app/~~a1b2c3==/com.whatsapp-Zx9==/base.apk=com.whatsapp versionCode:242113002 uid:10198
        package:/system_ext/priv-app/SystemUIGoogle/SystemUIGoogle.apk=com.android.systemui versionCode:34 uid:10120
        package:/product/app/Chrome/Chrome.apk=com.android.chrome versionCode:653017633 uid:10141
        package:/data/app/~~x==/app.linc.android-y==/base.apk=app.linc.android versionCode:18 uid:10310
        """;

    // Android 11: older paths, no '=' inside them; one line with Windows line endings.
    private const string List11 = "package:/data/app/com.instagram.android-1/base.apk=com.instagram.android versionCode:376211042 uid:10201\r\n" +
                                  "package:/system/priv-app/Settings/Settings.apk=com.android.settings versionCode:30 uid:1000\r\n";

    private const string DiskStats = """
        Latency: 2ms [512B Data Write]
        Data-Free: 61234567K / 118012345K total = 51% free
        Package Names: ["com.spotify.music","com.whatsapp","com.android.chrome"]
        App Sizes: [412000000,98000000,380000000]
        App Data Sizes: [120000000,1100000000,50000000]
        Cache Sizes: [30000000,2000000,40000000]
        """;

    private const string Details14 = """
        Activity Resolver Table:
          Non-Data Actions:
              android.intent.action.MAIN:
        Packages:
          Package [com.spotify.music] (a1b2c3):
            userId=10234
            pkg=Package{d4e5 com.spotify.music}
            versionCode=115800717 minSdk=24 targetSdk=34
            versionName=8.9.84.594
            installerPackageName=com.android.vending
            firstInstallTime=2024-02-11 18:22:05
            lastUpdateTime=2024-10-02 09:14:41
            install permissions:
              android.permission.INTERNET: granted=true
              android.permission.WAKE_LOCK: granted=true
            User 0: ceDataInode=12345 installed=true hidden=false suspended=false stopped=false notLaunched=false enabled=0 instant=false virtual=false
              gids=[3003]
              runtime permissions:
                android.permission.POST_NOTIFICATIONS: granted=true, flags=[ USER_SET|USER_SENSITIVE_WHEN_GRANTED ]
                android.permission.RECORD_AUDIO: granted=false, flags=[ USER_SENSITIVE_WHEN_GRANTED ]
                android.permission.ACCESS_FINE_LOCATION: granted=false
                android.permission.READ_MEDIA_AUDIO: granted=true
              disabledComponents:
                com.spotify.music.SomeService
          Package [com.spotify.music] (f00):
            versionName=1.0-system-copy
        """;

    // Android 11: runtime permissions sit directly under the package, no User block before them.
    private const string Details11 = """
        Packages:
          Package [com.instagram.android] (77):
            versionName=301.0.0.29.124
            targetSdk=30
            runtime permissions:
              android.permission.CAMERA: granted=true
              android.permission.READ_CONTACTS: granted=false
            enabledComponents:
        """;

    public static void Run(Action<bool, string> check)
    {
        var list = AppsParsers.ParsePackageList(List14);
        check(list.Count == 5, "Android 14 list: every package line is read");
        var spotify = list.FirstOrDefault(p => p.Package == "com.spotify.music");
        check(spotify is { VersionCode: 115800717, Uid: 10234 } && spotify.ApkPath.EndsWith("/base.apk", StringComparison.Ordinal),
            "an apk path holding '=' keeps the package after the last '='");
        var list11 = AppsParsers.ParsePackageList(List11);
        check(list11.Count == 2 && list11[0].Package == "com.instagram.android" && list11[1].Uid == 1000, "Android 11 list with CRLF is read");
        check(AppsParsers.ParsePackageList("package:com.foo.bar\npackage:com.foo.bar\njunk line").Count == 1,
            "plain package lines are read once, junk is skipped");
        check(AppsParsers.PackageNames(List14).Contains("app.linc.android"), "PackageNames gives the set");

        var sizes = AppsParsers.ParseDiskStats(DiskStats);
        check(sizes.Count == 3 && sizes["com.whatsapp"].Total == 1_200_000_000, "diskstats arrays line up by index");
        check(AppsParsers.ParseDiskStats("Package Names: [\"a\",\"b\"]\nApp Sizes: [1]").Count == 0, "mismatched diskstats arrays give nothing, not wrong sizes");
        check(AppsParsers.ParseDiskStats("").Count == 0, "no diskstats, no sizes");

        var d = AppsParsers.ParseDetails(Details14);
        check(d.VersionName == "8.9.84.594", "versionName comes from the first package entry, not a system copy");
        check(d.TargetSdk == 34 && d.Installer == "com.android.vending", "targetSdk and installer are read");
        check(d.FirstInstall == "2024-02-11 18:22:05" && d.LastUpdate.StartsWith("2024-10-02", StringComparison.Ordinal), "install and update times are read");
        check(d.Permissions.Count == 4, "only runtime permissions are listed, not install ones");
        check(d.Permissions.Any(p => p.Name == "android.permission.RECORD_AUDIO" && !p.Granted && p.Label == "Microphone"),
            "a denied runtime permission reads as denied, with a plain label");
        check(d.Permissions.Any(p => p.Name == "android.permission.POST_NOTIFICATIONS" && p.Granted), "flags after granted= don't break it");
        var d11 = AppsParsers.ParseDetails(Details11);
        check(d11.TargetSdk == 30 && d11.Permissions.Count == 2 && d11.Permissions[0].Label == "Camera", "Android 11 details are read");
        check(AppsParsers.ParseDetails("Unable to find package: com.nope").Permissions.Count == 0, "an unknown package gives empty details");

        var paths = AppsParsers.ParseApkPaths("package:/data/app/~~a==/com.x-b==/base.apk\npackage:/data/app/~~a==/com.x-b==/split_config.arm64_v8a.apk\n");
        check(paths.Count == 2 && paths[1].EndsWith("split_config.arm64_v8a.apk", StringComparison.Ordinal), "pm path gives every split");

        check(AppsParsers.ParseLauncher("priority=0 preferredOrder=0 match=0x108000 specificIndex=-1 isDefault=false\ncom.google.android.apps.nexuslauncher/.NexusLauncherActivity\n")
              == "com.google.android.apps.nexuslauncher", "the home app is the last line's package");
        check(AppsParsers.ParseLauncher("No activity found") == "", "no home app answer gives none");

        check(AppsParsers.ProtectedReason("com.android.systemui", "") is not null, "System UI can't be removed");
        check(AppsParsers.ProtectedReason("app.linc.android", "") == "This is Linc itself.", "Linc protects itself");
        check(AppsParsers.ProtectedReason("com.sec.android.app.launcher", "com.sec.android.app.launcher") is not null, "the current launcher is protected");
        check(AppsParsers.ProtectedReason("com.spotify.music", "x") is null, "an ordinary app isn't protected");

        check(AppsParsers.PrettyName("com.spotify.music") == "Spotify", "known apps get their real name");
        check(AppsParsers.PrettyName("com.example.notes") == "Example Notes", "others drop the domain and keep two words");
        check(AppsParsers.PrettyName("com.google.android.apps.fitness") == "Fitness", "vendor words are dropped");
        check(AppsParsers.PrettyName("com.samsung.android.app.notes") == "Notes", "Samsung's vendor words are dropped");
        check(AppsParsers.PrettyName("android") == "Android", "a one-word package still has a name");

        check(AppsParsers.IsValidPackage("com.foo_bar.baz2") && !AppsParsers.IsValidPackage("com.foo; rm -rf /") &&
              !AppsParsers.IsValidPackage("") && !AppsParsers.IsValidPackage("a..b"), "only real package names are accepted");
        check(AppsParsers.PermissionLabel("com.example.permission.MAKE_COFFEE") == "Make coffee", "unknown permissions read as words");
        check(AppsParsers.FormatSize(1536) == "1.5 KB" && AppsParsers.FormatSize(734003200) == "700 MB" && AppsParsers.FormatSize(0) == "",
            "sizes read as KB/MB/GB");
        check(AppsParsers.SafeFolderName("Spotify 8.9: \"beta\"") == "Spotify 8.9_ _beta_", "folder names lose characters Windows refuses");
    }
}
