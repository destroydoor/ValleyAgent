#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ValleyAgent.WebSocket;

namespace ValleyAgent.TestMod.Mock;

/// <summary>
///     JSON loader for mock LLM responses. Reads <c>Data/mock_llm_responses.json</c>
///     and exposes trigger-based lookup helpers used by <see cref="MockLLMProvider" />.
///     decision 管道已删除，仅保留 dialogue/tool call 响应。
/// </summary>
public class MockResponseLibrary
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly List<MockDialogueResponse> _dialogueResponses;
    private readonly List<MockToolCall> _toolCalls;

    private MockResponseLibrary(
        List<MockDialogueResponse> dialogueResponses,
        List<MockToolCall> toolCalls,
        DialogueResponse defaultDialogue)
    {
        _dialogueResponses = dialogueResponses;
        _toolCalls = toolCalls;
        DefaultDialogue = defaultDialogue;
    }

    /// <summary>Default dialogue returned when no trigger matches.</summary>
    public DialogueResponse DefaultDialogue { get; }

    /// <summary>All configured dialogue responses (read-only view).</summary>
    public IReadOnlyList<MockDialogueResponse> DialogueResponses
    {
        get => _dialogueResponses;
    }

    /// <summary>All configured tool calls (read-only view).</summary>
    public IReadOnlyList<MockToolCall> AllToolCalls
    {
        get => _toolCalls;
    }

    /// <summary>
    ///     Load a <see cref="MockResponseLibrary" /> from the given JSON file path.
    ///     Throws if the file is missing or the JSON is invalid.
    /// </summary>
    /// <param name="path">Absolute or relative path to the JSON file.</param>
    /// <returns>A populated <see cref="MockResponseLibrary" /> instance.</returns>
    /// <exception cref="FileNotFoundException">Thrown if the file does not exist.</exception>
    /// <exception cref="JsonException">Thrown if the JSON cannot be deserialized.</exception>
    public static MockResponseLibrary Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must not be null or whitespace.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Mock response file not found.", path);
        }

        var json = File.ReadAllText(path);
        var data = JsonSerializer.Deserialize<MockResponsesData>(json, Options)
                   ?? throw new JsonException("Deserialized MockResponsesData was null.");

        return new MockResponseLibrary(
            data.DialogueResponses ?? new List<MockDialogueResponse>(),
            data.ToolCalls ?? new List<MockToolCall>(),
            data.DefaultDialogue ?? CreateDefaultDialogue());
    }

    /// <summary>
    ///     Load a <see cref="MockResponseLibrary" /> from the given path, returning
    ///     sensible defaults if the file is missing or malformed. Never throws.
    /// </summary>
    /// <param name="path">Absolute or relative path to the JSON file.</param>
    /// <param name="log">Optional log callback invoked when a fallback is used.</param>
    /// <returns>A <see cref="MockResponseLibrary" /> instance, always non-null.</returns>
    public static MockResponseLibrary LoadSafely(string path, Action<string>? log = null)
    {
        try
        {
            return Load(path);
        }
        catch (FileNotFoundException ex)
        {
            log?.Invoke($"[MockResponseLibrary] File not found: {ex.FileName}. Using defaults.");
        }
        catch (JsonException ex)
        {
            log?.Invoke($"[MockResponseLibrary] Invalid JSON: {ex.Message}. Using defaults.");
        }
        catch (IOException ex)
        {
            log?.Invoke($"[MockResponseLibrary] IO error: {ex.Message}. Using defaults.");
        }
        catch (UnauthorizedAccessException ex)
        {
            log?.Invoke($"[MockResponseLibrary] Access denied: {ex.Message}. Using defaults.");
        }

        return CreateDefault();
    }

    /// <summary>Linear search for a dialogue response matching the given trigger.</summary>
    public MockDialogueResponse? FindDialogueByTrigger(string trigger)
    {
        if (string.IsNullOrEmpty(trigger))
        {
            return null;
        }

        foreach (var item in _dialogueResponses)
        {
            if (string.Equals(item.Trigger, trigger, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>Linear search for a tool call matching the given trigger.</summary>
    public MockToolCall? FindToolCallByTrigger(string trigger)
    {
        if (string.IsNullOrEmpty(trigger))
        {
            return null;
        }

        foreach (var item in _toolCalls)
        {
            if (string.Equals(item.Trigger, trigger, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>Creates a library instance with built-in sensible defaults.</summary>
    public static MockResponseLibrary CreateDefault()
    {
        return new MockResponseLibrary(
            new List<MockDialogueResponse>(),
            new List<MockToolCall>(),
            CreateDefaultDialogue());
    }

    private static DialogueResponse CreateDefaultDialogue()
    {
        return new DialogueResponse(
            "嗯...",
            new List<ToolAction>(),
            "Neutral");
    }
}

/// <summary>Wraps a <see cref="DialogueResponse" /> with a trigger matcher.</summary>
public record MockDialogueResponse(string Id, string Trigger, DialogueResponse Response);

/// <summary>Describes a simulated LLM tool call (give_gift / speak / emote / etc.).</summary>
public record MockToolCall(string Id, string Trigger, string Tool, Dictionary<string, object>? Args);

/// <summary>Root JSON document loaded by <see cref="MockResponseLibrary.Load" />.</summary>
public record MockResponsesData(
    List<MockDialogueResponse>? DialogueResponses,
    List<MockToolCall>? ToolCalls,
    DialogueResponse? DefaultDialogue);