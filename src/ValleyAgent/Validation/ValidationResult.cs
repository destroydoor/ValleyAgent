namespace ValleyAgent.Validation;

/// <summary>
///     Result of an action validation attempt.
/// </summary>
public class ValidationResult
{
    private ValidationResult(bool isValid, string reason)
    {
        IsValid = isValid;
        Reason = reason;
    }

    /// <summary>
    ///     Whether the action is valid and can be performed.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    ///     Human-readable reason for validation failure. Empty if IsValid is true.
    /// </summary>
    public string Reason { get; }

    /// <summary>
    ///     Shared successful validation result instance.
    /// </summary>
    public static ValidationResult Success { get; } = new(true, string.Empty);

    /// <summary>
    ///     Creates a failed validation result with the given reason.
    /// </summary>
    /// <param name="reason">Human-readable explanation of why validation failed.</param>
    public static ValidationResult Fail(string reason) => new(false, reason);
}