using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ValleyTalk.LLM;

internal sealed class KimiProvider : ILLMProvider
{
    public const string DefaultEndpoint = "https://api.moonshot.cn/v1";
    public const string DefaultModel = "moonshot-v1-8k";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly LLMProviderConfig _config;
    private readonly string _endpoint;

    private readonly HttpClient _httpClient;
    private readonly string _model;

    public KimiProvider(HttpClient httpClient, LLMProviderConfig config)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _endpoint = config.Endpoint ?? DefaultEndpoint;
        _model = string.IsNullOrEmpty(config.Model) ? DefaultModel : config.Model;

        _httpClient.Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds);
    }

    public string ProviderName
    {
        get => "Kimi";
    }

    public async Task<LLMResponse> ChatCompletionAsync(
        List<LLMMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages == null || messages.Count == 0)
        {
            return LLMResponse.Failure("Messages list cannot be empty.");
        }

        var requestBody = BuildRequestBody(messages, false);
        var maxAttempts = _config.MaxRetries + 1;
        var deltasBuffer = new List<string>();

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            deltasBuffer.Clear();
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = CreateRequestMessage(requestBody);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead,
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
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                if (attempt < maxAttempts)
                {
                    await Task.Delay(CalculateBackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return LLMResponse.Failure($"Request timed out after {_config.TimeoutSeconds}s.");
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
        var maxAttempts = _config.MaxRetries + 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var success = false;
            List<string> deltas = new();

            try
            {
                deltas = await ReadStreamDeltasAsync(requestBody, cancellationToken).ConfigureAwait(false);
                success = true;
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                if (attempt < maxAttempts)
                {
                    await Task.Delay(CalculateBackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new TimeoutException($"Streaming request timed out after {_config.TimeoutSeconds}s.");
            }
            catch (HttpRequestException)
            {
                if (attempt < maxAttempts)
                {
                    await Task.Delay(CalculateBackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw;
            }
            catch (JsonException)
            {
                if (attempt < maxAttempts)
                {
                    await Task.Delay(CalculateBackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw;
            }

            if (success)
            {
                foreach (var delta in deltas)
                {
                    yield return delta;
                }

                yield break;
            }
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

    private async Task<List<string>> ReadStreamDeltasAsync(string requestBody, CancellationToken cancellationToken)
    {
        var deltas = new List<string>();

        using var request = CreateRequestMessage(requestBody);
        using var response =
            await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

        if (response.StatusCode == (HttpStatusCode)429)
        {
            throw new InvalidOperationException("Rate limit exceeded during streaming.");
        }

        if ((int)response.StatusCode >= 500 && (int)response.StatusCode < 600)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Server error during streaming: {(int)response.StatusCode} - {errorBody}");
        }

        if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
        {
            var clientErrorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Client error during streaming: {(int)response.StatusCode} - {clientErrorBody}");
        }

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
                    break;
                }

                var contentDelta = ParseStreamChunk(data);
                if (contentDelta != null)
                {
                    deltas.Add(contentDelta);
                }
            }
        }
        finally
        {
            await responseStream.DisposeAsync().ConfigureAwait(false);
        }

        return deltas;
    }

    #region JSON DTOs for Kimi (Moonshot) API

    private sealed class KimiChatCompletionRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")] public List<LLMMessage> Messages { get; set; } = new();

        [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.7;

        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; } = 2048;

        [JsonPropertyName("stream")] public bool Stream { get; set; }
    }

    #endregion

    #region Private Methods

    private string BuildRequestBody(List<LLMMessage> messages, bool stream)
    {
        var requestObj = new KimiChatCompletionRequest
        {
            Model = _model,
            Messages = messages,
            Temperature = _config.Temperature,
            MaxTokens = _config.MaxTokens,
            Stream = stream
        };

        return JsonSerializer.Serialize(requestObj, JsonOptions);
    }

    private HttpRequestMessage CreateRequestMessage(string requestBody)
    {
        var url = $"{_endpoint.TrimEnd('/')}/v1/chat/completions";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrEmpty(_config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        }

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
        var model = json?["model"]?.GetValue<string>() ?? _model;

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