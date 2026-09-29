namespace Arcadia.Core.ItTools;

/// <summary>Why an IT tool operation failed, so the UI can explain it instead of showing a stack trace.</summary>
public enum ToolFailure
{
    InvalidInput,
    DnsFailure,
    HostUnreachable,
    Timeout,
    AccessDenied,
    RequiresElevation,
    NotFound,
    Unavailable,
    Unexpected,
}

/// <summary>An expected, explainable failure of an IT tool (the technical detail goes to the log).</summary>
public sealed class ToolException(ToolFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public ToolFailure Failure { get; } = failure;
}
