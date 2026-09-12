#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using ValleyAgent.WebSocket;

namespace ValleyAgent.TestMod.Mock;

/// <summary>
///     Response matching engine for the mock LLM layer. Maintains internal state
///     (last NPC state) and resolves incoming dialogue requests to canned
///     <see cref="DialogueResponse" /> instances loaded from
///     <see cref="MockResponseLibrary" />.
///     decision 管道已删除（TS 端无路由），仅保留 dialogue 响应匹配。
/// </summary>
public class MockLLMProvider
{
    private const string PlayerSaysPrefix = "player_says:";

    private readonly MockResponseLibrary _library;
    private readonly object _overrideLock = new();
    private readonly object _stateLock = new();

    private string _lastState = string.Empty;

    // 集成测试用：注入一次性的 override 响应，优先于 library 匹配返回。
    // 测试设置后，下一次 GetDialogueResponse 调用会返回此响应并自动清空，
    // 避免影响后续测试。null 表示无 override。
    private DialogueResponse? _overrideResponse;

    /// <summary>
    ///     Create a new <see cref="MockLLMProvider" /> backed by the given library.
    /// </summary>
    /// <param name="library">The response library to match against.</param>
    public MockLLMProvider(MockResponseLibrary library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
    }

    /// <summary>Last NPC state observed (thread-safe read).</summary>
    public string LastState
    {
        get
        {
            lock (_stateLock)
            {
                return _lastState;
            }
        }
    }

    /// <summary>
    ///     Resolve a dialogue request payload to a <see cref="DialogueResponse" />.
    /// </summary>
    /// <param name="requestPayload">The raw JSON payload of the dialogue request.</param>
    /// <returns>A matched <see cref="DialogueResponse" />, or the library default.</returns>
    public DialogueResponse GetDialogueResponse(JsonElement requestPayload)
    {
        // 集成测试 override 优先：返回后自动清空，避免污染后续测试。
        lock (_overrideLock)
        {
            if (_overrideResponse != null)
            {
                var resp = _overrideResponse;
                _overrideResponse = null;
                return resp;
            }
        }

        var playerInput = ExtractString(requestPayload, "playerInput");

        var trigger = MatchDialogueTrigger(playerInput);
        var mock = trigger != null ? _library.FindDialogueByTrigger(trigger) : null;
        return mock?.Response ?? _library.DefaultDialogue;
    }

    /// <summary>
    ///     注入一次性的 override 响应。下一次 <see cref="GetDialogueResponse" /> 调用
    ///     会返回此响应并自动清空。用于集成测试模拟 LLM 返回特定 action
    ///     （如 set_state FOLLOW / give_gift / chop_tree），无需修改 library JSON。
    /// </summary>
    /// <param name="response">The override response to return next.</param>
    public void SetOverrideResponse(DialogueResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (_overrideLock)
        {
            _overrideResponse = response;
        }
    }

    /// <summary>清除已设置的 override 响应（如果有）。测试 teardown 时调用以确保隔离。</summary>
    public void ClearOverrideResponse()
    {
        lock (_overrideLock)
        {
            _overrideResponse = null;
        }
    }

    /// <summary>
    ///     Notify the provider of an external state change (e.g. test setup).
    ///     Updates the internal last-state used for trigger matching.
    /// </summary>
    /// <param name="newState">The new NPC state name (e.g. "FARM").</param>
    public void NotifyStateChange(string newState)
    {
        lock (_stateLock)
        {
            _lastState = newState ?? string.Empty;
        }
    }

    /// <summary>Reset all internal counters and state to initial values.</summary>
    public void Reset()
    {
        lock (_stateLock)
        {
            _lastState = string.Empty;
        }

        ClearOverrideResponse();
    }

    /// <summary>
    ///     Look up a pending tool call by trigger. Tool calls are NOT auto-sent;
    ///     tests call API methods directly to simulate LLM-driven tool invocations.
    /// </summary>
    /// <param name="trigger">The trigger string to match (e.g. "after_dialogue:gift").</param>
    /// <returns>A <see cref="ToolCallRequest" /> if matched, otherwise null.</returns>
    public ToolCallRequest? GetPendingToolCall(string trigger)
    {
        if (string.IsNullOrEmpty(trigger))
        {
            return null;
        }

        var mock = _library.FindToolCallByTrigger(trigger);
        if (mock is null)
        {
            return null;
        }

        var args = mock.Args != null
            ? new Dictionary<string, object>(mock.Args, StringComparer.OrdinalIgnoreCase)
            : null;
        return new ToolCallRequest(mock.Tool, args);
    }

    private string? MatchDialogueTrigger(string playerInput)
    {
        var candidates = new List<string>();

        if (string.IsNullOrWhiteSpace(playerInput))
        {
            candidates.Add("greeting");
        }

        foreach (var resp in _library.DialogueResponses)
        {
            if (resp.Trigger.StartsWith(PlayerSaysPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var keyword = resp.Trigger[PlayerSaysPrefix.Length..];
                if (!string.IsNullOrEmpty(keyword)
                    && playerInput.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(resp.Trigger);
                }
            }
        }

        if (ContainsGift(playerInput))
        {
            candidates.Add("gift_received");
        }

        candidates.Add("default");

        foreach (var candidate in candidates)
        {
            if (_library.FindDialogueByTrigger(candidate) != null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ExtractString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static bool ContainsGift(string input)
    {
        return input.Contains("gift", StringComparison.OrdinalIgnoreCase)
               || input.Contains("礼物", StringComparison.Ordinal);
    }
}

/// <summary>
///     Represents a simulated LLM tool call (give_gift / speak / emote / etc.).
///     Tests retrieve these via <see cref="MockLLMProvider.GetPendingToolCall" />
///     and invoke the corresponding API methods directly.
/// </summary>
public record ToolCallRequest(string Tool, Dictionary<string, object>? Args);