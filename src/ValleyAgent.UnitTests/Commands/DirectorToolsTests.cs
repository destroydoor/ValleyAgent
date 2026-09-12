using System.Text.Json;
using ValleyAgent.Beats;
using ValleyAgent.Commands;
using ValleyAgent.Protocol;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using Xunit;
using ActionResultReason = ValleyAgent.Protocol.ProtocolV2.ActionResultReason;

namespace ValleyAgent.UnitTests.Commands;

/// <summary>
///     阶段 3 DirectorTools 单元测试（spec §2.5 / §2.9）。
///     通过内部构造注入 brain 解析器与 BeatStore（单测接缝），不依赖 AgentService / Game1。
///     参数经 JSON 反序列化构造（JsonElement 值），与线上 director_command 消息格式一致。
/// </summary>
public class DirectorToolsTests
{
    private readonly Dictionary<string, AgentInstance> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly BeatStore _beatStore = new();

    /// <summary>按 JSON 构造 args（值与线上 System.Text.Json 反序列化结果一致：JsonElement）。</summary>
    private static Dictionary<string, object> ArgsJson(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, object>>(json)!;

    private DirectorTools CreateTools(bool withBeatStore = true, int defaultMinutes = 120) =>
        new(null, withBeatStore ? _beatStore : null, defaultMinutes,
            name => _agents.TryGetValue(name, out var agent) ? agent : null);

    private static AgentInstance CreateAgent(string name) =>
        new(name, new AgentStateMachine(), maxHealth: 100);

    // ───────────────────────── set_npc_mood ─────────────────────────

    [Fact]
    public void SetNpcMood_SetsMoodTag()
    {
        _agents["Shane"] = CreateAgent("Shane");
        var tools = CreateTools();

        var (success, reason, _) = tools.SetNpcMood(ArgsJson("""{"npc":"Shane","moodTag":"irritable"}"""));

        Assert.True(success);
        Assert.Equal(ActionResultReason.None, reason);
        Assert.Equal("irritable", _agents["Shane"].Brain.MoodTag);
    }

    [Fact]
    public void SetNpcMood_MissingNpc_Fails()
    {
        var tools = CreateTools();

        var (success, reason, _) = tools.SetNpcMood(ArgsJson("""{"npc":"Nobody","moodTag":"angry"}"""));

        Assert.False(success);
        Assert.Equal(ActionResultReason.AgentMissing, reason);
    }

    [Fact]
    public void SetNpcMood_EmptyMoodTag_Clears()
    {
        _agents["Shane"] = CreateAgent("Shane");
        _agents["Shane"].Brain.MoodTag = "angry";
        var tools = CreateTools();

        tools.SetNpcMood(ArgsJson("""{"npc":"Shane","moodTag":""}"""));

        Assert.Equal("", _agents["Shane"].Brain.MoodTag);
    }

    // ───────────────────────── set_npc_working_on ─────────────────────────

    [Fact]
    public void SetNpcWorkingOn_SetsAndClears()
    {
        _agents["Marnie"] = CreateAgent("Marnie");
        var tools = CreateTools();

        tools.SetNpcWorkingOn(ArgsJson("""{"npc":"Marnie","workingOn":"feeding_animals"}"""));
        Assert.Equal("feeding_animals", _agents["Marnie"].Brain.WorkingOn);

        tools.SetNpcWorkingOn(ArgsJson("""{"npc":"Marnie","workingOn":""}"""));
        Assert.Null(_agents["Marnie"].Brain.WorkingOn);
    }

    [Fact]
    public void SetNpcWorkingOn_MissingNpc_Fails()
    {
        var tools = CreateTools();

        var (success, reason, _) = tools.SetNpcWorkingOn(ArgsJson("""{"npc":"Nobody","workingOn":"chores"}"""));

        Assert.False(success);
        Assert.Equal(ActionResultReason.AgentMissing, reason);
    }

    // ───────────────────────── set_npc_recent_events ─────────────────────────

    [Fact]
    public void SetNpcRecentEvents_ReplacesTodayEvents()
    {
        _agents["Abigail"] = CreateAgent("Abigail");
        _agents["Abigail"].Brain.AddTodayEvent("old event", "Y1_spring_1");
        var tools = CreateTools();

        var (success, _, _) = tools.SetNpcRecentEvents(
            ArgsJson("""{"npc":"Abigail","events":["found a diamond","visited the mines"]}"""));

        Assert.True(success);
        Assert.Equal(2, _agents["Abigail"].Brain.TodayEvents.Count);
        // Director 整体替换写入无日期前缀 → 原样保留
        Assert.Equal("found a diamond", _agents["Abigail"].Brain.TodayEvents[0]);
        Assert.Equal("visited the mines", _agents["Abigail"].Brain.TodayEvents[1]);
    }

    [Fact]
    public void SetNpcRecentEvents_EmptyList_Fails()
    {
        _agents["Abigail"] = CreateAgent("Abigail");
        var tools = CreateTools();

        var (success, reason, _) = tools.SetNpcRecentEvents(ArgsJson("""{"npc":"Abigail","events":[]}"""));

        Assert.False(success);
        Assert.Equal(ActionResultReason.InvalidState, reason);
    }

    [Fact]
    public void SetNpcRecentEvents_ExceedsMax_Truncates()
    {
        _agents["Abigail"] = CreateAgent("Abigail");
        var tools = CreateTools();
        var events = string.Join(",", Enumerable.Range(1, 9).Select(i => $"\"event {i}\""));

        var (success, _, _) = tools.SetNpcRecentEvents(ArgsJson($$"""{"npc":"Abigail","events":[{{events}}]}"""));

        Assert.True(success);
        Assert.Equal(ValleyAgent.Brain.AgentBrain.MaxTodayEvents, _agents["Abigail"].Brain.TodayEvents.Count);
    }

    // ───────────────────────── set_npc_money ─────────────────────────

    [Fact]
    public void SetNpcMoney_PositiveDelta_AddsFunds()
    {
        _agents["Lewis"] = CreateAgent("Lewis");
        var tools = CreateTools();

        var (success, reason, _) = tools.SetNpcMoney(ArgsJson("""{"npc":"Lewis","delta":200}"""));

        Assert.True(success);
        Assert.Equal(ActionResultReason.None, reason);
        Assert.Equal(200, _agents["Lewis"].Inventory.Money);
    }

    [Fact]
    public void SetNpcMoney_NegativeDelta_SpendsFunds()
    {
        _agents["Lewis"] = CreateAgent("Lewis");
        _agents["Lewis"].Inventory.AddMoney(500, "test_seed");
        var tools = CreateTools();

        var (success, _, _) = tools.SetNpcMoney(ArgsJson("""{"npc":"Lewis","delta":-150}"""));

        Assert.True(success);
        Assert.Equal(350, _agents["Lewis"].Inventory.Money);
    }

    [Fact]
    public void SetNpcMoney_InsufficientFunds_Fails()
    {
        _agents["Lewis"] = CreateAgent("Lewis"); // 余额 0
        var tools = CreateTools();

        var (success, reason, _) = tools.SetNpcMoney(ArgsJson("""{"npc":"Lewis","delta":-50}"""));

        Assert.False(success);
        Assert.Equal(ActionResultReason.InvalidState, reason);
    }

    [Fact]
    public void SetNpcMoney_ZeroDelta_Fails()
    {
        _agents["Lewis"] = CreateAgent("Lewis");
        var tools = CreateTools();

        var (success, reason, _) = tools.SetNpcMoney(ArgsJson("""{"npc":"Lewis","delta":0}"""));

        Assert.False(success);
        Assert.Equal(ActionResultReason.InvalidState, reason);
    }

    // ───────────────────────── set_npc_inventory ─────────────────────────

    [Fact]
    public void SetNpcInventory_NoAddRemove_NoopSucceeds()
    {
        _agents["Marnie"] = CreateAgent("Marnie");
        var tools = CreateTools();

        var (success, reason, _) = tools.SetNpcInventory(ArgsJson("""{"npc":"Marnie"}"""));

        Assert.True(success);
        Assert.Equal(ActionResultReason.None, reason);
    }

    [Fact]
    public void SetNpcInventory_MissingNpc_Fails()
    {
        var tools = CreateTools();

        var (success, reason, _) = tools.SetNpcInventory(ArgsJson("""{"npc":"Nobody"}"""));

        Assert.False(success);
        Assert.Equal(ActionResultReason.AgentMissing, reason);
    }

    [Fact]
    public void SetNpcInventory_RemoveUnknownItem_Fails()
    {
        _agents["Marnie"] = CreateAgent("Marnie");
        var tools = CreateTools();

        var (success, reason, _) = tools.SetNpcInventory(
            ArgsJson("""{"npc":"Marnie","remove":["(O)99999"]}"""));

        Assert.False(success);
        Assert.Equal(ActionResultReason.ItemNotFound, reason);
    }

    // ───────────────────────── set_npc_position ─────────────────────────

    [Fact]
    public void SetNpcPosition_NoGameState_FailsGracefully()
    {
        // 无 Game1 状态（单测环境）：Game1.getCharacterFromName 抛 NRE → 降级 AgentMissing，不崩溃
        var tools = CreateTools();

        var (success, reason, _) = tools.SetNpcPosition(
            ArgsJson("""{"npc":"Shane","location":"Town","tile":[5,4]}"""));

        Assert.False(success);
        Assert.Equal(ActionResultReason.AgentMissing, reason);
    }

    [Fact]
    public void SetNpcPosition_MissingTile_Fails()
    {
        var tools = CreateTools();

        var (success, reason, _) = tools.SetNpcPosition(
            ArgsJson("""{"npc":"Shane","location":"Town"}"""));

        Assert.False(success);
        Assert.Equal(ActionResultReason.InvalidState, reason);
    }

    [Fact]
    public void SetNpcPosition_MissingLocation_Fails()
    {
        var tools = CreateTools();

        var (success, reason, _) = tools.SetNpcPosition(
            ArgsJson("""{"npc":"Shane","tile":[5,4]}"""));

        Assert.False(success);
        Assert.Equal(ActionResultReason.InvalidState, reason);
    }

    // ───────────────────────── spawn_beat / spawn_group_beat ─────────────────────────

    [Fact]
    public void SpawnBeat_CreatesBeat()
    {
        var tools = CreateTools();

        var (success, reason, _) = tools.SpawnBeat(
            ArgsJson("""{"npc":"Shane","sceneDesc":"Shane broods by the river","expectedInteraction":"talk to Shane","durationMinutes":60}"""));

        Assert.True(success);
        Assert.Equal(ActionResultReason.None, reason);
        var beat = _beatStore.GetActiveBeat("Shane");
        Assert.NotNull(beat);
        Assert.Equal("Shane", beat!.Npc);
        Assert.Equal("Shane broods by the river", beat.SceneDesc);
        Assert.Equal("talk to Shane", beat.ExpectedInteraction);
        Assert.True(beat.PlayerVisible);
    }

    [Fact]
    public void SpawnBeat_MissingSceneDesc_Fails()
    {
        var tools = CreateTools();

        var (success, reason, _) = tools.SpawnBeat(ArgsJson("""{"npc":"Shane"}"""));

        Assert.False(success);
        Assert.Equal(ActionResultReason.InvalidState, reason);
    }

    [Fact]
    public void SpawnBeat_NoBeatStore_Fails()
    {
        var tools = CreateTools(withBeatStore: false);

        var (success, reason, _) = tools.SpawnBeat(
            ArgsJson("""{"npc":"Shane","sceneDesc":"some scene"}"""));

        Assert.False(success);
        Assert.Equal(ActionResultReason.InternalError, reason);
    }

    [Fact]
    public void SpawnBeat_SameNpc_ReplacesExisting()
    {
        var tools = CreateTools();
        tools.SpawnBeat(ArgsJson("""{"npc":"Shane","sceneDesc":"first scene"}"""));
        tools.SpawnBeat(ArgsJson("""{"npc":"Shane","sceneDesc":"second scene"}"""));

        var beats = _beatStore.GetAllActive().Where(b => b.Npc == "Shane").ToList();
        Assert.Single(beats);
        Assert.Equal("second scene", beats[0].SceneDesc);
    }

    [Fact]
    public void SpawnGroupBeat_CreatesBeatForEachNpc()
    {
        var tools = CreateTools();

        var (success, reason, _) = tools.SpawnGroupBeat(
            ArgsJson("""{"npcs":["Emily","Haley"],"location":"Saloon","sceneScript":"sisters argue over a drink"}"""));

        Assert.True(success);
        Assert.Equal(ActionResultReason.None, reason);
        Assert.NotNull(_beatStore.GetActiveBeat("Emily"));
        Assert.NotNull(_beatStore.GetActiveBeat("Haley"));
        // 场景描述带位置前缀
        Assert.Contains("Saloon", _beatStore.GetActiveBeat("Emily")!.SceneDesc);
    }

    [Fact]
    public void SpawnGroupBeat_MissingNpcs_Fails()
    {
        var tools = CreateTools();

        var (success, reason, _) = tools.SpawnGroupBeat(
            ArgsJson("""{"npcs":[],"location":"Saloon","sceneScript":"nobody here"}"""));

        Assert.False(success);
        Assert.Equal(ActionResultReason.InvalidState, reason);
    }

    // ───────────────────────── inject_memory ─────────────────────────

    [Fact]
    public void InjectMemory_AddsMemory()
    {
        _agents["Shane"] = CreateAgent("Shane");
        var tools = CreateTools();

        var (success, reason, _) = tools.InjectMemory(
            ArgsJson("""{"npc":"Shane","text":"the farmer bought him a beer","importance":3,"tags":["player","kindness"]}"""));

        Assert.True(success);
        Assert.Equal(ActionResultReason.None, reason);
        Assert.Single(_agents["Shane"].Brain.ShortTermMemories);
        Assert.Equal("the farmer bought him a beer", _agents["Shane"].Brain.ShortTermMemories[0].Text);
    }

    [Fact]
    public void InjectMemory_MissingText_Fails()
    {
        _agents["Shane"] = CreateAgent("Shane");
        var tools = CreateTools();

        var (success, reason, _) = tools.InjectMemory(ArgsJson("""{"npc":"Shane"}"""));

        Assert.False(success);
        Assert.Equal(ActionResultReason.InvalidState, reason);
    }

    [Fact]
    public void InjectMemory_MissingNpc_Fails()
    {
        var tools = CreateTools();

        var (success, reason, _) = tools.InjectMemory(ArgsJson("""{"npc":"Nobody","text":"hi"}"""));

        Assert.False(success);
        Assert.Equal(ActionResultReason.AgentMissing, reason);
    }

    // ───────────────────────── Execute 入口 ─────────────────────────

    [Fact]
    public void Execute_UnknownTool_Fails()
    {
        var tools = CreateTools();

        var (success, reason, message) = tools.Execute("set_npc_teleport", new Dictionary<string, object>());

        Assert.False(success);
        Assert.Equal(ActionResultReason.InvalidState, reason);
        Assert.Contains("unknown_director_tool", message);
    }

    [Fact]
    public void Execute_EmptyTool_Fails()
    {
        var tools = CreateTools();

        var (success, reason, _) = tools.Execute("", new Dictionary<string, object>());

        Assert.False(success);
        Assert.Equal(ActionResultReason.InvalidState, reason);
    }

    [Fact]
    public void Execute_NullArgs_HandledGracefully()
    {
        var tools = CreateTools();

        var (success, reason, _) = tools.Execute("set_npc_mood", null);

        Assert.False(success);
        Assert.Equal(ActionResultReason.AgentMissing, reason);
    }
}
