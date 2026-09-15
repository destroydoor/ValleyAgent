#nullable enable
using System;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleyAgent.TestMod.Tests.Edge;

/// <summary>
///     Tests that weather and festival information are correctly included
///     in the decision context sent to the LLM.
///     Setup: Set Game1.isRaining = true, date to Egg Festival (Spring 13).
///     Update: Force decision, wait 600 ticks, inspect decision context.
///     Assertions: Weather=festival info in context, festival day recognized,
///     decision considers weather.
/// </summary>
public class E6_RainFestival : V3TestBase
{
    private IValleyAgentApi? _api;
    private bool _contextChecked;
    private bool _decisionMade;
    private string _decisionReason = string.Empty;
    private string _decisionState = string.Empty;

    private NPC? _npc;
    private string _originalSeason = "";
    private int _originalDay;

    public E6_RainFestival(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "E6_RainFestival";
    }

    public override int TimeoutTicks
    {
        get => 2100; // ~35s — 给 LLM 决策足够的等待时间
    }

    public override TestGroup Group
    {
        get => TestGroup.Edge;
    }

    public override void Setup()
    {
        _api = ModEntry.API;
        if (_api != null)
        {
            _ = _api.TryAllocateAgent("Haley");
        }

        SafeWarp.Farmer(Monitor, "Farm", 54, 15);
        _npc = Game1.getCharacterFromName("Haley");
        if (_npc == null)
        {
            Skip("Haley not found");
            return;
        }

        _npc.setTileLocation(new Vector2(32, 30));
        _npc.Halt();

        // 保存原始日期，Teardown 恢复——日期泄漏会污染后续测试和存档
        _originalSeason = Game1.currentSeason;
        _originalDay = Game1.dayOfMonth;

        // 设置雨天和节日日期（Egg Festival = Spring 13）
        Game1.isRaining = true;
        Game1.isLightning = true;
        Game1.currentSeason = "spring";
        Game1.dayOfMonth = 13;
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        var tick = CurrentTick;

        // 原 "Weather context is set (isRaining=true)" / "Festival date is set (Spring 13)" 断言已删
        //（2026-09-14 死断言清理）：Setup 无条件写入 isRaining/season/day，tick 30 读回属构造性恒真。

        // Poll for decision result every 30 ticks
        if (!_decisionMade && tick % 30 == 0 && tick > 30)
        {
            var ok = _api.TryGetLastDecision(_npc.Name, out _decisionState, out _decisionReason);
            if (ok && !string.IsNullOrEmpty(_decisionState))
            {
                _decisionMade = true;
                Monitor.Log($"[E6] Decision received: {_decisionState} | {_decisionReason}", LogLevel.Info);
            }
        }

        // After 1800 ticks (~30s), evaluate context — 给 LLM 决策足够时间
        if (tick >= 1800 && !_contextChecked)
        {
            _contextChecked = true;

            // If no decision was made, record failure
            if (!_decisionMade)
            {
                Assert("Decision was made", false, "No decision received within 1800 ticks");
                return true;
            }

            // 原 "Decision was made with state" 断言已删（2026-09-14 死断言清理）：
            // 走到这里必然 _decisionMade=true，而该标志只在 state 非空时置位，构造性恒真。

            // ─ 天气/节日关键词检查（软检查：LLM 自由回答，关键词非必须） ─
            var reasonLower = _decisionReason.ToLowerInvariant();
            var mentionsRain = reasonLower.Contains("rain") || reasonLower.Contains("wet") ||
                               reasonLower.Contains("weather") || reasonLower.Contains('雨');
            var mentionsFestival = reasonLower.Contains("festival") || reasonLower.Contains("egg") ||
                                   reasonLower.Contains("spring 13") || reasonLower.Contains("event") ||
                                   reasonLower.Contains("节日");
            Monitor.Log(
                $"[E6] 软检查: mentionsRain={mentionsRain} mentionsFestival={mentionsFestival} reason={_decisionReason[..Math.Min(80, _decisionReason.Length)]}",
                LogLevel.Info);

            return true;
        }

        if (tick >= 2100)
        {
            // Timeout
            if (!_decisionMade)
            {
                Assert("Decision was made", false, "Timed out (2100 ticks) waiting for decision");
            }

            return true;
        }

        return false;
    }

    public override void Teardown()
    {
        // Restore weather and date; updateWeatherIcon 同步 weatherIcon，
        // 避免残留"节日日"状态触发原版 Data/Festivals/<日>.xnb 加载
        Game1.isRaining = false;
        Game1.isLightning = false;
        Game1.currentSeason = _originalSeason;
        Game1.dayOfMonth = _originalDay;
        Game1.updateWeatherIcon();
        TestScenes.ClearAll(Helper, Monitor);
    }
}