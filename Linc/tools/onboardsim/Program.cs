using Linc.Desktop.Services;
using static Linc.Desktop.Services.OnboardingStep;

// Verifies the add-a-phone flow (D-079) headlessly: OnboardingFlow is linked verbatim from the
// desktop, so this is the exact logic OnboardingViewModel drives — asserted with no app launch,
// no UI-Automation, no cursor movement, and nothing touched on the phone.
//
//   dotnet run --project tools/onboardsim

var failures = new List<string>();

void Expect<T>(string name, T actual, T expected)
{
    var pass = EqualityComparer<T>.Default.Equals(actual, expected);
    Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name} — got {actual}, expected {expected}");
    if (!pass)
    {
        failures.Add(name);
    }
}

Console.WriteLine("New phone over USB: Choose → Prepare USB → Checking → Done");
var s = Choose;
Expect("choose-usb", s = OnboardingFlow.Choose(s, OnboardingRoute.Usb), PrepareUsb);
Expect("usb-phone-found", s = OnboardingFlow.PhoneFound(s), Checking);
Expect("usb-connected", s = OnboardingFlow.Connected(s), Done);

Console.WriteLine("New phone over Wi-Fi: Choose → Prepare Wi-Fi → (paired) Checking → Done");
s = Choose;
Expect("choose-wifi", s = OnboardingFlow.Choose(s, OnboardingRoute.Wifi), PrepareWifi);
Expect("wifi-paired", s = OnboardingFlow.PhoneFound(s), Checking);
Expect("wifi-connected", OnboardingFlow.Connected(s), Done);

Console.WriteLine("Skips: a known phone, or 'already set up', go straight to Checking");
Expect("known-phone-skips-pairing", OnboardingFlow.Choose(Choose, OnboardingRoute.KnownPhone), Checking);
Expect("already-set-up-skips-pairing", OnboardingFlow.Choose(Choose, OnboardingRoute.AlreadySetUp), Checking);

Console.WriteLine("Back always returns to the choice, from any screen before Done");
Expect("back-from-usb", OnboardingFlow.Back(PrepareUsb), Choose);
Expect("back-from-wifi", OnboardingFlow.Back(PrepareWifi), Choose);
Expect("back-from-checking", OnboardingFlow.Back(Checking), Choose);
Expect("back-noop-on-choose", OnboardingFlow.Back(Choose), Choose);
Expect("back-noop-on-done", OnboardingFlow.Back(Done), Done);

Console.WriteLine("Out-of-order events never move the wrong way");
// A link that was already up (or reconnects) while the user is still choosing must not finish the flow.
Expect("connected-noop-on-choose", OnboardingFlow.Connected(Choose), Choose);
// Seeing the phone only advances from a prepare screen.
Expect("found-noop-on-choose", OnboardingFlow.PhoneFound(Choose), Choose);
Expect("found-noop-on-checking", OnboardingFlow.PhoneFound(Checking), Checking);
Expect("found-noop-on-done", OnboardingFlow.PhoneFound(Done), Done);
// A route can only be chosen from Choose.
Expect("choose-noop-off-choose", OnboardingFlow.Choose(Checking, OnboardingRoute.Usb), Checking);
// The phone can answer before the user presses "my phone is plugged in".
Expect("connected-from-prepare-usb", OnboardingFlow.Connected(PrepareUsb), Done);

Console.WriteLine("Done says whether the phone was new or already added");
string[] knownBefore = ["2C141FDH20089M"];
Expect("readded-known-phone", OnboardingFlow.WasAlreadyAdded("2c141fdh20089m", knownBefore), true);
Expect("new-phone", OnboardingFlow.WasAlreadyAdded("131722549O000808", knownBefore), false);
Expect("no-serial-is-not-known", OnboardingFlow.WasAlreadyAdded("", knownBefore), false);

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("onboardsim: all checks green.");
    return 0;
}
Console.WriteLine($"onboardsim: {failures.Count} FAILED — {string.Join(", ", failures)}");
return 1;
