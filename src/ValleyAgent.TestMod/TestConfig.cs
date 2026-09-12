#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod;

/// <summary>
///     统一测试配置 — 从 test_config.json 加载，控制所有测试的开关。
///     改一个文件即可，无需修改代码。
/// </summary>
public static class TestConfig
{
    private const string FileName = "test_config.json";

    private static ConfigData? s_data;
    private static string? s_configPath;

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>配置是否已成功加载。</summary>
    public static bool IsLoaded
    {
        get => s_data != null;
    }

    // ── Runner ──
    public static string Runner
    {
        get => s_data?.Runner ?? "v3";
    }

    // ── Test Group ──
    public static string TestGroupName
    {
        get => s_data?.TestGroup ?? "All";
    }

    // ── Test Filter ──
    public static IReadOnlyList<string> EnabledTests
    {
        get => s_data?.TestFilter?.Enable ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    public static IReadOnlyList<string> DisabledTests
    {
        get => s_data?.TestFilter?.Disable ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    // ── 超时语义（2026-08-20 测试系统大改 Phase 1：防乐观判定）──
    // 默认超时 = 失败。设计为"跑满时长再断言"的测试必须在 timeoutExemptions
    // 白名单登记（条目支持通配，且必须注明原因——见 test_config.json 注释约定）。
    public static bool TimeoutAsFailure
    {
        get => s_data?.TimeoutAsFailure ?? true;
    }

    public static IReadOnlyList<string> TimeoutExemptions
    {
        get => s_data?.TimeoutExemptions ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    public static bool IsTimeoutExempt(string testClassName)
    {
        if (TimeoutExemptions.Count == 0)
        {
            return false;
        }

        return TimeoutExemptions.Any(e => MatchesTestName(testClassName, e));
    }

    // ── Debug Flags ──
    public static bool SuppressDecisions
    {
        get => s_data?.DebugFlags?.SuppressDecisions ?? false;
    }

    public static bool SuppressPathfindCooldown
    {
        get => s_data?.DebugFlags?.SuppressPathfindCooldown ?? false;
    }

    public static bool ForceMiningLocation
    {
        get => s_data?.DebugFlags?.ForceMiningLocation ?? false;
    }

    public static bool ForceForageLocation
    {
        get => s_data?.DebugFlags?.ForceForageLocation ?? false;
    }

    public static int GameSpeedMultiplier
    {
        get => Math.Max(1, s_data?.DebugFlags?.GameSpeedMultiplier ?? 1);
    }

    // ── Scene ──
    public static string NpcName
    {
        get => s_data?.Scene?.NpcName ?? "Haley";
    }

    public static int PlayerTileX
    {
        get => s_data?.Scene?.PlayerTileX ?? 54;
    }

    public static int PlayerTileY
    {
        get => s_data?.Scene?.PlayerTileY ?? 30;
    }

    public static int FriendshipHearts
    {
        get => Math.Clamp(s_data?.Scene?.FriendshipHearts ?? 4, 0, 10);
    }

    public static int TimeOfDay
    {
        get => s_data?.Scene?.TimeOfDay ?? 1000;
    }

    public static int Season
    {
        get => Math.Clamp(s_data?.Scene?.Season ?? 1, 0, 3);
    }

    public static int Day
    {
        get => Math.Clamp(s_data?.Scene?.Day ?? 15, 1, 28);
    }

    public static int Year
    {
        get => Math.Max(1, s_data?.Scene?.Year ?? 1);
    }

    // ── Auto ──
    public static bool AutoExitOnComplete
    {
        get => s_data?.Auto?.ExitOnComplete ?? false;
    }

    public static int AutoStartDelayTicks
    {
        get => Math.Max(0, s_data?.Auto?.StartDelayTicks ?? 120);
    }

    // ── Test Params ──
    public static int MonitorIntervalTicks
    {
        get => s_data?.TestParams?.MonitorIntervalTicks ?? 60;
    }

    public static int LlmResponseTimeoutTicks
    {
        get => s_data?.TestParams?.LlmResponseTimeoutTicks ?? 6000;
    }

    public static int DefaultTestDurationTicks
    {
        get => s_data?.TestParams?.DefaultTestDurationTicks ?? 5000;
    }

    public static int AcceleratedSecondsPerTenMinutes
    {
        get => s_data?.TestParams?.AcceleratedSecondsPerTenMinutes ?? 3;
    }

    public static int DefaultSecondsPerTenMinutes
    {
        get => s_data?.TestParams?.DefaultSecondsPerTenMinutes ?? 7;
    }

    public static bool EnableTimeAcceleration
    {
        get => s_data?.TestParams?.EnableTimeAcceleration ?? true;
    }

    // ── Narrative ──
    public static bool NarrativeEnabled(string name) =>
        s_data?.NarrativeScenarios != null &&
        s_data.NarrativeScenarios.TryGetValue(name, out var enabled) && enabled;

    // ── Visual ──
    public static bool VisualTestEnabled(string name) =>
        s_data?.VisualTests != null &&
        s_data.VisualTests.TryGetValue(name, out var enabled) && enabled;

    // ── Apply to DebugFlags ──
    public static void ApplyDebugFlags()
    {
        DebugFlags.SuppressDecisions = SuppressDecisions;
        DebugFlags.SuppressPathfindCooldown = SuppressPathfindCooldown;
        DebugFlags.ForceMiningLocation = ForceMiningLocation;
        DebugFlags.ForceForageLocation = ForceForageLocation;
        DebugFlags.GameSpeedMultiplier = GameSpeedMultiplier;
    }

    // ── Load ──
    public static bool Load(IModHelper helper, IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(monitor);

        s_configPath = Path.Combine(helper.DirectoryPath, FileName);

        if (!File.Exists(s_configPath))
        {
            monitor.Log($"[TestConfig] 未找到 {s_configPath}，使用默认设置。", LogLevel.Warn);
            s_data = new ConfigData();
            return false;
        }

        try
        {
            var json = File.ReadAllText(s_configPath);
            s_data = JsonSerializer.Deserialize<ConfigData>(json, s_jsonOpts);

            if (s_data == null)
            {
                monitor.Log("[TestConfig] 解析失败（返回 null），使用默认设置。", LogLevel.Warn);
                s_data = new ConfigData();
                return false;
            }

            monitor.Log($"[TestConfig] 已加载: runner={s_data.Runner}, group={s_data.TestGroup}", LogLevel.Info);
            ApplyDebugFlags();
            return true;
        }
        catch (JsonException ex)
        {
            monitor.Log($"[TestConfig] JSON 解析错误: {ex.Message}，使用默认设置。", LogLevel.Warn);
            s_data = new ConfigData();
            return false;
        }
        catch (IOException ex)
        {
            monitor.Log($"[TestConfig] 读取错误: {ex.Message}，使用默认设置。", LogLevel.Warn);
            s_data = new ConfigData();
            return false;
        }
    }

    /// <summary>解析 TestGroup 字符串为位标志。支持 "All" 或 "Fuzzy|Edge" 格式。</summary>
    public static TestGroup ParseTestGroup(string? groupStr)
    {
        var raw = (groupStr ?? s_data?.TestGroup ?? "All").Trim();

        if (string.IsNullOrEmpty(raw))
        {
            return TestGroup.All;
        }

        // "All" 快捷方式
        if (raw.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            return TestGroup.All;
        }

        // 按 | 分割解析每个标志
        TestGroup result = 0;
        foreach (var part in raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<TestGroup>(part, true, out var flag))
            {
                result |= flag;
            }
        }

        return result == 0 ? TestGroup.All : result;
    }

    /// <summary>检查测试是否应该运行（根据 enable/disable 过滤器）。</summary>
    public static bool ShouldTestRun(string testClassName)
    {
        var enabled = EnabledTests;
        var disabled = DisabledTests;

        // 如果 enable 列表非空，只有列表中的测试才运行
        if (enabled.Count > 0)
        {
            return enabled.Any(e => MatchesTestName(testClassName, e));
        }

        // 如果 disable 列表非空，跳过列表中的测试
        if (disabled.Count > 0)
        {
            return !disabled.Any(d => MatchesTestName(testClassName, d));
        }

        return true;
    }

    /// <summary>
    ///     模糊匹配：支持子串匹配。
    ///     例如 "E6" 会匹配 "E6_RainFestival"。
    ///     "Rain" 也会匹配 "E6_RainFestival"。
    /// </summary>
    private static bool MatchesTestName(string className, string pattern)
    {
        if (pattern.Contains('*'))
        {
            // 简单通配符：E6_* 或 *_Rain*
            var starIdx = pattern.IndexOf('*');
            if (starIdx == 0)
            {
                return className.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
            }

            if (starIdx == pattern.Length - 1)
            {
                return className.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
            }

            var parts = pattern.Split('*', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                return className.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase) &&
                       className.EndsWith(parts[1], StringComparison.OrdinalIgnoreCase);
            }

            return className.Contains(pattern.Replace("*", ""), StringComparison.OrdinalIgnoreCase);
        }

        return className.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
               className.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }
}

// ── JSON 模型 ──

internal sealed class ConfigData
{
    public string Runner { get; set; } = "v3";
    public string TestGroup { get; set; } = "All";
    public TestFilterData? TestFilter { get; set; }
    public DebugFlagsData? DebugFlags { get; set; }
    public SceneData? Scene { get; set; }
    public Dictionary<string, bool>? NarrativeScenarios { get; set; }
    public Dictionary<string, bool>? VisualTests { get; set; }
    public AutoData? Auto { get; set; }
    public TestParamsData? TestParams { get; set; }

    // 2026-08-20：超时语义（Phase 1 防乐观判定）。默认 true（超时=失败）。
    public bool TimeoutAsFailure { get; set; } = true;

    /// <summary>超时豁免白名单：测试名/通配符列表。条目必须在 test_config.json 注明原因。</summary>
    public List<string>? TimeoutExemptions { get; set; }
}

internal sealed class TestFilterData
{
    public List<string>? Enable { get; set; }
    public List<string>? Disable { get; set; }
}

internal sealed class DebugFlagsData
{
    public bool SuppressDecisions { get; set; }
    public bool SuppressPathfindCooldown { get; set; }
    public bool ForceMiningLocation { get; set; }
    public bool ForceForageLocation { get; set; }
    public int GameSpeedMultiplier { get; set; } = 1;
}

internal sealed class SceneData
{
    public string NpcName { get; set; } = "Haley";
    public int PlayerTileX { get; set; } = 54;
    public int PlayerTileY { get; set; } = 30;
    public int FriendshipHearts { get; set; } = 4;
    public int TimeOfDay { get; set; } = 1000;
    public int Season { get; set; } = 1;
    public int Day { get; set; } = 15;
    public int Year { get; set; } = 1;
}

internal sealed class AutoData
{
    public bool ExitOnComplete { get; set; }
    public int StartDelayTicks { get; set; } = 120;
}

internal sealed class TestParamsData
{
    public int MonitorIntervalTicks { get; set; } = 60;
    public int LlmResponseTimeoutTicks { get; set; } = 6000;
    public int DefaultTestDurationTicks { get; set; } = 5000;
    public int AcceleratedSecondsPerTenMinutes { get; set; } = 3;
    public int DefaultSecondsPerTenMinutes { get; set; } = 7;
    public bool EnableTimeAcceleration { get; set; } = true;
}