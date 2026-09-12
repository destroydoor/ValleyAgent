#nullable enable
using System;
using System.Threading;

namespace ValleyAgent.TestMod;

public static class DialogueInteractionTests
{
    private static int _requestCount;
    private static int _successCount;
    private static int _rejectCount;
    private static string? _lastResponse;

    public static void Reset()
    {
        _requestCount = 0;
        _successCount = 0;
        _rejectCount = 0;
        _lastResponse = null;
    }

    public static bool SendDuplicateRequests(IValleyAgentApi api, string npcName, string message)
    {
        ArgumentNullException.ThrowIfNull(api);
        _requestCount = 0;
        _successCount = 0;
        _rejectCount = 0;

        var first = api.TryGenerateDialogue(npcName, message);
        _requestCount++;
        if (first)
        {
            _successCount++;
        }
        else
        {
            _rejectCount++;
        }

        var second = api.TryGenerateDialogue(npcName, message);
        _requestCount++;
        if (second)
        {
            _successCount++;
        }
        else
        {
            _rejectCount++;
        }

        return first && !second;
    }

    public static TestResult DuplicateRequestWasRejected()
    {
        if (_requestCount != 2)
        {
            return TestResult.Fail($"Expected 2 requests, got {_requestCount}.");
        }

        if (_successCount != 1)
        {
            return TestResult.Fail($"Expected 1 successful request, got {_successCount}.");
        }

        if (_rejectCount != 1)
        {
            return TestResult.Fail($"Expected 1 rejected request, got {_rejectCount}.");
        }

        return TestResult.Pass("Duplicate dialogue request correctly rejected (1 accepted, 1 rejected).");
    }

    public static bool WaitForResponse(IValleyAgentApi api, string npcName, out string response, int maxTicks = 3600)
    {
        ArgumentNullException.ThrowIfNull(api);
        response = string.Empty;
        _lastResponse = null;

        for (var i = 0; i < maxTicks; i++)
        {
            if (api.TryGetLastDialogue(npcName, out var resp))
            {
                if (!string.IsNullOrWhiteSpace(resp))
                {
                    response = resp;
                    _lastResponse = resp;
                    return true;
                }
            }

            Thread.Sleep(16);
        }

        return false;
    }

    public static void ClearDialogueCooldown(string npcName)
    {
        // 通过公共 API 清除对话冷却和挂起请求，避免反射访问 ValleyAgentApi 私有字段。
        // 原始反射实现会清除 _lastDialogueTimestamp 和 _pendingDialogueRequests 两个内部状态，
        // 现统一委托给 IValleyAgentApi.ClearDialogueCooldown（其内部会清除冷却时间戳）和
        // IValleyAgentApi.ClearDialogueState（其内部会清除挂起请求）。
        ModEntry.API?.ClearDialogueCooldown(npcName);
        ModEntry.API?.ClearDialogueState(npcName);
    }

    public static TestResult NpcRespondedNormally(string npcName, string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return TestResult.Fail($"NPC '{npcName}' did not produce any response.");
        }

        if (response.StartsWith("[Error:", StringComparison.OrdinalIgnoreCase))
        {
            return TestResult.Fail($"NPC '{npcName}' returned an error: {response}");
        }

        if (response.Length < 3)
        {
            return TestResult.Fail($"NPC '{npcName}' response too short: '{response}'");
        }

        return TestResult.Pass(
            $"NPC '{npcName}' responded normally ({response.Length} chars): {response.Substring(0, Math.Min(40, response.Length))}...");
    }
}