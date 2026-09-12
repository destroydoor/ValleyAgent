namespace ValleyTalk.LLM;

/// <summary>
///     Factory for creating LLM provider instances based on configuration.
///     Supports LM Studio, Kimi (Moonshot), DeepSeek, and OpenRouter providers.
/// </summary>
internal static class LLMProviderFactory
{
    /// <summary>
    ///     Gets the list of supported provider type strings.
    /// </summary>
    public static IReadOnlyList<string> SupportedProviders
    {
        get => new[]
        {
            "lmstudio",
            "kimi",
            "deepseek",
            "openrouter"
        };
    }

    /// <summary>
    ///     Creates an LLM provider instance based on the configuration.
    /// </summary>
    /// <param name="config">Provider configuration specifying type, API key, model, etc.</param>
    /// <param name="httpClient">Optional shared HttpClient instance. If null, a new one is created.</param>
    /// <returns>An initialized ILLMProvider instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when config is null.</exception>
    /// <exception cref="ArgumentException">Thrown when ProviderType is unknown.</exception>
    public static ILLMProvider Create(LLMProviderConfig config, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var client = httpClient ?? CreateDefaultHttpClient(config);

        return config.ProviderType.ToLowerInvariant() switch
        {
            "lmstudio" or "lm-studio" or "lm_studio" => new LMStudioProvider(client, config),
            "kimi" or "moonshot" => new KimiProvider(client, config),
            "deepseek" => new DeepSeekProvider(client, config),
            "openrouter" or "open-router" or "open_router" => new OpenRouterProvider(client, config),
            _ => throw new ArgumentException(
                $"Unknown provider type: '{config.ProviderType}'. " +
                $"Supported types: lmstudio, kimi, deepseek, openrouter",
                nameof(config))
        };
    }

    /// <summary>
    ///     Creates an LLM provider with a simple configuration.
    ///     Convenience method for quick setup without a full LLMProviderConfig object.
    /// </summary>
    /// <param name="providerType">The provider type string (e.g., "lmstudio", "kimi", "deepseek", "openrouter").</param>
    /// <param name="apiKey">Optional API key (required for remote providers, not needed for LM Studio).</param>
    /// <param name="model">Optional model name override.</param>
    /// <param name="endpoint">Optional endpoint URL override.</param>
    /// <returns>An initialized ILLMProvider instance.</returns>
    public static ILLMProvider Create(
        string providerType,
        string? apiKey = null,
        string? model = null,
        string? endpoint = null)
    {
        var config = new LLMProviderConfig
        {
            ProviderType = providerType,
            ApiKey = apiKey ?? string.Empty,
            Model = model ?? string.Empty,
            Endpoint = endpoint
        };

        return Create(config);
    }

    /// <summary>
    ///     Gets the default endpoint URL for a given provider type.
    /// </summary>
    /// <param name="providerType">The provider type string.</param>
    /// <returns>The default endpoint URL, or null if the provider type is unknown.</returns>
    public static string? GetDefaultEndpoint(string providerType)
    {
        ArgumentNullException.ThrowIfNull(providerType);

        return providerType.ToLowerInvariant() switch
        {
            "lmstudio" or "lm-studio" or "lm_studio" => LMStudioProvider.DefaultEndpoint,
            "kimi" or "moonshot" => KimiProvider.DefaultEndpoint,
            "deepseek" => DeepSeekProvider.DefaultEndpoint,
            "openrouter" or "open-router" or "open_router" => OpenRouterProvider.DefaultEndpoint,
            _ => null
        };
    }

    /// <summary>
    ///     Gets the default model name for a given provider type.
    /// </summary>
    /// <param name="providerType">The provider type string.</param>
    /// <returns>The default model name, or null if the provider type is unknown.</returns>
    public static string? GetDefaultModel(string providerType)
    {
        ArgumentNullException.ThrowIfNull(providerType);

        return providerType.ToLowerInvariant() switch
        {
            "lmstudio" or "lm-studio" or "lm_studio" => LMStudioProvider.DefaultModel,
            "kimi" or "moonshot" => KimiProvider.DefaultModel,
            "deepseek" => DeepSeekProvider.DefaultModel,
            "openrouter" or "open-router" or "open_router" => OpenRouterProvider.DefaultModel,
            _ => null
        };
    }

    private static HttpClient CreateDefaultHttpClient(LLMProviderConfig config)
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
        };

        return client;
    }
}