namespace Linc.Desktop.Services;

// Same stand-in tools\autostartsim uses, for the same reason: LogService.cs is compiled in only
// for the ILogService / LogLevel types that HotspotConnector's optional log parameter is typed
// against, but its constructor takes IDeviceRegistry.RootPath. This probe never constructs a
// LogService (it passes null) and never touches a registry, so a minimal interface keeps the
// real DeviceRegistry.cs and its whole dependency chain out of the build.
public interface IDeviceRegistry
{
    string RootPath { get; }
}
