using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace ValleyAgent.Config;

// Provider enumerations for the ValleyAgent SMAPI mod
public enum LlmProvider
{
    LMStudio,
    Kimi,
    DeepSeek,
    OpenRouter,
    MiniMax
}

// Language mode for AI dialogue
public enum LanguageMode
{
    English,
    Chinese,
    Custom
}

// Serializable configuration container for ValleyAgent
//
// ─── 配置项健康度登记（2026-09-15 文档↔代码偏移审查 C4，底稿见
//     docs/plan/2026-09-15-doc-code-drift-audit.md §3 C4）────────────────────────
// 本类共约 150 个属性，扫描"除 ModConfig/GMCMIntegration 外零 C# 引用"后分两类：
//
// 【A 类：刻意的兼容垫片 —— 勿删】仅在 MigrateLegacyFields()（见 :546 附近）读取旧键：
//   AutoStartPythonServer / PythonExecutablePath / PythonServerDirectory /
//   PythonServerStartupTimeoutSeconds / PythonServerMaxRestartAttempts（均已 [Obsolete]，包在
//   #pragma warning disable CS0618 内）、ModelName → LlmModel、DialogueTemperature（存档兼容）。
//   删除它们等于放弃老 config.json 的迁移，需连 MigrateLegacyFields + scripts/docker/prep-mods.ps1
//   一起改（该脚本会写 AutoStartPythonServer=false）。**A 类不在 #13 的摘除范围内。**
//
// 【B 类：消费者已被删除的孤儿配置 —— 待摘除，需一次带编译验证的改动】
//   ServerAddress（直连 LLM 通路已禁，改走 TS Agent Server）、DevMode、DebugLogEnabled、
//   CustomSystemPrompt、AIDailyTopicCount、StateRejectionCooldownTicks、MaxStateRejections、
//   FallbackAIMixProbability、FallbackLiveGenerationProbability、TodayEventsMaxCount（L2）、
//   Haggle.Enabled / Haggle.MaxRounds / Haggle.HostileThreshold（还价结算链已随账本迁 TS 删除）。
//   这些项仍被 Validate() 钳制；除 ServerAddress / TodayEventsMaxCount 外**都还渲染在 GMCM
//   面板上**——即玩家能改、能保存，但不产生任何效果。
//   摘除顺序：ModConfig 属性 → Validate 钳制 → GMCMIntegration 条目 → CopyFrom 复制，
//   并在有 dotnet 的环境跑 `dotnet build`（TestMod/UnitTests 均 TreatWarningsAsErrors）。
//   摘除进度跟踪：GitHub issue #13（"配置项可改但无效"）。
// ────────────────────────────────────────────────────────────────────────────
public class ModConfig
{
    [DefaultValue(LlmProvider.LMStudio)] public LlmProvider Provider { get; set; } = LlmProvider.LMStudio;

    [Obsolete("Use LlmApiKey instead. Retained for config.json backward compat.")]
    [DefaultValue("")]
    public string ApiKey { get; set; } = "";

    // DISABLED: must route through TS Agent Server. Direct LLM HTTP calls to
    // localhost:1234 are blocked. The C# mod routes ALL dialogue through the
    // TS Agent Server (valley-ai-server.exe) at ws://127.0.0.1:8765.
    [DefaultValue("http://localhost:1234")]
    public string ServerAddress { get; set; } = "http://localhost:1234";

    [Obsolete("Use LlmModel instead. Retained for config.json backward compat.")]
    [DefaultValue("")]
    public string Model { get; set; } = "";

    // ─── Concurrent Agent count range (three-tier: Min/Normal/Max) ───
    // Min:  硬下限。低于此值时 spark 概率加倍、导演优先选未分配 NPC。
    // Normal: 日常目标。spark 主动激活到此数量停止；导演 allocate_agent 可越过到 Max。
    // Max:  硬上限。任何分配不超过。
    // 约束：0 <= Min <= Normal <= Max <= 10。默认 0/1/2（第1天 0 个 Agent，纯懒加载）。
    [DefaultValue(0)] public int MinAgentNpcs { get; set; }

    [DefaultValue(1)] public int NormalAgentNpcs { get; set; } = 1;

    [DefaultValue(2)] public int MaxAgentNpcs { get; set; } = 2;

    [DefaultValue(true)] public bool EnableFriendshipChanges { get; set; } = true;

    [DefaultValue(true)] public bool EnableGifts { get; set; } = true;

    [DefaultValue(0)] public int TokenBudget { get; set; } // 0 means unlimited

    [DefaultValue(false)] public bool DebugMode { get; set; } = false;

    [DefaultValue(true)] public bool UseAgentServer { get; set; } = true;

    /// <summary>TS Agent Server 主机名/IP。默认 127.0.0.1；Docker 容器场景配 compose 服务名（如 valley-ts）。</summary>
    [DefaultValue("127.0.0.1")]
    public string AgentServerHost { get; set; } = "127.0.0.1";

    [DefaultValue("ws://127.0.0.1:8765")] public string AgentServerUri { get; set; } = "ws://127.0.0.1:8765";

    [Obsolete("Use AutoStartServer instead. Retained for config.json backward compat.")]
    [DefaultValue(true)]
    public bool AutoStartPythonServer { get; set; } = true;

    [Obsolete("Use ServerExecutablePath instead.")]
    [DefaultValue("")]
    public string PythonExecutablePath { get; set; } = "";

    [Obsolete("Use ServerDirectory instead.")]
    [DefaultValue("")]
    public string PythonServerDirectory { get; set; } = "";

    [Obsolete("Use ServerStartupTimeoutSeconds instead.")]
    [DefaultValue(30)]
    public int PythonServerStartupTimeoutSeconds { get; set; } = 30;

    [Obsolete("Use ServerMaxRestartAttempts instead.")]
    [DefaultValue(3)]
    public int PythonServerMaxRestartAttempts { get; set; } = 3;

    // ─── Server process configuration (replaces legacy Python* fields) ────
    [DefaultValue(true)] public bool AutoStartServer { get; set; } = true;

    [DefaultValue("")] public string ServerExecutablePath { get; set; } = "";

    [DefaultValue("")] public string ServerDirectory { get; set; } = "";

    [DefaultValue(8765)] public int ServerPort { get; set; } = 8765;

    /// <summary>
    ///     为 true 时 Agent 服务器在独立可见的 cmd 窗口中运行（便于查看 Agent 逻辑日志/报错）；
    ///     为 false 时隐藏窗口，输出重定向到 SMAPI 日志。
    /// </summary>
    [DefaultValue(true)]
    public bool ServerConsoleWindow { get; set; } = true;

    /// <summary>
    ///     为 true 时点击非 Agent 村民：先播原版对话，关闭后自动打开 AI 输入框继续聊。
    /// </summary>
    [DefaultValue(true)]
    public bool NonAgentAIChatEnabled { get; set; } = true;

    [DefaultValue(60)] public int ServerStartupTimeoutSeconds { get; set; } = 60;

    [DefaultValue(3)] public int ServerMaxRestartAttempts { get; set; } = 3;

    // ─── LLM configuration (canonical fields, replaces legacy ApiKey/Model) ──
    [DefaultValue("")] public string LlmApiKey { get; set; } = "";

    [DefaultValue("MiniMax-M3")] public string LlmModel { get; set; } = "MiniMax-M3";

    /// <summary>
    ///     LLM API base URL forwarded to the TS Agent Server via --llm-base-url.
    ///     Default: MiniMax domestic endpoint. Switch to https://api.minimaxi.com/v1 for international.
    ///     Empty string falls back to the TS server's built-in default (https://api.minimax.chat/v1).
    /// </summary>
    [DefaultValue("https://api.minimax.chat/v1")]
    public string LlmBaseUrl { get; set; } = "https://api.minimax.chat/v1";

    [DefaultValue(false)] public bool DevMode { get; set; } = false;

    [DefaultValue(60)] public int LLMTimeoutSeconds { get; set; } = 60;

    [DefaultValue(3)] public int MaxRetries { get; set; } = 3;

    [DefaultValue(0.5f)] public float DecisionIntervalMinutes { get; set; } = 0.5f;

    [DefaultValue(3000)] public int DialogueCooldownMs { get; set; } = 3000;

    [DefaultValue(0.7f)] public float DialogueTemperature { get; set; } = 0.7f;

    [DefaultValue(false)] public bool PauseAllDialogue { get; set; } = false;

    [DefaultValue(5)] public int CircuitBreakerThreshold { get; set; } = 5;

    [DefaultValue(true)] public bool EnableCombatAssist { get; set; } = true;

    [DefaultValue(true)] public bool EnableFarmingAssist { get; set; } = true;

    [DefaultValue(true)] public bool EnableMiningAssist { get; set; } = true;

    [DefaultValue(true)] public bool EnableForagingAssist { get; set; } = true;

    [DefaultValue(3)] public int FollowDistance { get; set; } = 3;

    // ─── E2-1 动态速度（距离分段，无体力惩罚）────────────────────────────
    // NPC 距目标越远移动越快：默认 >8 格 2×、3–8 格 1.5×、<3 格 1×。
    // 走行动画帧间隔随倍率等比缩短（见 MovementConstants.GetEffectiveWalkAnimationIntervalMs）。

    /// <summary>是否启用动态速度：NPC 距目标越远移动越快（不影响体力）。</summary>
    [DefaultValue(true)]
    public bool DynamicSpeedEnabled { get; set; } = true;

    /// <summary>远距分段阈值（格）：距离超过该值按远距倍率移动。</summary>
    [DefaultValue(8f)]
    public float DynamicSpeedFarThreshold { get; set; } = 8f;

    /// <summary>中距分段阈值（格）：距离小于该值按近距倍率移动。</summary>
    [DefaultValue(3f)]
    public float DynamicSpeedMidThreshold { get; set; } = 3f;

    /// <summary>近距（&lt; MidThreshold）速度倍率。</summary>
    [DefaultValue(1.0f)]
    public float DynamicSpeedNearMultiplier { get; set; } = 1.0f;

    /// <summary>中距（[MidThreshold, FarThreshold]）速度倍率。</summary>
    [DefaultValue(1.5f)]
    public float DynamicSpeedMidMultiplier { get; set; } = 1.5f;

    /// <summary>远距（&gt; FarThreshold）速度倍率。</summary>
    [DefaultValue(2.0f)]
    public float DynamicSpeedFarMultiplier { get; set; } = 2.0f;

    [DefaultValue(0.5f)] public float FallbackAIMixProbability { get; set; } = 0.5f;

    // ─── E2-2 聊天栏玩家→NPC 路由 ─────────────────────────────────────
    // 玩家在聊天栏打字即视为对在场/跟随中的 NPC 说话；四层路由消歧；
    // 会话模式 60s 超时；会话内来回不计主动额度（额度强制是 E5-3）。

    /// <summary>是否启用聊天栏路由总开关。</summary>
    [DefaultValue(true)]
    public bool ChatBarRoutingEnabled { get; set; } = true;

    /// <summary>会话模式超时（秒）：玩家 60 秒未回应则退出会话。</summary>
    [DefaultValue(60)]
    public int ChatSessionTimeoutSeconds { get; set; } = 60;

    /// <summary>路由第 4 层"最近交互"窗口（秒）：30 秒内交互过且在附近优先。</summary>
    [DefaultValue(30)]
    public int ChatRecentInteractionWindowSeconds { get; set; } = 30;

    /// <summary>路由"附近"阈值（格）：与主动说话气泡阈值一致（~8 格）。</summary>
    [DefaultValue(8)]
    public int ChatNearbyDistanceTiles { get; set; } = 8;

    /// <summary>群体称呼（"你们""大家"）最多接话人数（防刷屏）。</summary>
    [DefaultValue(2)]
    public int ChatGroupResponseMax { get; set; } = 2;

    // ─── E5-3 主动发言额度（ProactiveSpeechQuota，E5-2 喊话/E5-3 搭话共享）───
    // NPC 主动喊话/搭话受"每日上限 + 冷却"约束，防止 LLM 反复主动发言刷屏。
    // 会话内来回与被动回应不受限（见 docs/ideas/quota-implementation-思路.md）。

    /// <summary>每日主动发言额度上限：NPC 主动喊话/搭话每天最多 N 次。</summary>
    [DefaultValue(2)]
    public int ProactiveSpeechDailyLimit { get; set; } = 2;

    /// <summary>主动发言冷却（分钟）：同 NPC 两次主动发言至少间隔该时长；0 = 关闭冷却。</summary>
    [DefaultValue(30)]
    public int ProactiveSpeechCooldownMinutes { get; set; } = 30;

    [DefaultValue(0.15f)] public float FallbackLiveGenerationProbability { get; set; } = 0.15f;

    [DefaultValue(LanguageMode.Chinese)] public LanguageMode Language { get; set; } = LanguageMode.Chinese;

    [DefaultValue("")] public string CustomSystemPrompt { get; set; } = "";

    [DefaultValue(true)] public bool EnableFirstClickVanilla { get; set; } = true;

    [DefaultValue(3)] public int AIDailyTopicCount { get; set; } = 3;

    [DefaultValue(3)] public int MaxConsecutiveIdleBeforeRelease { get; set; } = 3;

    [DefaultValue(300)] public int TaskCompleteDecisionCooldownTicks { get; set; } = 300;

    [DefaultValue(180)] public int StateRejectionCooldownTicks { get; set; } = 180;

    [DefaultValue(2)] public int MaxStateRejections { get; set; } = 2;

    [DefaultValue(0.3f)] public float EmergencyHealthThreshold { get; set; } = 0.3f;

    [DefaultValue(5.0f)] public float EmergencyMonsterDistance { get; set; } = 5.0f;

    [DefaultValue(7.0f)] public float PlayerInDangerDistance { get; set; } = 7.0f;

    // ─── NPC Behavior Design Flaw Fix Config ──────────────────────────────

    /// <summary>Issue 10: Minimum ticks between same-type emotion changes for an NPC (prevents emotion spam).</summary>
    [DefaultValue(300)]
    public int EmotionCooldownTicks { get; set; } = 300;

    /// <summary>Issue 10: Similarity threshold for emotion dedup (0-1). If new emotion's primary matches the last, skip.</summary>
    [DefaultValue(0.15f)]
    public float EmotionDedupThreshold { get; set; } = 0.15f;

    /// <summary>Issue 6: Ticks to block emergency FIGHT re-entry after FightHandler exits (prevents oscillation).</summary>
    [DefaultValue(300)]
    public int FightExitCooldownTicks { get; set; } = 300;

    /// <summary>Issue 11: Minimum friendship points for TALK rejection to fall back to FOLLOW instead of IDLE.</summary>
    [DefaultValue(200)]
    public int TalkRejectionFollowThreshold { get; set; } = 200;

    /// <summary>Issue 1: Max tile distance for cross-map travel to skip departure delay (lower = more walking, less teleport).</summary>
    [DefaultValue(10f)]
    public float DepartureSkipDistance { get; set; } = 10f;

    /// <summary>Issue 12: Maximum friendship change per single interaction (diminishing returns cap).</summary>
    [DefaultValue(80)]
    public int MaxFriendshipChangePerInteraction { get; set; } = 80;

    public Dictionary<string, double> MinimumStateDuration { get; set; } = new()
    {
        { "FIGHT", 15.0 },
        { "FARM", 8.0 },
        { "MINE", 10.0 },
        { "FORAGE", 8.0 },
        { "FOLLOW", 3.0 }
    };

    [DefaultValue(100)] public int DefaultMaxHealth { get; set; } = 100;

    /// <summary>
    ///     JSONL 全量留痕配置（E1-1 TranscriptSink）。文件写入 {modDir}/{Transcript.RootDir}/{gameDate}/{npcName}.jsonl。
    /// </summary>
    public TranscriptConfig Transcript { get; set; } = new();

    /// <summary>
    ///     E3-1 NPC 经济系统配置（钱包/档案）。DataFile 相对 mod 目录。
    /// </summary>
    public EconomyConfig Economy { get; set; } = new();

    /// <summary>
    ///     E3-2 还价/定价配置（2026-08-15 步骤 2：状态机逻辑已随 C# 还价链路移除，配置保留兼容）。
    ///     Validate() 负责钳制到安全范围。
    /// </summary>
    public HaggleConfig Haggle { get; set; } = new();

    /// <summary>
    ///     阶段 2 Goal 系统配置（set_goal + GoalExecutor）。
    ///     全局超时 / 汇报超时 / 到达距离 / 卡死阈值。
    /// </summary>
    public GoalConfig Goals { get; set; } = new();

    /// <summary>
    ///     阶段 3 导演系统配置（DirectorContextBuilder token 预算 / beat 默认有效期）。
    /// </summary>
    public DirectorConfig Director { get; set; } = new();

    /// <summary>
    ///     阶段 3 L2 状态摘要配置（todayEvents 保留天数 / 条数上限）。
    /// </summary>
    public L2Config L2 { get; set; } = new();

    // ─── GMCM-managed config fields (two-layer UI) ──────────────────────────

    /// <summary>Master on/off switch for the ValleyAgent mod.</summary>
    [DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    /// <summary>LLM provider used for agent decisions. Legacy Provider field retained for save compatibility.</summary>
    [DefaultValue(LlmProvider.DeepSeek)]
    public LlmProvider LlmProvider { get; set; } = LlmProvider.DeepSeek;

    /// <summary>
    ///     Model identifier sent to the LLM provider (e.g. deepseek-chat). Legacy Model field retained for save
    ///     compatibility.
    /// </summary>
    [DefaultValue("deepseek-chat")]
    public string ModelName { get; set; } = "deepseek-chat";

    /// <summary>
    ///     WebSocket URL of the TS Agent Server (valley-ai-server.exe). Not hot-reloadable; restart required after
    ///     change. Legacy AgentServerUri field retained for save compatibility.
    /// </summary>
    [Obsolete("WebSocket URL is auto-bound from ServerPort. Field retained for save compat.")]
    [DefaultValue("ws://127.0.0.1:8765")]
    public string WebSocketUrl { get; set; } = "ws://127.0.0.1:8765";

    /// <summary>
    ///     Sampling temperature for LLM generation (0.0 - 2.0). Legacy DialogueTemperature field retained for save
    ///     compatibility.
    /// </summary>
    [DefaultValue(0.7f)]
    public float Temperature { get; set; } = 0.7f;

    /// <summary>Seconds of NPC idle before the agent releases the NPC back to vanilla behavior.</summary>
    [DefaultValue(90)]
    public int IdleThresholdSeconds { get; set; } = 90;

    /// <summary>Cooldown in milliseconds between gift-giving actions by the agent.</summary>
    [DefaultValue(30000)]
    public int GiftCooldownMs { get; set; } = 30000;

    /// <summary>Whether to show AI dialogue as in-game chat bubbles.</summary>
    [DefaultValue(true)]
    public bool ChatBubbleEnabled { get; set; } = true;

    /// <summary>
    ///     E2-3 长文本规则：>1 句的长文按句弹入聊天栏的间隔（毫秒）。
    ///     首句立即弹出，后续每句间隔该时长；0 表示不间隔（一次弹出全部）。
    /// </summary>
    [DefaultValue(2000)]
    public int LongTextIntervalMs { get; set; } = 2000;

    /// <summary>Whether to emit verbose debug logs. Legacy DebugMode field retained for save compatibility.</summary>
    [DefaultValue(false)]
    public bool DebugLogEnabled { get; set; } = false;

    // ─── 多 Provider 模式（3 角色 × 主备） ───────────────────────────

    /// <summary>启用多 Provider 模式：true=3 角色主备配置；false=单 Provider。</summary>
    [DefaultValue(false)]
    public bool MultiProviderEnabled { get; set; } = false;

    // 导演（narrative director）
    /// <summary>导演主 Provider 标识（minimax/deepseek/openai 等）。</summary>
    [DefaultValue("minimax")]
    public string DirectorPrimaryProvider { get; set; } = "minimax";

    /// <summary>导演主 Provider API Key。</summary>
    [DefaultValue("")]
    public string DirectorPrimaryApiKey { get; set; } = "";

    /// <summary>导演主 Provider 模型标识。</summary>
    [DefaultValue("MiniMax-M3")]
    public string DirectorPrimaryModel { get; set; } = "MiniMax-M3";

    /// <summary>导演主 Provider API Base URL。</summary>
    [DefaultValue("https://api.minimax.chat/v1")]
    public string DirectorPrimaryBaseUrl { get; set; } = "https://api.minimax.chat/v1";

    /// <summary>导演备 Provider 标识（留空=无回退）。</summary>
    [DefaultValue("deepseek")]
    public string DirectorFallbackProvider { get; set; } = "deepseek";

    /// <summary>导演备 Provider API Key。</summary>
    [DefaultValue("")]
    public string DirectorFallbackApiKey { get; set; } = "";

    /// <summary>导演备 Provider 模型标识。</summary>
    [DefaultValue("deepseek-chat")]
    public string DirectorFallbackModel { get; set; } = "deepseek-chat";

    /// <summary>导演备 Provider API Base URL。</summary>
    [DefaultValue("https://api.deepseek.com/v1")]
    public string DirectorFallbackBaseUrl { get; set; } = "https://api.deepseek.com/v1";

    // 主角 NPC
    /// <summary>主角 NPC 主 Provider 标识。</summary>
    [DefaultValue("minimax")]
    public string ProtagonistPrimaryProvider { get; set; } = "minimax";

    /// <summary>主角 NPC 主 Provider API Key。</summary>
    [DefaultValue("")]
    public string ProtagonistPrimaryApiKey { get; set; } = "";

    /// <summary>主角 NPC 主 Provider 模型标识。</summary>
    [DefaultValue("MiniMax-M2.7-highspeed")]
    public string ProtagonistPrimaryModel { get; set; } = "MiniMax-M2.7-highspeed";

    /// <summary>主角 NPC 主 Provider API Base URL。</summary>
    [DefaultValue("https://api.minimax.chat/v1")]
    public string ProtagonistPrimaryBaseUrl { get; set; } = "https://api.minimax.chat/v1";

    /// <summary>主角 NPC 备 Provider 标识。</summary>
    [DefaultValue("deepseek")]
    public string ProtagonistFallbackProvider { get; set; } = "deepseek";

    /// <summary>主角 NPC 备 Provider API Key。</summary>
    [DefaultValue("")]
    public string ProtagonistFallbackApiKey { get; set; } = "";

    /// <summary>主角 NPC 备 Provider 模型标识。</summary>
    [DefaultValue("deepseek-chat")]
    public string ProtagonistFallbackModel { get; set; } = "deepseek-chat";

    /// <summary>主角 NPC 备 Provider API Base URL。</summary>
    [DefaultValue("https://api.deepseek.com/v1")]
    public string ProtagonistFallbackBaseUrl { get; set; } = "https://api.deepseek.com/v1";

    // 普通 NPC
    /// <summary>普通 NPC 主 Provider 标识。</summary>
    [DefaultValue("deepseek")]
    public string NpcPrimaryProvider { get; set; } = "deepseek";

    /// <summary>普通 NPC 主 Provider API Key。</summary>
    [DefaultValue("")]
    public string NpcPrimaryApiKey { get; set; } = "";

    /// <summary>普通 NPC 主 Provider 模型标识。</summary>
    [DefaultValue("deepseek-chat")]
    public string NpcPrimaryModel { get; set; } = "deepseek-chat";

    /// <summary>普通 NPC 主 Provider API Base URL。</summary>
    [DefaultValue("https://api.deepseek.com/v1")]
    public string NpcPrimaryBaseUrl { get; set; } = "https://api.deepseek.com/v1";

    /// <summary>普通 NPC 备 Provider 标识（留空=无回退）。</summary>
    [DefaultValue("")]
    public string NpcFallbackProvider { get; set; } = "";

    /// <summary>普通 NPC 备 Provider API Key。</summary>
    [DefaultValue("")]
    public string NpcFallbackApiKey { get; set; } = "";

    /// <summary>普通 NPC 备 Provider 模型标识。</summary>
    [DefaultValue("")]
    public string NpcFallbackModel { get; set; } = "";

    /// <summary>普通 NPC 备 Provider API Base URL。</summary>
    [DefaultValue("")]
    public string NpcFallbackBaseUrl { get; set; } = "";

    // ─── 角色标记 ──────────────────────────────────────────────────

    /// <summary>启用角色区分：true=按 ProtagonistNpcs 列表区分主角/普通 NPC；false=全部走普通 NPC。</summary>
    [DefaultValue(true)]
    public bool EnableProtagonistMapping { get; set; } = true;

    /// <summary>主角 NPC 列表（逗号分隔）。其余 NPC 归普通 NPC 角色。</summary>
    [DefaultValue("Abigail, Haley, Sebastian, Sam, Penny, Alex, Maru, Leah, Elliott, Shane, Emily, Harvey")]
    public string ProtagonistNpcs { get; set; } =
        "Abigail, Haley, Sebastian, Sam, Penny, Alex, Maru, Leah, Elliott, Shane, Emily, Harvey";

    // ─── 新增功能开关 ──────────────────────────────────────────────

    /// <summary>NPC 交易功能总开关。</summary>
    [DefaultValue(true)]
    public bool EnableTrade { get; set; } = true;

    /// <summary>NPC 雇佣功能总开关。</summary>
    [DefaultValue(true)]
    public bool EnableHire { get; set; } = true;

    /// <summary>叙事导演功能总开关（false 时 NPC 完全自主）。</summary>
    [DefaultValue(true)]
    public bool EnableDirector { get; set; } = true;

    /// <summary>NPC 主动发言（喊话/搭话）总开关。</summary>
    [DefaultValue(true)]
    public bool EnableProactiveSpeech { get; set; } = true;

    /// <summary>无限对话开关（非 Agent NPC 也能 AI 对话）。</summary>
    [DefaultValue(true)]
    public bool EnableInfiniteDialogue { get; set; } = true;

    // ─── 新增概率设置 [0, 1] ───────────────────────────────────────

    /// <summary>NPC 主动搭话触发概率。</summary>
    [DefaultValue(0.3f)]
    public float ProactiveSpeechProbability { get; set; } = 0.3f;

    /// <summary>NPC 主动送礼触发概率。</summary>
    [DefaultValue(0.1f)]
    public float ProactiveGiftProbability { get; set; } = 0.1f;

    /// <summary>NPC 主动跟随触发概率。</summary>
    [DefaultValue(0.2f)]
    public float ProactiveFollowProbability { get; set; } = 0.2f;

    /// <summary>NPC 主动求购触发概率。</summary>
    [DefaultValue(0.15f)]
    public float ProactiveTradeProbability { get; set; } = 0.15f;

    // ─── 冷却改秒（旧 ms 字段保留供迁移） ─────────────────────────

    /// <summary>同一 NPC 相邻对话最小间隔（秒，1-60）。</summary>
    [DefaultValue(3)]
    public int DialogueCooldownSeconds { get; set; } = 3;

    /// <summary>两次送礼最小间隔（秒，1-600）。</summary>
    [DefaultValue(30)]
    public int GiftCooldownSeconds { get; set; } = 30;

    // Parameterless constructor is enough for JSON deserialization.

    /// <summary>
    ///     Copies any non-empty legacy field values into the canonical fields.
    ///     Called after config load to preserve user settings across the rename.
    ///     Idempotent: only writes when canonical field is empty/default.
    /// </summary>
    public void MigrateLegacyFields()
    {
#pragma warning disable CS0618
        if (AutoStartServer && !AutoStartPythonServer)
        {
            AutoStartServer = AutoStartPythonServer;
        }

        if (string.IsNullOrEmpty(ServerExecutablePath) && !string.IsNullOrEmpty(PythonExecutablePath))
        {
            ServerExecutablePath = PythonExecutablePath;
        }

        if (string.IsNullOrEmpty(ServerDirectory) && !string.IsNullOrEmpty(PythonServerDirectory))
        {
            ServerDirectory = PythonServerDirectory;
        }

        if (ServerStartupTimeoutSeconds == 60 && PythonServerStartupTimeoutSeconds != 30)
        {
            ServerStartupTimeoutSeconds = PythonServerStartupTimeoutSeconds;
        }

        if (ServerMaxRestartAttempts == 3 && PythonServerMaxRestartAttempts != 3)
        {
            ServerMaxRestartAttempts = PythonServerMaxRestartAttempts;
        }

        if (string.IsNullOrEmpty(LlmApiKey) && !string.IsNullOrEmpty(ApiKey))
        {
            LlmApiKey = ApiKey;
        }

        if (string.IsNullOrEmpty(LlmModel) || LlmModel == "MiniMax-M2")
        {
            if (!string.IsNullOrEmpty(Model))
            {
                LlmModel = Model;
            }
            else if (!string.IsNullOrEmpty(ModelName))
            {
                LlmModel = ModelName;
            }
        }

        // Agent count migration: legacy saves stored MaxAgentNpcs=1 (the old default)
        // and have no MinAgentNpcs field (defaults to 0 after deserialization).
        // Bump to the new default range [0, 2] so existing users see the new ceiling.
        // Users who explicitly set MaxAgentNpcs to a non-1 value are preserved.
        if (MaxAgentNpcs == 1 && MinAgentNpcs == 0)
        {
            MaxAgentNpcs = 2;
        }

        // 三档迁移：旧存档无 NormalAgentNpcs 字段，反序列化默认 0。
        // 旧存档特征：Min==0（旧两档无 Min 字段）。迁移到 Max，行为与旧两档一致。
        // 用户显式设了 Min>0 则非旧存档，由 Validate 的 Normal∈[Min,Max] 钳位处理。
        if (NormalAgentNpcs == 0 && MinAgentNpcs == 0 && MaxAgentNpcs > 0)
        {
            NormalAgentNpcs = MaxAgentNpcs;
        }

        // 冷却字段迁移：ms → seconds（仅在 seconds 还是默认值且 ms 被修改过时迁移）
        if (DialogueCooldownSeconds == 3 && DialogueCooldownMs != 3000)
        {
            DialogueCooldownSeconds = Math.Max(1, DialogueCooldownMs / 1000);
        }

        if (GiftCooldownSeconds == 30 && GiftCooldownMs != 30000)
        {
            GiftCooldownSeconds = Math.Max(1, GiftCooldownMs / 1000);
        }
#pragma warning restore CS0618
    }

    /// <summary>
    ///     Validates and clamps configuration values to safe ranges.
    ///     Returns true if any value was corrected.
    /// </summary>
    public bool Validate()
    {
        MigrateLegacyFields();
        var changed = false;
        if (string.IsNullOrWhiteSpace(ModelName))
        {
            ModelName = "deepseek-chat";
            changed = true;
        }
#pragma warning disable CS0618
        if (string.IsNullOrWhiteSpace(WebSocketUrl))
        {
            WebSocketUrl = "ws://127.0.0.1:8765";
            changed = true;
        }
#pragma warning restore CS0618
        if (Temperature < 0f || Temperature > 2f)
        {
            Temperature = 0.7f;
            changed = true;
        }

        if (DecisionIntervalMinutes < 0.1f || DecisionIntervalMinutes > 5f)
        {
            DecisionIntervalMinutes = 0.5f;
            changed = true;
        }

        if (IdleThresholdSeconds < 30 || IdleThresholdSeconds > 300)
        {
            IdleThresholdSeconds = 90;
            changed = true;
        }

        if (GiftCooldownMs < 0 || GiftCooldownMs > 60000)
        {
            GiftCooldownMs = 30000;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(LlmModel))
        {
            LlmModel = "MiniMax-M3";
            changed = true;
        }

        if (ServerPort < 1 || ServerPort > 65535)
        {
            ServerPort = 8765;
            changed = true;
        }

        if (ServerStartupTimeoutSeconds < 10 || ServerStartupTimeoutSeconds > 300)
        {
            ServerStartupTimeoutSeconds = 60;
            changed = true;
        }

        if (ServerMaxRestartAttempts < 0 || ServerMaxRestartAttempts > 10)
        {
            ServerMaxRestartAttempts = 3;
            changed = true;
        }

        // Agent 行为 — 三档约束：0 <= Min <= Normal <= Max <= 10
        if (MinAgentNpcs < 0)
        {
            MinAgentNpcs = 0;
            changed = true;
        }

        if (MaxAgentNpcs < 0)
        {
            MaxAgentNpcs = 0;
            changed = true;
        }

        if (MinAgentNpcs > MaxAgentNpcs)
        {
            // Min cannot exceed Max — clamp Min down to Max (preserve user's Max choice)
            MinAgentNpcs = MaxAgentNpcs;
            changed = true;
        }

        if (MaxAgentNpcs > 10)
        {
            MaxAgentNpcs = 10;
            changed = true;
        }

        if (MinAgentNpcs > 10)
        {
            MinAgentNpcs = 10;
            changed = true;
        }

        // Normal 必须落在 [Min, Max] 区间（Min 被钳位后再校验 Normal，顺序很重要）
        if (NormalAgentNpcs < MinAgentNpcs)
        {
            NormalAgentNpcs = MinAgentNpcs;
            changed = true;
        }

        if (NormalAgentNpcs > MaxAgentNpcs)
        {
            NormalAgentNpcs = MaxAgentNpcs;
            changed = true;
        }

        if (FollowDistance < 1 || FollowDistance > 10)
        {
            FollowDistance = 3;
            changed = true;
        }

        // E2-1 动态速度：阈值保持 mid <= far，倍率限制在 [0.25, 5] 防止极端值
        if (DynamicSpeedFarThreshold < DynamicSpeedMidThreshold)
        {
            DynamicSpeedFarThreshold = 8f;
            DynamicSpeedMidThreshold = 3f;
            changed = true;
        }

        if (DynamicSpeedFarThreshold < 1f || DynamicSpeedFarThreshold > 50f)
        {
            DynamicSpeedFarThreshold = 8f;
            changed = true;
        }

        if (DynamicSpeedMidThreshold < 1f || DynamicSpeedMidThreshold > 50f)
        {
            DynamicSpeedMidThreshold = 3f;
            changed = true;
        }

        if (DynamicSpeedNearMultiplier < 0.25f || DynamicSpeedNearMultiplier > 5f)
        {
            DynamicSpeedNearMultiplier = 1.0f;
            changed = true;
        }

        if (DynamicSpeedMidMultiplier < 0.25f || DynamicSpeedMidMultiplier > 5f)
        {
            DynamicSpeedMidMultiplier = 1.5f;
            changed = true;
        }

        if (DynamicSpeedFarMultiplier < 0.25f || DynamicSpeedFarMultiplier > 5f)
        {
            DynamicSpeedFarMultiplier = 2.0f;
            changed = true;
        }

        if (FallbackAIMixProbability < 0f || FallbackAIMixProbability > 1f)
        {
            FallbackAIMixProbability = 0.5f;
            changed = true;
        }

        if (FallbackLiveGenerationProbability < 0f || FallbackLiveGenerationProbability > 1f)
        {
            FallbackLiveGenerationProbability = 0.15f;
            changed = true;
        }

        if (MaxConsecutiveIdleBeforeRelease < 1 || MaxConsecutiveIdleBeforeRelease > 10)
        {
            MaxConsecutiveIdleBeforeRelease = 3;
            changed = true;
        }

        // E2-2 聊天栏路由：会话超时 10~600s、交互窗口 5~300s、附近阈值 1~50 格、群体上限 1~5 人
        if (ChatSessionTimeoutSeconds < 10 || ChatSessionTimeoutSeconds > 600)
        {
            ChatSessionTimeoutSeconds = 60;
            changed = true;
        }

        if (ChatRecentInteractionWindowSeconds < 5 || ChatRecentInteractionWindowSeconds > 300)
        {
            ChatRecentInteractionWindowSeconds = 30;
            changed = true;
        }

        if (ChatNearbyDistanceTiles < 1 || ChatNearbyDistanceTiles > 50)
        {
            ChatNearbyDistanceTiles = 8;
            changed = true;
        }

        if (ChatGroupResponseMax < 1 || ChatGroupResponseMax > 5)
        {
            ChatGroupResponseMax = 2;
            changed = true;
        }

        // E5-3 主动发言额度：上限 [1, 20]（0 或负数无意义）、冷却 [0, 240] 分钟（0 = 关闭冷却）
        if (ProactiveSpeechDailyLimit < 1 || ProactiveSpeechDailyLimit > 20)
        {
            ProactiveSpeechDailyLimit = 2;
            changed = true;
        }

        if (ProactiveSpeechCooldownMinutes < 0 || ProactiveSpeechCooldownMinutes > 240)
        {
            ProactiveSpeechCooldownMinutes = 30;
            changed = true;
        }

        // E3-2 还价配置：轮次 [1,10]、恶意阈值 (0,1)、浮动率 0 ≤ min ≤ max ≤ 1、让步比例逐项 (0,1] 且累积 ≤ 1
        Haggle ??= new HaggleConfig();
        if (Haggle.MaxRounds < 1 || Haggle.MaxRounds > 10)
        {
            Haggle.MaxRounds = 3;
            changed = true;
        }

        if (Haggle.HostileThreshold <= 0 || Haggle.HostileThreshold >= 1)
        {
            Haggle.HostileThreshold = 0.5;
            changed = true;
        }

        if (Haggle.MaxSavvySpread <= 0 || Haggle.MaxSavvySpread > 1)
        {
            Haggle.MaxSavvySpread = 0.5;
            changed = true;
        }

        if (Haggle.MinSavvySpread < 0 || Haggle.MinSavvySpread >= 1)
        {
            Haggle.MinSavvySpread = 0.05;
            changed = true;
        }

        if (Haggle.MinSavvySpread > Haggle.MaxSavvySpread)
        {
            Haggle.MinSavvySpread = 0.05;
            Haggle.MaxSavvySpread = 0.5;
            changed = true;
        }

        if (Haggle.MarkdownRatios is null || Haggle.MarkdownRatios.Count == 0)
        {
            Haggle.MarkdownRatios = new List<double> { 0.5, 0.25, 0.125 };
            changed = true;
        }
        else
        {
            var ratioSum = 0.0;
            foreach (var ratio in Haggle.MarkdownRatios)
            {
                if (ratio <= 0 || ratio > 1)
                {
                    Haggle.MarkdownRatios = new List<double> { 0.5, 0.25, 0.125 };
                    changed = true;
                    break;
                }

                ratioSum += ratio;
            }

            if (ratioSum > 1)
            {
                Haggle.MarkdownRatios = new List<double> { 0.5, 0.25, 0.125 };
                changed = true;
            }
        }

        // 阶段 2 Goal 配置：全局超时 [30, 1440] 分钟、汇报超时 [10, 720] 分钟、到达距离 (0, 30] 格、卡死阈值 [30, 600] tick
        Goals ??= new GoalConfig();
        if (Goals.GlobalTimeoutMinutes < 30 || Goals.GlobalTimeoutMinutes > 1440)
        {
            Goals.GlobalTimeoutMinutes = 240;
            changed = true;
        }

        if (Goals.ReportTimeoutMinutes < 10 || Goals.ReportTimeoutMinutes > 720)
        {
            Goals.ReportTimeoutMinutes = 120;
            changed = true;
        }

        if (Goals.ReportArrivalDistance <= 0f || Goals.ReportArrivalDistance > 30f)
        {
            Goals.ReportArrivalDistance = 5f;
            changed = true;
        }

        if (Goals.StuckTickThreshold < 30 || Goals.StuckTickThreshold > 600)
        {
            Goals.StuckTickThreshold = 120;
            changed = true;
        }

        // 阶段 3 Director 配置：token 预算 [200, 4000] 且 min ≤ max；beat 默认有效期 [10, 1440] 游戏分钟
        Director ??= new DirectorConfig();
        if (Director.ContextMinTokens < 200 || Director.ContextMinTokens > 4000)
        {
            Director.ContextMinTokens = 800;
            changed = true;
        }

        if (Director.ContextMaxTokens < 200 || Director.ContextMaxTokens > 4000)
        {
            Director.ContextMaxTokens = 1500;
            changed = true;
        }

        if (Director.ContextMinTokens > Director.ContextMaxTokens)
        {
            Director.ContextMinTokens = 800;
            Director.ContextMaxTokens = 1500;
            changed = true;
        }

        if (Director.BeatDefaultDurationMinutes < 10 || Director.BeatDefaultDurationMinutes > 1440)
        {
            Director.BeatDefaultDurationMinutes = 120;
            changed = true;
        }

        // 导演触发概率 [0, 1]
        if (Director.TriggerProbability < 0 || Director.TriggerProbability > 1)
        {
            Director.TriggerProbability = 0.1;
            changed = true;
        }

        // 阶段 3 L2 配置：todayEvents 保留天数 [1, 7]、条数上限 [1, 20]
        L2 ??= new L2Config();
        if (L2.TodayEventsRetentionDays < 1 || L2.TodayEventsRetentionDays > 7)
        {
            L2.TodayEventsRetentionDays = 3;
            changed = true;
        }

        if (L2.TodayEventsMaxCount < 1 || L2.TodayEventsMaxCount > 20)
        {
            L2.TodayEventsMaxCount = 5;
            changed = true;
        }

        // 对话 / 社交
        if (DialogueCooldownMs < 0 || DialogueCooldownMs > 60000)
        {
            DialogueCooldownMs = 3000;
            changed = true;
        }

        // E2-3 长文间隔：钳制 [0, 10000]，防止 0 以下的负值导致调度时间倒挂
        if (LongTextIntervalMs < 0 || LongTextIntervalMs > 10000)
        {
            LongTextIntervalMs = 2000;
            changed = true;
        }

        if (MaxFriendshipChangePerInteraction < 0 || MaxFriendshipChangePerInteraction > 500)
        {
            MaxFriendshipChangePerInteraction = 80;
            changed = true;
        }

        if (TalkRejectionFollowThreshold < 0 || TalkRejectionFollowThreshold > 2500)
        {
            TalkRejectionFollowThreshold = 200;
            changed = true;
        }

        if (AIDailyTopicCount < 1 || AIDailyTopicCount > 20)
        {
            AIDailyTopicCount = 3;
            changed = true;
        }

        // LLM 性能
        if (LLMTimeoutSeconds < 5 || LLMTimeoutSeconds > 300)
        {
            LLMTimeoutSeconds = 60;
            changed = true;
        }

        if (MaxRetries < 0 || MaxRetries > 10)
        {
            MaxRetries = 3;
            changed = true;
        }

        if (TokenBudget < 0)
        {
            TokenBudget = 0;
            changed = true;
        }

        if (CircuitBreakerThreshold < 1 || CircuitBreakerThreshold > 50)
        {
            CircuitBreakerThreshold = 5;
            changed = true;
        }

        // 紧急 / 状态机
        if (EmergencyHealthThreshold < 0.05f || EmergencyHealthThreshold > 1f)
        {
            EmergencyHealthThreshold = 0.3f;
            changed = true;
        }

        if (EmergencyMonsterDistance < 1f || EmergencyMonsterDistance > 30f)
        {
            EmergencyMonsterDistance = 5f;
            changed = true;
        }

        if (PlayerInDangerDistance < 1f || PlayerInDangerDistance > 30f)
        {
            PlayerInDangerDistance = 7f;
            changed = true;
        }

        if (TaskCompleteDecisionCooldownTicks < 0 || TaskCompleteDecisionCooldownTicks > 10000)
        {
            TaskCompleteDecisionCooldownTicks = 300;
            changed = true;
        }

        if (StateRejectionCooldownTicks < 0 || StateRejectionCooldownTicks > 10000)
        {
            StateRejectionCooldownTicks = 180;
            changed = true;
        }

        if (MaxStateRejections < 0 || MaxStateRejections > 10)
        {
            MaxStateRejections = 2;
            changed = true;
        }

        if (DepartureSkipDistance < 1f || DepartureSkipDistance > 50f)
        {
            DepartureSkipDistance = 10f;
            changed = true;
        }

        if (FightExitCooldownTicks < 0 || FightExitCooldownTicks > 10000)
        {
            FightExitCooldownTicks = 300;
            changed = true;
        }

        // 情绪 / 健康
        if (EmotionCooldownTicks < 0 || EmotionCooldownTicks > 10000)
        {
            EmotionCooldownTicks = 300;
            changed = true;
        }

        if (EmotionDedupThreshold < 0f || EmotionDedupThreshold > 1f)
        {
            EmotionDedupThreshold = 0.15f;
            changed = true;
        }

        if (DefaultMaxHealth < 1 || DefaultMaxHealth > 1000)
        {
            DefaultMaxHealth = 100;
            changed = true;
        }

        // 冷却秒钳制
        if (DialogueCooldownSeconds < 1 || DialogueCooldownSeconds > 60)
        {
            DialogueCooldownSeconds = 3;
            changed = true;
        }

        if (GiftCooldownSeconds < 1 || GiftCooldownSeconds > 600)
        {
            GiftCooldownSeconds = 30;
            changed = true;
        }

        // 概率钳制 [0, 1]
        if (ProactiveSpeechProbability < 0f || ProactiveSpeechProbability > 1f)
        {
            ProactiveSpeechProbability = 0.3f;
            changed = true;
        }

        if (ProactiveGiftProbability < 0f || ProactiveGiftProbability > 1f)
        {
            ProactiveGiftProbability = 0.1f;
            changed = true;
        }

        if (ProactiveFollowProbability < 0f || ProactiveFollowProbability > 1f)
        {
            ProactiveFollowProbability = 0.2f;
            changed = true;
        }

        if (ProactiveTradeProbability < 0f || ProactiveTradeProbability > 1f)
        {
            ProactiveTradeProbability = 0.15f;
            changed = true;
        }

        return changed;
    }
}

/// <summary>
///     JSONL 留痕目录配置。RootDir 相对于 mod 目录。
/// </summary>
public class TranscriptConfig
{
    public string RootDir { get; set; } = "transcript";
}

/// <summary>
///     E3-1 NPC 经济系统配置。DataFile 相对于 mod 目录（默认 Data/npc_economy.json），
///     内容为 NpcEconomyProfile 数组（name/initialMoney/initialItems/savvy/budgetTier/dailyWage/talkativeness）。
/// </summary>
public class EconomyConfig
{
    /// <summary>经济系统总开关。false 时 NpcEconomyProfileLoader 不装载，钱包走默认 0 起步。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>NPC 经济档案 JSON 文件（相对 mod 目录）。</summary>
    public string DataFile { get; set; } = "Data/npc_economy.json";
}

/// <summary>
///     E3-2 还价/定价配置。默认值与 EconomyConstants 一致；Validate() 钳制到安全范围。
///     设计文档：docs/ideas/e32-implementation-思路.md §5。
/// </summary>
public class HaggleConfig
{
    /// <summary>还价状态机总开关。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>最大让步轮次（第 MaxRounds+1 轮必拒）。</summary>
    [DefaultValue(3)]
    public int MaxRounds { get; set; } = 3;

    /// <summary>恶意低价阈值（公道价比例，0.5 = 低于公道价一半视为恶意）。</summary>
    [DefaultValue(0.5)]
    public double HostileThreshold { get; set; } = 0.5;

    /// <summary>精明度 0 时的心理价浮动率（对钱没概念 ±50%）。</summary>
    [DefaultValue(0.5)]
    public double MaxSavvySpread { get; set; } = 0.5;

    /// <summary>精明度 1 时的心理价浮动率（精明商人 ±5%）。</summary>
    [DefaultValue(0.05)]
    public double MinSavvySpread { get; set; } = 0.05;

    /// <summary>每轮新增让步占初始差距的比例，逐轮减半（默认 0.5/0.25/0.125）。</summary>
    public List<double> MarkdownRatios { get; set; } = new() { 0.5, 0.25, 0.125 };
}

/// <summary>
///     阶段 2 Goal 执行器配置。默认值对应 spec §3.3 与 design doc §7.4/§7.5：
///     全局超时 4 游戏小时、汇报寻路超时 2 游戏小时、到达玩家距离 &lt; 5 格、卡死 120 tick。
/// </summary>
public class GoalConfig
{
    /// <summary>全局超时（游戏分钟）：目标执行超时判失败 → 汇报失败。默认 4 游戏小时 = 240 分钟。</summary>
    [DefaultValue(240)]
    public int GlobalTimeoutMinutes { get; set; } = 240;

    /// <summary>汇报寻路超时（游戏分钟）：完成后寻路回玩家阶段超时 → fallback 直接汇报。默认 2 游戏小时 = 120 分钟。</summary>
    [DefaultValue(120)]
    public int ReportTimeoutMinutes { get; set; } = 120;

    /// <summary>到达玩家判定距离（格）：NPC 距玩家 &lt; 该距离视为到达，触发汇报 LLM。</summary>
    [DefaultValue(5f)]
    public float ReportArrivalDistance { get; set; } = 5f;

    /// <summary>位置卡死判定：连续 N tick 位置不变且不在执行动作 → 判失败（复用 MovementService.HandleStuck 120-tick 语义）。</summary>
    [DefaultValue(120)]
    public int StuckTickThreshold { get; set; } = 120;
}

/// <summary>
///     阶段 3 导演系统配置。默认值对应设计 doc §11.4：
///     DirectorContextBuilder token 预算 800-1500、beat 默认有效期 2 游戏小时。
/// </summary>
public class DirectorConfig
{
    /// <summary>DirectorContextBuilder 拼装上下文的目标 token 预算下限（不足时不强凑）。</summary>
    [DefaultValue(800)]
    public int ContextMinTokens { get; set; } = 800;

    /// <summary>DirectorContextBuilder 拼装上下文的 token 预算上限（超出时裁掉低信息 NPC 行）。</summary>
    [DefaultValue(1500)]
    public int ContextMaxTokens { get; set; } = 1500;

    /// <summary>spawn_beat 未显式指定时长时的默认有效期（游戏分钟）。默认 2 游戏小时 = 120 分钟。</summary>
    [DefaultValue(120)]
    public int BeatDefaultDurationMinutes { get; set; } = 120;

    /// <summary>
    ///     导演每日触发概率（0-1）。TS 端 handleDayStarted 每次 roll，命中才调用 Director.morningPlan()。
    ///     默认 0.1（每天 10% 概率导演出手编排）；测试/调试可临时调 1.0 强制触发。
    /// </summary>
    [DefaultValue(0.1)]
    public double TriggerProbability { get; set; } = 0.1;
}

/// <summary>
///     阶段 3 L2 状态摘要配置。默认值对应设计 doc §11.4：todayEvents 保留 3 天、条数 ≤5。
/// </summary>
public class L2Config
{
    /// <summary>todayEvents 保留天数（近 N 天，day_started 清理更早的条目）。</summary>
    [DefaultValue(3)]
    public int TodayEventsRetentionDays { get; set; } = 3;

    /// <summary>todayEvents 条数上限（超出丢弃最旧）。</summary>
    [DefaultValue(5)]
    public int TodayEventsMaxCount { get; set; } = 5;
}