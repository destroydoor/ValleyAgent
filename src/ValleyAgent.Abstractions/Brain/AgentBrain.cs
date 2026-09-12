using System;
using System.Collections.Generic;
using System.Linq;
using ValleyAgent.Config;
using ValleyAgent.Goals;
using ValleyAgent.RAG.Models;

namespace ValleyAgent.Brain
{
    /// <summary>
    /// NPC 内在状态的 C# 端镜像。
    /// 所有情绪/记忆/决策逻辑由 TS Agent Server 驱动，C# 端仅接收并缓存 TS 服务器推送的状态，
    /// 用于 UI 渲染和上下文收集。
    /// </summary>
    public class AgentBrain
    {
        public string NpcName { get; }

        /// <summary>TS Agent Server 推送的 Bio 数据（用于上下文收集）</summary>
        public ValleyTalkBioData? Bio { get; set; }

        /// <summary>TS Agent Server 推送的情绪状态镜像</summary>
        public EmotionState CurrentEmotionState { get; set; } = EmotionState.Neutral();

        /// <summary>TS Agent Server 推送的情绪枚举</summary>
        public NpcEmotion Emotion => CurrentEmotionState.PrimaryEmotion;

        /// <summary>TS Agent Server 推送的短期记忆镜像（用于上下文收集）</summary>
        public List<MemoryEntry> ShortTermMemories { get; } = new();

        /// <summary>TS Agent Server 推送的长期记忆镜像（用于上下文收集）</summary>
        public List<MemoryEntry> LongTermMemories { get; } = new();

        public List<MemoryEntry> SignificantMemories { get; } = new();

        /// <summary>TS Agent Server 推送的当前目标</summary>
        public string CurrentGoal { get; set; } = string.Empty;

        /// <summary>
        ///     阶段 2：当前活跃的 Goal 实例（set_goal 工具创建，由 GoalExecutor 驱动）。
        ///     null = 无活跃目标。非 null 时 AgentTickLoop 会优先接管该 NPC 的移动。
        /// </summary>
        public IGoal? PendingGoal { get; set; }

        // ─── 阶段 3：L2 状态摘要（Director set_npc_* 工具写入，worldSnapshot L2 字段读取）───

        /// <summary>
        ///     L2 心情标签（如 "angry"/"cheerful"）。Director set_npc_mood 写入，
        ///     随 worldSnapshot npcMood 注入对话 prompt「你的状态」段。空串 = 无显式心情标记。
        /// </summary>
        public string MoodTag { get; set; } = "";

        /// <summary>
        ///     L2 近期事件（todayEvents），保留近 3 天、条数 ≤ <see cref="MaxTodayEvents" />。
        ///     条目格式 "{gameDateIso}::{text}"（日期前缀用于 day_started 清理）。
        ///     Director set_npc_recent_events 整体替换；日常小事只写这里，不污染 L1 长期记忆。
        /// </summary>
        public List<string> TodayEvents { get; } = new();

        /// <summary>L2 工作标记（如 "cleaning_coop"/"mining"）。Director set_npc_working_on 写入/清除；null = 无。</summary>
        public string? WorkingOn { get; set; }

        /// <summary>
        ///     最近一次对话的发起玩家 ID（UniqueMultiplayerID 字符串，2026-08-16 联机）。
        ///     dialogue_response 消息级 echo playerId 后写入；FOLLOW 等行为的目标玩家据此解析。
        ///     null = 尚未记录（单机或旧客户端，回落 Game1.player）。
        /// </summary>
        public string? LastDialoguePlayerId { get; set; }

        /// <summary>L2 欠款（g）。Director set_npc_money 等结算路径更新；默认 0 = 无欠款。</summary>
        public int OwedMoney { get; set; }

        /// <summary>todayEvents 条数上限（设计 doc §11.4：条数 ≤5）。</summary>
        public const int MaxTodayEvents = 5;

        /// <summary>
        ///     追加一条 L2 近期事件。自动附加游戏日期前缀（供 day_started 清理），
        ///     超出 <see cref="MaxTodayEvents" /> 时丢弃最旧条目。
        /// </summary>
        public void AddTodayEvent(string text, string gameDateIso)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            TodayEvents.Add($"{gameDateIso}::{text}");
            while (TodayEvents.Count > MaxTodayEvents)
            {
                TodayEvents.RemoveAt(0);
            }
        }

        /// <summary>
        ///     仅事件正文（剥离日期前缀），供 worldSnapshot npcRecentEvents 注入。
        /// </summary>
        public IReadOnlyList<string> RecentEventTexts
        {
            get
            {
                var result = new List<string>(TodayEvents.Count);
                foreach (var entry in TodayEvents)
                {
                    var sep = entry.IndexOf("::", StringComparison.Ordinal);
                    result.Add(sep >= 0 ? entry[(sep + 2)..] : entry);
                }

                return result;
            }
        }

        /// <summary>
        ///     清理超过保留天数的 todayEvents（day_started 调用）。
        ///     日期格式 "Y{year}_{season}_{day}" → 换算为总天数（每季 28 天）做比较；
        ///     无日期前缀的条目（外部整体替换写入）视为当日，不清理。
        /// </summary>
        /// <param name="currentDateIso">当前游戏日期 ISO key（如 "Y1_spring_1"）。</param>
        /// <param name="retainDays">保留近 N 天（设计 doc §11.4：3 天）。</param>
        /// <returns>清理掉的条目数。</returns>
        public int PruneTodayEvents(string currentDateIso, int retainDays)
        {
            if (retainDays < 0 || !TryParseGameDate(currentDateIso, out var currentDay))
            {
                return 0;
            }

            var cutoff = currentDay - retainDays;
            var removed = 0;
            for (var i = TodayEvents.Count - 1; i >= 0; i--)
            {
                var sep = TodayEvents[i].IndexOf("::", StringComparison.Ordinal);
                if (sep <= 0)
                {
                    continue; // 无日期前缀 → 视为当日，保留
                }

                if (TryParseGameDate(TodayEvents[i][..sep], out var eventDay) && eventDay < cutoff)
                {
                    TodayEvents.RemoveAt(i);
                    removed++;
                }
            }

            return removed;
        }

        /// <summary>把 "Y{year}_{season}_{day}" 换算为可比较的总天数（每季 28 天）。</summary>
        private static bool TryParseGameDate(string dateIso, out int totalDays)
        {
            totalDays = 0;
            if (string.IsNullOrWhiteSpace(dateIso) || dateIso[0] != 'Y')
            {
                return false;
            }

            var parts = dateIso[1..].Split('_');
            if (parts.Length != 3
                || !int.TryParse(parts[0], out var year)
                || year < 1
                || !int.TryParse(parts[2], out var day)
                || day < 1)
            {
                return false;
            }

            var seasonIndex = parts[1].ToLowerInvariant() switch
            {
                "spring" => 0,
                "summer" => 1,
                "fall" => 2,
                "winter" => 3,
                _ => -1
            };
            if (seasonIndex < 0)
            {
                return false;
            }

            totalDays = (year - 1) * 112 + seasonIndex * 28 + day;
            return true;
        }

        /// <summary>TS Agent Server 推送的玩家昵称</summary>
        public string FarmerNickname { get; set; } = GameConstants.DefaultFarmerNickname;

        /// <summary>TS Agent Server 推送的最近决策状态</summary>
        public string LastDecisionState { get; set; } = string.Empty;

        /// <summary>TS Agent Server 推送的最近决策原因</summary>
        public string LastDecisionReason { get; set; } = string.Empty;

        /// <summary>TS Agent Server 推送的最近对话回复</summary>
        public string LastDialogueResponse { get; set; } = string.Empty;

        /// <summary>任务5.1：最近对话响应的来源（LLM/Fallback/Error），用于测试系统断言。</summary>
        public DialogueResponseSource LastDialogueSource { get; set; } = DialogueResponseSource.None;

        /// <summary>当前活跃的说话次数（由 TS Agent Server 指令控制）</summary>
        public int ActiveSpeaks { get; set; }

        /// <summary>情绪变更历史（由 TS Agent Server 推送，C# 仅缓存）</summary>
        public Queue<string> EmotionHistory { get; } = new();

        public AgentBrain(string npcName)
        {
            NpcName = npcName ?? throw new ArgumentNullException(nameof(npcName));
        }

        /// <summary>
        /// 情绪变更事件。当情绪实际改变时触发（去重后）。
        /// 订阅者可用于显示游戏内视觉反馈（emote、头顶文字等）。
        /// 参数：NpcName, 旧情绪, 新情绪
        /// </summary>
        public event Action<string, NpcEmotion, NpcEmotion>? OnEmotionChanged;

        public event Action<string, string, float, string>? OnEmotionSynced;

        public event Action<string, string, double, string>? OnMemorySynced;

        /// <summary>Emotion dedup threshold (0-1). Same primary emotion with intensity difference less than this is skipped. Configurable via ModConfig.EmotionDedupThreshold.</summary>
        public float EmotionDedupThreshold { get; set; } = 0.15f;

        /// <summary>
        /// 从 TS Agent Server 同步情绪状态。C# 不再自行推导情绪。
        /// 去重：如果新情绪与当前相同且强度相近，跳过（防止每 tick 刷屏）。
        /// </summary>
        public void SyncEmotion(NpcEmotion emotion, float intensity, string reason)
        {
            // 去重：相同情绪 + 相近强度（差值 < 0.15）→ 不重复触发
            if (CurrentEmotionState.PrimaryEmotion == emotion
                && Math.Abs(CurrentEmotionState.Intensity - intensity) < EmotionDedupThreshold)
            {
                return;
            }

            var previousEmotion = CurrentEmotionState.PrimaryEmotion;
            CurrentEmotionState = new EmotionState(emotion, intensity, reason);
            RecordEmotionChange(reason, emotion);

            // 触发视觉反馈事件
            OnEmotionChanged?.Invoke(NpcName, previousEmotion, emotion);
            OnEmotionSynced?.Invoke(NpcName, emotion.ToString(), intensity, reason);
        }

        /// <summary>
        /// 从 TS Agent Server 同步决策记录。C# 不再自行记录决策。
        /// </summary>
        public void SyncDecision(string state, string reason)
        {
            LastDecisionState = state;
            LastDecisionReason = reason;
        }

        /// <summary>
        /// 从 TS Agent Server 同步记忆。C# 不再自行管理记忆压缩。
        /// </summary>
        public void SyncMemories(List<MemoryEntry> shortTerm, List<MemoryEntry> longTerm)
        {
            ShortTermMemories.Clear();
            if (shortTerm != null)
                ShortTermMemories.AddRange(shortTerm);

            LongTermMemories.Clear();
            if (longTerm != null)
                LongTermMemories.AddRange(longTerm);
        }

        /// <summary>
        /// 从 TS Agent Server 追加单条记忆。
        /// </summary>
        public void AddMemory(MemoryEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Text))
                return;
            if (ShortTermMemories.Any(m => m.Text == entry.Text))
                return;
            ShortTermMemories.Add(entry);
        }

        public void AddMemory(string memory, double importance = 1.0, MemoryEntryType entryType = MemoryEntryType.Generic, string location = "", List<string>? tags = null)
        {
            if (string.IsNullOrWhiteSpace(memory))
                return;
            if (ShortTermMemories.Any(m => m.Text == memory))
                return;
            ShortTermMemories.Add(new MemoryEntry(memory, importance, entryType, location, tags));
            OnMemorySynced?.Invoke(NpcName, memory, importance, entryType.ToString());
        }

        public void AddSignificantMemory(string text, double importance = 5.0, string entryType = "significant")
        {
            var entry = new MemoryEntry(text, importance, MemoryEntryType.Generic);
            SignificantMemories.Add(entry);
            OnMemorySynced?.Invoke(NpcName, text, importance, entryType);
        }

        /// <summary>
        /// 获取情绪描述（用于上下文收集发送给 TS Agent Server）。
        /// </summary>
        public string GetEmotionDescription()
        {
            var primary = CurrentEmotionState.PrimaryEmotion;
            var intensity = CurrentEmotionState.Intensity;

            if (intensity < 0.3f && primary != NpcEmotion.Neutral)
            {
                return EmoStateHelper.GetLowIntensityLabel(primary);
            }

            return primary switch
            {
                NpcEmotion.Neutral => "calm and composed",
                NpcEmotion.Happy => intensity > 0.7f ? "elated and joyful" : "in a good mood",
                NpcEmotion.Angry => intensity > 0.7f ? "furious" : "annoyed",
                NpcEmotion.Sad => intensity > 0.7f ? "deeply distressed" : "feeling down",
                NpcEmotion.Worried => intensity > 0.7f ? "anxious and restless" : "mildly concerned",
                NpcEmotion.Excited => intensity > 0.7f ? "bubbling with excitement" : "eager",
                NpcEmotion.Tired => intensity > 0.7f ? "completely exhausted" : "a bit worn out",
                NpcEmotion.Grateful => intensity > 0.7f ? "overflowing with gratitude" : "thankful",
                _ => "neutral",
            };
        }

        /// <summary>
        /// 获取记忆摘要（用于上下文收集发送给 TS Agent Server）。
        /// </summary>
        public string GetMemorySummary()
        {
            var allMemories = ShortTermMemories
                .Concat(LongTermMemories)
                .OrderByDescending(m => m.Timestamp)
                .Take(10)
                .ToList();
            return allMemories.Count == 0 ? string.Empty : "Recent events:\n" + string.Join("\n", allMemories.Select((m, i) => $"- {m.Text}"));
        }

        /// <summary>
        /// 获取最近的记忆（用于上下文收集）。
        /// </summary>
        public List<MemoryEntry> GetRecentMemories(int count = 10)
        {
            return ShortTermMemories
                .Concat(LongTermMemories)
                .OrderByDescending(m => m.Timestamp)
                .Take(count)
                .ToList();
        }

        private void RecordEmotionChange(string reason, NpcEmotion newEmotion)
        {
            EmotionHistory.Enqueue($"[{DateTime.UtcNow:HH:mm}] {reason} → {newEmotion}");
            while (EmotionHistory.Count > 10)
            {
                _ = EmotionHistory.Dequeue();
            }
        }
    }
}
