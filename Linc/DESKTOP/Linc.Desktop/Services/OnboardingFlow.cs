namespace Linc.Desktop.Services;

/// <summary>The onboarding wizard's five plain-language stages (M2b).</summary>
public enum OnboardingStep
{
    /// <summary>"Let's connect your phone."</summary>
    Intro,

    /// <summary>Turn on USB + Wireless debugging on the phone.</summary>
    EnableDebugging,

    /// <summary>Watch for the phone over USB, or pair wirelessly with the QR / code.</summary>
    DetectPair,

    /// <summary>The ADB link is up; the desktop is installing + starting the companion.</summary>
    Install,

    /// <summary>The companion answered and the phone is saved. "You're all set."</summary>
    Done,
}

/// <summary>
/// The wizard's step transitions as pure functions — no WinUI, no services — so the state machine
/// can be exercised headlessly by a console harness (tools/onboardsim) while <see cref="ViewModels"/>
/// just calls into it. Every transition is a no-op unless it applies to the current step, which is
/// what makes the flow safe to re-drive (idempotent Back/BeginDetection, out-of-order events).
/// </summary>
public static class OnboardingFlow
{
    /// <summary>Intro → EnableDebugging (the "Get started" button).</summary>
    public static OnboardingStep Start(OnboardingStep current) =>
        current == OnboardingStep.Intro ? OnboardingStep.EnableDebugging : current;

    /// <summary>EnableDebugging → DetectPair (arm detection and show the QR).</summary>
    public static OnboardingStep BeginDetection(OnboardingStep current) =>
        current == OnboardingStep.EnableDebugging ? OnboardingStep.DetectPair : current;

    /// <summary>One step back, where a back step exists (DetectPair → EnableDebugging → Intro).</summary>
    public static OnboardingStep Back(OnboardingStep current) => current switch
    {
        OnboardingStep.DetectPair => OnboardingStep.EnableDebugging,
        OnboardingStep.EnableDebugging => OnboardingStep.Intro,
        _ => current,
    };

    /// <summary>
    /// The ADB link came up (supervisor Connecting): move into the install screen, but only from
    /// the detect screen — a Connecting blip during a later reconnect must not drag the finished
    /// wizard backwards.
    /// </summary>
    public static OnboardingStep Connecting(OnboardingStep current) =>
        current == OnboardingStep.DetectPair ? OnboardingStep.Install : current;

    /// <summary>The companion answered (supervisor Connected): the flow is finished, from anywhere.</summary>
    public static OnboardingStep Connected(OnboardingStep current) => OnboardingStep.Done;
}
