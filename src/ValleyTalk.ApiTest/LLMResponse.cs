using System.Text.Json.Serialization;

namespace ValleyTalk.LLM;

/// <summary>
///     Represents a message in an LLM conversation.
/// </summary>
internal sealed class LLMMessage
{
    public LLMMessage()
    {
    }

    public LLMMessage(string role, string content)
    {
        Role = role;
        Content = content;
    }

    /// <summary>
    ///     The role of the message author. One of: "system", "user", "assistant".
    /// </summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    /// <summary>
    ///     The content of the message.
    /// </summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

/// <summary>
///     Represents the response from an LLM chat completion request.
/// </summary>
internal sealed class LLMResponse
{
    /// <summary>
    ///     The generated text content from the assistant.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    ///     The model used for this completion.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    ///     The reason the model stopped generating. One of: "stop", "length", "content_filter".
    /// </summary>
    public string FinishReason { get; set; } = string.Empty;

    /// <summary>
    ///     Token usage information for this request.
    /// </summary>
    public LLMUsage? Usage { get; set; }

    /// <summary>
    ///     Whether the request completed successfully.
    /// </summary>
    public bool IsSuccess { get; set; } = true;

    /// <summary>
    ///     Error message if the request failed.
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    ///     HTTP status code from the response, if applicable.
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    ///     Creates a successful response.
    /// </summary>
    public static LLMResponse Success(string content, string model, string finishReason, LLMUsage? usage = null)
    {
        return new LLMResponse
        {
            Content = content,
            Model = model,
            FinishReason = finishReason,
            Usage = usage,
            IsSuccess = true
        };
    }

    /// <summary>
    ///     Creates a failed response.
    /// </summary>
    public static LLMResponse Failure(string errorMessage, int statusCode = 0)
    {
        return new LLMResponse
        {
            IsSuccess = false,
            ErrorMessage = errorMessage,
            StatusCode = statusCode
        };
    }
}

/// <summary>
///     Represents token usage information from an LLM request.
/// </summary>
internal sealed class LLMUsage
{
    /// <summary>
    ///     Number of tokens in the prompt.
    /// </summary>
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    /// <summary>
    ///     Number of tokens in the completion.
    /// </summary>
    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    /// <summary>
    ///     Total tokens used (prompt + completion).
    /// </summary>
    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

/// <summary>
///     Configuration for creating an LLM provider instance.
/// </summary>
internal sealed class LLMProviderConfig
{
    /// <summary>
    ///     The provider type. One of: "lmstudio", "kimi", "deepseek", "openrouter".
    /// </summary>
    public string ProviderType { get; set; } = "lmstudio";

    /// <summary>
    ///     API key for remote providers. Not required for LM Studio (local).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    ///     The model name to use for completions.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    ///     Optional custom endpoint URL override. If not set, the provider default is used.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    ///     Request timeout in seconds. Default: 30.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    ///     Maximum number of retries for failed requests. Default: 3.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    ///     Temperature for response generation. Default: 0.7.
    /// </summary>
    public double Temperature { get; set; } = 0.7;

    /// <summary>
    ///     Maximum tokens in the response. Default: 2048.
    /// </summary>
    public int MaxTokens { get; set; } = 2048;
}