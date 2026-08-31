namespace Linc.Desktop.Services;

/// <summary>What EnsureReadyAsync should do about installing the companion on this phone.</summary>
public enum CompanionInstallDecision
{
    /// <summary>Nothing to do — already installed, or the user already said no this session.</summary>
    Skip,

    /// <summary>Install without asking — the onboarding wizard is already the user's explicit
    /// "set up a phone" action, and it narrates the Install step itself (M12c-amend C2.4).</summary>
    InstallSilently,

    /// <summary>Ask first — a non-wizard connect must never push an APK to someone's phone
    /// without saying so (M12c-amend C2.1).</summary>
    Ask,
}

/// <summary>
/// M12c-amend Part C: the pure decision table behind the companion-install confirmation gate.
/// No WinUI, no ADB — so tools/apkinstallsim calls this exact function instead of modelling its
/// truth table (GUIDE.md 4.1). PhoneSetupService.EnsureReadyAsync is the only caller that may act
/// on the result; every other caller of the install path must go through it (C3.1).
/// </summary>
public static class CompanionInstallGate
{
    public static CompanionInstallDecision Decide(bool alreadyInstalled, bool wizardActive, bool declinedThisSession)
    {
        if (alreadyInstalled)
        {
            return CompanionInstallDecision.Skip;
        }
        if (wizardActive)
        {
            return CompanionInstallDecision.InstallSilently;
        }
        return declinedThisSession ? CompanionInstallDecision.Skip : CompanionInstallDecision.Ask;
    }
}
