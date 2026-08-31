namespace Linc.Desktop.Services;

/// <summary>
/// The ADB shell commands PC-driven onboarding runs after installing the companion (M01, D-035),
/// as a plain ordered list plus a runner that cannot fail the flow.
/// <para>
/// It is split out from <see cref="PhoneSetupService"/> for one reason: <c>GrantAllAsync</c>
/// needs a live <c>AdbClient</c> and a real phone, so the property that actually matters —
/// <b>a grant that fails must not stop the ones after it and must never fail onboarding</b> —
/// could not otherwise be proven without hardware. <c>tools\hotspotsim</c> runs
/// <see cref="RunPlanAsync"/> with a runner that throws, which is the real production loop, not
/// a harness-side copy of it.
/// </para>
/// </summary>
public static class PhoneSetupCommands
{
    /// <summary>
    /// The one-time grant that lets the phone arm wireless debugging by itself (M13c §2.1).
    /// <para>
    /// It needs a cable or an existing ADB session exactly once — which onboarding already has,
    /// because it just installed the APK over that same connection. Putting it here means the
    /// user never touches ADB or a terminal (D-001): it happens invisibly inside something they
    /// were already doing, and if it fails, everything else still works.
    /// </para>
    /// </summary>
    public const string SelfArmPermission = "android.permission.WRITE_SECURE_SETTINGS";

    /// <summary>
    /// Every command onboarding runs after the install, in order. Deterministic and idempotent:
    /// `pm grant`, `cmd notification allow_listener` and `appops set` all no-op when the state is
    /// already what they ask for, so running the plan twice changes nothing.
    /// </summary>
    public static IReadOnlyList<string> GrantPlan(
        string package, string listenerComponent, IReadOnlyList<string> runtimePermissions)
    {
        var plan = new List<string>
        {
            // Notification access — the grant that otherwise needs a trip into Settings.
            $"cmd notification allow_listener {listenerComponent}",
        };
        foreach (var permission in runtimePermissions)
        {
            plan.Add($"pm grant {package} {permission}");
        }
        // All-files access (D-021) backs wallpaper, the files channel and photos.
        plan.Add($"appops set {package} MANAGE_EXTERNAL_STORAGE allow");
        // M13c §2.1 — last, deliberately: it is the newest and least essential of these, and
        // ordering it after the rest means a phone whose OEM blocks it still gets everything
        // that came before.
        plan.Add($"pm grant {package} {SelfArmPermission}");
        return plan;
    }

    /// <summary>True when a command in a plan is the self-arm grant.</summary>
    public static bool IsSelfArmGrant(string command) =>
        command.Contains(SelfArmPermission, StringComparison.Ordinal);

    /// <summary>
    /// Runs every command in the plan and returns the ones that failed. <b>Never throws</b>, and
    /// never stops early: onboarding must complete even when the phone refuses a grant, so each
    /// failure is recorded and the next command runs regardless.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RunPlanAsync(
        IReadOnlyList<string> plan,
        Func<string, CancellationToken, Task> run,
        Action<string, Exception>? onFailure,
        CancellationToken ct)
    {
        var failed = new List<string>();
        foreach (var command in plan)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await run(command, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // a cancelled connect is not a failed grant
            }
            catch (Exception ex)
            {
                failed.Add(command);
                onFailure?.Invoke(command, ex);
            }
        }
        return failed;
    }

    /// <summary>
    /// What to tell the user when the self-arm grant did not land. Plain language, and it names
    /// what still works — the feature degrades, it does not break (CONTRIBUTING.md "errors speak
    /// human"; disable, don't hide).
    /// </summary>
    public static string SelfArmUnavailableReason =>
        "This phone wouldn't let Linc turn wireless debugging on by itself, so you'll need to " +
        "switch it on from the phone's Developer options when you want an instant hotspot link. " +
        "Everything else works normally.";
}
