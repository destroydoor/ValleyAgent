namespace ValleyTalk.LLM;

/// <summary>
///     LLM provider for DeepSeek API.
///     Uses the OpenAI-compatible API at https://api.deepseek.com/v1 by default.
///     API key is required.
/// </summary>
internal sealed class DeepSeekProvider : OpenAICompatibleProvider
{
    public const string DefaultEndpoint = "https://api.deepseek.com/v1";
    public new const string DefaultModel = "deepseek-chat";

    /// <summary>
    ///     Creates a new DeepSeek provider instance.
    /// </summary>
    /// <param name="httpClient">Shared HttpClient instance.</param>
    /// <param name="config">Provider configuration. ApiKey is required.</param>
    public DeepSeekProvider(HttpClient httpClient, LLMProviderConfig config)
        : base(httpClient, config, DefaultEndpoint, DefaultModel)
    {
    }

    public override string ProviderName
    {
        get => "DeepSeek";
    }

    /// <summary>
    ///     DeepSeek uses the standard OpenAI-compatible path.
    /// </summary>
    protected override string GetChatCompletionsPath() => "/v1/chat/completions";

    /// <summary>
    ///     DeepSeek uses standard Bearer token authentication (handled by base class).
    /// </summary>
    protected override void ConfigureRequestHeaders(HttpRequestMessage request)
    {
        // DeepSeek uses standard Bearer token auth, already handled by base class
    }
}