namespace Linc.Desktop.Services;

/// <summary>
/// The add-a-phone flow's stages (D-079). One front door for first setup, adding another phone
/// and bringing back one that was closed: choose how, prepare (if pairing is needed), check the
/// connection, done — and "done" says whether the phone was new or already added.
/// </summary>
public enum OnboardingStep
{
    /// <summary>Pick a phone you've used before, or how to connect a new one (USB / Wi-Fi / already set up).</summary>
    Choose,

    /// <summary>New phone over a cable: turn on USB debugging and plug in.</summary>
    PrepareUsb,

    /// <summary>New phone over Wi-Fi: turn on Wireless debugging, then scan the QR or type the code.</summary>
    PrepareWifi,

    /// <summary>Linc is finding and connecting the phone (and installing the companion if needed).</summary>
    Checking,

    /// <summary>Connected: "Added X" or "X was already added — connected again".</summary>
    Done,
}

/// <summary>How the user said they want to connect, on the Choose screen.</summary>
public enum OnboardingRoute { Usb, Wifi, AlreadySetUp, KnownPhone }

/// <summary>
/// The flow's transitions as pure functions — no WinUI, no services — so tools/onboardsim can drive
/// it headlessly. Every transition is a no-op unless it applies to the current step, so the flow is
/// safe to re-drive with repeated or out-of-order events.
/// </summary>
public static class OnboardingFlow
{
    /// <summary>Choose → the route's next screen. Known phones and "already set up" skip pairing.</summary>
    public static OnboardingStep Choose(OnboardingStep current, OnboardingRoute route) =>
        current != OnboardingStep.Choose ? current : route switch
        {
            OnboardingRoute.Usb => OnboardingStep.PrepareUsb,
            OnboardingRoute.Wifi => OnboardingStep.PrepareWifi,
            _ => OnboardingStep.Checking,
        };

    /// <summary>
    /// The phone showed up (USB seen, wireless pairing succeeded, or "my phone is ready"):
    /// from a prepare screen to Checking.
    /// </summary>
    public static OnboardingStep PhoneFound(OnboardingStep current) =>
        current is OnboardingStep.PrepareUsb or OnboardingStep.PrepareWifi ? OnboardingStep.Checking : current;

    /// <summary>Back to the choice, from any screen before Done.</summary>
    public static OnboardingStep Back(OnboardingStep current) =>
        current is OnboardingStep.PrepareUsb or OnboardingStep.PrepareWifi or OnboardingStep.Checking
            ? OnboardingStep.Choose
            : current;

    /// <summary>
    /// The phone is connected: Done — but never from Choose. A link that was already up when the
    /// flow opened (or reconnects underneath it) must not make it declare a phone added before the
    /// user has picked anything.
    /// </summary>
    public static OnboardingStep Connected(OnboardingStep current) =>
        current == OnboardingStep.Choose ? current : OnboardingStep.Done;

    /// <summary>
    /// What Done says: a phone whose hardware serial was known before the flow opened was
    /// already added; anything else is new.
    /// </summary>
    public static bool WasAlreadyAdded(string? serial, IEnumerable<string> knownBefore) =>
        !string.IsNullOrEmpty(serial) && knownBefore.Contains(serial, StringComparer.OrdinalIgnoreCase);
}
