using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;

namespace ValleyAgent.Beats;

/// <summary>
///     单个 NPC 的活跃 beat 场景描述（阶段 3，3.5）。beat 有效期内作为 L3 临时剧本
///     强制注入 NPC prompt（第三人称描述，绝不提"导演"——职责隔离）。
/// </summary>
public sealed record ActiveBeat(
    string Npc,
    string SceneDesc,
    string? ExpectedInteraction,
    DateTime ExpireTime,
    bool PlayerVisible);

/// <summary>
///     C# 端当前活跃 beat 存储（阶段 3，3.5）。Director 工具 spawn_beat / spawn_group_beat 写入，
///     WorldSnapshotBuilder 读取当前 NPC 的 beat 场景描述注入 worldSnapshot.currentBeat。
///     TS 端 beat-store.ts 存导演产出（SQLite），C# 端只存"当前活跃、供 L3 注入"的场景描述，
///     简单内存存储即可——无需数据库。
/// </summary>
public class BeatStore
{
    private readonly List<ActiveBeat> _beats = new();
    private readonly object _lock = new();
    private readonly IMonitor? _monitor;

    /// <summary>
    ///     当前 BeatStore 实例（由 ServiceInitializer 注册时写入），
    ///     供 WorldSnapshotBuilder 等静态场景构建工具读取（AgentService.Current 同款模式）。
    /// </summary>
    public static BeatStore? Current { get; internal set; }

    public BeatStore(IMonitor? monitor = null)
    {
        _monitor = monitor;
    }

    /// <summary>当前未过期 beat 数（过期条目即时清除）。</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                PruneExpiredLocked();
                return _beats.Count;
            }
        }
    }

    /// <summary>
    ///     创建单 NPC beat。同 NPC 已有活跃 beat → 整体替换（导演新场景覆盖旧场景）。
    /// </summary>
    /// <param name="npc">目标 NPC 名。</param>
    /// <param name="sceneDesc">场景描述（第三人称，供 NPC L3 注入）。</param>
    /// <param name="expectedInteraction">期望玩家交互（可空，如 "talk to Shane about the festival"）。</param>
    /// <param name="duration">有效期（现实时间）。</param>
    /// <param name="playerVisible">玩家是否可见该 beat（用于 NPC 是否主动提及）。</param>
    public void Create(string npc, string sceneDesc, string? expectedInteraction, TimeSpan duration, bool playerVisible = true)
    {
        if (string.IsNullOrWhiteSpace(npc) || string.IsNullOrWhiteSpace(sceneDesc) || duration <= TimeSpan.Zero)
        {
            return;
        }

        lock (_lock)
        {
            PruneExpiredLocked();
            _beats.RemoveAll(b => b.Npc.Equals(npc, StringComparison.OrdinalIgnoreCase));
            _beats.Add(new ActiveBeat(npc, sceneDesc, expectedInteraction, DateTime.UtcNow + duration, playerVisible));
            _monitor?.Log($"[BeatStore] {npc}: beat active ({duration.TotalMinutes:F0} min)", LogLevel.Debug);
        }
    }

    /// <summary>
    ///     创建多 NPC 群体 beat（spawn_group_beat）。sceneScript 为群体场景描述，
    ///     对列表中每个 NPC 各建一条 beat（含其所在位置描述）。
    /// </summary>
    public void CreateGroup(
        IEnumerable<string> npcs, string location, string sceneScript, TimeSpan duration, bool playerVisible = true)
    {
        if (npcs == null || string.IsNullOrWhiteSpace(sceneScript) || duration <= TimeSpan.Zero)
        {
            return;
        }

        var names = npcs.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
        {
            return;
        }

        var sceneDesc = string.IsNullOrWhiteSpace(location)
            ? sceneScript
            : $"[{location}] {sceneScript}";

        lock (_lock)
        {
            PruneExpiredLocked();
            foreach (var npc in names)
            {
                _beats.RemoveAll(b => b.Npc.Equals(npc, StringComparison.OrdinalIgnoreCase));
                _beats.Add(new ActiveBeat(npc, sceneDesc, null, DateTime.UtcNow + duration, playerVisible));
            }

            _monitor?.Log($"[BeatStore] group beat for {names.Count} NPCs at '{location}' ({duration.TotalMinutes:F0} min)", LogLevel.Debug);
        }
    }

    /// <summary>清除全部 beat（换日/清档时调用）。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _beats.Clear();
        }
    }

    /// <summary>
    ///     获取 NPC 当前活跃 beat（未过期）；无则返回 null。
    ///     供 worldSnapshot.currentBeat 与 DirectorContextBuilder 读取。
    /// </summary>
    public ActiveBeat? GetActiveBeat(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
        {
            return null;
        }

        lock (_lock)
        {
            PruneExpiredLocked();
            return _beats.FirstOrDefault(b => b.Npc.Equals(npcName, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>全部未过期 beat（供调试/导演上下文）。</summary>
    public IReadOnlyList<ActiveBeat> GetAllActive()
    {
        lock (_lock)
        {
            PruneExpiredLocked();
            return _beats.ToList().AsReadOnly();
        }
    }

    private void PruneExpiredLocked()
    {
        var now = DateTime.UtcNow;
        for (var i = _beats.Count - 1; i >= 0; i--)
        {
            if (_beats[i].ExpireTime <= now)
            {
                _beats.RemoveAt(i);
            }
        }
    }
}
