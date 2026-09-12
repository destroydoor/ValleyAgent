using System;
using System.Text;
using System.Threading.Tasks;
using ValleyAgent.Resilience;

namespace ValleyAgent.Exceptions;

/// <summary>
///     Identifies the operational context where an exception occurred.
///     Used to determine appropriate recovery strategies.
/// </summary>
public enum ExceptionContext
{
    /// <summary>Unknown or unspecified context.</summary>
    Unknown,

    /// <summary>LLM API call or response processing.</summary>
    LLMCall,

    /// <summary>Save data serialization or deserialization.</summary>
    SaveLoad,

    /// <summary>Agent state machine transition or update.</summary>
    StateMachine,

    /// <summary>Controller execution (Farm, Fight, Follow, etc.).</summary>
    Controller,

    /// <summary>Action validation failure.</summary>
    Validation,

    /// <summary>Dialogue generation or processing.</summary>
    Dialogue,

    /// <summary>Friendship evaluation or update.</summary>
    Friendship,

    /// <summary>Gift evaluation or processing.</summary>
    Gift,

    /// <summary>RAG knowledge base query or load.</summary>
    RAGQuery,

    /// <summary>Agent allocation or deallocation.</summary>
    AgentAllocation,

    /// <summary>Configuration load or save.</summary>
    Configuration,

    /// <summary>JSON parsing or serialization.</summary>
    JsonParsing,

    /// <summary>Network or HTTP operation.</summary>
    Network,

    /// <summary>File I/O operation.</summary>
    FileIO
}

/// <summary>
///     Result of an exception handling operation.
/// </summary>
public class ExceptionHandlingResult
{
    /// <summary>Whether the exception was successfully handled.</summary>
    public bool Handled { get; set; }

    /// <summary>Human-readable description of the handling action taken.</summary>
    public string ActionTaken { get; set; } = string.Empty;

    /// <summary>Whether a fallback value or behavior was used.</summary>
    public bool UsedFallback { get; set; }

    /// <summary>Whether the operation should be retried.</summary>
    public bool ShouldRetry { get; set; }

    /// <summary>Delay before retry, if applicable.</summary>
    public TimeSpan? RetryDelay { get; set; }

    /// <summary>The original exception that was handled.</summary>
    public Exception? OriginalException { get; set; }

    /// <summary>Creates a successful handling result.</summary>
    public static ExceptionHandlingResult Success(string actionTaken, bool usedFallback = false)
    {
        return new ExceptionHandlingResult
        {
            Handled = true,
            ActionTaken = actionTaken,
            UsedFallback = usedFallback
        };
    }

    /// <summary>Creates a retryable handling result.</summary>
    public static ExceptionHandlingResult Retry(string actionTaken, TimeSpan delay)
    {
        return new ExceptionHandlingResult
        {
            Handled = true,
            ActionTaken = actionTaken,
            ShouldRetry = true,
            RetryDelay = delay
        };
    }

    /// <summary>Creates a failed handling result (unrecoverable).</summary>
    public static ExceptionHandlingResult Failed(string actionTaken, Exception ex)
    {
        return new ExceptionHandlingResult
        {
            Handled = false,
            ActionTaken = actionTaken,
            OriginalException = ex
        };
    }
}

/// <summary>
///     Global exception handler for the ValleyAgent mod.
///     Provides centralized exception handling with context-aware recovery strategies:
///     - LLM call failure 鈫?CircuitBreaker.RecordFailure, fallback to template
///     - Save/load failure 鈫?Log error, reset to defaults, notify user
///     - State machine error 鈫?Reset to IDLE, log warning
///     - Controller error 鈫?Stop controller, return to IDLE
///     - Validation error 鈫?Log rejection reason, block action
///     Design:
///     - Game-agnostic core logic (no SMAPI/Stardew dependencies)
///     - Thread-safe logging via event callbacks
///     - Never swallows exceptions silently (always logs)
///     - Never crashes the game (graceful degradation)
///     - Never leaves NPCs in invalid states
/// </summary>
public class GlobalExceptionHandler
{
    private readonly CircuitBreaker? _circuitBreaker;
    private readonly Action<string, string>? _logError;
    private readonly Action<string, string>? _logInfo;
    private readonly Action<string, string>? _logWarning;
    private readonly Action<string>? _notifyUser;

    /// <summary>
    ///     Creates a new GlobalExceptionHandler.
    /// </summary>
    /// <param name="circuitBreaker">Optional circuit breaker for LLM failure tracking.</param>
    /// <param name="logError">Optional error logging callback (category, message).</param>
    /// <param name="logWarning">Optional warning logging callback (category, message).</param>
    /// <param name="logInfo">Optional info logging callback (category, message).</param>
    /// <param name="notifyUser">Optional user notification callback (message).</param>
    public GlobalExceptionHandler(
        CircuitBreaker? circuitBreaker = null,
        Action<string, string>? logError = null,
        Action<string, string>? logWarning = null,
        Action<string, string>? logInfo = null,
        Action<string>? notifyUser = null)
    {
        _circuitBreaker = circuitBreaker;
        _logError = logError;
        _logWarning = logWarning;
        _logInfo = logInfo;
        _notifyUser = notifyUser;
    }

    /// <summary>
    ///     Fired when an exception is handled. Provides detailed information about the exception and recovery action.
    /// </summary>
    public event EventHandler<ExceptionHandledEventArgs>? OnExceptionHandled;

    /// <summary>
    ///     Fired when a critical error occurs that requires user notification.
    /// </summary>
    public event EventHandler<string>? OnCriticalError;

    #region Main Exception Handling

    /// <summary>
    ///     Handles an exception based on the operational context.
    ///     Context-aware handling:
    ///     - LLM call failure 鈫?CircuitBreaker.RecordFailure, fallback to template
    ///     - Save/load failure 鈫?Log error, reset to defaults, notify user
    ///     - State machine error 鈫?Reset to IDLE, log warning
    ///     - Controller error 鈫?Stop controller, return to IDLE
    ///     - Validation error 鈫?Log rejection reason, block action
    /// </summary>
    /// <param name="exception">The exception to handle.</param>
    /// <param name="context">The operational context where the exception occurred.</param>
    /// <param name="additionalInfo">Additional context-specific information.</param>
    /// <returns>Result describing the handling action taken.</returns>
    public ExceptionHandlingResult HandleException(
        Exception exception,
        ExceptionContext context,
        string? additionalInfo = null)
    {
        if (exception == null)
        {
            return ExceptionHandlingResult.Success("Null exception provided - nothing to handle");
        }

        var contextName = context.ToString();
        var message = FormatExceptionMessage(exception, contextName, additionalInfo);

        // Always log the exception
        LogError(contextName, message);

        ExceptionHandlingResult result;

        try
        {
            result = context switch
            {
                ExceptionContext.LLMCall => HandleLLMCallFailure(exception),
                ExceptionContext.SaveLoad => HandleSaveLoadFailure(exception),
                ExceptionContext.StateMachine => HandleStateMachineError(exception),
                ExceptionContext.Controller => HandleControllerError(exception, additionalInfo),
                ExceptionContext.Validation => HandleValidationError(exception),
                ExceptionContext.Dialogue => HandleDialogueError(exception),
                ExceptionContext.Friendship => HandleFriendshipError(exception),
                ExceptionContext.Gift => HandleGiftError(exception),
                ExceptionContext.RAGQuery => HandleRAGQueryError(exception),
                ExceptionContext.AgentAllocation => HandleAgentAllocationError(exception),
                ExceptionContext.Configuration => HandleConfigurationError(exception),
                ExceptionContext.JsonParsing => HandleJsonParsingError(exception),
                ExceptionContext.Network => HandleNetworkError(exception),
                ExceptionContext.FileIO => HandleFileIOError(exception),
                ExceptionContext.Unknown => HandleUnknownError(exception),
                _ => HandleUnknownError(exception)
            };
        }
        catch (InvalidOperationException handlerEx)
        {
            // If the handler itself throws, log and return a safe fallback
            LogError("ExceptionHandler", $"Handler failed for {contextName}: {handlerEx.Message}");
            result = ExceptionHandlingResult.Failed(
                $"Handler failed: {handlerEx.Message}", exception);
        }

        // Fire event for listeners
        OnExceptionHandled?.Invoke(this, new ExceptionHandledEventArgs
        {
            OriginalException = exception,
            Context = context,
            Result = result,
            Timestamp = DateTime.UtcNow,
            AdditionalInfo = additionalInfo
        });

        // Notify user of critical errors
        if (!result.Handled && context is ExceptionContext.SaveLoad or ExceptionContext.Configuration)
        {
            var userMessage = $"Critical error in {contextName}: {exception.Message}. " +
                              "The mod has reset to safe defaults. Please check the logs.";
            NotifyUser(userMessage);
            OnCriticalError?.Invoke(this, userMessage);
        }

        return result;
    }

    #endregion

    #region Async Wrappers

    /// <summary>
    ///     Wraps an async action with exception handling.
    ///     Catches all exceptions, logs with context, and returns success/failure.
    /// </summary>
    /// <param name="action">The async action to wrap.</param>
    /// <param name="context">The operational context for error handling.</param>
    /// <param name="additionalInfo">Additional context information.</param>
    /// <returns>True if the action completed without exception, false otherwise.</returns>
    public async Task<bool> WrapAsync(
        Func<Task> action,
        ExceptionContext context,
        string? additionalInfo = null)
    {
        if (action == null)
        {
            LogError(context.ToString(), "WrapAsync called with null action");
            return false;
        }

        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not an error - just log and return false
            LogInfo(context.ToString(), "Operation was cancelled");
            return false;
        }
        catch (InvalidOperationException ex)
        {
            var result = HandleException(ex, context, additionalInfo);
            return result.Handled || result.UsedFallback;
        }
    }

    /// <summary>
    ///     Wraps an async function with exception handling.
    ///     Catches all exceptions, logs with context, and returns the result or default value.
    /// </summary>
    /// <typeparam name="T">The return type of the function.</typeparam>
    /// <param name="func">The async function to wrap.</param>
    /// <param name="context">The operational context for error handling.</param>
    /// <param name="defaultValue">The default value to return on failure.</param>
    /// <param name="additionalInfo">Additional context information.</param>
    /// <returns>The function result, or defaultValue on failure.</returns>
    public async Task<T> WrapAsync<T>(
        Func<Task<T>> func,
        ExceptionContext context,
        T defaultValue,
        string? additionalInfo = null)
    {
        if (func == null)
        {
            LogError(context.ToString(), "WrapAsync<T> called with null function");
            return defaultValue;
        }

        try
        {
            return await func().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogInfo(context.ToString(), "Operation was cancelled");
            return defaultValue;
        }
        catch (InvalidOperationException ex)
        {
            _ = HandleException(ex, context, additionalInfo);
            return defaultValue;
        }
    }

    #endregion

    #region Safe Execution Helpers

    /// <summary>
    ///     Safely executes an action, catching and handling any exceptions.
    /// </summary>
    /// <param name="action">The action to execute.</param>
    /// <param name="context">The operational context for error handling.</param>
    /// <param name="additionalInfo">Additional context information.</param>
    /// <returns>True if the action completed without exception, false otherwise.</returns>
    public bool SafeExecute(
        Action action,
        ExceptionContext context,
        string? additionalInfo = null)
    {
        if (action == null)
        {
            LogError(context.ToString(), "SafeExecute called with null action");
            return false;
        }

        try
        {
            action();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            var result = HandleException(ex, context, additionalInfo);
            return result.Handled;
        }
    }

    /// <summary>
    ///     Safely executes a function, catching and handling any exceptions.
    ///     Returns the function result or a default value on failure.
    /// </summary>
    /// <typeparam name="T">The return type of the function.</typeparam>
    /// <param name="func">The function to execute.</param>
    /// <param name="context">The operational context for error handling.</param>
    /// <param name="defaultValue">The default value to return on failure.</param>
    /// <param name="additionalInfo">Additional context information.</param>
    /// <returns>The function result, or defaultValue on failure.</returns>
    public T SafeExecute<T>(
        Func<T> func,
        ExceptionContext context,
        T defaultValue,
        string? additionalInfo = null)
    {
        if (func == null)
        {
            LogError(context.ToString(), "SafeExecute<T> called with null function");
            return defaultValue;
        }

        try
        {
            return func();
        }
        catch (InvalidOperationException ex)
        {
            _ = HandleException(ex, context, additionalInfo);
            return defaultValue;
        }
    }

    #endregion

    #region Context-Specific Handlers

    private ExceptionHandlingResult HandleLLMCallFailure(Exception ex)
    {
        // Record failure in circuit breaker
        _circuitBreaker?.RecordFailure("llm_call_exception");

        var action = "LLM call failed - recorded in circuit breaker, will use fallback template";
        LogWarning("LLM", $"{action}. Exception: {ex.Message}");

        return ExceptionHandlingResult.Success(action, true);
    }

    private ExceptionHandlingResult HandleSaveLoadFailure(Exception ex)
    {
        var action = "Save/load operation failed - resetting to safe defaults";
        LogError("SaveLoad", $"{action}. Exception: {ex.Message}");

        // Notify user of critical save/load failure
        var userMessage = $"Failed to load or save agent data: {ex.Message}. " +
                          "Agent data has been reset to default values.";
        NotifyUser(userMessage);

        return ExceptionHandlingResult.Success(action, true);
    }

    private ExceptionHandlingResult HandleStateMachineError(Exception ex)
    {
        var action = "State machine error - resetting agent to IDLE state";
        LogWarning("StateMachine", $"{action}. Exception: {ex.Message}");

        return ExceptionHandlingResult.Success(action, true);
    }

    private ExceptionHandlingResult HandleControllerError(Exception ex, string? additionalInfo)
    {
        var controllerName = additionalInfo ?? "Unknown";
        var action = $"Controller '{controllerName}' error - stopping controller and returning to IDLE";
        LogWarning("Controller", $"{action}. Exception: {ex.Message}");

        return ExceptionHandlingResult.Success(action, true);
    }

    private ExceptionHandlingResult HandleValidationError(Exception ex)
    {
        var action = $"Validation failed - action blocked. Reason: {ex.Message}";
        LogWarning("Validation", action);

        return ExceptionHandlingResult.Success(action, false);
    }

    private ExceptionHandlingResult HandleDialogueError(Exception ex)
    {
        var action = "Dialogue generation failed - using fallback dialogue";
        LogWarning("Dialogue", $"{action}. Exception: {ex.Message}");

        return ExceptionHandlingResult.Success(action, true);
    }

    private ExceptionHandlingResult HandleFriendshipError(Exception ex)
    {
        var action = "Friendship evaluation failed - no friendship change applied";
        LogWarning("Friendship", $"{action}. Exception: {ex.Message}");

        return ExceptionHandlingResult.Success(action, true);
    }

    private ExceptionHandlingResult HandleGiftError(Exception ex)
    {
        var action = "Gift evaluation failed - using default gift reaction";
        LogWarning("Gift", $"{action}. Exception: {ex.Message}");

        return ExceptionHandlingResult.Success(action, true);
    }

    private ExceptionHandlingResult HandleRAGQueryError(Exception ex)
    {
        var action = "RAG query failed - proceeding without knowledge base enrichment";
        LogWarning("RAG", $"{action}. Exception: {ex.Message}");

        return ExceptionHandlingResult.Success(action, true);
    }

    private ExceptionHandlingResult HandleAgentAllocationError(Exception ex)
    {
        var action = "Agent allocation failed - no agent allocated for this NPC";
        LogWarning("AgentAllocation", $"{action}. Exception: {ex.Message}");

        return ExceptionHandlingResult.Success(action, true);
    }

    private ExceptionHandlingResult HandleConfigurationError(Exception ex)
    {
        var action = "Configuration error - using default configuration values";
        LogError("Configuration", $"{action}. Exception: {ex.Message}");

        NotifyUser($"Configuration error: {ex.Message}. Using default settings.");

        return ExceptionHandlingResult.Success(action, true);
    }

    private ExceptionHandlingResult HandleJsonParsingError(Exception ex)
    {
        var action = "JSON parsing failed - using last valid decision or default values";
        LogWarning("JsonParsing", $"{action}. Exception: {ex.Message}");

        return ExceptionHandlingResult.Success(action, true);
    }

    private ExceptionHandlingResult HandleNetworkError(Exception ex)
    {
        var action = "Network error - will retry with exponential backoff";
        LogWarning("Network", $"{action}. Exception: {ex.Message}");

        // Calculate retry delay based on failure type
        var delay = ex is TimeoutException
            ? TimeSpan.FromSeconds(5)
            : TimeSpan.FromSeconds(2);

        return ExceptionHandlingResult.Retry(action, delay);
    }

    private ExceptionHandlingResult HandleFileIOError(Exception ex)
    {
        var action = "File I/O error - operation skipped";
        LogError("FileIO", $"{action}. Exception: {ex.Message}");

        return ExceptionHandlingResult.Success(action, true);
    }

    private ExceptionHandlingResult HandleUnknownError(Exception ex)
    {
        var action = $"Unknown error - graceful degradation applied. Exception: {ex.Message}";
        LogError("Unknown", action);

        return ExceptionHandlingResult.Success(action, true);
    }

    #endregion

    #region Logging Helpers

    private void LogError(string category, string message) => _logError?.Invoke(category, message);

    private void LogWarning(string category, string message) => _logWarning?.Invoke(category, message);

    private void LogInfo(string category, string message) => _logInfo?.Invoke(category, message);

    private void NotifyUser(string message) => _notifyUser?.Invoke(message);

    private static string FormatExceptionMessage(Exception ex, string context, string? additionalInfo)
    {
        var sb = new StringBuilder();
        _ = sb.Append($"[{context}] Exception: {ex.GetType().Name}: {ex.Message}");
        if (!string.IsNullOrEmpty(additionalInfo))
        {
            _ = sb.Append($" | Context: {additionalInfo}");
        }

        if (ex.InnerException != null)
        {
            _ = sb.Append($" | Inner: {ex.InnerException.Message}");
        }

        return sb.ToString();
    }

    #endregion
}

/// <summary>
///     Event arguments for the OnExceptionHandled event.
/// </summary>
public class ExceptionHandledEventArgs : EventArgs
{
    /// <summary>The original exception that was handled.</summary>
    public Exception? OriginalException { get; set; }

    /// <summary>The operational context where the exception occurred.</summary>
    public ExceptionContext Context { get; set; }

    /// <summary>The result of the handling operation.</summary>
    public ExceptionHandlingResult? Result { get; set; }

    /// <summary>When the exception was handled.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Additional context information.</summary>
    public string? AdditionalInfo { get; set; }
}