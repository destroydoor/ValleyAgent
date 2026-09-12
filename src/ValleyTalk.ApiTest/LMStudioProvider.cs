namespace ValleyTalk.LLM;

/// <summary>
///     LLM provider for LM Studio (local inference server).
///     Uses the OpenAI-compatible API at http://localhost:1234 by default.
///     No API key required for local instances.
/// </summary>
internal sealed class LMStudioProvider : OpenAICompatibleProvider
{
    public const string DefaultEndpoint = "http://localhost:1234";
    public new const string DefaultModel = "local-model";

    /// <summary>
    ///     Creates a new LM Studio provider instance.
    /// </summary>
    /// <param name="httpClient">Shared HttpClient instance.</param>
    /// <param name="config">Provider configuration. ApiKey is optional for LM Studio.</param>
    public LMStudioProvider(HttpClient httpClient, LLMProviderConfig config)
        : base(httpClient, config, DefaultEndpoint, DefaultModel)
    {
    }

    public override string ProviderName
    {
        get => "LM Studio";
    }

    /// <summary>
    ///     LM Studio uses the standard OpenAI-compatible path.
    /// </summary>
    protected override string GetChatCompletionsPath() => "/v1/chat/completions";

    /// <summary>
    ///     LM Studio does not require authentication headers for local instances.
    /// </summary>
    protected override void ConfigureRequestHeaders(HttpRequestMessage request)
    {
        // LM Studio local server does not require auth headers
        // The base class already adds Bearer token if ApiKey is provided,
        // which is useful for remote LM Studio instances with auth
    }
}