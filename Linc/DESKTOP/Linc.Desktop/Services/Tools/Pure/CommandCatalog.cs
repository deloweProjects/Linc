using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Linc.Desktop.Services.Tools;

/// <summary>How careful a command is (PLAN.md §2 rule 4): one click, ask first, or can't be undone.</summary>
public enum CommandTier { Safe, Confirm, Destructive }

/// <summary>What a parameter holds, which decides how it is checked and how it goes into the command.</summary>
public enum ParamKind
{
    /// <summary>Free text; always quoted.</summary>
    Text,
    /// <summary>Text the phone types with <c>input text</c> (printable ASCII; see <see cref="AdbQuote.InputTextChunks"/>).</summary>
    TypedText,
    /// <summary>An app's package name, e.g. com.spotify.music.</summary>
    Package,
    /// <summary>A whole number between <see cref="CommandParam.Min"/> and <see cref="CommandParam.Max"/>.</summary>
    Number,
    /// <summary>One of <see cref="CommandParam.Choices"/>.</summary>
    Choice,
    /// <summary>A path on the phone (starts with /).</summary>
    PhonePath,
    /// <summary>A path inside the phone's own storage (/sdcard/… or /storage/…), never a storage root.
    /// Used by everything that deletes, moves or overwrites.</summary>
    UserPath,
    /// <summary>A file on this PC (Browse… picks one).</summary>
    PcFile,
    /// <summary>A folder on this PC (Browse… picks one).</summary>
    PcFolder,
    /// <summary>A settings key or similar identifier (letters, digits, dot, underscore).</summary>
    Name,
    /// <summary>A web address starting with http:// or https://.</summary>
    Url,
    /// <summary>An IP address or host name.</summary>
    Host,
    /// <summary>A phone number (digits, +, *, #).</summary>
    PhoneNumber,
}

/// <summary>One input of a command. <see cref="Name"/> is the <c>{name}</c> in the template.</summary>
public sealed record CommandParam(
    string Name,
    string Label,
    ParamKind Kind,
    string Example = "",
    string Default = "",
    bool Required = true,
    int Min = 0,
    int Max = int.MaxValue,
    IReadOnlyList<string>? Choices = null,
    string? Extension = null);

/// <summary>
/// One everyday command. <see cref="Template"/> is what follows <c>adb shell</c>, or, with
/// <see cref="Adb"/>, what follows <c>adb</c> (each space-separated word one argument).
/// <see cref="MinSdk"/> greys it out on older Android; <see cref="UsbOnly"/> over wireless.
/// </summary>
public sealed record CommandEntry(
    string Id,
    string Title,
    string Description,
    string Category,
    string Keywords,
    string Template,
    IReadOnlyList<CommandParam> Params,
    CommandTier Tier = CommandTier.Safe,
    bool Adb = false,
    int MinSdk = 0,
    bool UsbOnly = false,
    int TimeoutSeconds = 30);

/// <summary>A command filled in: what runs, and how it reads in a terminal.</summary>
public sealed record FilledCommand(string? Shell, IReadOnlyList<string>? AdbArgs, string Display);

/// <summary>
/// The Tools › Commands catalogue (the owner: "search a command, e.g. install app, with a
/// description, parameters, a run button and a copy button, for as many everyday adb commands as
/// possible"), with its search and the one place a parameter turns into part of a command.
/// Dependency-free, so tools/toolsim checks every entry. Nothing from TOOLS.md §13 belongs here.
/// </summary>
public static partial class CommandCatalog
{
    public const string Apps = "Apps", Files = "Files", Screen = "Screen", Power = "Power", Network = "Network",
        Settings = "Settings", Info = "Info", Debug = "Debug";

    public static IReadOnlyList<string> Categories { get; } = [Apps, Files, Screen, Power, Network, Settings, Info, Debug];

    public static string GlyphFor(string category) => category switch
    {
        Apps => "\uE71D",
        Files => "\uE8B7",
        Screen => "\uE7F4",
        Power => "\uE7E8",
        Network => "\uE701",
        Settings => "\uE713",
        Info => "\uE946",
        _ => "\uEBE8",
    };

    public static string TierLabel(CommandTier tier) => tier switch
    {
        CommandTier.Confirm => "Asks first",
        CommandTier.Destructive => "Can't be undone",
        _ => "Safe",
    };

    /// <summary>Apps nothing here may remove, disable or wipe (TOOLS.md §3 and §13).</summary>
    public static IReadOnlySet<string> ProtectedPackages { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "android", "com.android.systemui", "com.android.settings", "com.android.phone",
        "com.android.packageinstaller", "com.google.android.packageinstaller",
        "com.android.providers.telephony", "com.android.providers.settings", "com.android.shell",
        "com.google.android.gms", "com.android.vending", "app.linc.android",
    };

    /// <summary>Settings keys Settings › "Change a setting" refuses (security, or Linc's own line).</summary>
    public static IReadOnlyList<string> ForbiddenSettingKeys { get; } =
    [
        "package_verifier", "verifier_verify_adb_installs", "hidden_api_policy", "enabled_accessibility_services",
        "accessibility_enabled", "enabled_notification_listeners", "adb_enabled", "adb_wifi_enabled",
        "development_settings_enabled", "install_non_market_apps", "lock_screen", "lockscreen", "device_provisioned",
        "user_setup_complete", "default_input_method", "enabled_input_methods",
    ];

    /// <summary>Permissions "Allow" and "Take away" offer: runtime permissions only (TOOLS.md §13
    /// rules out handing other apps special powers).</summary>
    public static IReadOnlyList<string> RuntimePermissions { get; } =
    [
        "android.permission.CAMERA", "android.permission.RECORD_AUDIO", "android.permission.ACCESS_FINE_LOCATION",
        "android.permission.ACCESS_COARSE_LOCATION", "android.permission.ACCESS_BACKGROUND_LOCATION",
        "android.permission.READ_CONTACTS", "android.permission.WRITE_CONTACTS", "android.permission.READ_CALENDAR",
        "android.permission.WRITE_CALENDAR", "android.permission.READ_SMS", "android.permission.SEND_SMS",
        "android.permission.CALL_PHONE", "android.permission.READ_PHONE_STATE", "android.permission.READ_CALL_LOG",
        "android.permission.POST_NOTIFICATIONS", "android.permission.READ_MEDIA_IMAGES",
        "android.permission.READ_MEDIA_VIDEO", "android.permission.READ_MEDIA_AUDIO",
        "android.permission.READ_EXTERNAL_STORAGE", "android.permission.BODY_SENSORS",
        "android.permission.ACTIVITY_RECOGNITION", "android.permission.BLUETOOTH_CONNECT",
        "android.permission.BLUETOOTH_SCAN", "android.permission.NEARBY_WIFI_DEVICES",
    ];

    // ---- the catalogue ----------------------------------------------------------------------

    private static CommandParam Pkg(string label = "App (package name)") =>
        new("package", label, ParamKind.Package, "com.spotify.music");

    private static CommandParam OnOff(string name = "state", string label = "On or off", params string[] choices) =>
        new(name, label, ParamKind.Choice, Choices: choices, Default: choices[0]);

    private static readonly (string Id, string Title, string Action, string Keywords)[] SettingsScreens =
    [
        ("settings-home", "Open Settings", "android.settings.SETTINGS", "settings main menu"),
        ("settings-wifi", "Open Wi-Fi settings", "android.settings.WIFI_SETTINGS", "wifi wireless network internet"),
        ("settings-bluetooth", "Open Bluetooth settings", "android.settings.BLUETOOTH_SETTINGS", "bluetooth pair headphones"),
        ("settings-developer", "Open Developer options", "android.settings.APPLICATION_DEVELOPMENT_SETTINGS", "developer options usb debugging"),
        ("settings-battery", "Open battery settings", "android.intent.action.POWER_USAGE_SUMMARY", "battery usage power"),
        ("settings-display", "Open display settings", "android.settings.DISPLAY_SETTINGS", "display screen brightness"),
        ("settings-sound", "Open sound settings", "android.settings.SOUND_SETTINGS", "sound volume ringtone"),
        ("settings-location", "Open location settings", "android.settings.LOCATION_SOURCE_SETTINGS", "location gps"),
        ("settings-date", "Open date and time settings", "android.settings.DATE_SETTINGS", "date time clock timezone"),
        ("settings-storage", "Open storage settings", "android.settings.INTERNAL_STORAGE_SETTINGS", "storage space"),
        ("settings-apps", "Open the app list", "android.settings.MANAGE_APPLICATIONS_SETTINGS", "apps applications list"),
        ("settings-notifications", "Open notification settings", "android.settings.NOTIFICATION_SETTINGS", "notifications alerts"),
        ("settings-accessibility", "Open accessibility settings", "android.settings.ACCESSIBILITY_SETTINGS", "accessibility talkback"),
        ("settings-network", "Open mobile network settings", "android.settings.NETWORK_OPERATOR_SETTINGS", "mobile network sim carrier data"),
    ];

    private static readonly (string Id, string Title, string Key, string Keywords)[] Keys =
    [
        ("key-home", "Press Home", "KEYCODE_HOME", "home button key"),
        ("key-back", "Press Back", "KEYCODE_BACK", "back button key"),
        ("key-recents", "Show recent apps", "KEYCODE_APP_SWITCH", "recents overview multitask key"),
        ("key-power", "Press the power button", "KEYCODE_POWER", "power button key screen"),
        ("key-volume-up", "Volume up", "KEYCODE_VOLUME_UP", "volume louder key sound"),
        ("key-volume-down", "Volume down", "KEYCODE_VOLUME_DOWN", "volume quieter key sound"),
        ("key-mute", "Mute", "KEYCODE_VOLUME_MUTE", "mute silence volume key sound"),
        ("key-play-pause", "Play or pause music", "KEYCODE_MEDIA_PLAY_PAUSE", "media music play pause key"),
        ("key-next", "Next track", "KEYCODE_MEDIA_NEXT", "media music next skip key"),
        ("key-previous", "Previous track", "KEYCODE_MEDIA_PREVIOUS", "media music previous back key"),
        ("key-enter", "Press Enter", "KEYCODE_ENTER", "enter return key"),
        ("key-delete", "Press Backspace", "KEYCODE_DEL", "backspace delete key"),
    ];

    private static readonly string[] KeyNames =
    [
        "KEYCODE_HOME", "KEYCODE_BACK", "KEYCODE_APP_SWITCH", "KEYCODE_POWER", "KEYCODE_WAKEUP", "KEYCODE_SLEEP",
        "KEYCODE_VOLUME_UP", "KEYCODE_VOLUME_DOWN", "KEYCODE_VOLUME_MUTE", "KEYCODE_MEDIA_PLAY_PAUSE",
        "KEYCODE_MEDIA_NEXT", "KEYCODE_MEDIA_PREVIOUS", "KEYCODE_ENTER", "KEYCODE_DEL", "KEYCODE_TAB",
        "KEYCODE_ESCAPE", "KEYCODE_DPAD_UP", "KEYCODE_DPAD_DOWN", "KEYCODE_DPAD_LEFT", "KEYCODE_DPAD_RIGHT",
        "KEYCODE_DPAD_CENTER", "KEYCODE_CAMERA", "KEYCODE_SEARCH", "KEYCODE_MENU", "KEYCODE_NOTIFICATION",
    ];

    private const string DemoOn =
        "settings put global sysui_demo_allowed 1" +
        " && am broadcast -a com.android.systemui.demo -e command enter" +
        " && am broadcast -a com.android.systemui.demo -e command clock -e hhmm 1200" +
        " && am broadcast -a com.android.systemui.demo -e command battery -e level 100 -e plugged false" +
        " && am broadcast -a com.android.systemui.demo -e command network -e wifi show -e level 4" +
        " && am broadcast -a com.android.systemui.demo -e command notifications -e visible false";

    private const string UnlockSwipe =
        "s=$(wm size | tail -n 1 | cut -d ' ' -f 3); w=${s%x*}; h=${s#*x};" +
        " input swipe $((w/2)) $((h*4/5)) $((w/2)) $((h/4)) 250";

    public static IReadOnlyList<CommandEntry> All { get; } = Build();

    private static List<CommandEntry> Build()
    {
        var list = new List<CommandEntry>
        {
            // ---- Apps ---------------------------------------------------------------------
            new("install", "Install an app (APK)", "Installs an APK from this PC on the phone. If the app is already there, it is updated and keeps its data.",
                Apps, "install app apk add setup sideload update", "install -r {apk}",
                [new("apk", "APK file on this PC", ParamKind.PcFile, @"C:\Downloads\app.apk", Extension: ".apk")],
                CommandTier.Confirm, Adb: true, TimeoutSeconds: 180),
            new("install-downgrade", "Install an older version of an app", "Installs an APK even when the phone has a newer version, keeping the app's data. Use it to go back after a bad update.",
                Apps, "install downgrade older version rollback apk", "install -r -d {apk}",
                [new("apk", "APK file on this PC", ParamKind.PcFile, @"C:\Downloads\app.apk", Extension: ".apk")],
                CommandTier.Confirm, Adb: true, TimeoutSeconds: 180),
            new("install-grant", "Install an app and allow its permissions", "Installs an APK and allows every permission it asks for, so it doesn't ask on first start.",
                Apps, "install apk permissions grant all", "install -r -g {apk}",
                [new("apk", "APK file on this PC", ParamKind.PcFile, @"C:\Downloads\app.apk", Extension: ".apk")],
                CommandTier.Confirm, Adb: true, TimeoutSeconds: 180),
            new("uninstall", "Uninstall an app", "Removes an app you installed, with its data. To keep the data, use \"Uninstall an app but keep its data\".",
                Apps, "uninstall remove delete app", "pm uninstall {package}", [Pkg()], CommandTier.Destructive),
            new("uninstall-keep", "Uninstall an app but keep its data", "Removes an app and keeps its data on the phone, so installing it again picks up where it was.",
                Apps, "uninstall remove keep data app", "pm uninstall -k {package}", [Pkg()], CommandTier.Destructive),
            new("remove-preinstalled", "Remove a preinstalled app", "Removes an app that came with the phone for you only. It can be put back with \"Bring back a removed app\".",
                Apps, "debloat remove preinstalled system bloatware app", "pm uninstall -k --user 0 {package}", [Pkg()], CommandTier.Confirm),
            new("restore-app", "Bring back a removed app", "Puts back a preinstalled app that was removed with \"Remove a preinstalled app\".",
                Apps, "restore reinstall bring back preinstalled system app", "cmd package install-existing {package}", [Pkg()]),
            new("list-apps", "List the apps you installed", "Shows the package name of every app you installed yourself.",
                Apps, "list apps installed packages user", "pm list packages -3", []),
            new("list-system-apps", "List the system apps", "Shows the apps that came with the phone.",
                Apps, "list system apps preinstalled packages", "pm list packages -s", []),
            new("list-disabled-apps", "List the turned-off apps", "Shows apps that are disabled.",
                Apps, "list disabled turned off apps packages", "pm list packages -d", []),
            new("find-app", "Find an app's package name", "Shows the package names that contain a word, so you can use them in other commands.",
                Apps, "find search app package name", "pm list packages {word}",
                [new("word", "Part of the name", ParamKind.Text, "spotify")]),
            new("count-apps", "Count the apps", "Shows how many apps are on the phone, system apps included.",
                Apps, "count number apps how many", "pm list packages | wc -l", []),
            new("app-version", "Show an app's version", "Shows the version, and when the app was installed and last updated.",
                Apps, "version app installed updated date", "dumpsys package {package} | grep -E 'versionName|versionCode|firstInstallTime|lastUpdateTime'", [Pkg()]),
            new("open-app", "Open an app", "Starts an app on the phone, as if you tapped its icon.",
                Apps, "open launch start run app", "monkey -p {package} -c android.intent.category.LAUNCHER 1", [Pkg()]),
            new("stop-app", "Force stop an app", "Closes an app completely. Use it when an app is stuck.",
                Apps, "stop close kill force stop app frozen stuck", "am force-stop {package}", [Pkg()]),
            new("close-background", "Close background apps", "Ends apps running in the background to free memory. Apps on screen keep running.",
                Apps, "close kill background apps memory ram free", "am kill-all", []),
            new("clear-data", "Clear an app's data", "Wipes an app's data and settings, as if it was just installed. Sign-ins and saved files inside the app are lost.",
                Apps, "clear data reset app wipe storage", "pm clear {package}", [Pkg()], CommandTier.Destructive),
            new("clear-cache", "Free up cache space", "Asks Android to clear app caches until there is plenty of free space. App data stays.",
                Apps, "clear cache free space storage trim", "pm trim-caches 999G", [], CommandTier.Confirm, TimeoutSeconds: 120),
            new("disable-app", "Turn off an app", "Disables an app: it disappears and can't run until you turn it on again.",
                Apps, "disable turn off freeze app", "pm disable-user --user 0 {package}", [Pkg()], CommandTier.Confirm),
            new("enable-app", "Turn an app back on", "Enables an app that was turned off.",
                Apps, "enable turn on unfreeze app", "pm enable {package}", [Pkg()]),
            new("grant-permission", "Allow a permission", "Gives an app one permission, such as the camera or location.",
                Apps, "grant allow permission camera location microphone", "pm grant {package} {permission}",
                [Pkg(), new("permission", "Permission", ParamKind.Choice, Choices: RuntimePermissions, Default: RuntimePermissions[0])]),
            new("revoke-permission", "Take away a permission", "Removes one permission from an app. It asks again next time it needs it.",
                Apps, "revoke remove deny permission camera location microphone", "pm revoke {package} {permission}",
                [Pkg(), new("permission", "Permission", ParamKind.Choice, Choices: RuntimePermissions, Default: RuntimePermissions[0])]),
            new("list-permissions", "Show an app's permissions", "Lists the permissions an app has, and whether each is allowed.",
                Apps, "permissions list granted app", "dumpsys package {package} | grep -E 'permission.*granted='", [Pkg()]),
            new("apk-path", "Show where an app's APK is", "Shows the path of an app's APK files on the phone.",
                Apps, "apk path location file app", "pm path {package}", [Pkg()]),
            new("default-app", "Show the default app", "Shows which app is the default browser, texting app, phone app, home screen or assistant.",
                Apps, "default app browser sms dialer home launcher assistant role", "cmd role get-role-holders {role}",
                [new("role", "Which default", ParamKind.Choice, Default: "android.app.role.BROWSER", Choices:
                    ["android.app.role.BROWSER", "android.app.role.SMS", "android.app.role.DIALER", "android.app.role.HOME", "android.app.role.ASSISTANT"])]),
            new("app-on-screen", "Which app is open now", "Shows the app and screen in front right now.",
                Apps, "current app foreground activity on screen", "dumpsys activity activities | grep -E 'topResumedActivity|mResumedActivity'", []),
            new("app-info", "Open an app's info screen", "Opens the app's page in Settings on the phone (storage, permissions, battery).",
                Apps, "app info settings details", "am start -a android.settings.APPLICATION_DETAILS_SETTINGS -d package:{package}", [Pkg()]),
            new("app-background-on", "Let an app run in the background", "Takes an app off battery optimisation, so it isn't paused in the background.",
                Apps, "battery optimisation optimization background unrestricted whitelist doze", "dumpsys deviceidle whitelist +{package}", [Pkg()], CommandTier.Confirm),
            new("app-background-off", "Let battery saving pause an app again", "Puts an app back under battery optimisation.",
                Apps, "battery optimisation optimization background restricted whitelist doze", "dumpsys deviceidle whitelist -{package}", [Pkg()]),
            new("app-speed-up", "Speed up an app", "Compiles an app fully ahead of time, so it starts and runs faster. Takes a minute.",
                Apps, "compile speed optimise optimize faster app dex", "cmd package compile -m speed -f {package}", [Pkg()], TimeoutSeconds: 300),

            // ---- Files --------------------------------------------------------------------
            new("push", "Send a file to the phone", "Copies a file from this PC to a folder on the phone.",
                Files, "send copy push upload file to phone transfer", "push {file} {folder}",
                [new("file", "File on this PC", ParamKind.PcFile, @"C:\Pictures\photo.jpg"),
                 new("folder", "Folder on the phone", ParamKind.UserPath, "/sdcard/Download/", Default: "/sdcard/Download/")],
                Adb: true, TimeoutSeconds: 600),
            new("pull", "Copy a file from the phone", "Copies a file or folder from the phone to a folder on this PC.",
                Files, "copy pull download save file from phone transfer backup", "pull {path} {folder}",
                [new("path", "File or folder on the phone", ParamKind.PhonePath, "/sdcard/DCIM/Camera"),
                 new("folder", "Folder on this PC", ParamKind.PcFolder, @"D:\Phone backup")],
                Adb: true, TimeoutSeconds: 600),
            new("list-folder", "List a folder", "Shows what is in a folder on the phone, with sizes and dates.",
                Files, "list folder files directory ls browse", "ls -la {path}",
                [new("path", "Folder on the phone", ParamKind.PhonePath, "/sdcard", Default: "/sdcard")]),
            new("free-space", "Show free space", "Shows how much storage is used and free.",
                Files, "free space storage disk full", "df -h /data /sdcard", []),
            new("folder-size", "Show a folder's size", "Shows how much space a folder takes up.",
                Files, "folder size space usage du", "du -sh {path}",
                [new("path", "Folder on the phone", ParamKind.PhonePath, "/sdcard/DCIM", Default: "/sdcard/DCIM")], TimeoutSeconds: 120),
            new("biggest-folders", "Find what takes up space", "Lists the biggest folders in the phone's storage.",
                Files, "biggest largest folders space storage full", "du -d 1 -k /sdcard 2>/dev/null | sort -n -r | head -n 15", [], TimeoutSeconds: 180),
            new("find-files", "Find files by name", "Searches a folder for files whose names match, like *.pdf.",
                Files, "find search files name pdf", "find {path} -iname {pattern} 2>/dev/null | head -n 200",
                [new("path", "Where to look", ParamKind.PhonePath, "/sdcard", Default: "/sdcard"),
                 new("pattern", "Name", ParamKind.Text, "*.pdf", Default: "*.pdf")], TimeoutSeconds: 120),
            new("recent-downloads", "Show recent downloads", "Lists the newest files in the Download folder.",
                Files, "downloads recent newest files", "ls -lt /sdcard/Download | head -n 30", []),
            new("recent-photos", "Show recent photos", "Lists the newest photos in the camera folder.",
                Files, "photos pictures camera recent newest dcim", "ls -lt /sdcard/DCIM/Camera | head -n 30", []),
            new("read-file", "Show a text file", "Shows the first 200 lines of a text file on the phone.",
                Files, "read show text file cat view", "head -n 200 {path}",
                [new("path", "File on the phone", ParamKind.PhonePath, "/sdcard/Download/notes.txt")]),
            new("make-folder", "Make a folder", "Creates a folder on the phone (and any folders above it that are missing).",
                Files, "make create new folder directory mkdir", "mkdir -p {path}",
                [new("path", "New folder", ParamKind.UserPath, "/sdcard/Linc")]),
            new("move-file", "Move or rename a file", "Moves a file or folder on the phone, or gives it a new name.",
                Files, "move rename file folder mv", "mv {from} {to}",
                [new("from", "From", ParamKind.UserPath, "/sdcard/Download/old.pdf"),
                 new("to", "To", ParamKind.UserPath, "/sdcard/Documents/new.pdf")], CommandTier.Confirm),
            new("copy-file", "Copy a file on the phone", "Makes a copy of a file or folder on the phone.",
                Files, "copy duplicate file folder cp", "cp -r {from} {to}",
                [new("from", "From", ParamKind.UserPath, "/sdcard/DCIM/Camera"),
                 new("to", "To", ParamKind.UserPath, "/sdcard/Backup/Camera")], TimeoutSeconds: 600),
            new("delete-file", "Delete a file", "Deletes one file from the phone's storage.",
                Files, "delete remove erase file rm", "rm {path}",
                [new("path", "File on the phone", ParamKind.UserPath, "/sdcard/Download/old.pdf")], CommandTier.Destructive),
            new("delete-folder", "Delete a folder", "Deletes a folder and everything in it from the phone's storage.",
                Files, "delete remove erase folder directory rm", "rm -r {path}",
                [new("path", "Folder on the phone", ParamKind.UserPath, "/sdcard/Download/Old stuff")], CommandTier.Destructive, TimeoutSeconds: 120),

            // ---- Screen and keys ----------------------------------------------------------
            new("screenshot", "Take a screenshot on the phone", "Saves a screenshot in the phone's Pictures/Screenshots folder.",
                Screen, "screenshot screen capture picture", "mkdir -p /sdcard/Pictures/Screenshots && screencap -p /sdcard/Pictures/Screenshots/linc-$(date +%Y%m%d-%H%M%S).png", []),
            new("screenrecord", "Record the screen", "Records the phone's screen into the Movies folder for the number of seconds you choose (up to 3 minutes).",
                Screen, "record screen video recording capture", "screenrecord --time-limit {seconds} /sdcard/Movies/linc-$(date +%Y%m%d-%H%M%S).mp4",
                [new("seconds", "Seconds", ParamKind.Number, "30", Default: "30", Min: 1, Max: 180)], TimeoutSeconds: 200),
            new("screen-size", "Show the screen size", "Shows the screen's resolution in pixels.",
                Screen, "screen size resolution pixels display", "wm size", []),
            new("screen-density", "Show the screen density", "Shows the display density (DPI), which sets how big everything looks.",
                Screen, "screen density dpi display size scale", "wm density", []),
            new("set-screen-size", "Change the screen size", "Draws the screen at another resolution. Put it back with \"Reset the screen size\".",
                Screen, "screen size resolution change set display", "wm size {width}x{height}",
                [new("width", "Width", ParamKind.Number, "1080", Min: 200, Max: 8000), new("height", "Height", ParamKind.Number, "2400", Min: 200, Max: 8000)],
                CommandTier.Confirm),
            new("reset-screen-size", "Reset the screen size", "Puts the screen back to its own resolution.",
                Screen, "screen size resolution reset restore display", "wm size reset", []),
            new("set-density", "Make everything bigger or smaller", "Changes the display density: a lower number fits more on the screen, a higher one makes things bigger.",
                Screen, "density dpi display size bigger smaller scale", "wm density {dpi}",
                [new("dpi", "Density (DPI)", ParamKind.Number, "420", Min: 120, Max: 800)], CommandTier.Confirm),
            new("reset-density", "Reset the display size", "Puts the display density back to the phone's own.",
                Screen, "density dpi reset restore display size", "wm density reset", []),
            new("rotate", "Turn the screen", "Locks the screen in one direction: 0 upright, 1 landscape, 2 upside down, 3 landscape the other way.",
                Screen, "rotate rotation orientation landscape portrait", "settings put system accelerometer_rotation 0 && settings put system user_rotation {rotation}",
                [new("rotation", "Direction", ParamKind.Choice, Default: "1", Choices: ["0", "1", "2", "3"])]),
            new("auto-rotate", "Turn auto-rotate on", "Lets the screen turn with the phone again.",
                Screen, "auto rotate rotation orientation", "settings put system accelerometer_rotation 1", []),
            new("brightness", "Set the brightness", "Sets the screen brightness from 0 (darkest) to 255 (brightest) and turns adaptive brightness off.",
                Screen, "brightness screen light dim bright", "settings put system screen_brightness_mode 0 && settings put system screen_brightness {level}",
                [new("level", "Brightness (0–255)", ParamKind.Number, "128", Default: "128", Min: 0, Max: 255)]),
            new("auto-brightness", "Turn adaptive brightness on", "Lets the phone set its brightness from the light around it.",
                Screen, "auto adaptive brightness", "settings put system screen_brightness_mode 1", []),
            new("screen-timeout", "Set the screen timeout", "How long the screen stays on when you don't touch it, in milliseconds.",
                Screen, "screen timeout sleep after lock time", "settings put system screen_off_timeout {ms}",
                [new("ms", "Time (ms)", ParamKind.Choice, Default: "60000", Choices: ["15000", "30000", "60000", "120000", "300000", "600000", "1800000"])]),
            new("stay-awake", "Keep the screen on while charging", "true keeps the screen on while plugged in, usb or ac only for that charger, false turns it off.",
                Screen, "stay awake screen on charging", "svc power stayon {mode}",
                [new("mode", "When", ParamKind.Choice, Default: "true", Choices: ["true", "usb", "ac", "false"])]),
            new("wake", "Wake the screen", "Turns the screen on.", Screen, "wake screen on turn on", "input keyevent KEYCODE_WAKEUP", []),
            new("sleep", "Turn the screen off", "Turns the screen off and locks the phone.", Screen, "sleep screen off lock", "input keyevent KEYCODE_SLEEP", []),
            new("unlock-swipe", "Swipe up to unlock", "Swipes up the middle of the screen, which opens the PIN pad on most lock screens.",
                Screen, "unlock swipe up lock screen pin", UnlockSwipe, []),
            new("type-text", "Type text on the phone", "Types text into whatever box is open on the phone. Letters, numbers and symbols on a keyboard work; emoji and accents don't.",
                Screen, "type text keyboard write input", "input text {text}",
                [new("text", "Text", ParamKind.TypedText, "hello")]),
            new("press-key", "Press any key", "Presses a key on the phone, such as Tab or the arrow keys.",
                Screen, "key press keyevent button", "input keyevent {key}",
                [new("key", "Key", ParamKind.Choice, Default: "KEYCODE_HOME", Choices: KeyNames)]),
            new("tap", "Tap the screen", "Taps the screen at a point, counted in pixels from the top left.",
                Screen, "tap touch click screen point", "input tap {x} {y}",
                [new("x", "Across (x)", ParamKind.Number, "540", Min: 0, Max: 8000), new("y", "Down (y)", ParamKind.Number, "1200", Min: 0, Max: 8000)]),
            new("swipe", "Swipe the screen", "Swipes from one point to another over the time you choose.",
                Screen, "swipe scroll drag screen gesture", "input swipe {x1} {y1} {x2} {y2} {ms}",
                [new("x1", "From x", ParamKind.Number, "540", Min: 0, Max: 8000), new("y1", "From y", ParamKind.Number, "1800", Min: 0, Max: 8000),
                 new("x2", "To x", ParamKind.Number, "540", Min: 0, Max: 8000), new("y2", "To y", ParamKind.Number, "600", Min: 0, Max: 8000),
                 new("ms", "Time (ms)", ParamKind.Number, "300", Default: "300", Min: 1, Max: 5000)]),
            new("open-notifications", "Pull down notifications", "Opens the notification shade.",
                Screen, "notifications shade pull down", "cmd statusbar expand-notifications", []),
            new("open-quick-settings", "Open quick settings", "Opens the quick settings tiles.",
                Screen, "quick settings tiles toggles", "cmd statusbar expand-settings", []),
            new("close-shade", "Close the notification shade", "Closes notifications or quick settings.",
                Screen, "close collapse notifications shade", "cmd statusbar collapse", []),
            new("show-taps", "Show taps on the screen", "Shows a dot wherever the screen is touched (1 on, 0 off). Handy for recordings.",
                Screen, "show taps touches dot recording demo", "settings put system show_touches {state}", [OnOff("state", "On (1) or off (0)", "1", "0")]),
            new("pointer-location", "Show touch coordinates", "Shows the position of every touch at the top of the screen (1 on, 0 off).",
                Screen, "pointer location coordinates touch debug", "settings put system pointer_location {state}", [OnOff("state", "On (1) or off (0)", "1", "0")]),
            new("demo-on", "Clean status bar (demo mode)", "Shows 12:00, a full battery and full signal with no notifications, for clean screenshots.",
                Screen, "demo mode clean status bar screenshot", DemoOn, []),
            new("demo-off", "Leave demo mode", "Puts the real status bar back.",
                Screen, "demo mode exit status bar", "am broadcast -a com.android.systemui.demo -e command exit", []),
            new("open-url", "Open a web page", "Opens a link in the phone's browser.",
                Screen, "open url link web page browser website", "am start -a android.intent.action.VIEW -d {url}",
                [new("url", "Address", ParamKind.Url, "https://example.com")]),
            new("open-camera", "Open the camera", "Opens the phone's camera app.",
                Screen, "camera photo open", "am start -a android.media.action.STILL_IMAGE_CAMERA", []),
            new("dial", "Dial a number", "Opens the phone app with the number filled in. You press call on the phone.",
                Screen, "call dial phone number", "am start -a android.intent.action.DIAL -d tel:{number}",
                [new("number", "Phone number", ParamKind.PhoneNumber, "+15551234567")]),
            new("text-message", "Write a text message", "Opens the texting app with the number and message filled in. You press send on the phone.",
                Screen, "sms text message write send", "am start -a android.intent.action.SENDTO -d smsto:{number} --es sms_body {message}",
                [new("number", "Phone number", ParamKind.PhoneNumber, "+15551234567"), new("message", "Message", ParamKind.Text, "On my way")]),

            // ---- Power ----------------------------------------------------------------------
            new("reboot", "Reboot the phone", "Restarts the phone. Linc reconnects when it is back.",
                Power, "reboot restart phone", "reboot", [], CommandTier.Confirm, Adb: true),
            new("reboot-recovery", "Reboot into recovery", "Restarts into recovery mode, a menu for updates and factory reset. Use the phone's buttons there; Restart leaves it.",
                Power, "reboot recovery mode restart", "reboot recovery", [], CommandTier.Confirm, Adb: true),
            new("power-off", "Turn the phone off", "Shuts the phone down. Turn it on again with its power button.",
                Power, "power off shut down shutdown turn off", "svc power shutdown", [], CommandTier.Confirm),
            new("battery", "Show battery details", "Shows the battery level, charging state, temperature (in tenths of °C) and voltage.",
                Power, "battery level charge charging health temperature", "dumpsys battery", []),
            new("battery-level", "Show the battery level", "Shows how full the battery is, in percent.",
                Power, "battery level percent charge", "dumpsys battery | grep level", []),
            new("battery-saver", "Battery saver on or off", "Turns battery saver on (1) or off (0).",
                Power, "battery saver power saving low power", "cmd power set-mode {mode}", [OnOff("mode", "On (1) or off (0)", "1", "0")]),
            new("battery-unplug", "Pretend the charger is unplugged", "Makes apps think the phone is on battery, to test battery behaviour. Undo with \"Stop pretending about the battery\".",
                Power, "battery unplug simulate test charging", "dumpsys battery unplug", []),
            new("battery-reset", "Stop pretending about the battery", "Puts the real battery and charger state back.",
                Power, "battery reset real charging", "dumpsys battery reset", []),
            new("doze-now", "Put the phone to deep sleep now", "Sends the phone into Doze straight away, to test how apps behave asleep. Undo with \"Wake the phone from deep sleep\".",
                Power, "doze idle deep sleep battery test", "dumpsys deviceidle force-idle", []),
            new("doze-off", "Wake the phone from deep sleep", "Ends a Doze test started from here.",
                Power, "doze idle unforce wake", "dumpsys deviceidle unforce", []),
            new("dnd", "Do not disturb", "Turns Do not disturb on, priority only, alarms only, or off.",
                Power, "do not disturb dnd silent quiet", "cmd notification set_dnd {mode}",
                [new("mode", "Mode", ParamKind.Choice, Default: "on", Choices: ["on", "priority", "alarms", "off"])]),

            // ---- Network --------------------------------------------------------------------
            new("wifi-status", "Show Wi-Fi status", "Shows whether Wi-Fi is on and which network the phone is on.",
                Network, "wifi wi-fi status network connected", "cmd wifi status", []),
            new("wifi-onoff", "Wi-Fi on or off", "Turns Wi-Fi on or off. Over wireless debugging, turning it off disconnects Linc.",
                Network, "wifi wi-fi turn on off enable disable", "cmd wifi set-wifi-enabled {state}", [OnOff("state", "On or off", "enabled", "disabled")], CommandTier.Confirm),
            new("wifi-scan", "Find Wi-Fi networks nearby", "Scans for Wi-Fi networks and lists them with their signal.",
                Network, "wifi wi-fi scan networks nearby", "cmd wifi start-scan; sleep 4; cmd wifi list-scan-results", []),
            new("wifi-saved", "List saved Wi-Fi networks", "Lists the Wi-Fi networks the phone remembers (names only, not passwords).",
                Network, "wifi wi-fi saved networks remembered", "cmd wifi list-networks", []),
            new("wifi-signal", "Show Wi-Fi signal strength", "Shows the signal (RSSI, closer to 0 is stronger) and link speed.",
                Network, "wifi wi-fi signal strength rssi speed", "dumpsys wifi | grep -E 'mWifiInfo' | head -n 3", []),
            new("ip-address", "Show the phone's IP address", "Shows the phone's address on your Wi-Fi.",
                Network, "ip address network wifi lan", "ip -f inet addr show wlan0", []),
            new("ping", "Check the internet (ping)", "Sends four pings from the phone to an address and shows how long each took.",
                Network, "ping internet test connection latency", "ping -c 4 {host}",
                [new("host", "Address", ParamKind.Host, "8.8.8.8", Default: "8.8.8.8")]),
            new("mobile-data", "Mobile data on or off", "Turns mobile data on or off.",
                Network, "mobile data cellular 4g 5g", "svc data {state}", [OnOff("state", "On or off", "enable", "disable")]),
            new("airplane", "Airplane mode on or off", "Turns airplane mode on or off. Over wireless debugging, turning it on disconnects Linc.",
                Network, "airplane flight mode", "cmd connectivity airplane-mode {state}", [OnOff("state", "On or off", "enable", "disable")], CommandTier.Confirm),
            new("bluetooth", "Bluetooth on or off", "Turns Bluetooth on or off.",
                Network, "bluetooth turn on off", "cmd bluetooth_manager {state}", [OnOff("state", "On or off", "enable", "disable")], MinSdk: 31),
            new("location", "Location on or off", "Turns location (GPS) on or off.",
                Network, "location gps turn on off", "cmd location set-location-enabled {state}", [OnOff("state", "On or off", "true", "false")], MinSdk: 31),
            new("wireless-pair", "Pair over Wi-Fi", "Pairs this PC with a phone's wireless debugging, using the address and the six-digit code on the phone (Developer options › Wireless debugging › Pair with code).",
                Network, "pair wireless debugging wifi code", "pair {host}:{port} {code}",
                [new("host", "Phone's IP address", ParamKind.Host, "192.168.1.20"), new("port", "Port", ParamKind.Number, "37000", Min: 1, Max: 65535),
                 new("code", "Pairing code", ParamKind.Number, "123456", Min: 0, Max: 999999)], Adb: true),
            new("wireless-connect", "Connect over Wi-Fi", "Connects to a phone's wireless debugging at an address and port.",
                Network, "connect wireless debugging wifi", "connect {host}:{port}",
                [new("host", "Phone's IP address", ParamKind.Host, "192.168.1.20"), new("port", "Port", ParamKind.Number, "5555", Default: "5555", Min: 1, Max: 65535)], Adb: true),
            new("wireless-disconnect", "Disconnect a Wi-Fi phone", "Disconnects one wireless debugging connection.",
                Network, "disconnect wireless debugging wifi", "disconnect {host}:{port}",
                [new("host", "Phone's IP address", ParamKind.Host, "192.168.1.20"), new("port", "Port", ParamKind.Number, "5555", Default: "5555", Min: 1, Max: 65535)],
                CommandTier.Confirm, Adb: true),
            new("tcpip", "Allow Wi-Fi debugging on port 5555", "Lets adb connect over Wi-Fi without pairing, until the phone restarts. Turn it off with \"Back to USB only\".",
                Network, "tcpip 5555 wifi wireless debugging old way", "tcpip 5555", [], CommandTier.Confirm, Adb: true, UsbOnly: true),
            new("usb-only", "Back to USB only", "Turns off the port 5555 Wi-Fi debugging started with \"Allow Wi-Fi debugging on port 5555\".",
                Network, "usb only tcpip off wifi debugging", "usb", [], Adb: true),
            new("forward-list", "List port forwards", "Shows the ports forwarded from this PC to the phone (Linc uses some of them).",
                Network, "forward ports list tcp", "forward --list", [], Adb: true),
            new("forward-add", "Forward a port to the phone", "Makes a port on this PC reach a port on the phone, e.g. for a web server on the phone.",
                Network, "forward port tcp add", "forward tcp:{pc} tcp:{phone}",
                [new("pc", "Port on this PC", ParamKind.Number, "8080", Min: 1024, Max: 65535), new("phone", "Port on the phone", ParamKind.Number, "8080", Min: 1, Max: 65535)], Adb: true),
            new("forward-remove", "Remove a port forward", "Stops one forward. Removing one of Linc's own cuts its link until it reconnects.",
                Network, "forward port remove stop", "forward --remove tcp:{pc}",
                [new("pc", "Port on this PC", ParamKind.Number, "8080", Min: 1024, Max: 65535)], CommandTier.Confirm, Adb: true),
            new("reverse-list", "List reverse ports", "Shows the ports forwarded from the phone back to this PC.",
                Network, "reverse ports list tcp", "reverse --list", [], Adb: true),
            new("reverse-add", "Let the phone reach a PC port", "Makes a port on the phone reach a port on this PC, e.g. so the phone opens a site running on the PC.",
                Network, "reverse port tcp add localhost", "reverse tcp:{phone} tcp:{pc}",
                [new("phone", "Port on the phone", ParamKind.Number, "3000", Min: 1024, Max: 65535), new("pc", "Port on this PC", ParamKind.Number, "3000", Min: 1, Max: 65535)], Adb: true),
            new("reverse-remove", "Remove a reverse port", "Stops one reverse port. Removing one of Linc's own cuts its link until it reconnects.",
                Network, "reverse port remove stop", "reverse --remove tcp:{phone}",
                [new("phone", "Port on the phone", ParamKind.Number, "3000", Min: 1024, Max: 65535)], CommandTier.Confirm, Adb: true),

            // ---- Settings -----------------------------------------------------------------
            new("dark-mode", "Dark mode", "Turns dark mode on (yes), off (no) or automatic.",
                Settings, "dark mode night theme black", "cmd uimode night {state}", [OnOff("state", "Dark mode", "yes", "no", "auto")]),
            new("font-size", "Text size", "Makes text smaller or bigger: 0.85 small, 1.0 normal, 1.15 large, 1.3 larger.",
                Settings, "font text size scale bigger smaller", "settings put system font_scale {scale}",
                [new("scale", "Size", ParamKind.Choice, Default: "1.0", Choices: ["0.85", "1.0", "1.15", "1.3"])]),
            new("animations", "Animation speed", "Sets all animations: 0 turns them off (snappier), 0.5 faster, 1 normal.",
                Settings, "animations speed scale faster off snappy", "settings put global window_animation_scale {scale} && settings put global transition_animation_scale {scale} && settings put global animator_duration_scale {scale}",
                [new("scale", "Speed", ParamKind.Choice, Default: "0.5", Choices: ["0", "0.5", "1"])]),
            new("setting-get", "Read a setting", "Shows the value of one Android setting.",
                Settings, "settings get read value", "settings get {table} {key}",
                [new("table", "Group", ParamKind.Choice, Default: "system", Choices: ["system", "secure", "global"]),
                 new("key", "Setting", ParamKind.Name, "screen_off_timeout")]),
            new("setting-put", "Change a setting", "Sets one Android setting. Linc won't change settings that weaken the phone's security or cut its own connection.",
                Settings, "settings put change set value", "settings put {table} {key} {value}",
                [new("table", "Group", ParamKind.Choice, Default: "system", Choices: ["system", "secure", "global"]),
                 new("key", "Setting", ParamKind.Name, "screen_off_timeout"), new("value", "Value", ParamKind.Text, "60000")], CommandTier.Confirm),
            new("setting-list", "List all settings", "Shows every setting in a group and its value.",
                Settings, "settings list all values", "settings list {table}",
                [new("table", "Group", ParamKind.Choice, Default: "system", Choices: ["system", "secure", "global"])]),
            new("media-volume", "Set the media volume", "Sets the music and video volume (0 to 15 on most phones).",
                Settings, "volume media music sound level", "cmd media_session volume --show --stream 3 --set {level}",
                [new("level", "Volume", ParamKind.Number, "7", Default: "7", Min: 0, Max: 30)]),
            new("date-time", "Show the date and time", "Shows the phone's date, time and time zone.",
                Settings, "date time clock now", "date", []),
            new("timezone", "Show the time zone", "Shows the phone's time zone.",
                Settings, "timezone time zone", "getprop persist.sys.timezone", []),

            // ---- Info -----------------------------------------------------------------------
            new("model", "Phone model", "Shows the phone's maker and model.",
                Info, "model phone name maker brand manufacturer device", "getprop ro.product.manufacturer; getprop ro.product.model", []),
            new("android-version", "Android version", "Shows the Android version and its API level.",
                Info, "android version os api level sdk", "getprop ro.build.version.release; getprop ro.build.version.sdk", []),
            new("security-patch", "Security patch date", "Shows the date of the phone's latest security update.",
                Info, "security patch update date", "getprop ro.build.version.security_patch", []),
            new("serial", "Serial number", "Shows the phone's serial number as adb sees it.",
                Info, "serial number id", "get-serialno", [], Adb: true),
            new("uptime", "How long the phone has been on", "Shows the time since the phone last started.",
                Info, "uptime since restart boot time on", "uptime", []),
            new("build", "Build fingerprint", "Shows the exact software build, useful when reporting a bug.",
                Info, "build fingerprint software version firmware", "getprop ro.build.fingerprint", []),
            new("all-properties", "All system properties", "Lists every system property the phone shares.",
                Info, "getprop properties all system", "getprop", []),
            new("chip", "Processor", "Shows the phone's chip and CPU cores.",
                Info, "cpu processor chip soc cores", "getprop ro.soc.manufacturer; getprop ro.soc.model; getprop ro.board.platform; grep -c processor /proc/cpuinfo", []),
            new("memory", "Memory (RAM)", "Shows total and available memory.",
                Info, "memory ram free available", "grep -E 'MemTotal|MemAvailable|SwapTotal|SwapFree' /proc/meminfo", []),
            new("processes", "Running processes", "Lists everything running, with memory use.",
                Info, "processes running ps tasks", "ps -A -o PID,USER,RSS,NAME", []),
            new("top", "What uses the processor", "Shows the busiest processes right now (one snapshot).",
                Info, "top cpu usage busy processes", "top -b -n 1 -m 15", []),
            new("temperatures", "Phone temperatures", "Shows the phone's temperature sensors.",
                Info, "temperature thermal heat hot", "dumpsys thermalservice", []),
            new("display-info", "Screen details", "Shows the screen's size, density and refresh rate.",
                Info, "display screen refresh rate hz density", "wm size; wm density; dumpsys display | grep -E 'mRefreshRate|refreshRate' | head -n 3", []),
            new("storage-info", "Storage volumes", "Lists internal storage and any SD card.",
                Info, "storage volumes sd card", "sm list-volumes", []),

            // ---- Debug ----------------------------------------------------------------------
            new("log-recent", "Recent log", "Shows the last 200 lines of the phone's log.",
                Debug, "logcat log recent messages", "logcat -d -t 200", []),
            new("log-errors", "Recent errors", "Shows the last errors in the phone's log.",
                Debug, "logcat errors log crash problems", "logcat -d -t 300 '*:E'", []),
            new("log-app", "An app's log", "Shows recent log lines from one running app.",
                Debug, "logcat app log messages", "logcat -d -t 300 --pid=$(pidof -s {package})", [Pkg()]),
            new("log-crashes", "Recent crashes", "Shows recent app crashes.",
                Debug, "crash log logcat force close", "logcat -d -b crash -t 100", []),
            new("log-clear", "Clear the log", "Empties the phone's log, so the next look shows only what happens from now on.",
                Debug, "logcat clear log", "logcat -c", [], CommandTier.Confirm),
            new("services", "List system services", "Lists Android's system services (what dumpsys can show).",
                Debug, "services list dumpsys system", "dumpsys -l", []),
            new("bugreport", "Save a bug report", "Saves a full bug report zip to a folder on this PC. Takes a few minutes. It contains personal data, so share it carefully.",
                Debug, "bugreport bug report diagnostics zip", "bugreport {folder}",
                [new("folder", "Folder on this PC", ParamKind.PcFolder, @"D:\Phone backup")], CommandTier.Confirm, Adb: true, TimeoutSeconds: 600),
            new("reboot-bootloader", "Reboot into the bootloader", "Restarts into the bootloader screen (for developers). Nothing is changed; choose Start with the volume and power keys to go back.",
                Debug, "reboot bootloader fastboot mode", "reboot bootloader", [], CommandTier.Confirm, Adb: true),
            new("adb-devices", "Phones adb sees", "Lists every phone connected to this PC's adb.",
                Debug, "adb devices list connected", "devices -l", [], Adb: true),
            new("adb-state", "Connection state", "Shows whether adb can talk to this phone (device, offline or unauthorized).",
                Debug, "adb state connection", "get-state", [], Adb: true),
            new("notify-test", "Send a test notification", "Posts a notification on the phone, to check notifications reach this PC.",
                Debug, "test notification post", "cmd notification post -S bigtext -t 'Linc' linc-test 'A test notification from Linc'", []),
        };

        foreach (var (id, title, action, keywords) in SettingsScreens)
        {
            list.Add(new(id, title, "Opens this screen of Settings on the phone.", Settings, keywords + " open settings screen",
                $"am start -a {action}", []));
        }
        foreach (var (id, title, key, keywords) in Keys)
        {
            list.Add(new(id, title, "Presses this key on the phone.", Screen, keywords, $"input keyevent {key}", []));
        }
        return list;
    }

    // ---- search -----------------------------------------------------------------------------

    /// <summary>
    /// Entries matching <paramref name="query"/> in <paramref name="category"/> (null = all), best
    /// first: title starts with the query, then every word starts a title word, then every word is
    /// in the title or keywords, then in the description, then in the command. Ties keep catalogue
    /// order. An empty query lists the whole category.
    /// </summary>
    public static IReadOnlyList<CommandEntry> Search(string? query, string? category = null)
    {
        var pool = All.Where(e => category is null || e.Category == category);
        var q = Normalize(query ?? "");
        if (q.Length == 0) return pool.ToList();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return pool
            .Select((e, i) => (Entry: e, Index: i, Score: Score(e, q, words)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => x.Entry)
            .ToList();
    }

    internal static int Score(CommandEntry e, string q, string[] words)
    {
        var title = Normalize(e.Title);
        var titleWords = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keywords = Normalize(e.Keywords).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var description = Normalize(e.Description);
        var command = e.Template.ToLowerInvariant();
        if (title.StartsWith(q, StringComparison.Ordinal)) return 1000;
        bool InWords(string w, IEnumerable<string> set) => set.Any(t => t.StartsWith(w, StringComparison.Ordinal));
        if (words.All(w => InWords(w, titleWords))) return 800 + (InWords(words[0], titleWords.Take(1)) ? 50 : 0);
        if (words.All(w => InWords(w, titleWords) || InWords(w, keywords))) return 600;
        if (words.All(w => InWords(w, titleWords) || InWords(w, keywords) || description.Contains(w, StringComparison.Ordinal))) return 400;
        if (words.All(w => title.Contains(w, StringComparison.Ordinal) || description.Contains(w, StringComparison.Ordinal)
                           || command.Contains(w, StringComparison.Ordinal) || keywords.Any(k => k.Contains(w, StringComparison.Ordinal)))) return 200;
        return 0;
    }

    /// <summary>Lower case, Wi-Fi as wifi, punctuation as spaces.</summary>
    internal static string Normalize(string text)
    {
        var lower = text.ToLowerInvariant().Replace("wi-fi", "wifi").Replace("e-mail", "email");
        var sb = new StringBuilder(lower.Length);
        foreach (var c in lower) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // ---- parameters -------------------------------------------------------------------------

    [GeneratedRegex(@"\{([a-z][a-zA-Z0-9]*)\}")]
    internal static partial Regex Placeholder();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")]
    private static partial Regex PackageName();

    [GeneratedRegex(@"^[A-Za-z0-9_.]+$")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$")]
    private static partial Regex HostName();

    [GeneratedRegex(@"^\+?[0-9*#]{1,20}$")]
    private static partial Regex Phone();

    [GeneratedRegex(@"^https?://[^\s'""`]+$", RegexOptions.IgnoreCase)]
    private static partial Regex WebAddress();

    // A word that needs no quoting in the phone's shell: no spaces, quotes, $, `, ;, &, |, <, >,
    // (, ), *, ?, [, ], {, }, ~, #, ! or backslash.
    [GeneratedRegex(@"^[A-Za-z0-9._/:@%+=,-]+$")]
    private static partial Regex PlainWord();

    /// <summary>Why <paramref name="value"/> can't be used for <paramref name="p"/>, or null when it can.
    /// An empty value is fine for an optional parameter and "Required" for a required one.</summary>
    public static string? Validate(CommandParam p, string? value)
    {
        value ??= "";
        if (value.Length == 0) return p.Required ? "Required" : null;
        if (value.Contains('\n') || value.Contains('\r') || value.Contains('\0')) return "One line only";
        switch (p.Kind)
        {
            case ParamKind.Package:
                return PackageName().IsMatch(value) ? null : "A package name looks like com.example.app";
            case ParamKind.Number:
                if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)) return "A whole number";
                return n < p.Min || n > p.Max ? $"Between {p.Min} and {p.Max}" : null;
            case ParamKind.Choice:
                return p.Choices?.Contains(value) == true ? null : "Pick one from the list";
            case ParamKind.PhonePath:
                return value.StartsWith('/') ? null : "A path on the phone starts with /";
            case ParamKind.UserPath:
                return IsUserPath(value) ? null : "Only inside the phone's storage, e.g. /sdcard/Download/file";
            case ParamKind.PcFile:
                if (p.Extension is { } ext && !value.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return $"Pick a {ext} file";
                return File.Exists(value) ? null : "No such file on this PC";
            case ParamKind.PcFolder:
                return Directory.Exists(value) ? null : "No such folder on this PC";
            case ParamKind.Name:
                if (!Identifier().IsMatch(value)) return "Letters, numbers, dots and underscores only";
                return ForbiddenSettingKeys.Any(k => value.Contains(k, StringComparison.OrdinalIgnoreCase))
                    ? "Linc doesn't change this one: it affects the phone's security or Linc's connection" : null;
            case ParamKind.Url:
                return WebAddress().IsMatch(value) ? null : "A web address starting with https://";
            case ParamKind.Host:
                return HostName().IsMatch(value) ? null : "An IP address like 192.168.1.20";
            case ParamKind.PhoneNumber:
                return Phone().IsMatch(value) ? null : "Digits, and + at the start";
            case ParamKind.TypedText:
                return value.Any(AdbQuote.IsTypeable) ? null : "Nothing here can be typed: use letters, numbers and symbols";
            default:
                return null;
        }
    }

    /// <summary>A path inside the user's storage and below its root: /sdcard/x, /storage/emulated/0/x,
    /// /storage/ABCD-1234/x. Never the root itself, never "..".</summary>
    public static bool IsUserPath(string path)
    {
        if (!path.StartsWith('/')) return false;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or "..")) return false;
        if (parts.Length == 0) return false;
        return parts[0] switch
        {
            "sdcard" => parts.Length >= 2,
            "storage" when parts.Length >= 2 && parts[1] == "emulated" => parts.Length >= 4 && parts[2].All(char.IsDigit),
            "storage" when parts.Length >= 2 && parts[1] == "self" => parts.Length >= 4 && parts[2] == "primary",
            "storage" => parts.Length >= 3,
            _ => false,
        };
    }

    /// <summary>Extra refusal for one entry's values together (e.g. removing a protected app).</summary>
    public static string? ValidateEntry(CommandEntry entry, IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("package", out var pkg) && entry.Tier != CommandTier.Safe && ProtectedPackages.Contains(pkg))
        {
            return "Linc won't change this app: the phone needs it";
        }
        return null;
    }

    /// <summary>
    /// The entry with its parameters filled in, or null while any value isn't valid. Shell values go
    /// in as one shell word each (quoted unless plainly safe); adb-level values become whole
    /// arguments, so nothing typed can ever turn into more command.
    /// </summary>
    public static FilledCommand? Fill(CommandEntry entry, IReadOnlyDictionary<string, string> values)
    {
        foreach (var p in entry.Params)
        {
            values.TryGetValue(p.Name, out var v);
            if (Validate(p, v) is not null) return null;
        }
        if (ValidateEntry(entry, values) is not null) return null;
        var kinds = entry.Params.ToDictionary(p => p.Name, p => p.Kind);

        if (!entry.Adb)
        {
            var shell = Placeholder().Replace(entry.Template, m => ShellValue(kinds[m.Groups[1].Value], values[m.Groups[1].Value]));
            return new FilledCommand(shell, null, "adb shell " + shell);
        }

        var args = new List<string>();
        var display = new List<string>();
        foreach (var word in entry.Template.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var arg = Placeholder().Replace(word, m => values[m.Groups[1].Value]);
            args.Add(arg);
            display.Add(arg.Any(c => c is ' ' or '"' or '\'' or '&' or '|' or '^' or '%') ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg);
        }
        return new FilledCommand(null, args, "adb " + string.Join(' ', display));
    }

    /// <summary>One value as it goes into a shell command.</summary>
    internal static string ShellValue(ParamKind kind, string value) => kind switch
    {
        ParamKind.TypedText => string.Join(" && input text ", AdbQuote.InputTextChunks(value)),
        _ => PlainWord().IsMatch(value) && !value.StartsWith('-') ? value : AdbQuote.Arg(value),
    };
}
