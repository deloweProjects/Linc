namespace Linc.Desktop.Services;

/// <summary>
/// Pure capability logic for <c>pc.state</c>'s <c>canSleep</c> (M12i): decides sleep
/// availability from the two <c>SYSTEM_POWER_CAPABILITIES</c> flags <see cref="PcControlService"/>
/// reads via <c>GetPwrCapabilities</c> — zero P/Invoke in this file, same posture as
/// <see cref="PcControlValidator"/>, so <c>tools\pccontrolsim</c> can exercise it directly
/// without a machine that has either capability.
///
/// <para><b>Why OR, not legacy S3 alone.</b> <c>IsPwrSuspendAllowed()</c> answers for legacy
/// ACPI S3 only and returns <c>false</c> on Modern Standby (S0 low-power idle) machines where
/// Sleep works fine from the Start menu — measured on this dev machine in M4b. The hardware
/// advertises that second mechanism through <c>SYSTEM_POWER_CAPABILITIES.AoAc</c> ("Always On,
/// Always Connected"); a field literally named <c>SystemS0LowPower</c> does not exist in the
/// real struct (checked against the Windows 10.0.26100.0 SDK's <c>winnt.h</c> — M12i). The
/// parameter below is still named <c>s0LowPower</c> to match that capability's caller-facing
/// meaning; the caller is what supplies it from <c>AoAc</c>. A machine can sleep if either
/// mechanism is present.</para>
/// </summary>
public static class PcPowerCapabilities
{
    public static bool CanSleep(bool s3Allowed, bool s0LowPower) => s3Allowed || s0LowPower;
}
