using System.Text.Json;
using Linc.Desktop.Services;

Console.WriteLine("=== Linc Home Layout Verification Harness (homelayoutsim) ===");

var failures = new List<string>();

// D-057: every DeviceRegistry in this harness is built against one throwaway temp root. Sections
// 5 and 6 once wrote to the owner's real store from OUTSIDE the old backup/restore guard and
// corrupted it; there is now no real path for a section to escape to, and nothing to remember.
var root = Path.Combine(Path.GetTempPath(), "Linc_homelayoutsim_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var settingsPath = Path.Combine(root, "settings.json");

// 1. Round-trip HomeLayout through DeviceRegistry.
Console.WriteLine("\n[1/11] Round-tripping HomeLayout through DeviceRegistry...");
try
{
    var reg = new DeviceRegistry(root);
    reg.SavePairedDevice("TEST-SERIAL-HL1", "Pixel 7");

    // Start with default (all visible)
    var defaultLayout = reg.Home;
    if (defaultLayout == HomeLayout.Default && defaultLayout.IsVisible("phone") && defaultLayout.IsVisible("quickactions"))
    {
        Console.WriteLine("    PASS: Fresh device yields HomeLayout.Default (all six visible).");
    }
    else
    {
        Console.WriteLine($"    FAIL: Expected HomeLayout.Default, got {defaultLayout}");
        failures.Add("Fresh device does not yield HomeLayout.Default");
    }

    // Hide some sections and round-trip
    var hiddenLayout = defaultLayout
        .WithSection("media", false)
        .WithSection("photos", false);
    reg.SaveHome(hiddenLayout);

    var reloaded = new DeviceRegistry(root);
    var loadedLayout = reloaded.Home;

    if (loadedLayout == hiddenLayout &&
        !loadedLayout.IsVisible("media") &&
        !loadedLayout.IsVisible("photos") &&
        loadedLayout.IsVisible("phone") &&
        loadedLayout.IsVisible("quickactions") &&
        loadedLayout.IsVisible("shared") &&
        loadedLayout.IsVisible("clipboard"))
    {
        Console.WriteLine("    PASS: HomeLayout with hidden sections round-tripped identically.");
    }
    else
    {
        Console.WriteLine($"    FAIL: Settings mismatch after reload.\n      Expected: {hiddenLayout}\n      Actual:   {loadedLayout}");
        failures.Add("HomeLayout round-trip mismatch");
    }

    // 2. Backward-compatibility: a legacy KnownDevice JSON WITHOUT a Home field
    // must deserialize to HomeLayout.Default (so old settings.json files keep working).
    Console.WriteLine("\n[2/11] Checking backward compatibility (JSON without Home field)...");
    var legacyJson = """
    {
      "PairedSerial": "LEGACY-HL-SERIAL",
      "PairedModel": "Pixel 6",
      "KnownDevices": [
        {
          "Serial": "LEGACY-HL-SERIAL",
          "Model": "Pixel 6",
          "FirstPairedUtc": "2026-01-01T00:00:00+00:00",
          "LastHostPort": "192.168.1.100:5555"
        }
      ]
    }
    """;
    File.WriteAllText(settingsPath, legacyJson);

    var legacyReg = new DeviceRegistry(root);
    var legacyLayout = legacyReg.Home;

    if (legacyLayout == HomeLayout.Default)
    {
        Console.WriteLine("    PASS: Legacy KnownDevice JSON without Home deserialized with HomeLayout.Default (all visible).");
    }
    else
    {
        Console.WriteLine($"    FAIL: Legacy JSON did not yield HomeLayout.Default.\n      Expected: {HomeLayout.Default}\n      Actual:   {legacyLayout}");
        failures.Add("Backward-compat JSON check failed");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: Registry round-trip check threw: {ex.Message}");
    failures.Add($"Registry round-trip check threw: {ex.Message}");
}

// 3. IsVisible for every ID with an empty hidden list, and with each single ID hidden.
Console.WriteLine("\n[3/11] Checking IsVisible for all six IDs with empty hidden list and each single ID hidden...");
try
{
    var defaultLayout = HomeLayout.Default;
    var allVisible = HomeLayout.AllSectionIds.All(id => defaultLayout.IsVisible(id));
    if (allVisible)
    {
        Console.WriteLine("    PASS: Default layout shows all six IDs.");
    }
    else
    {
        Console.WriteLine("    FAIL: Default layout does not show all six.");
        failures.Add("Default layout IsVisible check failed");
    }

    foreach (var id in HomeLayout.AllSectionIds)
    {
        var oneHidden = HomeLayout.Default.WithSection(id, false);
        if (!oneHidden.IsVisible(id))
        {
            Console.WriteLine($"    PASS: WithSection(\"{id}\", false) hides {id}.");
        }
        else
        {
            Console.WriteLine($"    FAIL: WithSection(\"{id}\", false) did NOT hide {id}.");
            failures.Add($"IsVisible check failed for hidden {id}");
        }

        // Other IDs should still be visible
        var othersVisible = HomeLayout.AllSectionIds.Where(other => other != id).All(other => oneHidden.IsVisible(other));
        if (othersVisible)
        {
            Console.WriteLine($"    PASS: Hiding {id} leaves other five visible.");
        }
        else
        {
            Console.WriteLine($"    FAIL: Hiding {id} incorrectly hides others.");
            failures.Add($"Hiding {id} affected other IDs");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: IsVisible check threw: {ex.Message}");
    failures.Add($"IsVisible check threw: {ex.Message}");
}

// 4. WithSection(id, false) then WithSection(id, true) returns to all-visible;
//    no duplicate entries when hiding an already-hidden ID.
Console.WriteLine("\n[4/11] Checking WithSection idempotence and round-trip...");
try
{
    var layout = HomeLayout.Default;
    foreach (var id in HomeLayout.AllSectionIds)
    {
        var hidden = layout.WithSection(id, false);
        var shown = hidden.WithSection(id, true);

        if (shown == layout)
        {
            Console.WriteLine($"    PASS: Hide then show {id} returns to original layout.");
        }
        else
        {
            Console.WriteLine($"    FAIL: Hide then show {id} did not return to original.");
            failures.Add($"Hide-show round-trip failed for {id}");
        }

        // Hiding an already-hidden ID should return equivalent layout
        var hiddenAgain = hidden.WithSection(id, false);
        if (hiddenAgain == hidden)
        {
            Console.WriteLine($"    PASS: Hiding already-hidden {id} is idempotent.");
        }
        else
        {
            Console.WriteLine($"    FAIL: Hiding already-hidden {id} produced different record.");
            failures.Add($"Idempotent hide failed for {id}");
        }

        // Showing an already-visible ID should return equivalent layout
        var shownAgain = layout.WithSection(id, true);
        if (shownAgain == layout)
        {
            Console.WriteLine($"    PASS: Showing already-visible {id} is idempotent.");
        }
        else
        {
            Console.WriteLine($"    FAIL: Showing already-visible {id} produced different record.");
            failures.Add($"Idempotent show failed for {id}");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: WithSection idempotence check threw: {ex.Message}");
    failures.Add($"WithSection idempotence check threw: {ex.Message}");
}

// 5. Unknown IDs survive a round-trip and a WithSection call on a different ID (decision 2.5).
Console.WriteLine("\n[5/11] Checking unknown IDs survive round-trip and WithSection on another ID...");
try
{
    // Manually construct a HomeLayout with an unknown ID in HiddenSections
    var layoutWithUnknown = new HomeLayout(HiddenSections: new[] { "phone", "future-section-xyz" });
    var reg = new DeviceRegistry(root);
    reg.SavePairedDevice("TEST-UNKNOWN", "Test Phone");
    reg.SaveHome(layoutWithUnknown);

    var reloaded = new DeviceRegistry(root);
    var loaded = reloaded.Home;

    // The unknown ID should still be in the hidden list
    var hiddenList = loaded.HiddenSections ?? [];
    if (hiddenList.Contains("future-section-xyz", StringComparer.Ordinal))
    {
        Console.WriteLine("    PASS: Unknown ID 'future-section-xyz' survived round-trip in HiddenSections.");
    }
    else
    {
        Console.WriteLine($"    FAIL: Unknown ID was dropped. HiddenSections: [{string.Join(", ", hiddenList)}]");
        failures.Add("Unknown ID not preserved in round-trip");
    }

    // Now call WithSection on a DIFFERENT known ID - unknown should still survive
    var afterToggle = loaded.WithSection("media", false);
    var hiddenAfter = afterToggle.HiddenSections ?? [];
    if (hiddenAfter.Contains("future-section-xyz", StringComparer.Ordinal) &&
        hiddenAfter.Contains("media", StringComparer.Ordinal) &&
        hiddenAfter.Contains("phone", StringComparer.Ordinal)) // phone was in original hidden, so should still be there
    {
        Console.WriteLine("    PASS: Unknown ID survives WithSection on a different known ID.");
    }
    else
    {
        Console.WriteLine($"    FAIL: Unknown ID not preserved after WithSection. HiddenSections: [{string.Join(", ", hiddenAfter)}]");
        failures.Add("Unknown ID not preserved after WithSection on different ID");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: Unknown ID check threw: {ex.Message}");
    failures.Add($"Unknown ID check threw: {ex.Message}");
}

// 6. Hiding all six leaves a valid record that still deserializes.
Console.WriteLine("\n[6/11] Checking hiding all six sections leaves valid record...");
try
{
    var allHidden = HomeLayout.Default;
    foreach (var id in HomeLayout.AllSectionIds)
    {
        allHidden = allHidden.WithSection(id, false);
    }

    var expectedHidden = HomeLayout.AllSectionIds.Count;
    var hiddenCount = (allHidden.HiddenSections ?? []).Count;
    if (hiddenCount == expectedHidden)
    {
        Console.WriteLine($"    PASS: Hiding every section yields a record with {expectedHidden} hidden IDs.");
    }
    else
    {
        Console.WriteLine($"    FAIL: Expected {expectedHidden} hidden, got {hiddenCount}.");
        failures.Add("Hiding every section did not hide them all");
    }

    // Round-trip through registry
    var reg = new DeviceRegistry(root);
    reg.SavePairedDevice("TEST-ALL-HIDDEN", "Test Phone");
    reg.SaveHome(allHidden);

    var reloaded = new DeviceRegistry(root);
    var loaded = reloaded.Home;

    if (loaded == allHidden)
    {
        Console.WriteLine("    PASS: All-hidden layout round-trips correctly.");
    }
    else
    {
        Console.WriteLine($"    FAIL: All-hidden layout mismatch after reload.\n      Expected: {allHidden}\n      Actual:   {loaded}");
        failures.Add("All-hidden layout round-trip failed");
    }

    // AllSectionsHidden property should be true — checked over every declared ID.
    var stillVisible = HomeLayout.AllSectionIds.Where(loaded.IsVisible).ToList();
    if (stillVisible.Count == 0)
    {
        Console.WriteLine($"    PASS: All {HomeLayout.AllSectionIds.Count} sections are hidden in the loaded layout.");
    }
    else
    {
        Console.WriteLine($"    FAIL: Still visible after reload: {string.Join(", ", stillVisible)}");
        failures.Add("Not every section hidden after reload");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: All-hidden check threw: {ex.Message}");
    failures.Add($"All-hidden check threw: {ex.Message}");
}

// 7. DisplayName returns a non-empty label for all six and echoes an unknown ID.
Console.WriteLine("\n[7/11] Checking DisplayName for all six IDs and unknown...");
try
{
    var allKnown = HomeLayout.AllSectionIds.All(id =>
    {
        var name = HomeLayout.DisplayName(id);
        return !string.IsNullOrEmpty(name) && name != id;
    });

    if (allKnown)
    {
        Console.WriteLine("    PASS: All six known IDs have non-empty display names.");
    }
    else
    {
        Console.WriteLine("    FAIL: Some known IDs lack display names.");
        failures.Add("DisplayName missing for known IDs");
    }

    // Unknown ID should echo itself
    var unknownName = HomeLayout.DisplayName("totally-unknown-section");
    if (unknownName == "totally-unknown-section")
    {
        Console.WriteLine("    PASS: Unknown ID echoes itself as display name.");
    }
    else
    {
        Console.WriteLine($"    FAIL: Unknown ID display name was '{unknownName}', expected the ID itself.");
        failures.Add("Unknown ID DisplayName did not echo ID");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: DisplayName check threw: {ex.Message}");
    failures.Add($"DisplayName check threw: {ex.Message}");
}

// 8. PanesSwapped (M6b): defaults to false on a legacy record, round-trips through DeviceRegistry,
//    and toggling twice returns to the original.
Console.WriteLine("\n[8/11] Checking PanesSwapped default, round-trip and double-toggle...");
try
{
    // A legacy record written before M6a/M6b carried no PanesSwapped at all.
    var legacyLayout = new HomeLayout(HiddenSections: new[] { "media" });
    if (!legacyLayout.PanesSwapped && !HomeLayout.Default.PanesSwapped)
    {
        Console.WriteLine("    PASS: A record without PanesSwapped defaults to false.");
    }
    else
    {
        Console.WriteLine("    FAIL: PanesSwapped did not default to false.");
        failures.Add("PanesSwapped does not default to false");
    }

    // Toggling twice returns to the original record.
    var once = HomeLayout.Default.WithPanesSwapped(true);
    var twice = once.WithPanesSwapped(false);
    if (once.PanesSwapped && !twice.PanesSwapped && twice == HomeLayout.Default)
    {
        Console.WriteLine("    PASS: WithPanesSwapped toggled twice returns to the original layout.");
    }
    else
    {
        Console.WriteLine($"    FAIL: Double toggle did not return to the original.\n      Once: {once}\n      Twice: {twice}");
        failures.Add("PanesSwapped double-toggle did not return to original");
    }

    // Setting the value it already has must return the same record, so the VM's no-op guard holds.
    if (ReferenceEquals(once.WithPanesSwapped(true), once))
    {
        Console.WriteLine("    PASS: WithPanesSwapped(same value) is a no-op (the echo guard's precondition).");
    }
    else
    {
        Console.WriteLine("    FAIL: WithPanesSwapped(same value) produced a new record; the echo guard cannot rely on it.");
        failures.Add("WithPanesSwapped same-value is not a no-op");
    }

    // Hidden sections and PanesSwapped must survive the same round-trip together.
    if (File.Exists(settingsPath))
    {
        File.Delete(settingsPath);
    }
    var panesReg = new DeviceRegistry(root);
    panesReg.SavePairedDevice("TEST-SERIAL-HL2", "Pixel 7");
    var swappedLayout = panesReg.Home.WithSection("clipboard", false).WithPanesSwapped(true);
    panesReg.SaveHome(swappedLayout);

    var panesReloaded = new DeviceRegistry(root).Home;
    if (panesReloaded == swappedLayout && panesReloaded.PanesSwapped && !panesReloaded.IsVisible("clipboard"))
    {
        Console.WriteLine("    PASS: PanesSwapped round-tripped through DeviceRegistry alongside HiddenSections.");
    }
    else
    {
        Console.WriteLine($"    FAIL: PanesSwapped round-trip mismatch.\n      Expected: {swappedLayout}\n      Actual:   {panesReloaded}");
        failures.Add("PanesSwapped round-trip mismatch");
    }

    // A legacy settings.json whose Home has no PanesSwapped key must load as false, not throw.
    var legacyJson = """
    {
      "PairedSerial": "LEGACY-PANES",
      "PairedModel": "Pixel 6",
      "KnownDevices": [
        {
          "Serial": "LEGACY-PANES",
          "Model": "Pixel 6",
          "FirstPairedUtc": "2026-01-01T00:00:00+00:00",
          "Home": { "HiddenSections": ["media"] }
        }
      ]
    }
    """;
    File.WriteAllText(settingsPath, legacyJson);
    var legacyLoaded = new DeviceRegistry(root).Home;
    if (!legacyLoaded.PanesSwapped && !legacyLoaded.IsVisible("media"))
    {
        Console.WriteLine("    PASS: Legacy Home JSON without PanesSwapped loads as false.");
    }
    else
    {
        Console.WriteLine($"    FAIL: Legacy Home JSON did not default PanesSwapped to false. Actual: {legacyLoaded}");
        failures.Add("Legacy Home JSON PanesSwapped default failed");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: PanesSwapped check threw: {ex.Message}");
    failures.Add($"PanesSwapped check threw: {ex.Message}");
}

// 9. UI wiring checks: HomePage.xaml contains all six card bindings and SectionToggles binding.
Console.WriteLine("\n[9/11] UI wiring checks (crude text search — missing file = FAIL)...");
try
{
    var cwd = Directory.GetCurrentDirectory();
    var repoRoot = cwd;
    while (!Directory.Exists(Path.Combine(repoRoot, "DESKTOP")) && Directory.GetParent(repoRoot) != null)
    {
        repoRoot = Directory.GetParent(repoRoot)!.FullName;
    }
    var xamlPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Views", "HomePage.xaml");

    if (!File.Exists(xamlPath))
    {
        Console.WriteLine($"    FAIL: Cannot read {xamlPath}");
        failures.Add($"Missing XAML file: {xamlPath}");
    }
    else
    {
        var xaml = File.ReadAllText(xamlPath);

        // Check for every card's visibility binding. These are the EXACT strings each card
        // uses — "ViewModel.ShowMedia" alone would pass on the substring inside
        // "ViewModel.ShowMediaWidget" and so could not tell the two bindings apart.
        var showBindings = new[]
        {
            "ViewModel.ShowPhone, Mode=OneWay",
            "ViewModel.ShowQuickActions, Mode=OneWay",
            "ViewModel.ShowApps, Mode=OneWay",
            "ViewModel.ShowMediaWidget, Mode=OneWay",
            "ViewModel.ShowPhotosWidget, Mode=OneWay",
            "ViewModel.ShowSharedWidget, Mode=OneWay",
            "ViewModel.ShowClipboard, Mode=OneWay"
        };

        foreach (var binding in showBindings)
        {
            if (xaml.Contains(binding))
            {
                Console.WriteLine($"    PASS: XAML contains {binding}");
            }
            else
            {
                Console.WriteLine($"    FAIL: XAML missing {binding}");
                failures.Add($"XAML missing {binding}");
            }
        }

        // Check for SectionToggles binding (flyout ItemsControl)
        if (xaml.Contains("SectionToggles"))
        {
            Console.WriteLine("    PASS: XAML contains SectionToggles binding.");
        }
        else
        {
            Console.WriteLine("    FAIL: XAML missing SectionToggles binding.");
            failures.Add("XAML missing SectionToggles binding");
        }

        // Check for AllSectionsHidden text block
        if (xaml.Contains("AllSectionsHidden"))
        {
            Console.WriteLine("    PASS: XAML contains AllSectionsHidden binding.");
        }
        else
        {
            Console.WriteLine("    FAIL: XAML missing AllSectionsHidden binding.");
            failures.Add("XAML missing AllSectionsHidden binding");
        }

        // M6b: the Swap-sides button and the code-behind method it drives.
        if (xaml.Contains("ViewModel.SwapPanesCommand"))
        {
            Console.WriteLine("    PASS: XAML contains the Swap-sides button binding (ViewModel.SwapPanesCommand).");
        }
        else
        {
            Console.WriteLine("    FAIL: XAML missing the Swap-sides button binding (ViewModel.SwapPanesCommand).");
            failures.Add("XAML missing ViewModel.SwapPanesCommand");
        }

        // M6c-3: the split is only persisted if the drag actually reaches the view model.
        if (xaml.Contains("ManipulationCompleted=\"OnAppsSplitterDragCompleted\""))
        {
            Console.WriteLine("    PASS: XAML ends the Apps splitter drag on OnAppsSplitterDragCompleted (where the height is saved).");
        }
        else
        {
            Console.WriteLine("    FAIL: XAML has no OnAppsSplitterDragCompleted hook — a dragged split would never be persisted.");
            failures.Add("XAML missing OnAppsSplitterDragCompleted");
        }

        var codeBehindPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Views", "HomePage.xaml.cs");
        if (!File.Exists(codeBehindPath))
        {
            Console.WriteLine($"    FAIL: Cannot read {codeBehindPath}");
            failures.Add($"Missing code-behind file: {codeBehindPath}");
        }
        else if (File.ReadAllText(codeBehindPath).Contains("ApplyPaneOrder"))
        {
            Console.WriteLine("    PASS: HomePage.xaml.cs contains ApplyPaneOrder.");
        }
        else
        {
            Console.WriteLine("    FAIL: HomePage.xaml.cs missing ApplyPaneOrder.");
            failures.Add("HomePage.xaml.cs missing ApplyPaneOrder");
        }

        // M6a regression guard (M6b). ShowMediaWidget/ShowPhotosWidget/ShowSharedWidget each compose
        // a layout flag with a data flag, so they need a raise where BOTH halves change: one inside
        // RefreshSections and at least one at a data site. Counting occurrences is deliberately
        // crude — it cannot tell a good raise from a bad one, only that the data-site raises exist
        // at all, which is exactly what M6a shipped without and what no build or unit test caught.
        var vmPath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "ViewModels", "HomeViewModel.cs");
        if (!File.Exists(vmPath))
        {
            Console.WriteLine($"    FAIL: Cannot read {vmPath}");
            failures.Add($"Missing view-model file: {vmPath}");
        }
        else
        {
            var vm = File.ReadAllText(vmPath);

            if (vm.Contains("public double? AppsPaneHeight") && vm.Contains("public void SaveAppsPaneHeight("))
            {
                Console.WriteLine("    PASS: HomeViewModel exposes AppsPaneHeight and SaveAppsPaneHeight (M6c-3).");
            }
            else
            {
                Console.WriteLine("    FAIL: HomeViewModel does not expose AppsPaneHeight / SaveAppsPaneHeight; the split could not persist.");
                failures.Add("HomeViewModel missing AppsPaneHeight wiring");
            }

            foreach (var combined in new[] { "ShowMediaWidget", "ShowPhotosWidget", "ShowSharedWidget" })
            {
                var needle = $"OnPropertyChanged(nameof({combined}))";
                var count = 0;
                for (var i = vm.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                     i = vm.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                {
                    count++;
                }

                if (count >= 2)
                {
                    Console.WriteLine($"    PASS: HomeViewModel raises {combined} in {count} places (layout + data sites).");
                }
                else
                {
                    Console.WriteLine($"    FAIL: HomeViewModel raises {combined} only {count} time(s); the data half will not refresh the card.");
                    failures.Add($"{combined} raised fewer than twice in HomeViewModel");
                }
            }
        }

        Console.WriteLine("    (These crude checks exist so a future session that unwires a card fails loudly.)");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: UI wiring check threw: {ex.Message}");
    failures.Add($"UI wiring check threw: {ex.Message}");
}

// 10. D-057 guard: no harness may construct a DeviceRegistry against the real store.
// Constructing one with no root argument resolves to %LOCALAPPDATA%\Linc — the owner's pairing and
// its unrecoverable TLS certificate. Twice now a harness has destroyed that file, so the rule is
// no longer "remember to back it up" but "never open it": every harness construction passes a
// throwaway root. This is a crude text search over EVERY tools\*\Program.cs — a directory scan,
// not a hardcoded list, so a harness written next session is covered the moment the file exists
// (H1 shipped this with four filenames baked in; a fifth harness would have walked straight past
// it). Like the [9/11] wiring checks above it cannot tell a good root from a bad one, only that
// the argument is there at all, which is precisely what a copy-paste of the old style would omit.
//
// blescan is the one exemption: it MUST open the real store, because its whole job is matching a
// BLE beacon against the owner's real pinned certificates and a temp root has none. That is safe
// only while it never writes, so instead of the root check it gets a stricter one — no Save* call
// on a registry, at all. If a future session adds one, this fails.
Console.WriteLine("\n[10/11] D-057 check: no harness constructs DeviceRegistry against the real store...");
try
{
    var cwd = Directory.GetCurrentDirectory();
    var repoRoot = cwd;
    while (!Directory.Exists(Path.Combine(repoRoot, "DESKTOP")) && Directory.GetParent(repoRoot) != null)
    {
        repoRoot = Directory.GetParent(repoRoot)!.FullName;
    }

    const string BleExempt = "blescan";
    var toolsDir = Path.Combine(repoRoot, "tools");
    var harnesses = Directory.Exists(toolsDir)
        ? Directory.GetDirectories(toolsDir)
            .Where(d => File.Exists(Path.Combine(d, "Program.cs")))
            .Select(d => Path.GetFileName(d)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList()
        : new List<string>();

    if (harnesses.Count == 0)
    {
        Console.WriteLine($"    FAIL: No tools\\*\\Program.cs found under {toolsDir} — the scan is not looking where it thinks it is.");
        failures.Add("D-057 scan found no harness sources");
    }
    else
    {
        Console.WriteLine($"    (scanning {harnesses.Count} harness(es): {string.Join(", ", harnesses)})");
    }

    foreach (var harness in harnesses)
    {
        var programPath = Path.Combine(repoRoot, "tools", harness, "Program.cs");
        if (!File.Exists(programPath))
        {
            Console.WriteLine($"    FAIL: Cannot read {programPath}");
            failures.Add($"Missing harness source: {programPath}");
            continue;
        }

        var source = File.ReadAllText(programPath);

        if (harness == BleExempt)
        {
            // Exempt from the root rule, held to a harder one: read-only forever. Crude on purpose
            // — any "…Save<something>(" token in the file trips it, which is the right side to err
            // on for a harness holding the owner's unrecoverable pairing certificate.
            var writes = new List<string>();
            const string SaveNeedle = "Save";
            for (var i = source.IndexOf(SaveNeedle, StringComparison.Ordinal); i >= 0;
                 i = source.IndexOf(SaveNeedle, i + SaveNeedle.Length, StringComparison.Ordinal))
            {
                if (i > 0 && (char.IsLetterOrDigit(source[i - 1]) || source[i - 1] == '_'))
                {
                    continue; // part of a longer identifier, e.g. "NotSaveable"
                }
                var close = source.IndexOf('(', i);
                if (close < 0) continue;
                var token = source[i..close];
                if (token.Length > 0 && token.All(c => char.IsLetterOrDigit(c) || c == '_'))
                {
                    writes.Add(token);
                }
            }

            if (writes.Count == 0)
            {
                Console.WriteLine($"    PASS: {harness} is exempt from the root rule and contains no Save*( call — still read-only.");
            }
            else
            {
                Console.WriteLine($"    FAIL: {harness} opens the REAL store and now calls {string.Join(", ", writes.Distinct())} — its exemption only holds while it never writes.");
                failures.Add($"{harness} writes to the real store ({string.Join(", ", writes.Distinct())})");
            }
            continue;
        }

        const string Needle = "new DeviceRegistry(";
        var bare = 0;
        var total = 0;
        for (var i = source.IndexOf(Needle, StringComparison.Ordinal); i >= 0;
             i = source.IndexOf(Needle, i + Needle.Length, StringComparison.Ordinal))
        {
            if (i > 0 && source[i - 1] == '"')
            {
                continue; // the needle's own literal, three lines up
            }
            total++;
            if (source[i + Needle.Length] == ')')
            {
                bare++;
            }
        }

        if (bare == 0)
        {
            Console.WriteLine($"    PASS: {harness} has {total} registry construction(s), all with an explicit root.");
        }
        else
        {
            Console.WriteLine($"    FAIL: {harness} has {bare} of {total} registry construction(s) with NO root — they write the owner's real settings.json.");
            failures.Add($"{harness} constructs DeviceRegistry against the real store ({bare} site(s))");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: D-057 check threw: {ex.Message}");
    failures.Add($"D-057 check threw: {ex.Message}");
}

// 11. AppsPaneHeight (M6c-3): null means "use the default split", a dragged height round-trips
// through DeviceRegistry alongside the rest of the layout, and setting the value it already holds
// is a no-op — which is the precondition the view model's echo guard relies on.
Console.WriteLine("\n[11/11] Checking AppsPaneHeight default, round-trip, no-op guard and legacy JSON...");
try
{
    if (HomeLayout.Default.AppsPaneHeight is null && new HomeLayout(HiddenSections: new[] { "media" }).AppsPaneHeight is null)
    {
        Console.WriteLine("    PASS: AppsPaneHeight defaults to null (= use the default split).");
    }
    else
    {
        Console.WriteLine("    FAIL: AppsPaneHeight did not default to null.");
        failures.Add("AppsPaneHeight does not default to null");
    }

    var sized = HomeLayout.Default.WithAppsPaneHeight(320);
    if (sized.AppsPaneHeight == 320 && ReferenceEquals(sized.WithAppsPaneHeight(320), sized))
    {
        Console.WriteLine("    PASS: WithAppsPaneHeight(same value) is a no-op (the echo guard's precondition).");
    }
    else
    {
        Console.WriteLine($"    FAIL: WithAppsPaneHeight set/no-op wrong. Actual: {sized.AppsPaneHeight}");
        failures.Add("WithAppsPaneHeight set or no-op failed");
    }

    if (ReferenceEquals(sized.WithAppsPaneHeight(320.2), sized))
    {
        Console.WriteLine("    PASS: a sub-pixel difference is not a change (a drag cannot save on jitter).");
    }
    else
    {
        Console.WriteLine("    FAIL: a sub-pixel difference produced a new record.");
        failures.Add("Sub-pixel AppsPaneHeight change not absorbed");
    }

    var cleared = sized.WithAppsPaneHeight(null);
    if (cleared.AppsPaneHeight is null && ReferenceEquals(cleared.WithAppsPaneHeight(null), cleared))
    {
        Console.WriteLine("    PASS: clearing back to null works and is itself idempotent.");
    }
    else
    {
        Console.WriteLine("    FAIL: clearing AppsPaneHeight back to null misbehaved.");
        failures.Add("AppsPaneHeight clear-to-null failed");
    }

    // Round-trip through DeviceRegistry together with the other two fields.
    if (File.Exists(settingsPath))
    {
        File.Delete(settingsPath);
    }
    var heightReg = new DeviceRegistry(root);
    heightReg.SavePairedDevice("TEST-SERIAL-HL3", "Pixel 7");
    var heightLayout = heightReg.Home.WithSection("media", false).WithPanesSwapped(true).WithAppsPaneHeight(412);
    heightReg.SaveHome(heightLayout);

    var heightLoaded = new DeviceRegistry(root).Home;
    if (heightLoaded == heightLayout && heightLoaded.AppsPaneHeight == 412 && heightLoaded.PanesSwapped && !heightLoaded.IsVisible("media"))
    {
        Console.WriteLine("    PASS: AppsPaneHeight round-tripped through DeviceRegistry alongside HiddenSections and PanesSwapped.");
    }
    else
    {
        Console.WriteLine($"    FAIL: AppsPaneHeight round-trip mismatch.\n      Expected: {heightLayout}\n      Actual:   {heightLoaded}");
        failures.Add("AppsPaneHeight round-trip mismatch");
    }

    // Equals/GetHashCode must agree about the new field, or a saved record stops comparing equal
    // to its reload and the no-op guard silently stops guarding.
    if (heightLoaded.GetHashCode() == heightLayout.GetHashCode()
        && HomeLayout.Default.WithAppsPaneHeight(100) != HomeLayout.Default.WithAppsPaneHeight(200))
    {
        Console.WriteLine("    PASS: Equals/GetHashCode account for AppsPaneHeight (equal records hash equal, different heights differ).");
    }
    else
    {
        Console.WriteLine("    FAIL: Equals/GetHashCode do not account for AppsPaneHeight.");
        failures.Add("AppsPaneHeight not covered by Equals/GetHashCode");
    }

    // A settings.json written before M6c-3 has no AppsPaneHeight key: it must load as null.
    var legacyHeightJson = """
    {
      "PairedSerial": "LEGACY-HEIGHT",
      "PairedModel": "Pixel 6",
      "KnownDevices": [
        {
          "Serial": "LEGACY-HEIGHT",
          "Model": "Pixel 6",
          "FirstPairedUtc": "2026-01-01T00:00:00+00:00",
          "Home": { "HiddenSections": ["media"], "PanesSwapped": true }
        }
      ]
    }
    """;
    File.WriteAllText(settingsPath, legacyHeightJson);
    var legacyHeight = new DeviceRegistry(root).Home;
    if (legacyHeight.AppsPaneHeight is null && legacyHeight.PanesSwapped)
    {
        Console.WriteLine("    PASS: Legacy Home JSON without AppsPaneHeight loads as null (default split).");
    }
    else
    {
        Console.WriteLine($"    FAIL: Legacy Home JSON did not default AppsPaneHeight to null. Actual: {legacyHeight}");
        failures.Add("Legacy Home JSON AppsPaneHeight default failed");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: AppsPaneHeight check threw: {ex.Message}");
    failures.Add($"AppsPaneHeight check threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------------
// M15b D4: Home says the connection's state in exactly ONE place.
//
// This lives here rather than in a new harness because homelayoutsim already owns Home's layout
// contract and already pins Home strings; a second harness for one file would be a worse home for
// it. The check is deliberately crude — it reads the production XAML and fails if disconnect
// vocabulary appears as a literal anywhere in it. Everything Home legitimately says about the link
// arrives through the ConnectionCaption binding on the Phone card, so a literal in this file is
// always a second statement creeping back in (M15b D2, and the third time this complaint has been
// raised).
//
// XML comments are stripped first: HomePage.xaml's own comments discuss the offline banner by
// name, and a check that fires on a comment would be noise rather than evidence.
Console.WriteLine("\n[M15b D4] Home states the connection once, in the Phone card...");
try
{
    var repoRoot = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(repoRoot, "DESKTOP")) && Directory.GetParent(repoRoot) != null)
    {
        repoRoot = Directory.GetParent(repoRoot)!.FullName;
    }
    var homePagePath = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Views", "HomePage.xaml");
    if (!File.Exists(homePagePath))
    {
        Console.WriteLine($"    FAIL: cannot read {homePagePath} (run this with CWD = ...\\yellow\\Linc)");
        failures.Add("Cannot read HomePage.xaml for the D4 connection-statement check");
    }
    else
    {
        var xaml = File.ReadAllText(homePagePath);
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            xaml, "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline);

        // Only what is RENDERED: the values of Text=/Content=/PlaceholderText= that are literals
        // rather than bindings. Scanning the raw file instead would fire on identifiers —
        // ShowOfflineBanner and OfflineBannerText are binding names, not sentences, and the first
        // draft of this check failed on exactly that.
        var literals = System.Text.RegularExpressions.Regex
            .Matches(stripped, @"(?:Text|Content|PlaceholderText|ToolTipService\.ToolTip)\s*=\s*""([^""]*)""")
            .Select(m => m.Groups[1].Value)
            .Where(v => !v.TrimStart().StartsWith('{'))
            .ToList();

        // The vocabulary a second connection statement would have to use. "Connect your phone"
        // is deliberately NOT here: HomePage's never-paired empty state uses it as a call to
        // action for a phone that has never existed, which is a different fact from a link that
        // is currently down.
        string[] banned =
        [
            "Disconnected", "disconnected", "Not connected", "not connected",
            "No phone connected", "Offline copy", "offline copy", "went offline",
        ];
        var found = literals
            .SelectMany(text => banned.Where(b => text.Contains(b, StringComparison.Ordinal))
                                      .Select(b => (Banned: b, Text: text)))
            .ToList();
        if (found.Count > 0)
        {
            Console.WriteLine(
                "    FAIL: Views\\HomePage.xaml renders a second connection statement: " +
                string.Join("; ", found.Select(f => $"\"{f.Banned}\" in \"{f.Text}\"")) +
                ". Home has one connection statement and it is the Phone card's ConnectionCaption binding (M15b D2).");
            failures.Add("A second connection-state string reappeared in Views\\HomePage.xaml");
        }
        else
        {
            Console.WriteLine("    PASS: no connection-state literal in Views\\HomePage.xaml.");
        }

        // The counterweight: the check above must not be satisfiable by deleting the one
        // statement Home is supposed to have.
        if (stripped.Contains("ViewModel.ConnectionCaption", StringComparison.Ordinal))
        {
            Console.WriteLine("    PASS: the Phone card still binds ConnectionCaption — the one statement survives.");
        }
        else
        {
            Console.WriteLine("    FAIL: Views\\HomePage.xaml no longer binds ViewModel.ConnectionCaption.");
            failures.Add("Home lost its single connection statement (ConnectionCaption binding)");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: D4 connection-statement check threw: {ex.Message}");
    failures.Add($"D4 connection-statement check threw: {ex.Message}");
}

// ---------------------------------------------------------------------------------------------
// M15b Part F: Apps switched off gives its space to the tabs panel.
//
// The rows live in XAML and the collapse lives in code-behind, neither of which a net8.0 console
// can instantiate, so this is a source-text check of the three things that make the difference
// between "hidden" and "removed". Hiding the card alone leaves the split's share of the column
// empty — the gap the owner reported.
Console.WriteLine("\n[M15b F] Apps off collapses its row AND the splitter row...");
try
{
    var repoRoot = Directory.GetCurrentDirectory();
    while (!Directory.Exists(Path.Combine(repoRoot, "DESKTOP")) && Directory.GetParent(repoRoot) != null)
    {
        repoRoot = Directory.GetParent(repoRoot)!.FullName;
    }
    var viewsDir = Path.Combine(repoRoot, "DESKTOP", "Linc.Desktop", "Views");
    var xamlPath = Path.Combine(viewsDir, "HomePage.xaml");
    var codePath = Path.Combine(viewsDir, "HomePage.xaml.cs");
    if (!File.Exists(xamlPath) || !File.Exists(codePath))
    {
        Console.WriteLine("    FAIL: cannot read HomePage.xaml / HomePage.xaml.cs");
        failures.Add("Cannot read HomePage for the Part F check");
    }
    else
    {
        var xaml = File.ReadAllText(xamlPath);
        var code = File.ReadAllText(codePath);

        void Sub(bool ok, string claim)
        {
            Console.WriteLine($"    {(ok ? "PASS" : "FAIL")}: {claim}");
            if (!ok)
            {
                failures.Add(claim);
            }
        }

        Sub(xaml.Contains("x:Name=\"AppsSplitterRow\"", StringComparison.Ordinal),
            "the splitter's row is named, so it can be collapsed rather than left to Auto");
        Sub(xaml.Contains("x:Name=\"AppsSplitter\"", StringComparison.Ordinal) &&
            xaml.Contains("Visibility=\"{x:Bind ViewModel.ShowApps, Mode=OneWay}\"", StringComparison.Ordinal),
            "the splitter itself disappears with the Apps section");

        // The load-bearing pair. Negative proof (b) removes these: hide Apps without collapsing
        // its row/splitter, and this check must fail.
        var collapse = code.IndexOf("if (!ViewModel.ShowApps)", StringComparison.Ordinal);
        Sub(collapse >= 0, "ApplyAppsHeight branches on ShowApps before anything else");
        if (collapse >= 0)
        {
            // Scope the search to the collapse branch, not the whole file: an assignment of zero
            // anywhere else would satisfy a naive grep and prove nothing (GUIDE.md §4.4).
            var branch = code.Substring(collapse, Math.Min(700, code.Length - collapse));
            Sub(branch.Contains("AppsRow.Height = new GridLength(0", StringComparison.Ordinal),
                "…and that branch zeroes the Apps row");
            Sub(branch.Contains("AppsSplitterRow.Height = new GridLength(0", StringComparison.Ordinal),
                "…and zeroes the splitter row too — hiding the card alone would leave the gap");
            Sub(branch.Contains("TabsRow.Height = new GridLength(1, GridUnitType.Star)", StringComparison.Ordinal),
                "…and gives the freed space to the tabs panel");
        }
        Sub(code.Contains("nameof(HomeViewModel.ShowApps)", StringComparison.Ordinal),
            "toggling the Apps section re-runs the layout instead of waiting for a device switch");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"    FAIL: Part F check threw: {ex.Message}");
    failures.Add($"Part F check threw: {ex.Message}");
}

// A killed run leaks this folder instead of damaging the owner's pairing (D-057).
try { Directory.Delete(root, recursive: true); } catch (IOException) { }

// Summary
Console.WriteLine("\n=== SUMMARY ===");
if (failures.Count == 0)
{
    Console.WriteLine("PASS: All verification checks succeeded.");
    return 0;
}
else
{
    Console.WriteLine($"FAIL: {failures.Count} check(s) failed:");
    foreach (var f in failures)
    {
        Console.WriteLine($"  - {f}");
    }
    return 1;
}