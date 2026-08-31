namespace Linc.Desktop.Services;

/// <summary>
/// An error with a user-facing, plain-language message. Raw adb/socket details
/// go in <see cref="Exception.InnerException"/> and never in the UI (docs/CONTRIBUTING.md).
/// </summary>
public sealed class LincException(string friendlyMessage, Exception? inner = null)
    : Exception(friendlyMessage, inner);
