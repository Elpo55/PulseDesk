namespace Sysora.Core.Results;

/// <summary>Reason an operation failed.</summary>
public enum OperationError
{
    None,

    /// <summary>The target no longer exists (or its ID now belongs to something else).</summary>
    NotFound,

    /// <summary>Windows denied access; administrator rights may be required.</summary>
    AccessDenied,

    /// <summary>The target is protected and Sysora refuses to act on it.</summary>
    Protected,

    /// <summary>The operation is not supported on this system.</summary>
    NotSupported,

    /// <summary>Any other failure.</summary>
    Failed,
}

/// <summary>
/// Outcome of an operation that can fail for expected reasons. Expected failures are values,
/// not exceptions, so callers can show a clear message without try/catch.
/// </summary>
public readonly record struct OperationResult(OperationError Error, string? Message)
{
    /// <summary>True when the operation completed.</summary>
    public bool Succeeded => Error == OperationError.None;

    /// <summary>A successful result.</summary>
    public static OperationResult Success { get; } = new(OperationError.None, null);

    /// <summary>Creates a failed result.</summary>
    public static OperationResult Failure(OperationError error, string message)
    {
        if (error == OperationError.None)
        {
            throw new ArgumentException("A failure needs an error.", nameof(error));
        }

        return new OperationResult(error, message);
    }
}
