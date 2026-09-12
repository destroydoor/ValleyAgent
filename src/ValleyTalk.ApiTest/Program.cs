using System.Text.Json;
using ValleyTalk.LLM;
using static ValleyTalk.ApiTest.Resources;

namespace ValleyTalk.ApiTest;

internal abstract class Program
{
    private static async Task Main(string[] args)
    {
        Console.WriteLine(BannerSeparator);
        Console.WriteLine(BannerTitle);
        Console.WriteLine(BannerEnd);

        var config = new LLMProviderConfig
        {
            ProviderType = "lmstudio",
            ApiKey = "",
            Model = "local-model",
            Endpoint = "http://localhost:1234",
            TimeoutSeconds = 30,
            MaxRetries = 1,
            Temperature = 0.7,
            MaxTokens = 100
        };

        using var httpClient = new HttpClient();
        var provider = LLMProviderFactory.Create(config, httpClient);

        Console.WriteLine($"Provider: {provider.ProviderName}");
        Console.WriteLine($"Endpoint: {config.Endpoint}");
        Console.WriteLine($"Model: {config.Model}\n");

        Console.Write(Test1Label);
        try
        {
            var isValid = await provider.ValidateConnectionAsync().ConfigureAwait(false);
            Console.WriteLine(isValid ? Pass : FailNoResponse);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"{FailPrefix}{ex.Message}");
            return;
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"{FailPrefix}{ex.Message}");
            return;
        }
        catch (OperationCanceledException ex)
        {
            Console.WriteLine($"{FailPrefix}{ex.Message}");
            return;
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"{FailPrefix}{ex.Message}");
            return;
        }

        Console.Write(Test2Label);
        try
        {
            var messages = new List<LLMMessage>
            {
                new("system", "You are a helpful assistant. Keep responses brief."),
                new("user", "Say 'Hello from ValleyTalk!' and nothing else.")
            };

            var response = await provider.ChatCompletionAsync(messages).ConfigureAwait(false);

            if (response.IsSuccess)
            {
                Console.WriteLine(Pass);
                Console.WriteLine($"  Response: {response.Content}");
                Console.WriteLine($"  Model: {response.Model}");
                Console.WriteLine($"  FinishReason: {response.FinishReason}");
                if (response.Usage != null)
                {
                    Console.WriteLine(
                        $"  Tokens: {response.Usage.TotalTokens} (prompt: {response.Usage.PromptTokens}, completion: {response.Usage.CompletionTokens})");
                }
            }
            else
            {
                Console.WriteLine($"{FailPrefix}{response.ErrorMessage}");
            }
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"{FailPrefix}{ex.Message}");
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"{FailPrefix}{ex.Message}");
        }
        catch (OperationCanceledException ex)
        {
            Console.WriteLine($"{FailPrefix}{ex.Message}");
        }

        Console.Write(Test3Label);
        try
        {
            var messages = new List<LLMMessage>
            {
                new("user", "Count from 1 to 3.")
            };

            var chunks = new List<string>();
            await foreach (var chunk in provider.StreamCompletionAsync(messages).ConfigureAwait(false))
            {
                chunks.Add(chunk);
                Console.Write(chunk);
            }

            Console.WriteLine($"\n{StreamPass}");
            Console.WriteLine($"  Chunks received: {chunks.Count}");
            Console.WriteLine($"  Full text: {string.Join("", chunks)}");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"{FailPrefix}{ex.Message}");
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"{FailPrefix}{ex.Message}");
        }
        catch (OperationCanceledException ex)
        {
            Console.WriteLine($"{FailPrefix}{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"{FailPrefix}{ex.Message}");
        }

        Console.WriteLine($"\n{FooterSeparator}");
        Console.WriteLine(TestComplete);
        Console.WriteLine(BannerSeparator);
    }
}