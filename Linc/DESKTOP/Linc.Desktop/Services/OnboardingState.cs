namespace Linc.Desktop.Services;

/// <summary>
/// Whether the onboarding wizard is currently open (M12c-amend C1/C2.4). A singleton so
/// PhoneSetupService can read it without depending on AppShellViewModel or any WinUI type —
/// AppShellViewModel mirrors its existing IsOnboarding property onto this on every change.
/// </summary>
public interface IOnboardingState
{
    bool IsActive { get; set; }
}

public sealed class OnboardingState : IOnboardingState
{
    public bool IsActive { get; set; }
}
