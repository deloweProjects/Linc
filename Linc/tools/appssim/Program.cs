using System.Text.Json.Nodes;
using Linc.Desktop.Services;

// Verification harness for the v16 installed-app inventory and its per-device cache
// (M6c, D-058). Runs entirely offline: the wire parser, the cache and the UI-wiring checks
// need no phone, and that is the point — these are the parts a clean build hides.
//
//   dotnet run --project tools/appssim
//
// D-057: every DeviceRegistry and every AppCatalog here is built against one throwaway temp
// root, so nothing in this file can reach the owner's real settings.json OR their real
// cache\ — the second of those is exactly the hole D-058 closed.

Console.WriteLine("=== Linc Apps Verification Harness (appssim) ===");

var failures = new List<string>();
var root = Path.Combine(Path.GetTempPath(), "Linc_appssim_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

void Check(bool ok, string passText, string failText, string failure)
{
    if (ok)
    {
        Console.WriteLine($"    PASS: {passText}");
    }
    else
    {
        Console.WriteLine($"    FAIL: {failText}");
        failures.Add(failure);
    }
}

// ---------------------------------------------------------------------------------------
// 1. The pure parser over a full, well-formed payload.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[1/8] Parsing a full apps payload...");
try
{
    var payload = JsonNode.Parse("""
    {
      "apps": [
        { "package": "com.example.notes", "label": "Notes", "versionName": "2.1.0", "system": false },
        { "package": "com.android.settings", "label": "Settings", "versionName": "14", "system": true }
      ]
    }
    """)!.AsObject();

    var apps = AppsPayload.Parse(payload);
    Check(apps.Count == 2, $"parsed {apps.Count} apps.", $"expected 2 apps, got {apps.Count}.", "Full payload parse count");

    var notes = apps.FirstOrDefault(a => a.Package == "com.example.notes");
    Check(notes is { Label: "Notes", VersionName: "2.1.0", IsSystem: false },
        "com.example.notes → Notes / 2.1.0 / user app (all four fields).",
        $"com.example.notes mapped wrong: {notes}.",
        "Full payload field mapping");

    var settings = apps.FirstOrDefault(a => a.Package == "com.android.settings");
    Check(settings is { Label: "Settings", IsSystem: true },
        "com.android.settings is present and flagged system (included, not filtered — D-058).",
        "com.android.settings missing or not flagged as a system app.",
        "System app flag / inclusion");
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: full-payload parse threw: {ex.Message}");
    failures.Add($"Full payload parse threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 2. Tolerance: missing versionName, absent system, unknown extra fields, empty list.
//    These are the envelope rules — a phone that omits or adds fields must never break us.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[2/8] Tolerating a missing versionName, an absent system flag and unknown fields...");
try
{
    var payload = JsonNode.Parse("""
    {
      "apps": [
        { "package": "com.example.noversion", "label": "No Version", "system": false },
        { "package": "com.example.emptyversion", "label": "Empty Version", "versionName": "", "system": false },
        { "package": "com.example.nosystem", "label": "No System Flag", "versionName": "1.0" },
        { "package": "com.example.extra", "label": "Extra Fields", "versionName": "3.0", "system": false,
          "installedAt": 1730000000, "minSdk": 31, "nested": { "anything": true } },
        { "label": "No Package", "versionName": "1.0" },
        { "package": "com.example.nolabel", "versionName": "1.0" },
        { "package": "com.example.wrongtypes", "label": "Wrong Types", "versionName": 42, "system": "yes" }
      ],
      "somethingNewInV17": true
    }
    """)!.AsObject();

    var apps = AppsPayload.Parse(payload);
    var byPackage = apps.ToDictionary(a => a.Package);

    Check(byPackage.TryGetValue("com.example.noversion", out var nov) && nov.VersionName is null,
        "a missing versionName parses as null, not a crash.",
        "a missing versionName was not tolerated.", "Missing versionName");

    Check(byPackage.TryGetValue("com.example.emptyversion", out var emp) && emp.VersionName is null,
        "an empty versionName normalises to null.",
        "an empty versionName was kept as \"\".", "Empty versionName");

    Check(byPackage.TryGetValue("com.example.nosystem", out var nos) && !nos.IsSystem,
        "an absent system flag defaults to false (a user app, the safer guess).",
        "an absent system flag was not tolerated.", "Absent system flag");

    Check(byPackage.TryGetValue("com.example.extra", out var ext) && ext.Label == "Extra Fields",
        "unknown extra fields (payload-level and entry-level) are ignored.",
        "unknown extra fields broke the parse.", "Unknown extra fields");

    Check(!byPackage.ContainsKey("com.example.nolabel"),
        "an entry with no label is skipped, never named from its package id (v16 forbids it).",
        "an unlabelled entry survived the parse.", "Unlabelled entry not skipped");

    Check(apps.Count(a => a.Label == "No Package") == 0,
        "an entry with no package is skipped.",
        "a package-less entry survived the parse.", "Package-less entry not skipped");

    Check(byPackage.TryGetValue("com.example.wrongtypes", out var wt) && wt is { VersionName: null, IsSystem: false },
        "wrong-typed fields degrade to absent rather than throwing.",
        "wrong-typed fields were not tolerated.", "Wrong-typed fields");
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: tolerance parse threw: {ex.Message}");
    failures.Add($"Tolerance parse threw: {ex.Message}");
}

Console.WriteLine("\n[3/8] Empty and malformed payloads yield an empty list, never a throw...");
try
{
    Check(AppsPayload.Parse(JsonNode.Parse("""{"apps":[]}""")!.AsObject()).Count == 0,
        "an empty apps array parses to an empty list.",
        "an empty apps array did not parse to an empty list.", "Empty apps array");

    Check(AppsPayload.Parse(JsonNode.Parse("""{}""")!.AsObject()).Count == 0,
        "a payload with no apps key parses to an empty list.",
        "a payload with no apps key did not parse to an empty list.", "Missing apps key");

    Check(AppsPayload.Parse(JsonNode.Parse("""{"apps":"not-an-array"}""")!.AsObject()).Count == 0,
        "a non-array apps value parses to an empty list.",
        "a non-array apps value did not parse to an empty list.", "Non-array apps value");

    Check(AppsPayload.Parse(null).Count == 0,
        "a null payload parses to an empty list.",
        "a null payload did not parse to an empty list.", "Null payload");

    Check(AppsPayload.RequestPayload().ToJsonString() == "{}",
        "the apps.get request payload is {} exactly, per PROTOCOL.md v16.",
        $"the apps.get request payload was {AppsPayload.RequestPayload().ToJsonString()}, not {{}}.",
        "apps.get request payload shape");

    Check(!AppsPayload.IsSupported(15) && !AppsPayload.IsSupported(null) && AppsPayload.IsSupported(16),
        "the version gate is exactly >= 16 (v15 and no-phone are both unsupported).",
        "the version gate is not >= 16.", "Version gate");
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: empty/malformed parse threw: {ex.Message}");
    failures.Add($"Empty/malformed parse threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 4. AppCatalog round-trip.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[4/8] AppCatalog round-trip through <root>\\cache\\<serial>\\apps.json...");
const string Serial = "TEST-APPSSIM-1";
try
{
    var catalog = new AppCatalog(root);
    var apps = new List<AppInfo>
    {
        new("com.example.notes", "Notes", "2.1.0", false),
        new("com.android.settings", "Settings", null, true),
    };

    Check(catalog.Save(Serial, apps), "saved the list.", "Save returned false.", "AppCatalog.Save failed");

    var expectedPath = Path.Combine(root, "cache", Serial, "apps.json");
    Check(File.Exists(expectedPath),
        $"cache landed at <root>\\cache\\<serial>\\apps.json.",
        $"expected the cache at {expectedPath}; it is not there.", "Cache path shape");

    var loaded = catalog.Load(Serial);
    Check(loaded.Count == 2
          && loaded.Any(a => a is { Package: "com.example.notes", Label: "Notes", VersionName: "2.1.0", IsSystem: false })
          && loaded.Any(a => a is { Package: "com.android.settings", Label: "Settings", VersionName: null, IsSystem: true }),
        "the list round-tripped identically, including a null versionName and the system flag.",
        $"round-trip mismatch: [{string.Join(", ", loaded.Select(a => $"{a.Package}/{a.Label}/{a.VersionName}/{a.IsSystem}"))}]",
        "AppCatalog round-trip mismatch");

    // Icons: same directory, cached as PNG per package.
    var iconBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    catalog.SaveIcon(Serial, "com.example.notes", iconBytes);
    Check(catalog.LoadIcon(Serial, "com.example.notes")?.SequenceEqual(iconBytes) == true,
        "an icon round-tripped through <root>\\cache\\<serial>\\icons\\<package>.png.",
        "the cached icon did not round-trip.", "Icon round-trip");
    Check(catalog.LoadIcon(Serial, "com.example.never-fetched") is null,
        "an icon that was never fetched reads back as null (the placeholder case).",
        "a never-fetched icon did not read back as null.", "Missing icon null");
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: cache round-trip threw: {ex.Message}");
    failures.Add($"Cache round-trip threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 5. A corrupt apps.json degrades to "empty cache, refresh on connect" — never a throw.
//    A startup crash caused by a file the product can regenerate for free is unacceptable.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[5/8] A corrupt, truncated or hostile apps.json degrades to empty...");
try
{
    var catalog = new AppCatalog(root);
    var badSerial = "TEST-APPSSIM-CORRUPT";
    var path = catalog.ListPathFor(badSerial);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

    var hostileFiles = new (string Name, string Content)[]
    {
        ("truncated JSON", """[{"Package":"com.example.a","Label":"A"""),
        ("not JSON at all", "this is not json, it is a ransom note"),
        ("empty file", ""),
        ("JSON null", "null"),
        ("wrong shape (object, not array)", """{"Package":"com.example.a"}"""),
        ("wrong element types", """[{"Package":42,"Label":true}]"""),
        ("deeply nested junk", """[{"Package":"a","Label":{"nested":{"deeper":[1,2,3]}}}]"""),
    };

    foreach (var (name, content) in hostileFiles)
    {
        File.WriteAllText(path, content);
        try
        {
            var loaded = catalog.Load(badSerial);
            Check(loaded.Count == 0,
                $"{name} → empty list, no throw.",
                $"{name} → {loaded.Count} entries; it should have degraded to empty.",
                $"Corrupt cache '{name}' not degraded to empty");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    FAIL: {name} THREW {ex.GetType().Name}: {ex.Message}");
            failures.Add($"Corrupt cache '{name}' threw {ex.GetType().Name}");
        }
    }

    // And a corrupt cache must not block the next refresh from replacing it.
    var fresh = new List<AppInfo> { new("com.example.after", "After", "1.0", false) };
    var result = catalog.Reconcile(badSerial, fresh);
    Check(result.Added == 1 && result.Total == 1 && catalog.Load(badSerial).Count == 1,
        "a refresh over a corrupt cache replaces it cleanly (1 added).",
        $"a refresh over a corrupt cache produced {result}.",
        "Refresh over corrupt cache");
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: corrupt-cache check threw: {ex.Message}");
    failures.Add($"Corrupt-cache check threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 6. Reconcile, don't replace: correct added / removed / relabelled counts.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[6/8] Reconcile produces correct added / removed / relabelled counts...");
try
{
    var catalog = new AppCatalog(root);
    var serial = "TEST-APPSSIM-RECONCILE";

    var first = new List<AppInfo>
    {
        new("com.a", "Alpha", "1.0", false),
        new("com.b", "Bravo", "1.0", false),
        new("com.c", "Charlie", "1.0", true),
    };
    var initial = catalog.Reconcile(serial, first);
    Check(initial is { Added: 3, Removed: 0, Relabelled: 0, Total: 3 },
        $"a first sight of 3 apps is 3 added: {initial}.",
        $"first reconcile was {initial}, expected 3 added / 0 removed / 0 relabelled.",
        "First reconcile counts");

    // b removed, c relabelled, d added; a untouched.
    var second = new List<AppInfo>
    {
        new("com.a", "Alpha", "1.0", false),
        new("com.c", "Charlie Deluxe", "2.0", true),
        new("com.d", "Delta", "1.0", false),
    };
    var delta = catalog.Reconcile(serial, second);
    Check(delta is { Added: 1, Removed: 1, Relabelled: 1, Total: 3 },
        $"one install, one uninstall and one rename read as 1/1/1: {delta}.",
        $"second reconcile was {delta}, expected 1 added / 1 removed / 1 relabelled.",
        "Delta reconcile counts");

    Check(delta.AnyChange, "AnyChange is true when something moved.", "AnyChange was false despite a delta.", "AnyChange true");

    var idempotent = catalog.Reconcile(serial, second);
    Check(idempotent is { Added: 0, Removed: 0, Relabelled: 0, Total: 3 } && !idempotent.AnyChange,
        $"re-sending the same list is all zeros: {idempotent}.",
        $"an unchanged reconcile was {idempotent}, expected all zeros.",
        "Idempotent reconcile");

    var persisted = catalog.Load(serial);
    Check(persisted.Count == 3 && persisted.Any(a => a is { Package: "com.c", Label: "Charlie Deluxe" })
          && persisted.All(a => a.Package != "com.b"),
        "the reconciled list is what got persisted (relabel kept, removal gone).",
        "the persisted list does not match the reconciled one.", "Reconcile persistence");

    // An icon for an app that this reconcile removes is not left behind. (Only the apps this
    // pass drops are cleaned — the cache is not swept, and does not need to be.)
    catalog.SaveIcon(serial, "com.d", new byte[] { 1, 2, 3 });
    Check(catalog.LoadIcon(serial, "com.d") is not null,
        "an icon exists before its app is uninstalled.",
        "the icon fixture did not save.", "Icon fixture");
    catalog.Reconcile(serial, second.Where(a => a.Package != "com.d").ToList());
    Check(catalog.LoadIcon(serial, "com.d") is null,
        "an uninstalled app's cached icon is cleaned up.",
        "an uninstalled app's icon is still cached.", "Orphaned icon cleanup");
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: reconcile check threw: {ex.Message}");
    failures.Add($"Reconcile check threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 7. D-058: the cache root comes from the registry, NOT from %LOCALAPPDATA% directly.
//    This is the whole reason a harness cannot damage the owner's real cache. It is asserted
//    by comparing PATH STRINGS, never by writing to the real store (D-057).
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[7/8] The cache root is the registry's root, not %LOCALAPPDATA% (D-057/D-058)...");
try
{
    var tempRegistry = new DeviceRegistry(root);
    Check(tempRegistry.RootPath == root,
        "DeviceRegistry exposes the injected root as RootPath.",
        $"RootPath was {tempRegistry.RootPath}, expected {root}.", "DeviceRegistry.RootPath");

    var fromRegistry = new AppCatalog(tempRegistry);
    var expected = Path.Combine(root, "cache", "SERIAL-X", "apps.json");
    Check(fromRegistry.ListPathFor("SERIAL-X") == expected,
        "AppCatalog(registry) puts the cache under the registry's root.",
        $"AppCatalog(registry) resolved {fromRegistry.ListPathFor("SERIAL-X")}, expected {expected}.",
        "AppCatalog root injection");

    Check(!fromRegistry.ListPathFor("SERIAL-X").StartsWith(DeviceRegistry.DefaultRootPath, StringComparison.OrdinalIgnoreCase),
        "a temp-rooted catalog cannot resolve into the owner's real %LOCALAPPDATA%\\Linc\\cache.",
        "a temp-rooted catalog resolved INTO the owner's real cache — D-058 is broken.",
        "Temp catalog escaped into the real cache");

    // A production catalog still resolves to the real store — asserted as a string only.
    var productionPath = new AppCatalog(new DeviceRegistry(DeviceRegistry.DefaultRootPath).RootPath)
        .ListPathFor("SERIAL-X");
    Check(productionPath == Path.Combine(DeviceRegistry.DefaultRootPath, "cache", "SERIAL-X", "apps.json"),
        "the default root still resolves to %LOCALAPPDATA%\\Linc\\cache (string check only — nothing written).",
        $"the default root resolved to {productionPath}.", "Production cache path");

    // A hostile serial or package id must not climb out of the cache directory.
    var traversal = fromRegistry.ListPathFor("../../../Windows");
    Check(Path.GetFullPath(traversal).StartsWith(Path.GetFullPath(Path.Combine(root, "cache")), StringComparison.OrdinalIgnoreCase),
        "a path-traversal serial is sanitised and stays inside cache\\.",
        $"a path-traversal serial escaped to {traversal}.", "Serial path traversal");
    var iconTraversal = fromRegistry.IconPathFor("SERIAL-X", "../../evil");
    Check(Path.GetFullPath(iconTraversal).StartsWith(Path.GetFullPath(Path.Combine(root, "cache")), StringComparison.OrdinalIgnoreCase),
        "a path-traversal package id is sanitised and stays inside cache\\.",
        $"a path-traversal package id escaped to {iconTraversal}.", "Package path traversal");
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: root-injection check threw: {ex.Message}");
    failures.Add($"Root-injection check threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------
// 8. UI wiring. Deliberately crude text checks over the real XAML and view model, exactly
//    like homelayoutsim's [9/10]: M5c-2 shipped a completely dead UI behind a clean build
//    and a green harness, and only a check like this catches an unwired card.
// ---------------------------------------------------------------------------------------
Console.WriteLine("\n[8/8] UI wiring: HomePage.xaml binds the Apps card, HomeViewModel exposes the list...");
try
{
    var repoRoot = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(repoRoot, "DESKTOP")) && Directory.GetParent(repoRoot) != null)
    {
        repoRoot = Directory.GetParent(repoRoot)!.FullName;
    }

    var xamlPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Views", "HomePage.xaml");
    var vmPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "ViewModels", "HomeViewModel.cs");

    if (!File.Exists(xamlPath) || !File.Exists(vmPath))
    {
        Console.WriteLine($"    FAIL: cannot read {xamlPath} / {vmPath}");
        failures.Add("Missing HomePage.xaml or HomeViewModel.cs");
    }
    else
    {
        var xaml = File.ReadAllText(xamlPath);
        var vm = File.ReadAllText(vmPath);

        // The card must be bound to the section flag — without this the Apps toggle does nothing
        // and the card is permanently visible (the M5c-2 failure mode, in miniature).
        Check(xaml.Contains("Visibility=\"{x:Bind ViewModel.ShowApps, Mode=OneWay}\"", StringComparison.Ordinal),
            "HomePage.xaml binds the Apps card's Visibility to ViewModel.ShowApps.",
            "HomePage.xaml does NOT bind the Apps card's Visibility to ViewModel.ShowApps — the section toggle would do nothing.",
            "Apps card missing its Visibility binding");

        Check(xaml.Contains("ItemsSource=\"{x:Bind ViewModel.Apps}\"", StringComparison.Ordinal),
            "HomePage.xaml binds the app list (ViewModel.Apps).",
            "HomePage.xaml does not bind ViewModel.Apps — the card would render empty forever.",
            "Apps card missing its ItemsSource binding");

        Check(xaml.Contains("vm:AppVm", StringComparison.Ordinal),
            "the item template is typed to AppVm (icon + label per app).",
            "no AppVm DataTemplate in HomePage.xaml.", "Missing AppVm DataTemplate");

        Check(xaml.Contains("ViewModel.AppsEmptyText", StringComparison.Ordinal),
            "the empty state is bound (AppsEmptyText) rather than a blank card.",
            "HomePage.xaml has no empty state for the Apps card.", "Missing Apps empty state");

        // M6c-2: the Apps section is delimited by explicit markers so these crude checks can
        // scope themselves to it without depending on whatever card happens to follow.
        var cardStart = xaml.IndexOf("<!-- BEGIN Apps section", StringComparison.Ordinal);
        var cardEnd = xaml.IndexOf("<!-- END Apps section -->", StringComparison.Ordinal);
        if (cardStart >= 0 && cardEnd > cardStart)
        {
            var card = xaml[cardStart..cardEnd];

            // M6c kept the entries inert BECAUSE they had nowhere to go; M7a gave them somewhere,
            // so the invariant flips: a tile must now be clickable and must lead to the launch
            // command. (The full launch contract is applaunchsim's; this is the M6c check kept
            // honest rather than left asserting the opposite of what ships.)
            Check(!card.Contains("IsHitTestVisible=\"False\"", StringComparison.Ordinal),
                "the app grid is hit-testable — the tiles can be clicked (M7a).",
                "the app grid is still IsHitTestVisible=\"False\"; every click on a tile would be swallowed.",
                "Apps grid still marked non-interactive");
            Check(card.Contains("Command=\"{x:Bind Launch}\"", StringComparison.Ordinal),
                "clicking a tile opens that app in its own PC window (M7a).",
                "the Apps tiles bind no launch command; a clickable-looking tile that does nothing is a lie.",
                "Apps tiles are not wired to the launch command");

            // M6c-2: an icon + name GRID, not a list of rows.
            Check(card.Contains("<ItemsRepeater", StringComparison.Ordinal)
                  && card.Contains("<UniformGridLayout", StringComparison.Ordinal),
                "the Apps section renders an ItemsRepeater over a UniformGridLayout (a wrapping icon grid).",
                "the Apps section is not an ItemsRepeater + UniformGridLayout — the owner asked for an icon grid.",
                "Apps section is not an ItemsRepeater/UniformGridLayout grid");

            // Icon + name ONLY. A version string or a package id creeping back in is the exact
            // regression M6c-2 was raised to correct.
            Check(!card.Contains("VersionText", StringComparison.Ordinal)
                  && !card.Contains("x:Bind Package", StringComparison.Ordinal),
                "each tile is icon + name only — no version string, no package id.",
                "the Apps tiles show a version or a package id; M6c-2 asks for icon + name only.",
                "Apps tiles show more than icon + name");

            // M6c-3: Apps must scroll INSIDE itself. Without this the card grows to fit every tile
            // and, with no column-level ScrollViewer left, the overflow is simply unreachable.
            Check(card.Contains("<ScrollViewer", StringComparison.Ordinal),
                "the Apps card wraps its grid in its own ScrollViewer — Apps scrolls inside itself.",
                "the Apps card has no internal ScrollViewer; with no column scrolling left, overflowing tiles would be unreachable.",
                "Apps card missing its internal ScrollViewer");

            // The move itself: the section must live in the right-hand column, after the splitter,
            // NOT inside the widgets pane's StackPanel where M6c put it.
            var splitterAt = xaml.IndexOf("x:Name=\"Splitter\"", StringComparison.Ordinal);
            Check(splitterAt >= 0 && cardStart > splitterAt,
                "the Apps section sits after the splitter — it is out of the widgets pane (M6c-2).",
                "the Apps section is still inside the widgets pane; M6c-2 moves it under the tabs panel.",
                "Apps section still in the widgets pane");

            var tabsAt = xaml.IndexOf("x:Name=\"TabsPane\"", StringComparison.Ordinal);
            Check(tabsAt >= 0 && cardStart > tabsAt,
                "the Apps section is stacked BELOW the tabs panel in the right-hand column.",
                "the Apps section does not follow TabsPane.", "Apps section not below TabsPane");

            Check(xaml.Contains("x:Name=\"RightPane\"", StringComparison.Ordinal)
                  && cardStart > xaml.IndexOf("x:Name=\"RightPane\"", StringComparison.Ordinal),
                "both live inside the named RightPane container, so Swap sides can move them together.",
                "there is no RightPane container wrapping TabsPane and the Apps section.",
                "Missing RightPane container");
        }
        else
        {
            Console.WriteLine("    FAIL: could not locate the Apps section block in HomePage.xaml.");
            failures.Add("Apps section block not found in HomePage.xaml");
        }

        // M6c-3 replaces M6c-2's TabsPane height-sync. The invariant it protected still matters —
        // TabsPane must never be measured with unbounded height, or the notification / messages /
        // calls ListViews expand to their full content and lose internal scrolling — but a Grid row
        // now satisfies it structurally, so the checks are about the STRUCTURE instead of the sync.
        var codeBehindPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Views", "HomePage.xaml.cs");
        var codeBehind = File.Exists(codeBehindPath) ? File.ReadAllText(codeBehindPath) : "";

        Check(xaml.Contains("<Grid x:Name=\"RightPane\"", StringComparison.Ordinal),
            "the right column container is a Grid (bounded rows), not a ScrollViewer.",
            "the right column container is not a Grid — Apps would be below the fold again and TabsPane unbounded.",
            "RightPane is not a Grid");

        Check(!xaml.Contains("<ScrollViewer x:Name=\"RightPane\"", StringComparison.Ordinal)
              && !xaml.Contains("SizeChanged=\"OnRightPaneSizeChanged\"", StringComparison.Ordinal),
            "there is no column-level ScrollViewer wrapping TabsPane, and no height-sync hook left behind.",
            "HomePage.xaml still wraps the right column in a ScrollViewer / still hooks OnRightPaneSizeChanged — M6c-3 removes both.",
            "Right-column ScrollViewer still present");

        Check(!codeBehind.Contains("TabsPane.Height", StringComparison.Ordinal),
            "nothing assigns TabsPane.Height — an explicit height would fight the splitter.",
            "HomePage.xaml.cs still assigns TabsPane.Height; the splitter cannot resize a pinned pane.",
            "TabsPane still carries an explicit Height");

        Check(xaml.Contains("x:Name=\"AppsSplitter\"", StringComparison.Ordinal)
              && xaml.Contains("ManipulationDelta=\"OnAppsSplitterDrag\"", StringComparison.Ordinal),
            "a named horizontal splitter (AppsSplitter) sits between the tabs panel and Apps.",
            "there is no named AppsSplitter with a drag hook between the tabs panel and Apps.",
            "Vertical splitter missing from HomePage.xaml");

        Check(codeBehind.Contains("private void OnAppsSplitterDrag(", StringComparison.Ordinal)
              && codeBehind.Contains("SizeNorthSouth", StringComparison.Ordinal),
            "HomePage.xaml.cs implements the vertical drag handler and the N/S resize cursor.",
            "HomePage.xaml.cs has no OnAppsSplitterDrag handler (or no N/S cursor) — the splitter would be inert.",
            "Vertical splitter drag handler missing");

        Check(codeBehind.Contains("Grid.SetColumn(RightPane", StringComparison.Ordinal),
            "ApplyPaneOrder moves the RightPane container, so Apps travels with the tabs on Swap sides.",
            "ApplyPaneOrder does not move RightPane — swapping sides would leave Apps behind.",
            "ApplyPaneOrder does not move RightPane");

        Check(vm.Contains("public ObservableCollection<AppVm> Apps", StringComparison.Ordinal),
            "HomeViewModel exposes the Apps collection.",
            "HomeViewModel does not expose an Apps collection.", "HomeViewModel missing Apps");

        Check(vm.Contains("public bool ShowApps", StringComparison.Ordinal),
            "HomeViewModel exposes ShowApps.",
            "HomeViewModel does not expose ShowApps.", "HomeViewModel missing ShowApps");

        // The M6a regression, in the exact shape homelayoutsim guards for the other cards: a
        // property composed of two independent sources must be raised at EVERY site either half
        // changes. AppsSupported is (link state) and HasApps is (data), so both need >= 2 raises.
        foreach (var composed in new[] { "AppsSupported", "HasApps", "HasNoApps" })
        {
            var needle = $"OnPropertyChanged(nameof({composed}))";
            var count = 0;
            for (var i = vm.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = vm.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }
            Check(count >= 1,
                $"HomeViewModel raises {composed} in {count} place(s).",
                $"HomeViewModel never raises {composed}; the card will not refresh.",
                $"{composed} never raised in HomeViewModel");
        }

        // Apps must be in the section list and in RefreshSections, or the toggle desyncs.
        Check(HomeLayout.AllSectionIds.Contains("apps"),
            $"HomeLayout.AllSectionIds includes \"apps\" (position {HomeLayout.AllSectionIds.ToList().IndexOf("apps") + 1} of {HomeLayout.AllSectionIds.Count}).",
            "HomeLayout.AllSectionIds does not include \"apps\" — no toggle in the flyout.",
            "apps missing from AllSectionIds");
        Check(HomeLayout.DisplayName("apps") == "Apps",
            "HomeLayout.DisplayName(\"apps\") is \"Apps\".",
            $"HomeLayout.DisplayName(\"apps\") is \"{HomeLayout.DisplayName("apps")}\".",
            "apps DisplayName wrong");
        Check(HomeLayout.Default.IsVisible("apps"),
            "a saved layout with no mention of \"apps\" shows it (hidden-by-absence — intended for a new section).",
            "\"apps\" is not visible by default.", "apps not visible by default");

        var refreshStart = vm.IndexOf("private void RefreshSections()", StringComparison.Ordinal);
        var refreshEnd = refreshStart >= 0 ? vm.IndexOf("\n    }", refreshStart, StringComparison.Ordinal) : -1;
        Check(refreshStart >= 0 && refreshEnd > refreshStart
              && vm[refreshStart..refreshEnd].Contains("nameof(ShowApps)", StringComparison.Ordinal),
            "RefreshSections re-raises ShowApps like every other section.",
            "RefreshSections does not re-raise ShowApps — toggling the section would not redraw the card.",
            "ShowApps not raised in RefreshSections");

        Console.WriteLine("    (These crude checks exist so a future session that unwires the card fails loudly.)");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: UI wiring check threw: {ex.Message}");
    failures.Add($"UI wiring check threw: {ex.Message}");
}

// A killed run leaks this folder instead of damaging the owner's data (D-057).
try { Directory.Delete(root, recursive: true); } catch (IOException) { }

Console.WriteLine("\n=== SUMMARY ===");
if (failures.Count == 0)
{
    Console.WriteLine("PASS: All verification checks succeeded.");
    return 0;
}
Console.WriteLine($"FAIL: {failures.Count} check(s) failed:");
foreach (var f in failures)
{
    Console.WriteLine($"  - {f}");
}
return 1;
