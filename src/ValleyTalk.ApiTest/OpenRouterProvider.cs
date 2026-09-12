namespace ValleyTalk.LLM;

/// <summary>
///     LLM provider for OpenRouter API.
///     Uses the OpenAI-compatible API at https://openrouter.ai/api/v1 by default.
///     API key is required. OpenRouter routes to multiple model providers.
/// </summary>
internal sealed class OpenRouterProvider : OpenAICompatibleProvider
{
    public const string DefaultEndpoint = "https://openrouter.ai/api/v1";
    public new const string DefaultModel = "openai/gpt-3.5-turbo";

    /// <summary>
    ///     Creates a new OpenRouter provider instance.
    /// </summary>
    /// <param name="httpClient">Shared HttpClient instance.</param>
    /// <param name="config">Provider configuration. ApiKey is required.</param>
    public OpenRouterProvider(HttpClient httpClient, LLMProviderConfig config)
        : base(httpClient, config, DefaultEndpoint, DefaultModel)
    {
    }

    public override string ProviderName
    {
        get => "OpenRouter";
    }

    /// <summary>
    ///     OpenRouter uses the standard OpenAI-compatible path.
    /// </summary>
    protected override string GetChatCompletionsPath() => "/v1/chat/completions";

    /// <summary>
    ///     OpenRouter requires additional headers: HTTP-Referer and X-Title.
    ///     These identify the application making the request.
    /// </summary>
    protected override void ConfigureRequestHeaders(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Headers.Add("HTTP-Referer", "https://github.com/dandm1/ValleyTalk");
        request.Headers.Add("X-Title", "ValleyTalk");

        // OpenRouter also supports Bearer token auth (handled by base class)
    }
}