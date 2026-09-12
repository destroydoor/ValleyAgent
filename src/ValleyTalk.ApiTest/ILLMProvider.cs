namespace ValleyTalk.LLM;

/// <summary>
///     Unified interface for LLM chat completion providers.
///     All providers must implement this interface to be used by the AI decision engine.
/// </summary>
internal interface ILLMProvider
{
    /// <summary>
    ///     The display name of this provider (e.g., "LM Studio", "Kimi", "DeepSeek", "OpenRouter").
    /// </summary>
    public string ProviderName { get; }

    /// <summary>
    ///     Send a chat completion request and return the full response.
    /// </summary>
    /// <param name="messages">The conversation messages to send.</param>
    /// <param name="cancellationToken">Token to cancel the async operation.</param>
    /// <returns>The LLM response containing the generated text and metadata.</returns>
    public Task<LLMResponse> ChatCompletionAsync(
        List<LLMMessage> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Send a streaming chat completion request, yielding content deltas as they arrive.
    /// </summary>
    /// <param name="messages">The conversation messages to send.</param>
    /// <param name="cancellationToken">Token to cancel the async operation.</param>
    /// <returns>An async enumerable of content string deltas.</returns>
    public IAsyncEnumerable<string> StreamCompletionAsync(
        List<LLMMessage> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validate that the connection to the LLM provider is working.
    ///     Sends a minimal request and checks for a successful response.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the async operation.</param>
    /// <returns>True if the connection is valid, false otherwise.</returns>
    public Task<bool> ValidateConnectionAsync(CancellationToken cancellationToken = default);
}