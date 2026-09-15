using System;
using System.Collections.Generic;
using StardewModdingAPI;
using ValleyAgent.Api;

namespace ValleyAgent.Config;

/// <summary>
///     Generic Mod Config Menu (GMCM) integration for ValleyAgent.
///     Uses a local API interface to avoid hard compile-time dependencies on GMCM.
///     If GMCM is not installed, this gracefully does nothing.
/// </summary>
public static class GMCMIntegration
{
    // ─── 多 Provider UI 辅助（Task 6.3） ───────────────────────────────
    // 用反射 GetConfigField/SetConfigField 统一处理 8 个 string 字段，
    // 避免 3 角色 × 8 字段 = 24 个手写闭包。

    /// <summary>Provider 名称预设（与 TS 端 LLMProviderType 联合类型保持一致）。</summary>
    private static readonly string[] s_providerPresets =
    {
        "minimax", "deepseek", "openai", "anthropic",
        "moonshot", "openrouter", "zhipu", "baichuan", "qwen",
        "sensenova", "mimo",
        "lmstudio", "custom"
    };

    /// <summary>备 Provider 预设：允许空串（表示无回退）。</summary>
    private static readonly string[] s_providerPresetsWithEmpty =
    {
        "", "minimax", "deepseek", "openai", "anthropic",
        "moonshot", "openrouter", "zhipu", "baichuan", "qwen",
        "sensenova", "mimo",
        "lmstudio", "custom"
    };

    /// <summary>
    ///     Callback invoked when GMCM configuration changes.
    /// </summary>
    public static event Action<ModConfig>? OnConfigChanged;

    // ──────────────────────────────────────────────────────────────────────
    // UI 文案：按当前 config.Language 选择中文 / 英文。
    //   - English → 英文
    //   - Chinese 或 Custom → 中文（Custom 视为中文回退，避免 UI 文案空白）
    // 所有 label/tooltip 在此集中维护，避免散落在闭包中。
    // ──────────────────────────────────────────────────────────────────────

    private static string T(string zh, string en, ModConfig cfg) => cfg.Language == LanguageMode.English ? en : zh;

    private static string PageBasic(ModConfig c) => T("基础", "Basic", c);
    private static string PageAdvanced(ModConfig c) => T("高级", "Advanced", c);
    private static string LinkAdvanced(ModConfig c) => T("高级设置 →", "Advanced Settings →", c);
    private static string LinkBasic(ModConfig c) => T("← 返回基础设置", "← Back to Basic", c);

    private static string SectionRequired(ModConfig c) => T("必调 (Required)", "Required", c);
    private static string SectionBehavior(ModConfig c) => T("Agent 行为 (Behavior)", "Agent Behavior", c);
    private static string SectionDialogue(ModConfig c) => T("对话与社交 (Dialogue & Social)", "Dialogue & Social", c);
    private static string SectionLlm(ModConfig c) => T("LLM 性能 (Performance)", "LLM Performance", c);

    private static string SectionEmergency(ModConfig c) =>
        T("紧急状态与状态机 (Emergency & State)", "Emergency & State Machine", c);

    private static string SectionEmotion(ModConfig c) => T("情绪与健康 (Emotion & Health)", "Emotion & Health", c);
    private static string SectionDebug(ModConfig c) => T("调试 (Debug)", "Debug", c);
    private static string SectionServer(ModConfig c) => T("服务器高级 (Server Advanced)", "Server Advanced", c);
    private static string SectionDynamicSpeed(ModConfig c) => T("动态速度 (Dynamic Speed)", "Dynamic Speed", c);
    private static string SectionChatBar(ModConfig c) => T("聊天栏路由 (Chat Bar Routing)", "Chat Bar Routing", c);
    private static string SectionEconomy(ModConfig c) => T("经济与还价 (Economy & Haggle)", "Economy & Haggle", c);
    private static string SectionTranscript(ModConfig c) => T("全量留痕 (Transcript)", "Transcript", c);

    private static string NoteRestartRequired(ModConfig c) =>
        T("修改 WebSocket URL / 端口 / 启用自动启动后需要重启游戏生效",
            "Restart required after changing WebSocket URL / Port / AutoStart", c);

    private static string NoteServerPathRestart(ModConfig c) =>
        T("修改服务器可执行文件路径 / 工作目录后需要重启游戏生效",
            "Restart required after changing ServerExecutablePath / ServerDirectory", c);

    private static string NoteMinStateDuration(ModConfig c) =>
        T("MinimumStateDuration(FIGHT/FARM/MINE/FORAGE/FOLLOW, 秒) 是字典字段，请在 config.json 中直接修改",
            "MinimumStateDuration (FIGHT/FARM/MINE/FORAGE/FOLLOW, seconds) is a dictionary — edit config.json directly",
            c);

    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Registers ValleyAgent options with GMCM if it is installed.
    ///     Renders a two-page UI (Basic / Advanced) covering all non-obsolete ModConfig fields.
    ///     If GMCM is not installed, logs an install hint and returns without throwing.
    ///     Any registration/validation error is logged instead of silently swallowed.
    /// </summary>
    public static void RegisterIfAvailable(IModHelper helper, IManifest manifest, ModConfig config)
    {
        IGenericModConfigMenuApi? gmcm;
        try
        {
            gmcm = helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        }
        catch (Exception ex)
        {
            ValleyAgentApi.Monitor?.Log($"GMCM API lookup failed: {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
            return;
        }

        if (gmcm == null)
        {
            // SMAPI 的 GetApi<T> 在接口签名与实际 API 有任何差异时静默返回 null。
            // 此时尝试用非泛型 GetApi 获取原始对象来确认 GMCM 确实加载了。
            object? rawApi = null;
            try
            {
                rawApi = helper.ModRegistry.GetApi("spacechase0.GenericModConfigMenu");
            }
            catch
            {
                /* ignore */
            }

            ValleyAgentApi.Monitor?.Log(
                rawApi != null
                    ? $"GMCM is loaded (type={rawApi.GetType().FullName}) but interface mapping failed — IGenericModConfigMenuApi signature mismatch with installed GMCM"
                    : "Install GMCM for in-game config UI",
                LogLevel.Warn);
            return;
        }

        ValleyAgentApi.Monitor?.Log($"GMCM API acquired: {gmcm.GetType().FullName}", LogLevel.Debug);

        // Register — 必须成功，否则后续 AddXxx 无意义
        try
        {
            gmcm.Register(
                manifest,
                () =>
                {
                    var resetConfig = new ModConfig();
                    CopyConfig(resetConfig, config);
                    NotifyConfigChanged(config);
                },
                () =>
                {
                    helper.WriteConfig(config);
                    NotifyConfigChanged(config);
                });
            ValleyAgentApi.Monitor?.Log("GMCM Register succeeded", LogLevel.Info);
        }
        catch (Exception ex)
        {
            ValleyAgentApi.Monitor?.Log($"GMCM Register failed: {ex.GetType().Name}: {ex.Message}", LogLevel.Error);
            return;
        }

        // Root page（默认页面）：直接放基础选项，用户打开配置即可见。
        // 不使用 AddPage("basic") 子页面 — GMCM 1.15.0 下多层 page link 嵌套会触发
        // SpecificModConfigMenu 构造器无限递归（ForceUpdateEvenHidden → OpenModMenuNew）。
        try
        {
            AddBasicPageOptions(gmcm, manifest, config);
            // 基础选项末尾添加到高级设置的链接（仍在 root page 上）
            gmcm.AddPageLink(manifest, "advanced", () => LinkAdvanced(config),
                () => T("打开高级设置", "Open advanced settings", config));
            ValleyAgentApi.Monitor?.Log("GMCM root page (basic options) added", LogLevel.Info);
        }
        catch (Exception ex)
        {
            ValleyAgentApi.Monitor?.Log($"GMCM root page failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}",
                LogLevel.Error);
        }

        // Advanced page — 子页面，用户点 PageLink 进入，用 GMCM 自带返回按钮回 root。
        // 不在 advanced 里加 AddPageLink("root") — root 无 pageId，且会触发递归。
        try
        {
            gmcm.AddPage(manifest, "advanced", () => PageAdvanced(config));
            AddAdvancedPageOptions(gmcm, manifest, config);
            ValleyAgentApi.Monitor?.Log("GMCM advanced page options added", LogLevel.Info);
        }
        catch (Exception ex)
        {
            ValleyAgentApi.Monitor?.Log(
                $"GMCM advanced page failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", LogLevel.Error);
        }
    }

    private static void AddBasicPageOptions(IGenericModConfigMenuApi gmcm, IManifest manifest, ModConfig config)
    {
        var providerNames = Enum.GetNames(typeof(LlmProvider));
        var languageNames = Enum.GetNames(typeof(LanguageMode));

        // ─── Section 1: Required ─────────────────────────────────────────
        gmcm.AddSectionTitle(manifest, () => SectionRequired(config));

        gmcm.AddBoolOption(
            manifest,
            name: () => T("启用模组", "Enabled", config),
            tooltip: () => T("ValleyAgent 总开关", "Master on/off switch for the ValleyAgent mod.", config),
            getValue: () => config.Enabled,
            setValue: value => config.Enabled = value);

        gmcm.AddTextOption(
            manifest,
            name: () => T("LLM 提供商", "LLM Provider", config),
            tooltip: () => T("决策 / 对话使用的 LLM 提供商", "LLM provider used for agent decisions and dialogue.", config),
            getValue: () => config.LlmProvider.ToString(),
            setValue: value =>
            {
                if (Enum.TryParse(value, out LlmProvider parsed))
                {
                    config.LlmProvider = parsed;
                }
            },
            allowedValues: providerNames);

        gmcm.AddTextOption(
            manifest,
            name: () => T("模型名称", "Model Name", config),
            tooltip: () => T("发送到 LLM 提供商的模型标识（如 deepseek-chat）",
                "Model identifier sent to the LLM provider (e.g. deepseek-chat).", config),
            getValue: () => config.ModelName ?? string.Empty,
            setValue: value => config.ModelName = value ?? string.Empty);

        gmcm.AddTextOption(
            manifest,
            name: () => T("API Key", "API Key", config),
            tooltip: () => T("所选 LLM 提供商的 API Key，明文存储", "API key for the selected LLM provider. Stored in plain text.",
                config),
            getValue: () => config.LlmApiKey ?? string.Empty,
            setValue: value => config.LlmApiKey = value ?? string.Empty);

        // WebSocket URL 已自动从 AgentServerHost + ServerPort 绑定（Task 5），不再暴露完整 URI UI。

        gmcm.AddTextOption(
            manifest,
            name: () => T("服务器主机名", "Server Host", config),
            tooltip: () => T("TS Agent Server 主机名/IP。默认 127.0.0.1；Docker 容器场景配 compose 服务名（如 valley-ts）。",
                "TS Agent Server hostname/IP. Default 127.0.0.1; use compose service name (e.g. valley-ts) for Docker.", config),
            getValue: () => config.AgentServerHost ?? "127.0.0.1",
            setValue: value => config.AgentServerHost = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("服务器端口", "Server Port", config),
            tooltip: () => T("Agent Server 监听端口", "Agent Server listening port.", config),
            getValue: () => config.ServerPort,
            setValue: value => config.ServerPort = Math.Clamp(value, 1, 65535),
            min: 1,
            max: 65535,
            interval: 1);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("温度 Temperature", "Temperature", config),
            tooltip: () => T("LLM 采样温度 (0.0 - 2.0)", "Sampling temperature for LLM generation (0.0 - 2.0).", config),
            getValue: () => config.Temperature,
            setValue: value => config.Temperature = Math.Clamp(value, 0f, 2f),
            min: 0f,
            max: 2f,
            interval: 0.1f);

        gmcm.AddTextOption(
            manifest,
            name: () => T("对话语言", "Dialogue Language", config),
            tooltip: () => T("AI 对话使用的语言；Custom 走 CustomSystemPrompt",
                "Language used for AI dialogue. Custom uses CustomSystemPrompt.", config),
            getValue: () => config.Language.ToString(),
            setValue: value =>
            {
                if (Enum.TryParse(value, out LanguageMode parsed))
                {
                    config.Language = parsed;
                }
            },
            allowedValues: languageNames);

        gmcm.AddParagraph(manifest, () => NoteRestartRequired(config));

        // ─── Section 2: Agent Behavior ───────────────────────────────────
        gmcm.AddSectionTitle(manifest, () => SectionBehavior(config));

        gmcm.AddNumberOption(
            manifest,
            // PR2 B6（设计 §3.1）：分配语义从"身份"改为"身体"——三档数值行为/钳制不动，仅文案对齐。
            name: () => T("最少 AI 身体数", "Min AI Bodies", config),
            tooltip: () => T("硬下限：低于此值时 spark 概率加倍、导演优先选无身体 NPC (0 - Normal)",
                "Hard floor: below this, spark probability doubles and the director prioritizes NPCs without an AI body (0 - Normal).",
                config),
            getValue: () => config.MinAgentNpcs,
            setValue: value =>
            {
                config.MinAgentNpcs = Math.Clamp(value, 0, config.MaxAgentNpcs);
                // 联动：Min 上调超过 Normal 时 Normal 跟进
                if (config.NormalAgentNpcs < config.MinAgentNpcs)
                {
                    config.NormalAgentNpcs = config.MinAgentNpcs;
                }
            },
            min: 0,
            max: 10,
            interval: 1);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("平时 AI 身体数", "Normal AI Bodies", config),
            tooltip: () => T("日常目标：spark 主动激活到此数量停止 (Min - Max)",
                "Daily target: spark stops proactive activation at this count (Min - Max).", config),
            getValue: () => config.NormalAgentNpcs,
            setValue: value =>
            {
                config.NormalAgentNpcs = Math.Clamp(value, config.MinAgentNpcs, config.MaxAgentNpcs);
            },
            min: 0,
            max: 10,
            interval: 1);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("AI 身体上限", "Max AI Bodies", config),
            tooltip: () => T("硬上限：同时持有的 AI 身体不超过此值 (Normal - 10)",
                "Hard ceiling: concurrent AI bodies never exceed this (Normal - 10).",
                config),
            getValue: () => config.MaxAgentNpcs,
            setValue: value =>
            {
                var newMax = Math.Clamp(value, 0, 10);
                config.MaxAgentNpcs = newMax;
                if (config.MinAgentNpcs > newMax)
                {
                    config.MinAgentNpcs = newMax;
                }

                // 联动：Max 下调低于 Normal 时 Normal 回退
                if (config.NormalAgentNpcs > newMax)
                {
                    config.NormalAgentNpcs = newMax;
                }
            },
            min: 0,
            max: 10,
            interval: 1);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("决策间隔（分钟）", "Decision Interval (minutes)", config),
            tooltip: () => T("Agent 多久做一次决策，分钟 (0.1 - 5.0)",
                "How often the agent makes decisions, in minutes (0.1 - 5.0).", config),
            getValue: () => config.DecisionIntervalMinutes,
            setValue: value => config.DecisionIntervalMinutes = Math.Clamp(value, 0.1f, 5f),
            min: 0.1f,
            max: 5f,
            interval: 0.1f);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("空闲释放阈值（秒）", "Idle Threshold (seconds)", config),
            tooltip: () => T("NPC 空闲多少秒后 Agent 释放控制权 (30 - 300)",
                "Seconds of NPC idle before the agent releases the NPC (30 - 300).", config),
            getValue: () => config.IdleThresholdSeconds,
            setValue: value => config.IdleThresholdSeconds = Math.Clamp(value, 30, 300),
            min: 30,
            max: 300,
            interval: 5);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("跟随距离（格）", "Follow Distance (tiles)", config),
            tooltip: () => T("FOLLOW 状态下 NPC 与玩家的目标距离 (1 - 10)",
                "Target tile distance between NPC and player in FOLLOW state (1 - 10).", config),
            getValue: () => config.FollowDistance,
            setValue: value => config.FollowDistance = Math.Clamp(value, 1, 10),
            min: 1,
            max: 10,
            interval: 1);

        gmcm.AddBoolOption(
            manifest,
            name: () => T("启用战斗辅助", "Enable Combat Assist", config),
            tooltip: () => T("Agent 是否可驱动 NPC 协助战斗", "Allow agent to drive NPC combat assistance.", config),
            getValue: () => config.EnableCombatAssist,
            setValue: value => config.EnableCombatAssist = value);

        gmcm.AddBoolOption(
            manifest,
            name: () => T("启用农场辅助", "Enable Farming Assist", config),
            tooltip: () => T("Agent 是否可驱动 NPC 进行农场作业", "Allow agent to drive NPC farming work.", config),
            getValue: () => config.EnableFarmingAssist,
            setValue: value => config.EnableFarmingAssist = value);

        gmcm.AddBoolOption(
            manifest,
            name: () => T("启用采矿辅助", "Enable Mining Assist", config),
            tooltip: () => T("Agent 是否可驱动 NPC 进行采矿", "Allow agent to drive NPC mining work.", config),
            getValue: () => config.EnableMiningAssist,
            setValue: value => config.EnableMiningAssist = value);

        gmcm.AddBoolOption(
            manifest,
            name: () => T("启用采集辅助", "Enable Foraging Assist", config),
            tooltip: () => T("Agent 是否可驱动 NPC 采集", "Allow agent to drive NPC foraging.", config),
            getValue: () => config.EnableForagingAssist,
            setValue: value => config.EnableForagingAssist = value);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("连续空闲释放次数", "Max Consecutive Idle Before Release", config),
            tooltip: () => T("连续多少次 idle 决策后强制释放 NPC (1 - 10)",
                "Consecutive idle decisions before forced NPC release (1 - 10).", config),
            getValue: () => config.MaxConsecutiveIdleBeforeRelease,
            setValue: value => config.MaxConsecutiveIdleBeforeRelease = Math.Clamp(value, 1, 10),
            min: 1,
            max: 10,
            interval: 1);

        // ─── 功能开关扩展（Task 6.1） ───
        gmcm.AddBoolOption(
            manifest,
            name: () => T("NPC 交易", "NPC Trade", config),
            tooltip: () => T("允许与 NPC 买卖物品", "Allow trading with NPCs", config),
            getValue: () => config.EnableTrade,
            setValue: value => config.EnableTrade = value);

        gmcm.AddBoolOption(
            manifest,
            name: () => T("NPC 雇佣", "NPC Hire", config),
            tooltip: () => T("允许雇佣 NPC 协助工作", "Allow hiring NPCs", config),
            getValue: () => config.EnableHire,
            setValue: value => config.EnableHire = value);

        gmcm.AddBoolOption(
            manifest,
            name: () => T("导演模式", "Director Mode", config),
            tooltip: () => T("开启叙事编排；关闭则 NPC 完全自主", "Enable director; off = fully autonomous", config),
            getValue: () => config.EnableDirector,
            setValue: value => config.EnableDirector = value);

        gmcm.AddBoolOption(
            manifest,
            name: () => T("NPC 主动发言", "Proactive Speech", config),
            tooltip: () => T("NPC 主动喊话/搭话总开关", "Master switch for proactive speech", config),
            getValue: () => config.EnableProactiveSpeech,
            setValue: value => config.EnableProactiveSpeech = value);

        gmcm.AddBoolOption(
            manifest,
            name: () => T("无限对话", "Infinite Dialogue", config),
            tooltip: () => T("非 Agent NPC 也能 AI 对话", "AI dialogue with all NPCs", config),
            getValue: () => config.EnableInfiniteDialogue,
            setValue: value => config.EnableInfiniteDialogue = value);

        // ─── Section 3: Dialogue & Social ────────────────────────────────
        gmcm.AddSectionTitle(manifest, () => SectionDialogue(config));

        gmcm.AddBoolOption(
            manifest,
            name: () => T("启用好感度变化", "Enable Friendship Changes", config),
            tooltip: () => T("Agent 交互是否影响玩家与 NPC 的好感度",
                "Whether agent interactions modify friendship with the player.", config),
            getValue: () => config.EnableFriendshipChanges,
            setValue: value => config.EnableFriendshipChanges = value);

        gmcm.AddBoolOption(
            manifest,
            name: () => T("启用送礼", "Enable Gifts", config),
            tooltip: () => T("Agent 是否可主动给玩家 / NPC 送礼", "Allow agent to give gifts to the player / NPCs.", config),
            getValue: () => config.EnableGifts,
            setValue: value => config.EnableGifts = value);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("送礼冷却（秒）", "Gift Cooldown (sec)", config),
            tooltip: () => T("两次送礼最小间隔 (1-600)", "Min gap between gifts (1-600 s)", config),
            getValue: () => config.GiftCooldownSeconds,
            setValue: value => config.GiftCooldownSeconds = Math.Clamp(value, 1, 600),
            min: 1,
            max: 600,
            interval: 1);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("对话冷却（秒）", "Dialogue Cooldown (sec)", config),
            tooltip: () => T(
                "同一 NPC 相邻对话最小间隔 (1-60)；仅作用于外部 API/测试通道，玩家聊天由服务器会话锁自动串行",
                "Min gap between dialogue (1-60 s). Only applies to the external API/test channel; player chat is serialized by the server session lock.",
                config),
            getValue: () => config.DialogueCooldownSeconds,
            setValue: value => config.DialogueCooldownSeconds = Math.Clamp(value, 1, 60),
            min: 1,
            max: 60,
            interval: 1);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("单次好感度变化上限", "Max Friendship Change Per Interaction", config),
            tooltip: () => T("单次交互引起的好感度变化最大值 (0 - 500)", "Cap on friendship change per single interaction (0 - 500).",
                config),
            getValue: () => config.MaxFriendshipChangePerInteraction,
            setValue: value => config.MaxFriendshipChangePerInteraction = Math.Clamp(value, 0, 500),
            min: 0,
            max: 500,
            interval: 10);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("TALK→FOLLOW 阈值", "Talk Rejection Follow Threshold", config),
            tooltip: () => T("好感度低于该值时 TALK 被拒则回退 FOLLOW (0 - 2500)",
                "Friendship points below which TALK rejection falls back to FOLLOW instead of IDLE (0 - 2500).",
                config),
            getValue: () => config.TalkRejectionFollowThreshold,
            setValue: value => config.TalkRejectionFollowThreshold = Math.Clamp(value, 0, 2500),
            min: 0,
            max: 2500,
            interval: 50);

        gmcm.AddBoolOption(
            manifest,
            name: () => T("显示聊天气泡", "Chat Bubble Enabled", config),
            tooltip: () => T("是否把 AI 对话显示为游戏内聊天气泡", "Show AI dialogue as in-game chat bubbles.", config),
            getValue: () => config.ChatBubbleEnabled,
            setValue: value => config.ChatBubbleEnabled = value);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("长文分句间隔（毫秒）", "Long Text Sentence Interval (ms)", config),
            tooltip: () => T("多于一句的长文按句弹入聊天栏的间隔，首句立即 (0 - 10000)",
                "Interval between sentences when long text (>1 sentence) pops into the chat bar; first sentence is immediate (0 - 10000).",
                config),
            getValue: () => config.LongTextIntervalMs,
            setValue: value => config.LongTextIntervalMs = Math.Clamp(value, 0, 10000),
            min: 0,
            max: 10000,
            interval: 100);

        gmcm.AddBoolOption(
            manifest,
            name: () => T("非 Agent NPC AI 续聊", "Non-Agent AI Chat", config),
            tooltip: () => T("与非 Agent 村民对话结束后，自动打开 AI 输入框",
                "After vanilla dialogue with a non-Agent villager ends, open the AI chat box.", config),
            getValue: () => config.NonAgentAIChatEnabled,
            setValue: value => config.NonAgentAIChatEnabled = value);

        gmcm.AddBoolOption(
            manifest,
            name: () => T("首次点击播原版对话", "First Click Plays Vanilla Dialogue", config),
            tooltip: () => T("首次点击 NPC 先播放原版对话", "On first click, play vanilla dialogue before AI takes over.", config),
            getValue: () => config.EnableFirstClickVanilla,
            setValue: value => config.EnableFirstClickVanilla = value);

        gmcm.AddBoolOption(
            manifest,
            name: () => T("暂停所有对话", "Pause All Dialogue", config),
            tooltip: () => T("紧急开关：暂时停止所有 AI 对话生成", "Emergency switch: temporarily halt all AI dialogue generation.",
                config),
            getValue: () => config.PauseAllDialogue,
            setValue: value => config.PauseAllDialogue = value);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("每日 AI 话题数", "Daily AI Topic Count", config),
            tooltip: () => T("每天生成的 AI 主动话题数量 (1 - 20)", "Number of proactive AI topics generated per day (1 - 20).",
                config),
            getValue: () => config.AIDailyTopicCount,
            setValue: value => config.AIDailyTopicCount = Math.Clamp(value, 1, 20),
            min: 1,
            max: 20,
            interval: 1);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("每日主动发言额度", "Proactive Speech Daily Limit", config),
            tooltip: () => T("NPC 主动喊话/搭话每天最多 N 次 (1 - 20)", "Max proactive speeches per NPC per day (1 - 20).",
                config),
            getValue: () => config.ProactiveSpeechDailyLimit,
            setValue: value => config.ProactiveSpeechDailyLimit = Math.Clamp(value, 1, 20),
            min: 1,
            max: 20,
            interval: 1);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("主动发言冷却（分钟）", "Proactive Speech Cooldown (min)", config),
            tooltip: () => T("同 NPC 两次主动发言至少间隔该时长；0 = 关闭冷却 (0 - 240)",
                "Minimum minutes between proactive speeches from the same NPC; 0 disables cooldown (0 - 240).", config),
            getValue: () => config.ProactiveSpeechCooldownMinutes,
            setValue: value => config.ProactiveSpeechCooldownMinutes = Math.Clamp(value, 0, 240),
            min: 0,
            max: 240,
            interval: 5);

        // ─── 概率设置（Task 6.1） ───
        gmcm.AddNumberOption(
            manifest,
            name: () => T("主动搭话概率", "Proactive Speech Prob", config),
            tooltip: () => T("NPC 主动搭话触发概率 (0-1)", "Proactive speech probability (0-1)", config),
            getValue: () => config.ProactiveSpeechProbability,
            setValue: value => config.ProactiveSpeechProbability = Math.Clamp(value, 0f, 1f),
            min: 0f,
            max: 1f,
            interval: 0.05f);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("主动送礼概率", "Proactive Gift Prob", config),
            tooltip: () => T("NPC 主动送礼触发概率 (0-1)", "Proactive gift probability (0-1)", config),
            getValue: () => config.ProactiveGiftProbability,
            setValue: value => config.ProactiveGiftProbability = Math.Clamp(value, 0f, 1f),
            min: 0f,
            max: 1f,
            interval: 0.05f);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("主动跟随概率", "Proactive Follow Prob", config),
            tooltip: () => T("NPC 主动跟随触发概率 (0-1)", "Proactive follow probability (0-1)", config),
            getValue: () => config.ProactiveFollowProbability,
            setValue: value => config.ProactiveFollowProbability = Math.Clamp(value, 0f, 1f),
            min: 0f,
            max: 1f,
            interval: 0.05f);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("主动交易概率", "Proactive Trade Prob", config),
            tooltip: () => T("NPC 主动求购触发概率 (0-1)", "Proactive trade probability (0-1)", config),
            getValue: () => config.ProactiveTradeProbability,
            setValue: value => config.ProactiveTradeProbability = Math.Clamp(value, 0f, 1f),
            min: 0f,
            max: 1f,
            interval: 0.05f);
    }

    private static void AddAdvancedPageOptions(IGenericModConfigMenuApi gmcm, IManifest manifest, ModConfig config)
    {
        // ─── 多 Provider 模式（Task 6.2） ─────────────────────────────
        gmcm.AddSectionTitle(manifest, () => T("多 Provider 模式", "Multi-Provider", config));

        gmcm.AddBoolOption(
            manifest,
            name: () => T("启用多 Provider", "Enable Multi-Provider", config),
            tooltip: () => T("开启后单 Provider 配置被忽略；需重启", "When on, single provider ignored. Restart required.", config),
            getValue: () => config.MultiProviderEnabled,
            setValue: value => config.MultiProviderEnabled = value);

        gmcm.AddParagraph(manifest, () => T(
            "⚠️ 启用后基础页的单 Provider 配置将被忽略。3 角色（导演/主角/普通 NPC）各可配主备 provider，主 provider 欠费时自动回退到备。",
            "⚠️ When enabled, single provider config on Basic page is ignored. 3 roles (Director/Protagonist/NPC) each have primary+fallback. Auto-fallback on billing error.",
            config));

        // ─── 导演 ───
        gmcm.AddSectionTitle(manifest, () => T("导演 (Director)", "Director", config));
        AddProviderSlotUI(gmcm, manifest, config, "Director", "导演", "Director");

        // ─── 主角 NPC ───
        gmcm.AddSectionTitle(manifest, () => T("主角 NPC (Protagonist)", "Protagonist", config));
        AddProviderSlotUI(gmcm, manifest, config, "Protagonist", "主角", "Protagonist");

        // ─── 普通 NPC ───
        gmcm.AddSectionTitle(manifest, () => T("普通 NPC (NPC)", "NPC", config));
        AddProviderSlotUI(gmcm, manifest, config, "Npc", "普通 NPC", "NPC");

        // ─── 角色标记 ───
        gmcm.AddSectionTitle(manifest, () => T("角色标记", "Role Mapping", config));

        gmcm.AddBoolOption(
            manifest,
            name: () => T("启用角色区分", "Enable Role Mapping", config),
            tooltip: () => T("关闭时所有 NPC 走普通 NPC 配置", "Off = all NPCs use NPC role", config),
            getValue: () => config.EnableProtagonistMapping,
            setValue: value => config.EnableProtagonistMapping = value);

        gmcm.AddTextOption(
            manifest,
            name: () => T("主角 NPC 列表", "Protagonist NPC List", config),
            tooltip: () => T("逗号分隔；其余归普通 NPC", "Comma-separated; others use NPC role", config),
            getValue: () => config.ProtagonistNpcs ?? string.Empty,
            setValue: value => config.ProtagonistNpcs = value ?? string.Empty);

        // ─── Section 4: Server Advanced ──────────────────────────────────
        gmcm.AddSectionTitle(manifest, () => SectionServer(config));

        gmcm.AddBoolOption(
            manifest,
            name: () => T("自动启动服务器", "Auto Start Server", config),
            tooltip: () => T("游戏启动时自动启动 Agent Server 子进程",
                "Automatically launch the Agent Server subprocess when the game starts.", config),
            getValue: () => config.AutoStartServer,
            setValue: value => config.AutoStartServer = value);

        gmcm.AddBoolOption(
            manifest,
            name: () => T("服务器控制台窗口", "Server Console Window", config),
            tooltip: () => T("在独立可见的 cmd 窗口中运行服务器（便于查看日志/报错）",
                "Run the Agent server in a separate visible console window for live logs.", config),
            getValue: () => config.ServerConsoleWindow,
            setValue: value => config.ServerConsoleWindow = value);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("服务器启动超时（秒）", "Server Startup Timeout (seconds)", config),
            tooltip: () => T("等待服务器就绪的最长时间 (10 - 300)", "Max wait time for the server to become ready (10 - 300 s).",
                config),
            getValue: () => config.ServerStartupTimeoutSeconds,
            setValue: value => config.ServerStartupTimeoutSeconds = Math.Clamp(value, 10, 300),
            min: 10,
            max: 300,
            interval: 5);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("服务器最大重启次数", "Server Max Restart Attempts", config),
            tooltip: () => T("服务器崩溃后自动重启的最大次数 (0 - 10)", "Max auto-restart attempts after a server crash (0 - 10).",
                config),
            getValue: () => config.ServerMaxRestartAttempts,
            setValue: value => config.ServerMaxRestartAttempts = Math.Clamp(value, 0, 10),
            min: 0,
            max: 10,
            interval: 1);

        gmcm.AddTextOption(
            manifest,
            name: () => T("服务器可执行文件", "Server Executable Path", config),
            tooltip: () => T("Agent Server 可执行文件绝对路径；留空使用默认",
                "Absolute path to the Agent Server executable. Empty = default.", config),
            getValue: () => config.ServerExecutablePath ?? string.Empty,
            setValue: value => config.ServerExecutablePath = value ?? string.Empty);

        gmcm.AddTextOption(
            manifest,
            name: () => T("服务器工作目录", "Server Working Directory", config),
            tooltip: () => T("Agent Server 启动时的工作目录；留空使用默认",
                "Working directory when launching the Agent Server. Empty = default.", config),
            getValue: () => config.ServerDirectory ?? string.Empty,
            setValue: value => config.ServerDirectory = value ?? string.Empty);

        gmcm.AddParagraph(manifest, () => NoteServerPathRestart(config));

        // ─── Section 5: LLM Performance ──────────────────────────────────
        gmcm.AddSectionTitle(manifest, () => SectionLlm(config));

        gmcm.AddNumberOption(
            manifest,
            name: () => T("LLM 超时（秒）", "LLM Timeout (seconds)", config),
            tooltip: () => T("单次 LLM 请求的最大等待时间 (5 - 300)", "Max wait time for a single LLM request (5 - 300 s).",
                config),
            getValue: () => config.LLMTimeoutSeconds,
            setValue: value => config.LLMTimeoutSeconds = Math.Clamp(value, 5, 300),
            min: 5,
            max: 300,
            interval: 5);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("最大重试次数", "Max Retries", config),
            tooltip: () => T("LLM 失败时的最大重试次数 (0 - 10)", "Max retries when an LLM call fails (0 - 10).", config),
            getValue: () => config.MaxRetries,
            setValue: value => config.MaxRetries = Math.Clamp(value, 0, 10),
            min: 0,
            max: 10,
            interval: 1);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("Token 预算", "Token Budget", config),
            tooltip: () => T("单次会话的 Token 上限；0 表示不限制", "Per-session token budget. 0 = unlimited.", config),
            getValue: () => config.TokenBudget,
            setValue: value => config.TokenBudget = Math.Clamp(value, 0, 1_000_000),
            min: 0,
            max: 1_000_000,
            interval: 1000);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("熔断阈值", "Circuit Breaker Threshold", config),
            tooltip: () => T("连续失败多少次后熔断 LLM 调用 (1 - 50)",
                "Consecutive failures before tripping the circuit breaker (1 - 50).", config),
            getValue: () => config.CircuitBreakerThreshold,
            setValue: value => config.CircuitBreakerThreshold = Math.Clamp(value, 1, 50),
            min: 1,
            max: 50,
            interval: 1);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("AI 混入概率", "Fallback AI Mix Probability", config),
            tooltip: () => T("回退路径中调用 AI 决策的比例 (0 - 1)", "Probability of invoking AI in the fallback path (0 - 1).",
                config),
            getValue: () => config.FallbackAIMixProbability,
            setValue: value => config.FallbackAIMixProbability = Math.Clamp(value, 0f, 1f),
            min: 0f,
            max: 1f,
            interval: 0.05f);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("实时生成概率", "Live Generation Probability", config),
            tooltip: () => T("回退路径中实时生成对话的比例 (0 - 1)", "Probability of live-generating dialogue in fallback (0 - 1).",
                config),
            getValue: () => config.FallbackLiveGenerationProbability,
            setValue: value => config.FallbackLiveGenerationProbability = Math.Clamp(value, 0f, 1f),
            min: 0f,
            max: 1f,
            interval: 0.05f);

        // ─── Section 6: Dynamic Speed ────────────────────────────────────
        gmcm.AddSectionTitle(manifest, () => SectionDynamicSpeed(config));

        gmcm.AddBoolOption(
            manifest,
            name: () => T("启用动态速度", "Enable Dynamic Speed", config),
            tooltip: () => T("NPC 距目标越远移动越快（不影响体力）", "NPC moves faster when far from target (no stamina penalty).",
                config),
            getValue: () => config.DynamicSpeedEnabled,
            setValue: value => config.DynamicSpeedEnabled = value);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("远距阈值（格）", "Far Threshold (tiles)", config),
            tooltip: () => T("距离超过该值按远距倍率移动 (1 - 50)", "Distance above which the far multiplier applies (1 - 50).",
                config),
            getValue: () => config.DynamicSpeedFarThreshold,
            setValue: value =>
                config.DynamicSpeedFarThreshold = Math.Clamp(value, config.DynamicSpeedMidThreshold, 50f),
            min: 1f,
            max: 50f,
            interval: 0.5f);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("中距阈值（格）", "Mid Threshold (tiles)", config),
            tooltip: () => T("距离小于该值按近距倍率移动 (1 - 50)", "Distance below which the near multiplier applies (1 - 50).",
                config),
            getValue: () => config.DynamicSpeedMidThreshold,
            setValue: value => config.DynamicSpeedMidThreshold = Math.Clamp(value, 1f, config.DynamicSpeedFarThreshold),
            min: 1f,
            max: 50f,
            interval: 0.5f);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("近距速度倍率", "Near Speed Multiplier", config),
            tooltip: () => T("近距离移动速度倍率 (0.25 - 5)", "Speed multiplier when close to target (0.25 - 5).", config),
            getValue: () => config.DynamicSpeedNearMultiplier,
            setValue: value => config.DynamicSpeedNearMultiplier = Math.Clamp(value, 0.25f, 5f),
            min: 0.25f,
            max: 5f,
            interval: 0.05f);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("中距速度倍率", "Mid Speed Multiplier", config),
            tooltip: () => T("中距离移动速度倍率 (0.25 - 5)", "Speed multiplier at medium distance (0.25 - 5).", config),
            getValue: () => config.DynamicSpeedMidMultiplier,
            setValue: value => config.DynamicSpeedMidMultiplier = Math.Clamp(value, 0.25f, 5f),
            min: 0.25f,
            max: 5f,
            interval: 0.05f);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("远距速度倍率", "Far Speed Multiplier", config),
            tooltip: () => T("远距离移动速度倍率 (0.25 - 5)", "Speed multiplier when far from target (0.25 - 5).", config),
            getValue: () => config.DynamicSpeedFarMultiplier,
            setValue: value => config.DynamicSpeedFarMultiplier = Math.Clamp(value, 0.25f, 5f),
            min: 0.25f,
            max: 5f,
            interval: 0.05f);

        // ─── Section 7: Chat Bar Routing ─────────────────────────────────
        gmcm.AddSectionTitle(manifest, () => SectionChatBar(config));

        gmcm.AddBoolOption(
            manifest,
            name: () => T("启用聊天栏路由", "Enable Chat Bar Routing", config),
            tooltip: () => T("玩家在聊天栏打字即视为对在场/跟随中的 NPC 说话",
                "Player chat messages are routed to nearby or following NPCs.", config),
            getValue: () => config.ChatBarRoutingEnabled,
            setValue: value => config.ChatBarRoutingEnabled = value);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("会话超时（秒）", "Session Timeout (seconds)", config),
            tooltip: () => T("玩家多久未回应则退出会话 (10 - 600)",
                "Seconds before a chat session times out without player reply (10 - 600).", config),
            getValue: () => config.ChatSessionTimeoutSeconds,
            setValue: value => config.ChatSessionTimeoutSeconds = Math.Clamp(value, 10, 600),
            min: 10,
            max: 600,
            interval: 5);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("最近交互窗口（秒）", "Recent Interaction Window (seconds)", config),
            tooltip: () => T("最近交互过且在附近优先接话的窗口 (5 - 300)",
                "Window in which recently-interacted nearby NPCs are preferred (5 - 300).", config),
            getValue: () => config.ChatRecentInteractionWindowSeconds,
            setValue: value => config.ChatRecentInteractionWindowSeconds = Math.Clamp(value, 5, 300),
            min: 5,
            max: 300,
            interval: 5);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("附近阈值（格）", "Nearby Distance (tiles)", config),
            tooltip: () => T("聊天栏路由的\"附近\"判定格数 (1 - 50)",
                "Tile distance considered 'nearby' for chat routing (1 - 50).", config),
            getValue: () => config.ChatNearbyDistanceTiles,
            setValue: value => config.ChatNearbyDistanceTiles = Math.Clamp(value, 1, 50),
            min: 1,
            max: 50,
            interval: 1);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("群体接话上限", "Group Response Max", config),
            tooltip: () => T("\"大家\" / \"你们\" 最多同时接话人数 (1 - 5)",
                "Max NPCs that respond to group-addressed chat (1 - 5).", config),
            getValue: () => config.ChatGroupResponseMax,
            setValue: value => config.ChatGroupResponseMax = Math.Clamp(value, 1, 5),
            min: 1,
            max: 5,
            interval: 1);

        // ─── Section 8: Economy & Haggle ─────────────────────────────────
        gmcm.AddSectionTitle(manifest, () => SectionEconomy(config));

        gmcm.AddBoolOption(
            manifest,
            name: () => T("启用 NPC 经济系统", "Enable NPC Economy", config),
            tooltip: () => T("为 NPC 加载钱包与初始物品档案", "Load wallets and initial inventories for NPCs.", config),
            getValue: () => config.Economy.Enabled,
            setValue: value => config.Economy.Enabled = value);

        gmcm.AddBoolOption(
            manifest,
            name: () => T("启用还价", "Enable Haggle", config),
            tooltip: () => T("允许玩家与 NPC 买卖时讨价还价", "Allow haggling when trading with NPCs.", config),
            getValue: () => config.Haggle.Enabled,
            setValue: value => config.Haggle.Enabled = value);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("最大让步轮次", "Max Haggle Rounds", config),
            tooltip: () => T("还价最多进行几轮 (1 - 10)", "Maximum haggling rounds (1 - 10).", config),
            getValue: () => config.Haggle.MaxRounds,
            setValue: value => config.Haggle.MaxRounds = Math.Clamp(value, 1, 10),
            min: 1,
            max: 10,
            interval: 1);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("恶意低价阈值", "Hostile Threshold", config),
            tooltip: () => T("低于公道价多少比例被视为恶意 (0.05 - 0.95)",
                "Price ratio below fair price considered hostile (0.05 - 0.95).", config),
            getValue: () => (float)config.Haggle.HostileThreshold,
            setValue: value => config.Haggle.HostileThreshold = Math.Clamp(value, 0.05f, 0.95f),
            min: 0.05f,
            max: 0.95f,
            interval: 0.05f);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("最大精明浮动", "Max Savvy Spread", config),
            tooltip: () => T("精明度 0 时的心理价浮动率 (0 - 1)", "Price spread for unsavvy NPCs (0 - 1).", config),
            getValue: () => (float)config.Haggle.MaxSavvySpread,
            setValue: value => config.Haggle.MaxSavvySpread = Math.Clamp(value, config.Haggle.MinSavvySpread, 1f),
            min: 0f,
            max: 1f,
            interval: 0.05f);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("最小精明浮动", "Min Savvy Spread", config),
            tooltip: () => T("精明度 1 时的心理价浮动率 (0 - 1)", "Price spread for savvy NPCs (0 - 1).", config),
            getValue: () => (float)config.Haggle.MinSavvySpread,
            setValue: value => config.Haggle.MinSavvySpread = Math.Clamp(value, 0f, config.Haggle.MaxSavvySpread),
            min: 0f,
            max: 1f,
            interval: 0.05f);

        // ─── Section 9: Transcript ───────────────────────────────────────
        gmcm.AddSectionTitle(manifest, () => SectionTranscript(config));

        gmcm.AddTextOption(
            manifest,
            name: () => T("留痕根目录", "Transcript Root Directory", config),
            tooltip: () => T("JSONL 留痕文件保存的相对目录", "Relative directory where JSONL transcript files are saved.", config),
            getValue: () => config.Transcript.RootDir ?? string.Empty,
            setValue: value => config.Transcript.RootDir = value ?? string.Empty);

        // ─── Section 10: Emergency & State Machine ───────────────────────
        gmcm.AddSectionTitle(manifest, () => SectionEmergency(config));

        gmcm.AddNumberOption(
            manifest,
            name: () => T("紧急生命阈值", "Emergency Health Threshold", config),
            tooltip: () => T("玩家生命值比例低于该值触发紧急状态 (0.05 - 1.0)",
                "Player health ratio below which emergency state triggers (0.05 - 1.0).", config),
            getValue: () => config.EmergencyHealthThreshold,
            setValue: value => config.EmergencyHealthThreshold = Math.Clamp(value, 0.05f, 1f),
            min: 0.05f,
            max: 1f,
            interval: 0.05f);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("紧急怪物距离（格）", "Emergency Monster Distance (tiles)", config),
            tooltip: () => T("怪物距离玩家近于此值时触发紧急状态 (1 - 30)",
                "Monster distance to player under which emergency state triggers (1 - 30).", config),
            getValue: () => config.EmergencyMonsterDistance,
            setValue: value => config.EmergencyMonsterDistance = Math.Clamp(value, 1f, 30f),
            min: 1f,
            max: 30f,
            interval: 0.5f);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("玩家受胁距离（格）", "Player In Danger Distance (tiles)", config),
            tooltip: () => T("NPC 距离玩家在此范围内视为「受胁」状态 (1 - 30)",
                "Distance within which NPC is considered 'in danger' near the player (1 - 30).", config),
            getValue: () => config.PlayerInDangerDistance,
            setValue: value => config.PlayerInDangerDistance = Math.Clamp(value, 1f, 30f),
            min: 1f,
            max: 30f,
            interval: 0.5f);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("任务完成冷却（ticks）", "Task Complete Cooldown (ticks)", config),
            tooltip: () => T("任务完成后到下次决策的冷却 ticks (0 - 10000)",
                "Cooldown ticks between task completion and next decision (0 - 10000).", config),
            getValue: () => config.TaskCompleteDecisionCooldownTicks,
            setValue: value => config.TaskCompleteDecisionCooldownTicks = Math.Clamp(value, 0, 10000),
            min: 0,
            max: 10000,
            interval: 50);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("状态拒绝冷却（ticks）", "State Rejection Cooldown (ticks)", config),
            tooltip: () => T("状态被拒绝后到下次决策的冷却 ticks (0 - 10000)",
                "Cooldown ticks after a state is rejected (0 - 10000).", config),
            getValue: () => config.StateRejectionCooldownTicks,
            setValue: value => config.StateRejectionCooldownTicks = Math.Clamp(value, 0, 10000),
            min: 0,
            max: 10000,
            interval: 30);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("最大状态拒绝次数", "Max State Rejections", config),
            tooltip: () => T("连续拒绝状态多少次后强制释放 (0 - 10)", "Consecutive state rejections before forced release (0 - 10).",
                config),
            getValue: () => config.MaxStateRejections,
            setValue: value => config.MaxStateRejections = Math.Clamp(value, 0, 10),
            min: 0,
            max: 10,
            interval: 1);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("跨地图跳过距离（格）", "Departure Skip Distance (tiles)", config),
            tooltip: () => T("跨地图距离低于此值跳过出发延迟 (1 - 50)",
                "Cross-map distance below which the departure delay is skipped (1 - 50).", config),
            getValue: () => config.DepartureSkipDistance,
            setValue: value => config.DepartureSkipDistance = Math.Clamp(value, 1f, 50f),
            min: 1f,
            max: 50f,
            interval: 1f);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("战斗退出冷却（ticks）", "Fight Exit Cooldown (ticks)", config),
            tooltip: () => T("战斗处理器退出后阻止再次进入 FIGHT 的 ticks (0 - 10000)",
                "Ticks to block re-entry into FIGHT after exiting (0 - 10000).", config),
            getValue: () => config.FightExitCooldownTicks,
            setValue: value => config.FightExitCooldownTicks = Math.Clamp(value, 0, 10000),
            min: 0,
            max: 10000,
            interval: 30);

        // ─── Section 11: Emotion & Health ────────────────────────────────
        gmcm.AddSectionTitle(manifest, () => SectionEmotion(config));

        gmcm.AddNumberOption(
            manifest,
            name: () => T("情绪冷却（ticks）", "Emotion Cooldown (ticks)", config),
            tooltip: () => T("同一 NPC 相同情绪类型变化的最小间隔 (0 - 10000)",
                "Minimum ticks between same-type emotion changes for an NPC (0 - 10000).", config),
            getValue: () => config.EmotionCooldownTicks,
            setValue: value => config.EmotionCooldownTicks = Math.Clamp(value, 0, 10000),
            min: 0,
            max: 10000,
            interval: 30);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("情绪去重阈值", "Emotion Dedup Threshold", config),
            tooltip: () => T("新情绪与上次主类相似度阈值 (0 - 1)，高于则跳过",
                "Similarity threshold (0 - 1) above which a new emotion is skipped as duplicate.", config),
            getValue: () => config.EmotionDedupThreshold,
            setValue: value => config.EmotionDedupThreshold = Math.Clamp(value, 0f, 1f),
            min: 0f,
            max: 1f,
            interval: 0.05f);

        gmcm.AddNumberOption(
            manifest,
            name: () => T("默认最大生命", "Default Max Health", config),
            tooltip: () => T("NPC 的默认最大生命值 (1 - 1000)", "Default max health for NPCs (1 - 1000).", config),
            getValue: () => config.DefaultMaxHealth,
            setValue: value => config.DefaultMaxHealth = Math.Clamp(value, 1, 1000),
            min: 1,
            max: 1000,
            interval: 10);

        // ─── Section 12: Debug ───────────────────────────────────────────
        gmcm.AddSectionTitle(manifest, () => SectionDebug(config));

        gmcm.AddBoolOption(
            manifest,
            name: () => T("调试日志", "Debug Log Enabled", config),
            tooltip: () => T("输出详细调试日志，可能影响性能", "Emit verbose debug logs. May impact performance.", config),
            getValue: () => config.DebugLogEnabled,
            setValue: value => config.DebugLogEnabled = value);

        gmcm.AddBoolOption(
            manifest,
            name: () => T("开发者模式", "Developer Mode", config),
            tooltip: () => T("启用开发者专用的调试命令与诊断功能", "Enable developer-only debug commands and diagnostics.", config),
            getValue: () => config.DevMode,
            setValue: value => config.DevMode = value);

        gmcm.AddTextOption(
            manifest,
            name: () => T("自定义系统提示", "Custom System Prompt", config),
            tooltip: () => T("覆盖默认系统提示；留空表示使用内置模板",
                "Override the default system prompt. Empty = use built-in template.", config),
            getValue: () => config.CustomSystemPrompt ?? string.Empty,
            setValue: value => config.CustomSystemPrompt = value ?? string.Empty);

        gmcm.AddParagraph(manifest, () => NoteMinStateDuration(config));
    }

    /// <summary>
    ///     为一个角色（导演/主角/普通 NPC）注册 8 个 string 字段的 GMCM UI：
    ///     主 Provider/ApiKey/Model/BaseUrl + 备 Provider/ApiKey/Model/BaseUrl。
    ///     字段名通过反射拼接 prefix+后缀 取，避免重复闭包。
    /// </summary>
    /// <param name="gmcm">GMCM API 实例。</param>
    /// <param name="manifest">Mod manifest。</param>
    /// <param name="config">当前配置实例。</param>
    /// <param name="prefix">字段前缀（Director/Protagonist/Npc）。</param>
    /// <param name="labelCn">UI 中文标签前缀。</param>
    /// <param name="labelEn">UI 英文标签前缀。</param>
    private static void AddProviderSlotUI(
        IGenericModConfigMenuApi gmcm, IManifest manifest, ModConfig config,
        string prefix, string labelCn, string labelEn)
    {
        // 主 Provider
        gmcm.AddTextOption(
            manifest,
            name: () => T($"{labelCn} 主 Provider", $"{labelEn} Primary Provider", config),
            tooltip: () => T("LLM 提供商", "LLM provider", config),
            getValue: () => (string)GetConfigField(config, prefix + "PrimaryProvider"),
            setValue: v => SetConfigField(config, prefix + "PrimaryProvider", v),
            allowedValues: s_providerPresets);

        gmcm.AddTextOption(
            manifest,
            name: () => T($"{labelCn} 主 API Key", $"{labelEn} Primary API Key", config),
            tooltip: () => T("API 密钥", "API key", config),
            getValue: () => (string)GetConfigField(config, prefix + "PrimaryApiKey"),
            setValue: v => SetConfigField(config, prefix + "PrimaryApiKey", v));

        gmcm.AddTextOption(
            manifest,
            name: () => T($"{labelCn} 主 Model", $"{labelEn} Primary Model", config),
            tooltip: () => T("模型标识", "Model identifier", config),
            getValue: () => (string)GetConfigField(config, prefix + "PrimaryModel"),
            setValue: v => SetConfigField(config, prefix + "PrimaryModel", v));

        gmcm.AddTextOption(
            manifest,
            name: () => T($"{labelCn} 主 Base URL", $"{labelEn} Primary Base URL", config),
            tooltip: () => T("API 基础 URL", "API base URL", config),
            getValue: () => (string)GetConfigField(config, prefix + "PrimaryBaseUrl"),
            setValue: v => SetConfigField(config, prefix + "PrimaryBaseUrl", v));

        // 备 Provider（允许空串=无回退，用 s_providerPresetsWithEmpty）
        gmcm.AddTextOption(
            manifest,
            name: () => T($"{labelCn} 备 Provider", $"{labelEn} Fallback Provider", config),
            tooltip: () => T("留空=无回退", "Empty = no fallback", config),
            getValue: () => (string)GetConfigField(config, prefix + "FallbackProvider"),
            setValue: v => SetConfigField(config, prefix + "FallbackProvider", v),
            allowedValues: s_providerPresetsWithEmpty);

        gmcm.AddTextOption(
            manifest,
            name: () => T($"{labelCn} 备 API Key", $"{labelEn} Fallback API Key", config),
            tooltip: () => T("备 provider 密钥", "Fallback API key", config),
            getValue: () => (string)GetConfigField(config, prefix + "FallbackApiKey"),
            setValue: v => SetConfigField(config, prefix + "FallbackApiKey", v));

        gmcm.AddTextOption(
            manifest,
            name: () => T($"{labelCn} 备 Model", $"{labelEn} Fallback Model", config),
            tooltip: () => T("备模型标识", "Fallback model identifier", config),
            getValue: () => (string)GetConfigField(config, prefix + "FallbackModel"),
            setValue: v => SetConfigField(config, prefix + "FallbackModel", v));

        gmcm.AddTextOption(
            manifest,
            name: () => T($"{labelCn} 备 Base URL", $"{labelEn} Fallback Base URL", config),
            tooltip: () => T("备 API 基础 URL", "Fallback API base URL", config),
            getValue: () => (string)GetConfigField(config, prefix + "FallbackBaseUrl"),
            setValue: v => SetConfigField(config, prefix + "FallbackBaseUrl", v));
    }

    /// <summary>反射读取 ModConfig 的 string 属性，找不到时返回空串。</summary>
    /// <param name="config">配置实例。</param>
    /// <param name="fieldName">属性名（如 DirectorPrimaryProvider）。</param>
    /// <returns>属性值；属性不存在或为 null 时返回 <see cref="string.Empty" />。</returns>
    private static object GetConfigField(ModConfig config, string fieldName)
    {
        var prop = typeof(ModConfig).GetProperty(fieldName);
        return prop?.GetValue(config) ?? string.Empty;
    }

    /// <summary>反射写入 ModConfig 的 string 属性，找不到时静默忽略。</summary>
    /// <param name="config">配置实例。</param>
    /// <param name="fieldName">属性名。</param>
    /// <param name="value">要写入的值。</param>
    private static void SetConfigField(ModConfig config, string fieldName, object value)
    {
        var prop = typeof(ModConfig).GetProperty(fieldName);
        prop?.SetValue(config, value);
    }

    private static void CopyConfig(ModConfig source, ModConfig target)
    {
        target.Provider = source.Provider;
        target.LlmApiKey = source.LlmApiKey;
        target.ServerAddress = source.ServerAddress;
        target.LlmModel = source.LlmModel;
        target.MinAgentNpcs = source.MinAgentNpcs;
        target.NormalAgentNpcs = source.NormalAgentNpcs;
        target.MaxAgentNpcs = source.MaxAgentNpcs;
        target.EnableFriendshipChanges = source.EnableFriendshipChanges;
        target.EnableGifts = source.EnableGifts;
        target.TokenBudget = source.TokenBudget;
        target.DebugMode = source.DebugMode;
        target.LLMTimeoutSeconds = source.LLMTimeoutSeconds;
        target.MaxRetries = source.MaxRetries;
        target.DecisionIntervalMinutes = source.DecisionIntervalMinutes;
        target.CircuitBreakerThreshold = source.CircuitBreakerThreshold;
        target.EnableCombatAssist = source.EnableCombatAssist;
        target.EnableFarmingAssist = source.EnableFarmingAssist;
        target.EnableMiningAssist = source.EnableMiningAssist;
        target.EnableForagingAssist = source.EnableForagingAssist;
        target.FollowDistance = source.FollowDistance;
        target.DynamicSpeedEnabled = source.DynamicSpeedEnabled;
        target.DynamicSpeedFarThreshold = source.DynamicSpeedFarThreshold;
        target.DynamicSpeedMidThreshold = source.DynamicSpeedMidThreshold;
        target.DynamicSpeedNearMultiplier = source.DynamicSpeedNearMultiplier;
        target.DynamicSpeedMidMultiplier = source.DynamicSpeedMidMultiplier;
        target.DynamicSpeedFarMultiplier = source.DynamicSpeedFarMultiplier;
        target.FallbackAIMixProbability = source.FallbackAIMixProbability;
        target.FallbackLiveGenerationProbability = source.FallbackLiveGenerationProbability;
        target.Language = source.Language;
        target.CustomSystemPrompt = source.CustomSystemPrompt;
        target.EnableFirstClickVanilla = source.EnableFirstClickVanilla;
        target.AIDailyTopicCount = source.AIDailyTopicCount;
        target.MaxConsecutiveIdleBeforeRelease = source.MaxConsecutiveIdleBeforeRelease;
        target.TaskCompleteDecisionCooldownTicks = source.TaskCompleteDecisionCooldownTicks;
        target.StateRejectionCooldownTicks = source.StateRejectionCooldownTicks;
        target.MaxStateRejections = source.MaxStateRejections;
        target.EmergencyHealthThreshold = source.EmergencyHealthThreshold;
        target.EmergencyMonsterDistance = source.EmergencyMonsterDistance;
        target.PlayerInDangerDistance = source.PlayerInDangerDistance;
        target.EmotionCooldownTicks = source.EmotionCooldownTicks;
        target.EmotionDedupThreshold = source.EmotionDedupThreshold;
        target.FightExitCooldownTicks = source.FightExitCooldownTicks;
        target.TalkRejectionFollowThreshold = source.TalkRejectionFollowThreshold;
        target.DepartureSkipDistance = source.DepartureSkipDistance;
        target.MaxFriendshipChangePerInteraction = source.MaxFriendshipChangePerInteraction;
        target.DefaultMaxHealth = source.DefaultMaxHealth;
        target.PauseAllDialogue = source.PauseAllDialogue;
        target.DialogueCooldownMs = source.DialogueCooldownMs;
        target.DevMode = source.DevMode;
        target.UseAgentServer = source.UseAgentServer;
        target.AgentServerHost = source.AgentServerHost;
        target.AutoStartServer = source.AutoStartServer;
        target.ServerExecutablePath = source.ServerExecutablePath;
        target.ServerDirectory = source.ServerDirectory;
        target.ServerPort = source.ServerPort;
        target.ServerConsoleWindow = source.ServerConsoleWindow;
        target.ServerStartupTimeoutSeconds = source.ServerStartupTimeoutSeconds;
        target.ServerMaxRestartAttempts = source.ServerMaxRestartAttempts;

        // E2-2 聊天栏路由
        target.ChatBarRoutingEnabled = source.ChatBarRoutingEnabled;
        target.ChatSessionTimeoutSeconds = source.ChatSessionTimeoutSeconds;
        target.ChatRecentInteractionWindowSeconds = source.ChatRecentInteractionWindowSeconds;
        target.ChatNearbyDistanceTiles = source.ChatNearbyDistanceTiles;
        target.ChatGroupResponseMax = source.ChatGroupResponseMax;

        // E5-3 主动发言额度
        target.ProactiveSpeechDailyLimit = source.ProactiveSpeechDailyLimit;
        target.ProactiveSpeechCooldownMinutes = source.ProactiveSpeechCooldownMinutes;

        // E3-1 / E3-2 经济与还价
        target.Economy.Enabled = source.Economy.Enabled;
        target.Economy.DataFile = source.Economy.DataFile;
        target.Haggle.Enabled = source.Haggle.Enabled;
        target.Haggle.MaxRounds = source.Haggle.MaxRounds;
        target.Haggle.HostileThreshold = source.Haggle.HostileThreshold;
        target.Haggle.MaxSavvySpread = source.Haggle.MaxSavvySpread;
        target.Haggle.MinSavvySpread = source.Haggle.MinSavvySpread;
        target.Haggle.MarkdownRatios =
            new List<double>(source.Haggle.MarkdownRatios ?? new List<double> { 0.5, 0.25, 0.125 });

        // E1-1 留痕
        target.Transcript.RootDir = source.Transcript.RootDir;

        // GMCM-managed fields
        target.Enabled = source.Enabled;
        target.LlmProvider = source.LlmProvider;
        target.ModelName = source.ModelName;
#pragma warning disable CS0618
        target.WebSocketUrl = source.WebSocketUrl;
#pragma warning restore CS0618
        target.Temperature = source.Temperature;
        target.IdleThresholdSeconds = source.IdleThresholdSeconds;
        target.GiftCooldownMs = source.GiftCooldownMs;
        target.ChatBubbleEnabled = source.ChatBubbleEnabled;
        target.LongTextIntervalMs = source.LongTextIntervalMs;
        target.DebugLogEnabled = source.DebugLogEnabled;
        target.NonAgentAIChatEnabled = source.NonAgentAIChatEnabled;

        // 多 Provider 模式（Task 6.4）
        target.MultiProviderEnabled = source.MultiProviderEnabled;
        target.DirectorPrimaryProvider = source.DirectorPrimaryProvider;
        target.DirectorPrimaryApiKey = source.DirectorPrimaryApiKey;
        target.DirectorPrimaryModel = source.DirectorPrimaryModel;
        target.DirectorPrimaryBaseUrl = source.DirectorPrimaryBaseUrl;
        target.DirectorFallbackProvider = source.DirectorFallbackProvider;
        target.DirectorFallbackApiKey = source.DirectorFallbackApiKey;
        target.DirectorFallbackModel = source.DirectorFallbackModel;
        target.DirectorFallbackBaseUrl = source.DirectorFallbackBaseUrl;
        target.ProtagonistPrimaryProvider = source.ProtagonistPrimaryProvider;
        target.ProtagonistPrimaryApiKey = source.ProtagonistPrimaryApiKey;
        target.ProtagonistPrimaryModel = source.ProtagonistPrimaryModel;
        target.ProtagonistPrimaryBaseUrl = source.ProtagonistPrimaryBaseUrl;
        target.ProtagonistFallbackProvider = source.ProtagonistFallbackProvider;
        target.ProtagonistFallbackApiKey = source.ProtagonistFallbackApiKey;
        target.ProtagonistFallbackModel = source.ProtagonistFallbackModel;
        target.ProtagonistFallbackBaseUrl = source.ProtagonistFallbackBaseUrl;
        target.NpcPrimaryProvider = source.NpcPrimaryProvider;
        target.NpcPrimaryApiKey = source.NpcPrimaryApiKey;
        target.NpcPrimaryModel = source.NpcPrimaryModel;
        target.NpcPrimaryBaseUrl = source.NpcPrimaryBaseUrl;
        target.NpcFallbackProvider = source.NpcFallbackProvider;
        target.NpcFallbackApiKey = source.NpcFallbackApiKey;
        target.NpcFallbackModel = source.NpcFallbackModel;
        target.NpcFallbackBaseUrl = source.NpcFallbackBaseUrl;
        target.EnableProtagonistMapping = source.EnableProtagonistMapping;
        target.ProtagonistNpcs = source.ProtagonistNpcs;

        // 功能开关
        target.EnableTrade = source.EnableTrade;
        target.EnableHire = source.EnableHire;
        target.EnableDirector = source.EnableDirector;
        target.EnableProactiveSpeech = source.EnableProactiveSpeech;
        target.EnableInfiniteDialogue = source.EnableInfiniteDialogue;

        // 概率
        target.ProactiveSpeechProbability = source.ProactiveSpeechProbability;
        target.ProactiveGiftProbability = source.ProactiveGiftProbability;
        target.ProactiveFollowProbability = source.ProactiveFollowProbability;
        target.ProactiveTradeProbability = source.ProactiveTradeProbability;

        // 冷却秒
        target.DialogueCooldownSeconds = source.DialogueCooldownSeconds;
        target.GiftCooldownSeconds = source.GiftCooldownSeconds;
    }

    internal static void NotifyConfigChanged(ModConfig cfg) => OnConfigChanged?.Invoke(cfg);
}