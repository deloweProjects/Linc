using System.Text.RegularExpressions;

namespace Linc.Desktop.Services;

/// <summary>
/// What kind of connection an adb serial names (D-078). adb gives a USB phone its hardware serial
/// and a network phone one of: <c>192.168.1.61:5555</c>, <c>[2605:59c1::d6cf]:33705</c> (IPv6), or
/// the mDNS service name wireless pairing produces, <c>adb-131722549O000808-hiwood._adb-tls-connect._tcp</c>.
/// Linc used to test "contains a colon" in one place and "IPv4 host:port" in another, so the IPv6
/// and mDNS forms were taken for USB cables — the link was labelled USB and then "unplugged" the
/// moment the serial changed, ten seconds after a friend's first setup completed.
/// </summary>
public static class AdbSerial
{
    private static readonly Regex HostPort = new(@"^(\[[0-9A-Fa-f:.%a-z]+\]|[^\s\[\]:]+):\d+$", RegexOptions.Compiled);

    /// <summary>True for a network (wireless debugging) connection, false for a USB cable.</summary>
    public static bool IsNetwork(string? serial) =>
        !string.IsNullOrEmpty(serial) &&
        (HostPort.IsMatch(serial) ||
         serial.Contains("._adb-tls-connect.", StringComparison.OrdinalIgnoreCase) ||
         serial.Contains("._adb._tcp", StringComparison.OrdinalIgnoreCase));
}
