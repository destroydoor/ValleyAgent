using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using ValleyAgent.Save;
using ValleyAgent.Save.Models;
using ValleyAgent.StateMachine;

namespace SaveCompileTest;

internal abstract class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("SaveDataManager compilation test");

        // Test 1: Create SaveDataManager
        var manager = new SaveDataManager();
        Console.WriteLine("[PASS] SaveDataManager created");

        // Test 2: Serialize default SaveData
        var defaultData = new SaveData();
        var json = manager.Serialize(defaultData);
        if (string.IsNullOrWhiteSpace(json))
        {
            Console.WriteLine("[FAIL] Serialize returned empty JSON");
            return 1;
        }

        Console.WriteLine("[PASS] Serialize default SaveData");
        Console.WriteLine("JSON preview:");
        Console.WriteLine(json.Length > 500 ? json.Substring(0, 500) + "..." : json);

        // Test 3: Deserialize
        var deserialized = manager.Deserialize(json);
        if (deserialized == null)
        {
            Console.WriteLine("[FAIL] Deserialize returned null");
            return 1;
        }

        Console.WriteLine("[PASS] Deserialize returned non-null");

        // Test 4: Validate default data
        var isValid = manager.Validate(deserialized);
        Console.WriteLine($"[{(isValid ? "PASS" : "FAIL")}] Validate default data: {isValid}");
        if (!isValid)
        {
            return 1;
        }

        // Test 5: Round-trip with populated data
        var populated = new SaveData
        {
            Version = "2.0.0",
            AgentStates = new Dictionary<string, AgentStateData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Abigail"] = new()
                {
                    NpcName = "Abigail",
                    CurrentState = AgentState.FOLLOW,
                    CurrentLocation = "Farm",
                    PositionX = 64.5f,
                    PositionY = 32.0f,
                    CurrentTarget = "Player",
                    StateStartTime = DateTime.UtcNow.Ticks
                }
            },
            Memories = new Dictionary<string, MemoryData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Abigail"] = new()
                {
                    NpcName = "Abigail",
                    DialogueHistory = new List<DialogueExchangeData>
                    {
                        new()
                        {
                            PlayerInput = "Hello!",
                            NpcResponse = "Hi there!",
                            Timestamp = DateTime.UtcNow
                        }
                    },
                    EventHistory = new List<EventRecordData>
                    {
                        new()
                        {
                            EventType = "Gift",
                            Description = "Gave a diamond",
                            Timestamp = DateTime.UtcNow,
                            DateKey = "Spring_15_Year2"
                        }
                    }
                }
            },
            FriendshipHistory = new Dictionary<string, FriendshipHistoryData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Abigail"] = new()
                {
                    NpcName = "Abigail",
                    CurrentPoints = 1250,
                    Changes = new List<FriendshipChangeData>
                    {
                        new()
                        {
                            Timestamp = DateTime.UtcNow,
                            DateKey = "Spring_15_Year2",
                            InteractionType = "Conversation",
                            ChangeAmount = 5,
                            PointsBefore = 1245,
                            PointsAfter = 1250,
                            Reason = "Friendly chat",
                            SpecialEvents = "None",
                            DiminishingReturnsApplied = false,
                            Confidence = 0.95
                        }
                    }
                }
            },
            Allocations = new AllocationData
            {
                AgentNpcNames = new List<string> { "Abigail" },
                ManualOverrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Abigail"] = false
                }
            },
            Statistics = new StatisticsData
            {
                SessionStartTime = DateTime.UtcNow,
                TotalLlmCalls = 42,
                TotalTokensUsed = 8192,
                TotalDecisions = 15,
                AverageLlmResponseTime = 1250.5
            }
        };

        var populatedJson = manager.Serialize(populated);
        var populatedDeserialized = manager.Deserialize(populatedJson);

        var populatedValid = manager.Validate(populatedDeserialized);
        Console.WriteLine($"[{(populatedValid ? "PASS" : "FAIL")}] Validate populated data: {populatedValid}");
        if (!populatedValid)
        {
            return 1;
        }

        // Test 6: THINKING state sanitization (THINKING 已从 AgentState 枚举移除，
        // 但 SaveDataManager 仍兼容 V1 数据中的 THINKING 字符串，反序列化时替换为 IDLE)
        var thinkingJson =
            @"{ ""version"": ""2.0.0"", ""agentStates"": { ""Abigail"": { ""npcName"": ""Abigail"", ""currentState"": ""THINKING"" } }, ""memories"": {} }";
        var thinkingDeserialized = manager.Deserialize(thinkingJson);
        var thinkingValid = manager.Validate(thinkingDeserialized)
                            && thinkingDeserialized.AgentStates["Abigail"].CurrentState == AgentState.IDLE;
        Console.WriteLine(
            $"[{(thinkingValid ? "PASS" : "FAIL")}] THINKING state sanitized and validates: {thinkingValid}");
        if (!thinkingValid)
        {
            return 1;
        }

        // Test 7: V1 migration
        var v1Json = @"{ ""version"": ""1.0.0"", ""agentStates"": {}, ""memories"": {} }";
        var migrated = manager.Deserialize(v1Json);
        var migratedValid = manager.Validate(migrated);
        Console.WriteLine($"[{(migratedValid ? "PASS" : "FAIL")}] V1 migration and validation: {migratedValid}");
        if (!migratedValid)
        {
            return 1;
        }

        // Test 8: Empty/null JSON returns defaults
        var emptyData = manager.Deserialize("");
        var emptyValid = manager.Validate(emptyData);
        Console.WriteLine($"[{(emptyValid ? "PASS" : "FAIL")}] Empty JSON returns valid defaults: {emptyValid}");
        if (!emptyValid)
        {
            return 1;
        }

        // Test 9: Invalid JSON returns defaults (no throw)
        var invalidData = manager.Deserialize("{ not valid json }");
        var invalidValid = manager.Validate(invalidData);
        Console.WriteLine($"[{(invalidValid ? "PASS" : "FAIL")}] Invalid JSON returns valid defaults: {invalidValid}");
        if (!invalidValid)
        {
            return 1;
        }

        // Test 10: Thread safety - run concurrent operations
        var threadTestData = new SaveData();
        var results = new ConcurrentBag<bool>();
        var threads = new List<Thread>();
        for (var i = 0; i < 10; i++)
        {
            var t = new Thread(() =>
            {
                try
                {
                    var j = manager.Serialize(threadTestData);
                    var d = manager.Deserialize(j);
                    results.Add(manager.Validate(d));
                }
                catch
                {
                    results.Add(false);
                }
            });
            threads.Add(t);
            t.Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        var allThreadPass = true;
        foreach (var r in results)
        {
            if (!r)
            {
                allThreadPass = false;
                break;
            }
        }

        Console.WriteLine($"[{(allThreadPass ? "PASS" : "FAIL")}] Thread safety (10 concurrent ops): {allThreadPass}");
        if (!allThreadPass)
        {
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("=== ALL TESTS PASSED ===");
        return 0;
    }
}