using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ValleyTalk.LLM;

internal abstract class OpenAICompatibleProvider : ILLMProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    protected readonly string BaseEndpoint;
    protected readonly LLMProviderConfig Config;
    protected readonly string DefaultModel;
    protected readonly HttpClient HttpClient;

    protected OpenAICompatibleProvider(
        HttpClient httpClient,
        LLMProviderConfig config,
        string baseEndpoint,
        string defaultModel)
    {
        HttpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        BaseEndpoint = config.Endpoint ?? baseEndpoint;
        DefaultModel = string.IsNullOrEmpty(config.Model) ? defaultModel : config.Model;

        HttpClient.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
    }

    public abstract string ProviderName { get; }

    public async Task<LLMResponse> ChatCompletionAsync(
        List<LLMMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages == null || messages.Count == 0)
        {
            return LLMResponse.Failure("Messages list cannot be empty.");
        }

        var requestBody = BuildRequestBody(messages, false);
        var maxAttempts = Config.MaxRetries + 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = CreateRequestMessage(requestBody);
                using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead,
                    cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == (HttpStatusCode)429)
                {
                    if (attempt < maxAttempts)
                    {
                        await ApplyRetryDelayAsync(attempt, response, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return LLMResponse.Failure("Rate limit exceeded after maximum retries.", (int)response.StatusCode);
                }

                if ((int)response.StatusCode >= 500 && (int)response.StatusCode < 600)
                {
                    if (attempt < maxAttempts)
                    {
                        await ApplyRetryDelayAsync(attempt, response, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var serverErrorBody =
                        await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    return LLMResponse.Failure($"Server error: {(int)response.StatusCode} - {serverErrorBody}",
                        (int)response.StatusCode);
                }

                if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
                {
                    var clientErrorBody =
                        await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    return LLMResponse.Failure($"Client error: {(int)response.StatusCode} - {clientErrorBody}",
                        (int)response.StatusCode);
                }

                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseCompletionResponse(responseBody);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                if (attempt < maxAttempts)
                {
                    await Task.Delay(CalculateBackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return LLMResponse.Failure($"Request timed out after {Config.TimeoutSeconds}s.");
            }
            catch (HttpRequestException ex)
            {
                if (attempt < maxAttempts)
                {
                    await Task.Delay(CalculateBackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return LLMResponse.Failure($"HTTP request failed: {ex.Message}");
            }
            catch (JsonException ex)
            {
                return LLMResponse.Failure($"Failed to parse response JSON: {ex.Message}");
            }
        }

        return LLMResponse.Failure("Failed to complete request after maximum retries.");
    }

    public async IAsyncEnumerable<string> StreamCompletionAsync(
        List<LLMMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (messages == null || messages.Count == 0)
        {
            yield break;
        }

        var requestBody = BuildRequestBody(messages, true);
        using var request = CreateRequestMessage(requestBody);
        using var response =
            await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var reader = new StreamReader(responseStream);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (line == null)
                {
                    // ReadLineAsync 返回 null 表示已到流末尾，避免在异步方法中使用同步阻塞的 EndOfStream
                    break;
                }

                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                if (!line.StartsWith("data: "))
                {
                    continue;
                }

                var data = line.Substring(6);

                if (data == "[DONE]")
                {
                    yield break;
                }

                var contentDelta = ParseStreamChunk(data);
                if (contentDelta != null)
                {
                    yield return contentDelta;
                }
            }
        }
        finally
        {
            await responseStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<bool> ValidateConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var testMessages = new List<LLMMessage>
            {
                new("user", "Hello")
            };

            var result = await ChatCompletionAsync(testMessages, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    #region JSON DTOs for OpenAI-compatible API

    private sealed class ChatCompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")] public List<LLMMessage> Messages { get; set; } = new();

        [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.7;

        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; } = 2048;

        [JsonPropertyName("stream")] public bool Stream { get; set; }
    }

    #endregion

    #region Protected Virtual Methods

    protected virtual void ConfigureRequestHeaders(HttpRequestMessage request)
    {
    }

    protected virtual string GetChatCompletionsPath() => "/v1/chat/completions";

    #endregion

    #region Private Methods

    private string BuildRequestBody(List<LLMMessage> messages, bool stream)
    {
        var requestObj = new ChatCompletionRequest
        {
            Model = DefaultModel,
            Messages = messages,
            Temperature = Config.Temperature,
            MaxTokens = Config.MaxTokens,
            Stream = stream
        };

        return JsonSerializer.Serialize(requestObj, JsonOptions);
    }

    private HttpRequestMessage CreateRequestMessage(string requestBody)
    {
        var url = $"{BaseEndpoint.TrimEnd('/')}{GetChatCompletionsPath()}";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrEmpty(Config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);
        }

        ConfigureRequestHeaders(request);

        return request;
    }

    private LLMResponse ParseCompletionResponse(string responseBody)
    {
        var json = JsonNode.Parse(responseBody);
        var choices = json?["choices"]?.AsArray();

        if (choices == null || choices.Count == 0)
        {
            return LLMResponse.Failure("No choices returned in response.");
        }

        var firstChoice = choices[0];
        var content = firstChoice?["message"]?["content"]?.GetValue<string>() ?? string.Empty;
        var finishReason = firstChoice?["finish_reason"]?.GetValue<string>() ?? string.Empty;
        var model = json?["model"]?.GetValue<string>() ?? DefaultModel;

        LLMUsage? usage = null;
        var usageNode = json?["usage"];
        if (usageNode != null)
        {
            usage = new LLMUsage
            {
                PromptTokens = usageNode["prompt_tokens"]?.GetValue<int>() ?? 0,
                CompletionTokens = usageNode["completion_tokens"]?.GetValue<int>() ?? 0,
                TotalTokens = usageNode["total_tokens"]?.GetValue<int>() ?? 0
            };
        }

        return LLMResponse.Success(content, model, finishReason, usage);
    }

    private static string? ParseStreamChunk(string data)
    {
        try
        {
            var json = JsonNode.Parse(data);
            return json?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task ApplyRetryDelayAsync(int attempt, HttpResponseMessage? response,
        CancellationToken cancellationToken)
    {
        var delayMs = CalculateBackoffDelay(attempt);

        if (response != null && response.StatusCode == (HttpStatusCode)429)
        {
            if (response.Headers.RetryAfter?.Delta.HasValue == true)
            {
                delayMs = Math.Max(delayMs, (int)response.Headers.RetryAfter.Delta.Value.TotalMilliseconds);
            }
        }

        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
    }

    private static int CalculateBackoffDelay(int attempt)
    {
        var baseDelay = (int)Math.Pow(2, attempt) * 1000;
        var jitter = RandomNumberGenerator.GetInt32(0, 500);
        return baseDelay + jitter;
    }

    #endregion
}