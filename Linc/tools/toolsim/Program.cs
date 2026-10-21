// toolsim: the Tools page's pure parts (Vibe/claude-notes/linc-tools/PLAN.md §5), with captured
// phone output as fixtures. Exits 0 when every check passes, 1 when any fails.
using Linc.ToolSim;

var failures = new List<string>();
void Check(bool ok, string what)
{
    Console.WriteLine($"    {(ok ? "ok  " : "FAIL")} {what}");
    if (!ok) failures.Add(what);
}

Console.WriteLine("[core] quoting and the pretend phone");
CoreTests.Run(Check);
Console.WriteLine("[type, rescue]");
TypeRescueTests.Run(Check);
Console.WriteLine("[apps]");
AppsTests.Run(Check);
Console.WriteLine("[capture, console]");
CaptureConsoleTests.Run(Check);
Console.WriteLine("[health, system]");
HealthSystemTests.Run(Check);
Console.WriteLine("[commands]");
CommandsTests.Run(Check);

Console.WriteLine(failures.Count == 0 ? "\ntoolsim: all checks passed" : $"\ntoolsim: {failures.Count} check(s) failed");
return failures.Count == 0 ? 0 : 1;
