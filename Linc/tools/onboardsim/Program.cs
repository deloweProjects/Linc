using Linc.Desktop.Services;
using static Linc.Desktop.Services.OnboardingStep;

// Verifies the guided-onboarding step machine (M2b, part 2) headlessly: OnboardingFlow is linked
// verbatim from the desktop, so this is the exact logic OnboardingViewModel drives — asserted with
// no app launch, no UI-Automation, no cursor movement, and nothing touched on the phone.
//
//   dotnet run --project tools/onboardsim

var failures = new List<string>();

void Expect(string name, OnboardingStep actual, OnboardingStep expected)
{
    var pass = actual == expected;
    Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name} — got {actual}, expected {expected}");
    if (!pass)
    {
        failures.Add(name);
    }
}

Console.WriteLine("Happy path: Intro → Enable → Detect → Install → Done");
var s = Intro;
Expect("start", s = OnboardingFlow.Start(s), EnableDebugging);
Expect("begin-detection", s = OnboardingFlow.BeginDetection(s), DetectPair);
Expect("connecting→install", s = OnboardingFlow.Connecting(s), Install);
Expect("connected→done", s = OnboardingFlow.Connected(s), Done);

Console.WriteLine("USB fast path: a phone appears before pairing → Connecting straight from Detect");
s = DetectPair;
Expect("usb-connecting", s = OnboardingFlow.Connecting(s), Install);
Expect("usb-connected", OnboardingFlow.Connected(s), Done);

Console.WriteLine("Back navigation is the inverse and bottoms out at Intro");
Expect("back-from-detect", OnboardingFlow.Back(DetectPair), EnableDebugging);
Expect("back-from-enable", OnboardingFlow.Back(EnableDebugging), Intro);
Expect("back-from-intro-noop", OnboardingFlow.Back(Intro), Intro);

Console.WriteLine("Idempotence / out-of-order events never move the wrong way");
Expect("start-noop-off-intro", OnboardingFlow.Start(DetectPair), DetectPair);
Expect("begin-noop-off-enable", OnboardingFlow.BeginDetection(DetectPair), DetectPair);
// A reconnect blip (Connecting) after the wizard has finished must not drag it back to Install.
Expect("connecting-noop-after-done", OnboardingFlow.Connecting(Done), Done);
Expect("connecting-noop-on-intro", OnboardingFlow.Connecting(Intro), Intro);
// Connected always wins — even a stray one mid-flow means the phone answered.
Expect("connected-from-enable", OnboardingFlow.Connected(EnableDebugging), Done);

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("onboardsim: all checks green.");
    return 0;
}
Console.WriteLine($"onboardsim: {failures.Count} FAILED — {string.Join(", ", failures)}");
return 1;
