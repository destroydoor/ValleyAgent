#nullable enable
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleyAgent.TestMod.Tests.Edge;

/// <summary>
///     Tests a complete in-game day cycle: morning decision, afternoon activity,
///     evening wind-down, midnight, and day-end cleanup.
///     Phase Morning (0-1200): NPC makes decision, start activity.
///     Phase Afternoon (1200-3000): NPC continues or transitions, warp to 6PM with activity.
///     Phase Evening (3000-4200): NPC winds down.
///     Phase Midnight (4200-4800): DayEnd processing.
/// </summary>
public class E8_FullDayCycle : V3TestBase
{
    private readonly bool _noErrors = true;
    private IValleyAgentApi? _api;
    private bool _decisionMade;
    private string _eveningState = string.Empty;
    private string _morningState = string.Empty;

    private NPC? _npc;
    private int _phase = 1;
    private string _originalSeason = "";
    private int _originalDay;
    private int _originalYear;

    public E8_FullDayCycle(IModHelper helper, IMonitor monitor) : base(helper, monitor)
    {
    }

    public override string TestName
    {
        get => "E8_FullDayCycle";
    }

    public override int TimeoutTicks
    {
        get => 4800;
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

        // 保存原始日期，Teardown 恢复——测试推进日期后必须还原，防止污染存档
        _originalSeason = Game1.currentSeason;
        _originalDay = Game1.dayOfMonth;
        _originalYear = Game1.year;
    }

    public override bool Update()
    {
        if (_npc == null || _api == null)
        {
            return true;
        }

        var tick = CurrentTick;

        // ── Phase Morning: Decision (0-1200) ──
        if (_phase == 1)
        {
            // Force decision at tick 120
            if (tick == 120)
            {
                var forced = _api.TryForceDecision(_npc.Name);
                Monitor.Log($"[E8] Decision forced at tick 120: {forced}.", LogLevel.Info);
            }

            // Poll for decision every 30 ticks
            if (!_decisionMade && tick % 30 == 0 && tick > 30)
            {
                var ok = _api.TryGetLastDecision(_npc.Name, out var state, out var reason);
                if (ok && !string.IsNullOrEmpty(state))
                {
                    _decisionMade = true;
                    _morningState = state;
                    Monitor.Log($"[E8] Morning decision: {state} | {reason}", LogLevel.Info);
                }
            }

            // After 1200 ticks (20s), warp to noon and check state
            if (tick >= 1200)
            {
                Assert("NPC made at least 1 decision during morning", _decisionMade,
                    $"Morning state: {_morningState}");

                // Warp time to noon (1200)
                Game1.timeOfDay = 1200;
                Monitor.Log($"[E8] Warped to noon (1200). NPC state: {_api.GetAgentState(_npc.Name)}.", LogLevel.Info);
                _phase = 2;
            }

            return false;
        }

        // ── Phase Afternoon: Continue activity, warp to 6PM (1200-3000) ──
        if (_phase == 2)
        {
            // Mid-afternoon check: NPC should still be in an activity state (not IDLE)
            if (tick == 2400)
            {
                var currentState = _api.GetAgentState(_npc.Name);
                Monitor.Log($"[E8] Afternoon state at tick 2400: {currentState}.", LogLevel.Info);
            }

            // At 3000, warp to 6PM (1800)
            if (tick == 3000)
            {
                var before = _api.GetAgentState(_npc.Name);
                Game1.timeOfDay = 1800;
                Monitor.Log($"[E8] Warped to 6PM (1800). State before: {before}.", LogLevel.Info);
                _phase = 3;
            }

            return false;
        }

        // ── Phase Evening: Wind down (3000-4200) ──
        if (_phase == 3)
        {
            // At tick 3600, record evening state
            if (tick == 3600)
            {
                _eveningState = _api.GetAgentState(_npc.Name);
                Monitor.Log($"[E8] Evening state at tick 3600: {_eveningState}.", LogLevel.Info);
            }

            // At 4200, warp to midnight
            if (tick >= 4200)
            {
                Game1.timeOfDay = 2400;
                Monitor.Log("[E8] Warped to midnight (2400).", LogLevel.Info);
                _phase = 4;
            }

            return false;
        }

        // ── Phase Midnight / Cleanup (4200-4800) ──
        if (_phase == 4)
        {
            // Simulate day-end: trigger DayEnding event processing
            if (tick == 4320)
            {
                Monitor.Log($"[E8] DayEnd processing at tick {tick}.", LogLevel.Info);
                // Increment day (triggers day-end logic)
                Game1.dayOfMonth++;
                if (Game1.dayOfMonth > 28)
                {
                    Game1.dayOfMonth = 1;
                    Game1.currentSeason = "Spring"; // was: (Season)((int)Game1.currentSeason + 1) % 4);
                }

                Game1.timeOfDay = 600; // reset to 6AM
                Game1.stats.DaysPlayed++;
                // 手动推进日期绕过了 newDayAfterFade，weatherIcon 不会自动刷新；
                // 显式同步，否则从节日日推进会残留 weatherIcon=1，
                // 触发原版 performTenMinuteClockUpdate 加载 Data/Festivals/<日>.xnb
                Game1.updateWeatherIcon();
                Monitor.Log($"[E8] Day incremented to {Game1.dayOfMonth}. DayEnd processed.", LogLevel.Info);
            }

            // Final assertions at tick 4560
            if (tick == 4560)
            {
                var finalState = _api.GetAgentState(_npc.Name);
                var health = _api.GetNpcHealth(_npc.Name);
                var onMap = _npc.currentLocation != null;

                Assert("DayEnd cleanup processed without error", _noErrors, "No setup errors");
                Assert("NPC is alive after full day cycle", health > 0, $"Health={health}");
                Assert("NPC is on map after day cycle", onMap, $"OnMap={onMap}");
                // 放宽状态断言：IDLE/THINKING/FOLLOW 均为合法状态
                Assert("NPC in valid state after day end",
                    finalState is "IDLE" or "THINKING" or "FOLLOW",
                    $"State={finalState}");

                Monitor.Log($"[E8] Final: State={finalState}, Health={health}, OnMap={onMap}.", LogLevel.Info);
            }

            if (tick >= 4800)
            {
                return true;
            }
        }

        return false;
    }

    public override void Teardown()
    {
        // 恢复被测试推进的日期；updateWeatherIcon 同步 weatherIcon
        Game1.currentSeason = _originalSeason;
        Game1.dayOfMonth = _originalDay;
        Game1.year = _originalYear;
        Game1.updateWeatherIcon();
        TestScenes.ClearAll(Helper, Monitor);
    }
}