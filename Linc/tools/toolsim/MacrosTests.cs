using Linc.Desktop.Services.Tools;

namespace Linc.ToolSim;

/// <summary>Tools › Macros: the steps, the templates, and the file they are kept in.</summary>
internal static class MacrosTests
{
    public static void Run(Action<bool, string> check)
    {
        check(MacroSteps.Kinds.Select(k => k.Kind).Distinct().Count() == MacroSteps.Kinds.Count, "every step kind is in the menu once");
        check(MacroSteps.Templates[0].Name == "Presenting", "the templates begin with Presenting");
        var bad = MacroSteps.Templates.SelectMany(m => m.Steps.Select(s => (m.Name, s, why: MacroSteps.Problem(s))))
            .Where(x => x.why is not null).Select(x => $"{x.Name}: {x.s.Kind} ({x.why})").ToList();
        check(bad.Count == 0, "every template step is complete" + (bad.Count > 0 ? ": " + string.Join(", ", bad) : ""));
        check(MacroSteps.Templates.Select(m => m.Id).Distinct().Count() == MacroSteps.Templates.Count, "template ids are unique");
        check(MacroSteps.Need(MacroStepKind.Ring) == MacroNeed.Link && MacroSteps.Need(MacroStepKind.Tap) == MacroNeed.Adb
              && MacroSteps.Need(MacroStepKind.Wait) == MacroNeed.Nothing && MacroSteps.Need(MacroStepKind.TapElement) == MacroNeed.Adb,
            "each step says whether it needs ADB, the link or nothing");

        // What a step needs filled in.
        check(MacroSteps.Problem(new(MacroStepKind.OpenApp, "com.spotify.music")) is null, "a package opens");
        check(MacroSteps.Problem(new(MacroStepKind.OpenApp, "spotify; reboot")) is not null, "a package with a command in it is refused");
        check(MacroSteps.Problem(new(MacroStepKind.OpenLink, "https://linc.app")) is null, "an https link opens");
        check(MacroSteps.Problem(new(MacroStepKind.OpenLink, "file:///sdcard")) is not null, "a file: link is refused");
        check(MacroSteps.Problem(new(MacroStepKind.Key, "HOME && reboot")) is not null, "a key with a command in it is refused");
        check(MacroSteps.Problem(new(MacroStepKind.Dnd, "maybe")) is not null, "DND is on or off");
        check(MacroSteps.Problem(new(MacroStepKind.Media, "prev")) is null && MacroSteps.Problem(new(MacroStepKind.Media, "stop")) is not null,
            "media actions are the phone's");
        check(MacroSteps.Problem(new(MacroStepKind.Wait, Seconds: 0)) is not null && MacroSteps.Problem(new(MacroStepKind.Wait, Seconds: 9999)) is not null,
            "a wait is between 0.1 s and 10 minutes");
        check(MacroSteps.Problem(new(MacroStepKind.PcOpen, " ")) is not null, "Open on this PC needs something to open");

        // The "Add step" box.
        check(MacroSteps.FromInput(MacroStepKind.Wait, "1.5") == new MacroStep(MacroStepKind.Wait, Seconds: 1.5), "a wait of 1.5 s");
        check(MacroSteps.FromInput(MacroStepKind.Swipe, "540, 1800, 540 600") == new MacroStep(MacroStepKind.Swipe, X: 540, Y: 1800, X2: 540, Y2: 600),
            "a swipe's four numbers");
        check(MacroSteps.Problem(MacroSteps.FromInput(MacroStepKind.Tap, "540")) is not null, "a tap needs both numbers");
        check(MacroSteps.FromInput(MacroStepKind.Dnd, " ON ").Value == "on", "DND's box is tidied");
        check(MacroSteps.FromInput(MacroStepKind.WaitText, "Done").Seconds == MacroSteps.DefaultWaitSeconds, "a wait-until waits 10 s by default");

        // A catalogue command comes in as a Shell step, filled in.
        var openApp = CommandCatalog.All.First(e => e.Id == "open-app");
        var filled = CommandCatalog.Fill(openApp, new Dictionary<string, string> { ["package"] = "com.android.settings" });
        check(filled?.Shell is { } shell && MacroSteps.Command(new(MacroStepKind.Shell, shell)) == shell, "a filled-in command runs as it reads");
        check(MacroSteps.Problem(new(MacroStepKind.Shell, openApp.Template)) == "Fill in {package}", "a picked command with a value left to fill in says which");

        // Reorder.
        check(MacroSteps.Moved(["a", "b", "c"], 2, 0).SequenceEqual(["c", "a", "b"]), "a step moves up to the top");
        check(MacroSteps.Moved(["a", "b"], 0, 5).SequenceEqual(["a", "b"]), "a move off the end changes nothing");

        // The file.
        var macros = new List<Macro>
        {
            new("m1", "Night", "\uE708", [new(MacroStepKind.Dnd, "on"), new(MacroStepKind.Wait, Seconds: 2), new(MacroStepKind.PcOpen, @"C:\x.txt")]),
            new("m2", "Empty", "\uE768", []),
        };
        var back = MacroSteps.TryDeserialize(MacroSteps.Serialize(macros));
        check(back is { Count: 2 } && back[0].Name == "Night" && back[0].Steps.SequenceEqual(macros[0].Steps) && back[1].Steps.Count == 0,
            "macros come back from the file the same");
        var newer = """[{"Id":"x","Name":"X","Glyph":"","Steps":[{"Kind":"Hologram","Value":"1"},{"Kind":"Ring"}],"Colour":"red"}]""";
        var fromNewer = MacroSteps.TryDeserialize(newer);
        check(fromNewer is { Count: 1 } && fromNewer[0].Steps.Count == 1 && fromNewer[0].Steps[0].Kind == MacroStepKind.Ring,
            "a newer Linc's file opens, without the steps this one doesn't know");
        check(MacroSteps.TryDeserialize("not json") is null && MacroSteps.TryDeserialize("{}") is null, "a broken file reads as none, not empty");
        check(MacroSteps.Deserialize("not json").Count == 0, "Deserialize never throws");
        check(MacroSteps.FailureText(1, new(MacroStepKind.Ring), "No phone is connected.") == "Step 2 (Ring the phone) failed: No phone is connected.",
            "a failure names the step");
    }
}
