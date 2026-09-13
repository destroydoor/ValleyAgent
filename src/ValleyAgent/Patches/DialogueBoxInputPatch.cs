using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using ValleyAgent.Agents;
using ValleyAgent.AI;
using ValleyAgent.Chat;
using ValleyAgent.Infrastructure;
using ValleyAgent.Multiplayer.Transports;
using ValleyAgent.RAG;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using ValleyAgent.WebSocket;

namespace ValleyAgent.Patches;

/// <summary>
///     在原生 DialogueBox 上为 NPC 对话增加文字输入能力（Agent 与非 Agent 村民通用）。
///     UI 原则：全部使用原版渲染路径——
///     - LLM 回复通过 setNewDialogue + Game1.drawDialogue 交给原版 DialogueBox 显示
///     （原版字体、原版打字机效果、原版排版与缩放）；
///     - 输入框用 IClickableMenu.drawTextureBox 绘制原版木框面板，置于对话框底部，
///     不超出对话框边界，文字使用原版 smallFont 与深棕色；
///     - 文字输入走 Game1.keyboardDispatcher（窗口 TextInput 事件），原生支持 IME 中文组字；
///     - LLM 回复经主线程队列渲染（后台线程只入队），避免跨线程操作 Game1 UI。
/// </summary>
[HarmonyPatch(typeof(DialogueBox))]
public static class DialogueBoxInputPatch
{
    // ESC 关闭时的最大模拟点击次数（补完打字机 + 翻完多页 + 关闭）
    private const int MaxEscCloseClicks = 8;
    private static string _activeAgentNpc = "";
    private static readonly StringBuilder _inputText = new();
    private static volatile bool _isWaitingForResponse;
    private static IMonitor? _monitor;
    private static AgentService? _agentService;
    private static IAgentServerProvider? _agentServerProvider;
    private static CommandExecutor? _commandExecutor;
    private static IDialogueTransport? _dialogueTransport;
    private static ValleyTalkBioLoader? _bioLoader;
    private static Action<AgentInstance>? _wireAgentEvents;
    private static InputButton[]? _savedChatButtons;
    private static IKeyboardSubscriber? _savedKeyboardSubscriber;
    private static readonly DialogueInputSubscriber s_inputSubscriber = new();
    private static readonly ConcurrentQueue<PendingReply> _pendingReplies = new();

    /// <summary>需要"身体"才能执行的动作：非 Agent NPC 收到这类动作时触发动态升级。</summary>
    private static readonly HashSet<string> s_promotionTriggerTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "set_state", "follow", "move_to", "harvest", "water", "attack", "mine", "forage",
        "eat_food", "drop_item", "use_item"
    };

    public static void Initialize(
        IMonitor monitor,
        AgentService? agentService,
        IAgentServerProvider? provider,
        CommandExecutor? commandExecutor,
        ValleyTalkBioLoader? bioLoader = null,
        Action<AgentInstance>? wireAgentEvents = null)
    {
        _monitor = monitor;
        _agentService = agentService;
        _agentServerProvider = provider;
        _commandExecutor = commandExecutor;
        _bioLoader = bioLoader;
        _wireAgentEvents = wireAgentEvents;
    }

    /// <summary>
    ///     注入对话传输层。设置后 SubmitInput 将通过该传输层发起对话请求，
    ///     否则回退到直接调用本地 IAgentServerProvider（Host 模式默认路径）。
    ///     由 ModEntry 在 ThinClient 模式判定后调用：远程 Farmhand 端注入 FarmhandDialogueTransport。
    ///     Host 模式不注入（保持 null），直接走 IAgentServerProvider 路径。
    /// </summary>
    public static void SetDialogueTransport(IDialogueTransport? transport) => _dialogueTransport = transport;

    /// <summary>
    ///     房客侧对话通道存在性：房客没有 AgentServerProvider（恒 null），
    ///     CloseDialoguePostfix 的放行判定要按 transport 判——否则房客播完原版台词后
    ///     AI 输入框永不弹出。主机形态恒为 false（provider 判定足够）。
    /// </summary>
    internal static bool HasDialogueTransport => _dialogueTransport != null;

    /// <summary>
    ///     身体类工具判定（s_promotionTriggerTools 清单的单一事实源只读入口）：
    ///     非身体 NPC 的回复动作命中此类工具时需先建身体才能执行。
    ///     HostRequestHandlers 中继回包的动作分发复用同一清单，不复制列表。
    /// </summary>
    internal static bool IsPromotionTriggerTool(string tool)
        => !string.IsNullOrEmpty(tool) && s_promotionTriggerTools.Contains(tool);

    public static void SetActiveAgentNpc(string npcName)
    {
        _activeAgentNpc = npcName;
        _inputText.Clear();
        _isWaitingForResponse = false;

        // E2-2: 对话框对话开始即记录一次"与玩家交互"，供聊天栏路由第 4 层
        // "最近交互 + 距离"消费（30 秒内交互过且附近优先）。
        ChatBarRouter.RecordInteraction(npcName);

        // 对话期间屏蔽 T 键打开原版聊天框：聊天框激活时 Game1.GetKeyboardState
        // 会返回空键盘状态，玩家输入会在第一个 't' 处确定性中断。
        // 直接临时清空 chatButton 绑定（比拦截 ChatBox.activate 更彻底，
        // 同时消除 GetKeyboardState 的吞键分支），对话结束时恢复。
        _savedChatButtons ??= Game1.options.chatButton;
        Game1.options.chatButton = new InputButton[0];

        // 接管键盘文本输入（含 IME 中文组字）：与原版 ChatBox 同一机制。
        _savedKeyboardSubscriber = Game1.keyboardDispatcher.Subscriber;
        Game1.keyboardDispatcher.Subscriber = s_inputSubscriber;
    }

    public static string? GetActiveAgentNpc() => string.IsNullOrEmpty(_activeAgentNpc) ? null : _activeAgentNpc;

    public static void ClearActiveAgentNpc()
    {
        _activeAgentNpc = "";
        _inputText.Clear();
        _isWaitingForResponse = false;
        if (_savedChatButtons != null)
        {
            Game1.options.chatButton = _savedChatButtons;
            _savedChatButtons = null;
        }

        if (Game1.keyboardDispatcher != null
            && ReferenceEquals(Game1.keyboardDispatcher.Subscriber, s_inputSubscriber))
        {
            Game1.keyboardDispatcher.Subscriber = _savedKeyboardSubscriber;
        }

        _savedKeyboardSubscriber = null;
    }

    private static void EndConversation()
    {
        var npcName = _activeAgentNpc;
        ClearActiveAgentNpc();
        NPCDialoguePatch.EndTopicConversationExternal(npcName);
    }

    // 双保险：即使 chatButton 绑定恢复时机异常，也不在 Agent 对话期间打开原版聊天框。
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ChatBox), nameof(ChatBox.activate))]
    public static bool ChatBoxActivatePrefix() => string.IsNullOrEmpty(_activeAgentNpc);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(DialogueBox.draw))]
    public static void DrawPostfix(DialogueBox __instance, SpriteBatch b)
    {
        if (string.IsNullOrEmpty(_activeAgentNpc))
        {
            return;
        }

        if (Game1.activeClickableMenu != __instance)
        {
            return;
        }

        // 对话框打开/关闭的缩放过渡期间不画输入框：原版在 transitioning 时
        // 同样只画过渡中的框体、不画文字内容。输入框与对话框内容同步出现/消失，
        // 二者在视觉上是完全绑定的一体。
        if (__instance.transitioning)
        {
            return;
        }

        DrawInputBox(b, __instance);

        // DialogueBox.draw 末尾自带 drawMouse（光标先于本 Postfix 绘制），
        // 输入框会盖住光标。重绘光标使其保持在输入框上层。
        __instance.drawMouse(b);
    }

    // 必须是 Prefix 而不是 Postfix：原版 DialogueBox.receiveKeyPress 会把
    // actionButton（默认 X）和 menuButton（默认 E/Escape，SnappyMenus 下）
    // 当作“推进/关闭对话”处理。玩家输入字母 e/x 时对话框会被原版直接关掉。
    // 这里在 Agent 对话期间接管所有按键。
    //
    // 注意：字母/数字/符号文本不再经由此处处理——由 keyboardDispatcher 的
    // TextInput 事件（DialogueInputSubscriber）注入，原生支持 IME。
    // 本前缀只负责对话控制键：Enter（发送/翻页）与 Escape（结束）。
    [HarmonyPrefix]
    [HarmonyPatch(nameof(DialogueBox.receiveKeyPress))]
    public static bool ReceiveKeyPressPrefix(DialogueBox __instance, Keys key)
    {
        if (string.IsNullOrEmpty(_activeAgentNpc))
        {
            return true;
        }

        if (Game1.activeClickableMenu != __instance)
        {
            return true;
        }

        // ESC：结束对话并关闭
        if (key == Keys.Escape)
        {
            EndConversation();
            // 不能依赖原版 receiveKeyPress 关闭：SnappyMenus 关闭时 ESC 在原版路径里
            // 对角色对话框什么都不做。marker 已清除，receiveLeftClick 前缀会放行，
            // 直接用它走原版关闭流程。
            // 注意要循环点击：打字机未播完时第一次点击只是“补完文字”，
            // 多页回复每次点击也只翻一页——单次点击会导致对话框残留
            // （“ESC 只退出了输入框”）。
            for (var i = 0; i < MaxEscCloseClicks; i++)
            {
                if (Game1.activeClickableMenu != __instance || !Game1.dialogueUp)
                {
                    break;
                }

                __instance.receiveLeftClick(0, 0, false);
            }

            return false;
        }

        // Enter：有输入则发送；空输入则作为“跳过打字/继续/关闭”（原版左键语义）
        if (key == Keys.Enter)
        {
            if (_isWaitingForResponse)
            {
                return false;
            }

            if (_inputText.Length > 0)
            {
                SubmitInput();
                return false;
            }

            __instance.receiveLeftClick(0, 0, false);
            if (!Game1.dialogueUp || Game1.activeClickableMenu != __instance)
            {
                EndConversation();
            }

            return false;
        }

        // 其余按键全部吞掉（阻断原版推进/关闭逻辑），文本由 IME subscriber 注入
        return false;
    }

    // 对话期间左键点击会触发原版 receiveLeftClick → 提前关闭对话框（误触）。
    // Agent 对话激活时吞掉左键；翻页/关闭用 Enter（空输入）或 ESC。
    [HarmonyPrefix]
    [HarmonyPatch(nameof(DialogueBox.receiveLeftClick))]
    public static bool ReceiveLeftClickPrefix(DialogueBox __instance)
    {
        if (string.IsNullOrEmpty(_activeAgentNpc))
        {
            return true;
        }

        if (Game1.activeClickableMenu != __instance)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    ///     以玩家口吻程序化注入一条消息（如送礼/交易菜单的"交易"选项注入交易意图），
    ///     走与 SubmitInput 完全相同的发送管道（计数、等待标记、transport/provider 路由）。
    ///     仅在存在活跃对话时生效——调用方须先开框（SetActiveAgentNpc）。
    /// </summary>
    public static void QueueExternalInput(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(_activeAgentNpc))
        {
            return;
        }

        // 正在等 LLM 回复时不叠加注入，避免与玩家手输/上一轮回复乱序
        if (_isWaitingForResponse)
        {
            _monitor?.Log("[Chat] QueueExternalInput skipped: waiting for response", LogLevel.Debug);
            return;
        }

        _inputText.Clear();
        _inputText.Append(text);
        SubmitInput();
    }

    private static void SubmitInput()
    {
        var text = _inputText.ToString().Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (_agentServerProvider == null && _dialogueTransport == null)
        {
            return;
        }

        var npcName = _activeAgentNpc;
        NPCDialoguePatch.IncrementConversationCount(npcName);
        _isWaitingForResponse = true;
        _inputText.Clear();
        _monitor?.Log($"[Chat] Player input to {npcName}: {text}", LogLevel.Debug);

        // 快照与玩家 ID 采集留在 Harmony Prefix（主线程）：WorldSnapshotBuilder.Build/GetNpcState
        // 读 Game1/NPC 状态，不得随 Task.Run 落到后台线程（2026-09-09 缺口③b，与 SendMessage 同纪律）。
        // 后台 lambda 只做网络等待与回包入队。
        var worldSnapshot = WorldSnapshotBuilder.Build(npcName, GetNpcState(npcName));
        var playerId = Game1.player.UniqueMultiplayerID.ToString();

        _ = Task.Run(async () =>
        {
            try
            {
                var request = new DialogueRequest(
                    "dialogue",
                    Guid.NewGuid().ToString("N"),
                    npcName,
                    text,
                    worldSnapshot,
                    playerId
                );

                DialogueResponse response;
                if (_dialogueTransport != null)
                {
                    response = await _dialogueTransport.SendAsync(npcName, text, worldSnapshot).ConfigureAwait(false);
                }
                else
                {
                    response = await _agentServerProvider!.GenerateDialogueAsync(request).ConfigureAwait(false);
                }

                // 后台线程只入队，UI 渲染由主线程 ProcessPendingReplies 执行
                _pendingReplies.Enqueue(new PendingReply(
                    npcName,
                    response.Speech ?? "...",
                    response.Actions ?? new List<ToolAction>(),
                    response.Fallback == true,
                    response.FallbackReason));
                // 深度告警：主线程泵停摆时回复堆积的早期信号（2026-09-11 生产化仪器）
                QueueTelemetry.WarnIfDeep("dialogue-replies", _pendingReplies.Count, _monitor);
            }
            catch (TimeoutException)
            {
                _pendingReplies.Enqueue(new PendingReply(npcName, $"（{npcName} 在思考...）", new List<ToolAction>()));
                QueueTelemetry.WarnIfDeep("dialogue-replies", _pendingReplies.Count, _monitor);
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[Chat] Dialogue request failed: {ex.Message}", LogLevel.Error);
                _pendingReplies.Enqueue(new PendingReply(npcName, "......", new List<ToolAction>()));
                QueueTelemetry.WarnIfDeep("dialogue-replies", _pendingReplies.Count, _monitor);
            }
        });
    }

    /// <summary>
    ///     主线程处理待渲染的 LLM 回复（每 tick 由 EventHandlerInitializer.OnUpdateTicked 调用）。
    ///     MonoGame/Game1 UI 操作只能在主线程执行，后台线程直接调 drawDialogue 会与渲染竞争。
    /// </summary>
    public static void ProcessPendingReplies()
    {
        while (_pendingReplies.TryDequeue(out var reply))
        {
            _isWaitingForResponse = false;

            // 对话已被玩家关闭（ESC/离开）→ 丢弃迟到回复
            if (string.IsNullOrEmpty(_activeAgentNpc)
                || !_activeAgentNpc.Equals(reply.NpcName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var npc = Game1.getCharacterFromName(reply.NpcName);
            if (npc == null)
            {
                continue;
            }

            // 2026-08-16 决策 #3：降级响应（BUSY 等）→ 灰色系统提示，不弹打字机对话框。
            // 2026-09-13 R3：灰字文案单一来源在 TS（busy=正在和别人交流，LLM 失败=走神/说不出来等），
            // C# 只负责灰字样式不维护话术；fallbackReason 仅用于日志留痕与房客广播透传。
            // 保持 continue：降级消息仍不弹对话框。
            if (reply.Fallback)
            {
                Game1.chatBox?.addMessage(reply.Speech, Color.Gray);
                _monitor?.Log(
                    $"[Chat] {reply.NpcName}: fallback (reason={reply.FallbackReason ?? "unspecified"})",
                    LogLevel.Debug);
                continue;
            }

            // 交给原版渲染：setNewDialogue + drawDialogue 会用原版字体、
            // 原版打字机效果和原版排版显示回复（含换行与缩放）。
            // 对话标记保持激活 → 玩家可继续输入下一轮（多轮对话）。
            npc.setNewDialogue(new StardewValley.Dialogue(npc, null, reply.Speech));
            Game1.drawDialogue(npc);

            DispatchDialogueActions(npc, reply.Actions);

            var preview = reply.Speech.Length > 80 ? reply.Speech.Substring(0, 80) : reply.Speech;
            _monitor?.Log($"[Chat] LLM response for {reply.NpcName}: {preview}", LogLevel.Debug);
        }
    }

    /// <summary>
    ///     分发对话响应附带的动作。非 Agent NPC：安全动作（emote/give_item 等）直接执行；
    ///     需要"身体"的动作（set_state/follow/干活类）先触发动态升级再执行。
    ///     E2-2：internal 供聊天栏路由（ChatBarRouter）复用同一分发逻辑。
    /// </summary>
    internal static void DispatchDialogueActions(NPC npc, IReadOnlyList<ToolAction> actions)
    {
        var isAgent = _agentService?.HasAgent(npc.Name) ?? false;

        foreach (var action in actions)
        {
            var tool = action.Tool ?? "";

            if (!isAgent && s_promotionTriggerTools.Contains(tool))
            {
                if (PromoteToAgent(npc.Name))
                {
                    isAgent = true;
                }
                else
                {
                    _monitor?.Log($"[Chat] {npc.Name}: action '{tool}' skipped — promotion to Agent failed",
                        LogLevel.Debug);
                    continue;
                }
            }

            try
            {
                // set_state 走 CommandExecutor.ExecuteAction 正常路径：
                // ExecuteSetState 真正调用状态机 ForceTransition + 成功时同步 preState，
                // 并通过 SendToolActionResultAsync 回发 action_result 给 TS（C3 反馈环）。
                // 海莉事件根因修复：不再丢弃 TrySetAgentState 返回值，不再 continue 跳过反馈。

                // speak 由对话文本本身承担；wait 在对话语境无意义
                if (tool is "speak" or "wait")
                {
                    continue;
                }

                _commandExecutor?.ExecuteAction(action, npc.Name);
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[Chat] Action {action.Tool} failed: {ex.Message}", LogLevel.Warn);
            }
        }
    }

    /// <summary>
    ///     获取 NPC 当前 AgentState，传给 TS 端让 LLM 感知自身状态。
    ///     非 Agent NPC 返回 "IDLE"。
    ///     E2-2：internal 供聊天栏路由（ChatBarRouter）复用。
    /// </summary>
    internal static string GetNpcState(string npcName)
    {
        if (_agentService != null && _agentService.TryGetAgent(npcName, out var agent) && agent != null)
        {
            return agent.StateMachine.CurrentStateFlag.ToString();
        }

        return "IDLE";
    }

    /// <summary>
    ///     动态分配：把对话中的非 Agent NPC 升级为 Agent。
    ///     满员时 AgentAllocationManager 自动淘汰最低优先级的现有 Agent（记忆保留在服务端记忆文件）。
    ///     E2-2：internal 供聊天栏路由（ChatBarRouter）复用。
    /// </summary>
    internal static bool PromoteToAgent(string npcName)
    {
        if (_agentService == null)
        {
            return false;
        }

        if (_agentService.HasAgent(npcName))
        {
            return true;
        }

        double hearts = 0;
        if (Game1.player.friendshipData.TryGetValue(npcName, out var friendship))
        {
            hearts = friendship.Points / 250.0;
        }

        var conversations = NPCDialoguePatch.GetConversationCount(npcName);

        // 捕捉被自动淘汰的旧 Agent（ForceAllocate 满员时淘汰最低优先级非手动 Agent）
        string? replacedNpc = null;

        void OnDeallocated(object? sender, AgentAllocationEventArgs e)
        {
            replacedNpc = e.NpcName;
        }

        var manager = _agentService.AllocationManager;
        manager.OnAgentDeallocated += OnDeallocated;
        bool allocated;
        try
        {
            // 玩家在对话中显式要求 NPC 执行物理动作（follow/set_state 等）属于玩家意图，
            // 应优先于基于长期 friendship 的自动分配。走 ForceAllocate 而非 TryAllocate：
            // - 释放先前对话留下的 manual override，让出可被替换的槽位
            //   （否则所有槽位都是 manual 时 ForceAllocate 会拒绝）
            // - ForceAllocate 会淘汰最低优先级的非 manual Agent 并把新 NPC 标记为 manual
            // - 写入度量指标供后续 ReevaluateAllocations 使用
            // 2026-08-23 审计 P1：导演 beat 有效期内（KeepUntil 未到期）的 manual override 不释放，
            // 否则下面的 ForceAllocate 可能把进行中的 beat 挤掉（全 kept 时升级失败走日志兜底）。
            var now = DateTime.UtcNow;
            foreach (var existing in manager.GetAllAllocatedAgents())
            {
                if (existing.IsManuallyOverridden
                    && !(existing.KeepUntil.HasValue && now < existing.KeepUntil.Value)
                    && !string.Equals(existing.NpcName, npcName, StringComparison.OrdinalIgnoreCase))
                {
                    _ = manager.ReleaseManualOverride(existing.NpcName);
                }
            }

            allocated = manager.ForceAllocate(npcName);
            if (allocated)
            {
                _ = manager.UpdatePriority(npcName, conversations, 0, hearts);
            }
        }
        finally
        {
            manager.OnAgentDeallocated -= OnDeallocated;
        }

        if (!allocated)
        {
            _monitor?.Log($"[Chat] Promotion failed for {npcName}: ForceAllocate returned false (no replaceable slot)",
                LogLevel.Debug);
            return false;
        }

        if (replacedNpc != null)
        {
            // F5 修复：满员淘汰时先显式 ForceTransition(IDLE, reason="evicted")
            // 让 StateChangedSender 发出带 reason 的 state_changed，TS 端可据此渲染 prompt。
            // 必须在 RemoveAgent 之前调用（RemoveAgent 会从 _agents 字典移除该实例）。
            // 注意：RemoveAgent 内部的 Reset() 会再发一次空 reason 的 state_changed，
            // TS 端 updateActualState 已调整为仅在 reason 非空时更新 lastTransitionReason，
            // 因此 evicted reason 不会被覆盖。
            if (_agentService.TryGetAgent(replacedNpc, out var evictedAgent) && evictedAgent != null)
            {
                _ = evictedAgent.StateMachine.ForceTransition(
                    AgentState.IDLE, true, reason: "evicted");
            }

            // 销毁被淘汰者的 Agent 实例，交还原版日程（淘汰≠删除记忆，服务端记忆文件保留）
            _ = _agentService.RemoveAgent(replacedNpc);
            var oldNpc = Game1.getCharacterFromName(replacedNpc);
            if (oldNpc != null)
            {
                oldNpc.controller = null;
                oldNpc.Halt();
                oldNpc.followSchedule = true;
                oldNpc.ignoreScheduleToday = false;
            }

            // F5: 玩家可见通知 — 聊天栏提示被淘汰的 NPC 已离开
            Game1.chatBox?.addMessage($"{replacedNpc} 告别离开了", Color.White);
            _monitor?.Log($"[Chat] {replacedNpc} deallocated — replaced by {npcName}", LogLevel.Info);
        }

        var agent = _agentService.CreateAgent(npcName);
        if (agent == null)
        {
            _monitor?.Log($"[Chat] Promotion failed for {npcName}: CreateAgent returned null", LogLevel.Warn);
            return false;
        }

        _bioLoader?.InjectBio(agent.Brain);
        _wireAgentEvents?.Invoke(agent);
        _monitor?.Log(
            $"[Chat] {npcName} promoted to Agent via dialogue (conversations={conversations}, hearts={hearts:F1})",
            LogLevel.Info);
        return true;
    }

    /// <summary>
    ///     在原生对话框底部绘制原版风格输入条：木框面板 + 深色小字，
    ///     宽度与对话框一致（含边距），不超出对话框边界。
    /// </summary>
    private static void DrawInputBox(SpriteBatch b, DialogueBox box)
    {
        // 注意：角色 DialogueBox 的真实位置存在私有字段 x/y 里（构造器：
        // x = 水平居中, y = uiViewport.Height - height - 64），
        // IClickableMenu 的 xPositionOnScreen/yPositionOnScreen 保持 0，不能用。
        // 这里按原版构造器同样的公式计算位置。
        var boxX = (Game1.uiViewport.Width - box.width) / 2;
        var boxY = Game1.uiViewport.Height - box.height - 64;

        // 输入条放在对话框文本区底部，与右下角“继续”图标（转动的小 X）平齐：
        // 图标位置 = (boxX + width - 40 - 492, boxY + height - 44)，绘制尺寸 44x48。
        // 输入条高度比图标略高（64），右端留到图标前。
        const int height = 64;
        var x = boxX + 24;
        var w = box.width - 492 - 40 - 48; // 到图标前，留出间距
        var y = boxY + box.height - height - 4;

        IClickableMenu.drawTextureBox(b, x, y, w, height, Color.White);

        string displayText;
        Color color;
        if (_isWaitingForResponse)
        {
            var dots = (int)((Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0) * 2 % 4);
            displayText = "等待回复" + new string('.', dots);
            color = Color.Gray;
        }
        else if (_inputText.Length == 0)
        {
            displayText = "输入消息后按 Enter 发送，ESC 关闭";
            color = Color.Gray;
        }
        else
        {
            displayText = _inputText.ToString();
            color = Game1.textColor;
        }

        var textY = y + (height - Game1.smallFont.MeasureString("A").Y) / 2f;
        b.DrawString(Game1.smallFont, displayText, new Vector2(x + 16, textY), color);

        // 光标
        if (!_isWaitingForResponse && _inputText.Length > 0
                                   && Game1.currentGameTime != null
                                   && Game1.currentGameTime.TotalGameTime.Milliseconds % 1000 < 500)
        {
            var textSize = Game1.smallFont.MeasureString(_inputText.ToString());
            b.DrawString(Game1.smallFont, "|", new Vector2(x + 16 + textSize.X, textY), Game1.textColor);
        }
    }

    /// <summary>待主线程渲染的 LLM 回复。</summary>
    private sealed class PendingReply
    {
        public PendingReply(string npcName, string speech, IReadOnlyList<ToolAction> actions, bool fallback = false,
            string? fallbackReason = null)
        {
            NpcName = npcName;
            Speech = speech;
            Actions = actions;
            Fallback = fallback;
            FallbackReason = fallbackReason;
        }

        public string NpcName { get; }
        public string Speech { get; }
        public IReadOnlyList<ToolAction> Actions { get; }
        /// <summary>规则引擎降级响应（BUSY 等）：灰色系统提示渲染，不弹对话框。</summary>
        public bool Fallback { get; }
        /// <summary>TS 降级原因（busy/llm_error/billing/unavailable），仅日志留痕。</summary>
        public string? FallbackReason { get; }
    }

    /// <summary>
    ///     键盘文本输入订阅者（与原版 ChatBox 同一机制）：走窗口 TextInput 事件，
    ///     原生支持 IME 中文组字。Enter/Escape 不在这里处理——由 receiveKeyPress
    ///     前缀统一做对话控制，避免同一按键双重触发。
    /// </summary>
    private sealed class DialogueInputSubscriber : IKeyboardSubscriber
    {
        public bool Selected { get; set; }

        public void RecieveTextInput(char inputChar)
        {
            if (_isWaitingForResponse)
            {
                return;
            }

            _inputText.Append(inputChar);
        }

        public void RecieveTextInput(string text)
        {
            if (_isWaitingForResponse)
            {
                return;
            }

            _inputText.Append(text);
        }

        public void RecieveCommandInput(char command)
        {
            // '\r'(Enter)/'\t' 由 receiveKeyPress 前缀处理，这里只处理退格
            if (command == '\b' && !_isWaitingForResponse && _inputText.Length > 0)
            {
                _inputText.Remove(_inputText.Length - 1, 1);
            }
        }

        public void RecieveSpecialInput(Keys key)
        {
            // 方向键/Delete 等暂不处理
        }
    }
}