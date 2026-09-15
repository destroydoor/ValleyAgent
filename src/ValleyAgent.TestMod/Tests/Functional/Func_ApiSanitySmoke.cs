#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod.Tests.Functional;

/// <summary>
///     功能测试：所有只读 API 方法在游戏运行时的基础健全性。
///     测试所有 IValleyAgentApi 只读方法，验证：
///     1. 不抛出异常
///     2. 返回合理值（非 null、非负、在有效范围内）
///     这是游戏运行时的"无 LLM 消耗"烟雾测试。
/// </summary>
public class Func_ApiSanitySmoke : V3TestBase
{
    private IValleyAgentApi? _api;
    private int _failed;

    private NPC? _npc;
    private int _passed;

    public Func_ApiSanitySmoke(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "Func_ApiSanitySmoke";
    }

    public override int TimeoutTicks
    {
        get => 300;
    }

    public override TestGroup Group
    {
        get => TestGroup.Functional;
    }

    public override void Setup()
    {
        DebugFlags.SuppressDecisions = true;

        _api = ModEntry.API;
        if (_api == null)
        {
            Skip("API不可用");
            return;
        }

        SafeWarp.Farmer(Monitor, "Farm", 54, 15);
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley NPC 未找到");
            return;
        }

        _npc.setTileLocation(new Vector2(32, 30));
        _npc.Halt();
        _ = _api.TryAllocateAgent("Haley");
        _ = _api.TryRevive("Haley");

        _passed = 0;
        _failed = 0;
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        // 在 tick 60 执行所有 API 调用
        if (CurrentTick == 60)
        {
            CheckApi("GetAgentState", () =>
            {
                var state = _api.GetAgentState("Haley");
                return !string.IsNullOrEmpty(state) && state != "UNKNOWN";
            });

            CheckApi("GetNpcHealth", () =>
            {
                var health = _api.GetNpcHealth("Haley");
                return health > 0;
            });

            CheckApi("GetNpcMaxHealth", () =>
            {
                var maxHealth = _api.GetNpcMaxHealth("Haley");
                return maxHealth > 0;
            });

            CheckApi("GetNpcFriendshipPoints", () =>
            {
                var points = _api.GetNpcFriendshipPoints("Haley");
                return points >= 0;
            });

            CheckApi("GetNpcEmotion", () =>
            {
                var emotion = _api.GetNpcEmotion("Haley");
                return !string.IsNullOrEmpty(emotion);
            });

            CheckApi("GetNpcInventory", () =>
            {
                var inv = _api.GetNpcInventory("Haley");
                return inv != null;
            });

            CheckApi("GetNpcMemories", () =>
            {
                var memories = _api.GetNpcMemories("Haley");
                return memories != null;
            });

            CheckApi("GetActiveAgentNames", () =>
            {
                var names = _api.GetActiveAgentNames();
                return names != null && names.Length >= 1 && names.Contains("Haley");
            });

            CheckApi("GetEnabledFeatures", () =>
            {
                var features = _api.GetEnabledFeatures();
                return features != null && features.Length > 0;
            });

            CheckApi("GetDialogueInterceptCount", () =>
            {
                var count = _api.GetDialogueInterceptCount();
                return count >= 0;
            });

            CheckApi("TryGetLastDecision", () =>
            {
                _api.TryGetLastDecision("Haley", out var _decState, out var _decReason);
                return true; // 可能没有决策，返回 false 也正常
            });

            CheckApi("TryGetLastDialogue", () =>
            {
                _api.TryGetLastDialogue("Haley", out var _dlgResp);
                return true; // 可能没有对话，返回 false 也正常
            });
        }

        if (CurrentTick >= 180)
        {
            Assert(
                "All_sanity_checks_pass",
                _failed == 0,
                $"通过={_passed} 失败={_failed} 共={_passed + _failed}");

            AssertEx(
                "At_least_10_API_calls_tested",
                _passed + _failed >= 10,
                "IValleyAgentApi 表面收缩（API 方法被删除或前置 Skip 提前退出），CheckApi 实际执行数跌破 10",
                $"只测试了{_passed + _failed}个API");

            return true;
        }

        return false;
    }

    private void CheckApi(string name, Func<bool> check)
    {
        try
        {
            if (check())
            {
                _passed++;
                Monitor.Log($"[Func_Smoke] {name}: PASS", LogLevel.Info);
            }
            else
            {
                _failed++;
                Monitor.Log($"[Func_Smoke] {name}: FAIL (returned unexpected value)", LogLevel.Warn);
            }
        }
        catch (InvalidOperationException ex)
        {
            _failed++;
            Monitor.Log($"[Func_Smoke] {name}: EXCEPTION {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
        }
        catch (NullReferenceException ex)
        {
            _failed++;
            Monitor.Log($"[Func_Smoke] {name}: EXCEPTION {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
        }
        catch (ArgumentException ex)
        {
            _failed++;
            Monitor.Log($"[Func_Smoke] {name}: EXCEPTION {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
        }
        catch (IndexOutOfRangeException ex)
        {
            _failed++;
            Monitor.Log($"[Func_Smoke] {name}: EXCEPTION {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
        }
        catch (KeyNotFoundException ex)
        {
            _failed++;
            Monitor.Log($"[Func_Smoke] {name}: EXCEPTION {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
        }
    }

    public override void Teardown()
    {
        DebugFlags.SuppressDecisions = false;
        TestScenes.ClearAll(Helper, Monitor);
    }
}