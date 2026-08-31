namespace Linc.Desktop.Services;

// M12g-amend Part A0: LogService.cs's constructor now depends on IDeviceRegistry.RootPath, but
// this harness only pulls LogService.cs in for the ILogService/LogLevel/LogEntry types (it
// supplies its own ConsoleLog : ILogService and never constructs LogService itself). Minimal
// stand-in so the file still compiles without pulling in the real DeviceRegistry.cs.
public interface IDeviceRegistry
{
    string RootPath { get; }
}
