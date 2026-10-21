using Linc.Desktop.Services.Tools;

namespace Linc.ToolSim;

internal static class CoreTests
{
    public static void Run(Action<bool, string> check)
    {
        check(AdbQuote.Arg("abc") == "'abc'", "Arg wraps a plain word in single quotes");
        check(AdbQuote.Arg("it's") == "'it'\\''s'", "Arg writes ' as '\\''");
        check(AdbQuote.Arg("$(rm -rf /); `x`") == "'$(rm -rf /); `x`'", "Arg keeps shell syntax literal");

        var hello = AdbQuote.InputTextChunks("hello world");
        check(hello.Count == 1 && hello[0] == "'hello%sworld'", "a space becomes %s");
        var pin = AdbQuote.InputTextChunks("1234");
        check(pin.Count == 1 && pin[0] == "'1234'", "a PIN goes as one chunk");
        var percent = AdbQuote.InputTextChunks("50%sale");
        check(percent.Count == 2 && percent[0] == "'50%'" && percent[1] == "'sale'", "a typed %s is split so it stays %s");
        var quote = AdbQuote.InputTextChunks("don't");
        check(quote.Count == 1 && quote[0] == "'don'\\''t'", "a quote survives typing");
        var accents = AdbQuote.InputTextChunks("café");
        check(accents.Count == 1 && accents[0] == "'caf'", "characters input text can't send are left out");
        check(!AdbQuote.IsTypeable('é') && AdbQuote.IsTypeable('~') && !AdbQuote.IsTypeable('\n'), "IsTypeable is printable ASCII");
        var longText = AdbQuote.InputTextChunks(new string('a', 250));
        check(longText.Count == 3 && longText.All(c => c.Length <= 102), "long text is split into chunks of 100");

        var dir = Path.Combine(Path.GetTempPath(), "linc-toolsim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.json"), "{\"dumpsys\": \"short\", \"dumpsys battery\": \"level: 82\"}");
            File.WriteAllText(Path.Combine(dir, "b.json"), "{ not json");
            var fake = new FakeAdbShell(dir);
            check(fake.Answer("dumpsys battery") == "level: 82", "the pretend phone answers with the longest matching prefix");
            check(fake.Answer("dumpsys wifi") == "short", "a shorter prefix answers the rest");
            check(fake.Answer("getprop") == "", "an unknown command gives empty output");
            var devices = fake.DevicesAsync(CancellationToken.None).Result;
            check(devices.Count == 1 && devices[0].Authorized && devices[0].Label == "Pixel 7 · USB", "the pretend phone is one allowed USB phone");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
        check(new ToolsDevice("x", "Pixel 7", false, false).Label == "Pixel 7 · Not allowed yet", "a phone that hasn't allowed this PC says so");
    }
}
