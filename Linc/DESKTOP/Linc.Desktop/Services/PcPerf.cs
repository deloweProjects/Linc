using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Linc.Desktop.Services;

/// <summary>
/// The figures Task Manager's Performance tab shows, for the phone's PC page (v28): processor
/// utility and speed, disk active time and throughput, GPU 3D load, and network send/receive.
///
/// <para>One performance-counter query lives for the app's lifetime; each sample reads the
/// counters' change since the previous one, which is how Task Manager gets its numbers (and why
/// the very first sample after start has no rates yet). Anything the PC's counters don't offer
/// is left out, never guessed.</para>
/// </summary>
internal static class Perf
{
    public sealed record Figures(int CpuPct, int CpuMhz, int DiskPct, long DiskReadKBps, long DiskWriteKBps, int GpuPct);
    public sealed record Hw(string? CpuName, int Cores, string? GpuName);

    private static readonly object Lock = new();
    private static IntPtr _query;
    private static IntPtr _cpu, _cpuPerf, _diskIdle, _diskRead, _diskWrite, _gpu;
    private static bool _broken;
    private static int _baseMhz;

    public static Figures? Sample()
    {
        lock (Lock)
        {
            if (_broken) return null;
            try
            {
                if (_query == IntPtr.Zero && !Open()) return null;
                if (PdhCollectQueryData(_query) != 0) return null;
                double cpu = Value(_cpu);
                double perf = Value(_cpuPerf);
                double idle = Value(_diskIdle);
                double read = Value(_diskRead);
                double write = Value(_diskWrite);
                double gpu = SumArray(_gpu);
                if (cpu < 0) return null;
                return new Figures(
                    (int)Math.Round(Math.Clamp(cpu, 0, 100)),
                    perf > 0 && _baseMhz > 0 ? (int)Math.Round(_baseMhz * perf / 100) : _baseMhz,
                    idle >= 0 ? (int)Math.Round(Math.Clamp(100 - idle, 0, 100)) : -1,
                    read >= 0 ? (long)(read / 1024) : -1,
                    write >= 0 ? (long)(write / 1024) : -1,
                    gpu >= 0 ? (int)Math.Round(Math.Clamp(gpu, 0, 100)) : -1);
            }
            catch (Exception)
            {
                _broken = true; // no counters on this PC (disabled, or pdh missing): fall back for good
                return null;
            }
        }
    }

    private static bool Open()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _broken = true; return false; }
        // "Processor Utility" is what Task Manager shows (it can pass 100 % under turbo; capped).
        if (PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out _cpu) != 0)
            PdhAddEnglishCounter(_query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _cpu);
        PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _cpuPerf);
        PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\% Idle Time", IntPtr.Zero, out _diskIdle);
        PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out _diskRead);
        PdhAddEnglishCounter(_query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out _diskWrite);
        PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpu);
        using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            _baseMhz = key?.GetValue("~MHz") is int mhz ? mhz : 0;
        PdhCollectQueryData(_query); // the first collection only sets the baseline
        Thread.Sleep(100);
        return true;
    }

    private static double Value(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return -1;
        if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) != 0 || v.CStatus > 1) return -1;
        return v.DoubleValue;
    }

    /// <summary>A wildcard counter's instances, summed (one per GPU engine).</summary>
    private static double SumArray(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return -1;
        uint size = 0;
        int hr = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out uint count, IntPtr.Zero);
        if (hr != PDH_MORE_DATA || size == 0) return -1;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE, ref size, out count, buffer) != 0) return -1;
            int stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * stride);
                if (item.Value.CStatus <= 1) sum += item.Value.DoubleValue;
            }
            return sum;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    // ---- network ----------------------------------------------------------------------------

    private static long _lastSent = -1, _lastReceived = -1, _lastAt;

    /// <summary>Send and receive, kilobits a second, over every live adapter; and the main one's name.</summary>
    public static (long SendKbps, long RecvKbps, string? Name) Network()
    {
        lock (Lock)
        {
            try
            {
                long sent = 0, received = 0;
                string? name = null;
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    var stats = nic.GetIPStatistics();
                    sent += stats.BytesSent;
                    received += stats.BytesReceived;
                    if (name is null && nic.GetIPProperties().GatewayAddresses.Count > 0)
                        name = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? $"Wi-Fi · {nic.Description}" : $"Ethernet · {nic.Description}";
                }
                long now = Environment.TickCount64;
                long sendKbps = -1, recvKbps = -1;
                if (_lastSent >= 0 && now > _lastAt)
                {
                    double seconds = (now - _lastAt) / 1000.0;
                    sendKbps = (long)Math.Max(0, (sent - _lastSent) * 8 / 1000.0 / seconds);
                    recvKbps = (long)Math.Max(0, (received - _lastReceived) * 8 / 1000.0 / seconds);
                }
                _lastSent = sent; _lastReceived = received; _lastAt = now;
                return (sendKbps, recvKbps, name);
            }
            catch (Exception) { return (-1, -1, null); }
        }
    }

    // ---- what the hardware is (looked up once) -----------------------------------------------

    private static Hw? _hw;

    public static Hw Hardware()
    {
        if (_hw is { } known) return known;
        string? cpu = null, gpu = null;
        int cores = 0;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpu = (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch { }
        try
        {
            using var search = new System.Management.ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor");
            foreach (var o in search.Get()) cores += Convert.ToInt32(o["NumberOfCores"]);
        }
        catch { }
        try
        {
            using var search = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (var o in search.Get())
            {
                var n = o["Name"] as string;
                // A real GPU over the basic display driver or a virtual one.
                if (string.IsNullOrWhiteSpace(n) || n.Contains("Basic", StringComparison.OrdinalIgnoreCase) || n.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
                gpu = n.Trim();
                break;
            }
        }
        catch { }
        return (_hw = new Hw(cpu, cores, gpu));
    }

    // ---- pdh ---------------------------------------------------------------------------------

    private const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr Name;
        public PDH_FMT_COUNTERVALUE Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
}
