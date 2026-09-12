namespace ValleyAgent.Validation;

/// <summary>
///     Validates agent actions before they are executed.
///     Security-critical component that prevents LLM from performing dangerous or invalid actions.
/// </summary>
public interface IActionValidator
{
    /// <summary>
    ///     Validates whether an action can be performed given the current context.
    /// </summary>
    /// <param name="context">Validation context containing action details and game state.</param>
    /// <returns>ValidationResult indicating success or failure with reason.</returns>
    public ValidationResult Validate(ValidationContext context);
}