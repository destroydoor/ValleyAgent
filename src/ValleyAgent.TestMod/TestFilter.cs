using System;

namespace ValleyAgent.TestMod;

/// <summary>
///     Controls which test groups run. Change test_config.json → testGroup to run specific groups.
///     No need to recompile — just edit the JSON and restart the game.
/// </summary>
public static class TestFilter
{
    /// <summary>
    ///     Active test group, loaded from test_config.json.
    ///     Set via <see cref="TestConfig.TestGroup" /> at runtime.
    /// </summary>
    public static TestGroup ActiveGroup { get; set; } = TestGroup.All;

    /// <summary>
    ///     Reload from TestConfig. Call once after config is loaded.
    /// </summary>
    public static void ReloadFromConfig() => ActiveGroup = TestConfig.ParseTestGroup(null);

    public static bool ShouldRun(TestGroup group) => (ActiveGroup & group) != 0;
}

[Flags]
public enum TestGroup
{
    Module = 1 << 0,
    Fuzzy = 1 << 1,
    Edge = 1 << 2,
    Functional = 1 << 3,
    Real = 1 << 4,
    Visual = 1 << 5,
    Experience = 1 << 6,
    Pipeline = 1 << 7,
    Integration = 1 << 8,
    // 2026-08-20 Phase 3：L3 复杂场景组（多 NPC 随机场景 + 幻觉率/执行成功率指标）
    ComplexScenario = 1 << 9,
    All = Module | Fuzzy | Edge | Functional | Real | Experience | Pipeline | Visual | Integration | ComplexScenario
}

public enum TestOutcome
{
    Passed,
    Failed,
    Skipped,
    Error
}