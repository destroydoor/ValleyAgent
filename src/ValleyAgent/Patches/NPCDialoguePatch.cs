using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using ValleyAgent.Api;
using ValleyAgent.Config;
using ValleyAgent.i18n;
using ValleyAgent.Multiplayer;
using ValleyAgent.Services;
using ValleyAgent.StateMachine;
using ValleyAgent.WebSocket;

namespace ValleyAgent.Patches;

[HarmonyPatch(typeof(NPC), nameof(NPC.checkAction))]
public static class NPCDialoguePatch
{
    private static readonly Dictionary<string, AgentState> _preDialogueStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     非 Agent 村民点击后待追加 AI 对话的 NPC 名。原版对话关闭时消费。
    /// </summary>
    private static string? _pendingVanillaDialogueNpc;

    /// <summary>
    ///     会话级对话频率计数（每条玩家消息 +1），供动态分配优先级使用。
    /// </summary>
    private static readonly ConcurrentDictionary<string, int> _conversationCounts =
        new(StringComparer.OrdinalIgnoreCase);

    public static AgentService? AgentService { get; set; }
    public static IAgentServerProvider? AgentServerProvider { get; set; }
    public static ITranslationProvider? Translation { get; set; }
    public static IMonitor? Monitor { get; set; }
    public static ModConfig? Config { get; set; }
    public static int InterceptCount { get; private set; }

    /// <summary>
    ///     ThinClient 模式下注入的远程 Agent 状态缓存。主机广播的 Agent 名单通过其 _remoteStates 的 key 体现。
    ///     Host 模式保持 null（用 AgentService 判定 isAgent）。
    ///     用于修复联机 farmhand 端 AgentService/AgentServerProvider 均为 null 导致补丁放行原版的问题。
    /// </summary>
    public static AgentRemoteRenderer? RemoteRenderer { get; set; }

    /// <summary>
    ///     Spark 激活回调：玩家与非 Agent 村民对话时调用。返回 true 表示已 spark 激活该 NPC 为 Agent。
    ///     由 EventHandlerInitializer 设置（需要访问 SparkAllocator + AllocationManager + CreateAgent + WireAgentStateEvents）。
    ///     仅 Host 模式赋值（ThinClient 不分配 Agent）。
    /// </summary>
    public static Func<string, bool>? OnSparkCandidate { get; set; }

    /// <summary>调试日志用的模式标签：ThinClient 或 Host。</summary>
    private static string ModeTag
    {
        get => RemoteRenderer != null && AgentServerProvider == null ? "ThinClient" : "Host";
    }

#pragma warning disable IDE0060 // Harmony Patch 参数必须按位置匹配，不可删除
    private static bool Prefix(NPC __instance, Farmer who, GameLocation l, ref bool __result)
#pragma warning restore IDE0060
    {
        if (Game1.eventUp || Game1.CurrentEvent != null)
        {
            return true;
        }

        if (Game1.isFestival())
        {
            return true;
        }

        // 守卫：Host 模式需 AgentServerProvider，ThinClient 模式需 RemoteRenderer。
        // 两者皆 null（未初始化或 Inert 模式）则放行原版。
        var isThinClient = RemoteRenderer != null && AgentServerProvider == null;
        if (!isThinClient && AgentServerProvider == null)
        {
            return true;
        }

        if (!__instance.IsVillager)
        {
            return true;
        }

        // IsDead 判定：Host 走 AgentService，ThinClient 走 RemoteRenderer 的远程状态
        if (isThinClient)
        {
            var remoteState = RemoteRenderer!.GetRemoteState(__instance.Name);
            if (remoteState?.IsDead ?? false)
            {
                __result = true;
                Game1.drawObjectDialogue(Translation?.GetString("DIALOG_Greet_Final") ?? "...");
                return false;
            }
        }
        else if (AgentService != null && AgentService.TryGetAgent(__instance.Name, out var agent) && agent != null &&
                 agent.Health.IsDead)
        {
            __result = true;
            Game1.drawObjectDialogue(Translation?.GetString("DIALOG_Greet_Final") ?? "...");
            return false;
        }

        // 手持可赠送物品右键 NPC → 弹 送礼/交易 选择菜单（前移到 isAgent 检查之前）。
        // 修复：原代码将此检查放在 isAgent 之后，导致非 Agent NPC 手持物品右键时
        // 直接走原版送礼（消耗物品），不弹菜单。现在所有村民（Agent 和非 Agent）都能弹菜单。
        // 武器/工具等不可赠送手持物视为空手，继续走下方对话流程。
        if (GiftTradeMenuLogic.ShouldOfferGiftTradeMenu(
                GiftTradeMenuLogic.IsGiftableHeldItem(who.ActiveObject)))
        {
            // M3：房客与主机同菜单（送礼走 gift transport、交易走 dialogue transport，两条管道房客侧都已接通）。
            ShowGiftTradeMenu(__instance, who, l, isThinClient);
            __result = true;
            InterceptCount++;
            return false;
        }

        // isAgent 判定：Host 走 AgentService.HasAgent，ThinClient 走 RemoteRenderer 远程名单
        var isAgent = isThinClient
            ? RemoteRenderer!.GetRemoteState(__instance.Name) != null
            : AgentService?.HasAgent(__instance.Name) ?? false;
        if (!isAgent)
        {
            // Spark 激活：玩家与非 Agent 村民对话时低概率激活为 Agent（设计文档 §4.2.3）。
            // 仅 Host 模式触发（ThinClient 不分配 Agent）。命中后 isAgent 重新判定为 true，
            // 继续走下方 Agent 对话流程（OpenAgentDialogue）。
            if (!isThinClient && OnSparkCandidate?.Invoke(__instance.Name) == true)
            {
                isAgent = AgentService?.HasAgent(__instance.Name) ?? false;
            }

            if (!isAgent)
            {
                // 非 Agent 村民对话路径（房客与主机同节奏：对话不需要身体，房客侧请求走 dialogue transport）：
                // - EnableFirstClickVanilla=false → 直接进入 AI 对话（跳过原版台词），支持无限对话
                // - EnableFirstClickVanilla=true  → 放行原版，关闭后追加 AI 对话（原行为）
                // EnableInfiniteDialogue（新开关）与 NonAgentAIChatEnabled（旧开关）同时控制，
                // 任一关闭即不追加 AI 对话——保留旧开关兼容已有存档配置。
                if ((Config?.EnableInfiniteDialogue ?? true)
                    && (Config?.NonAgentAIChatEnabled ?? true)
                    && !(Config?.EnableFirstClickVanilla ?? true))
                {
                    OpenAgentDialogue(__instance, isThinClient);
                    __result = true;
                    InterceptCount++;
                    Monitor?.Log($"[Dialogue] Non-agent {__instance.Name}: direct AI dialogue (skip vanilla)",
                        LogLevel.Debug);
                    return false;
                }

                if ((Config?.EnableInfiniteDialogue ?? true)
                    && (Config?.NonAgentAIChatEnabled ?? true))
                {
                    _pendingVanillaDialogueNpc = __instance.Name;
                }

                return true;
            }
            // spark 命中，isAgent 现在为 true，继续走下方 Agent 对话流程
        }

        OpenAgentDialogue(__instance, isThinClient);

        __result = true;
        InterceptCount++;
        Monitor?.Log($"[Dialogue] Agent {__instance.Name}: opened native DialogueBox (mode={ModeTag})", LogLevel.Debug);
        return false;
    }

    /// <summary>
    ///     打开 Agent AI 对话（空手右键与交易选项共用）：
    ///     保存对话前状态 → 打开原生 DialogueBox → 标记活跃对话。
    /// </summary>
    private static void OpenAgentDialogue(NPC npc, bool isThinClient)
    {
        // 保存对话前状态：仅 Host 端（ThinClient 无 AgentService/StateMachine，状态恢复由主机负责）
        if (!isThinClient && AgentService != null && AgentService.TryGetAgent(npc.Name, out var dialogAgent) &&
            dialogAgent != null)
        {
            _preDialogueStates[npc.Name] = dialogAgent.StateMachine.CurrentStateFlag;
            Monitor?.Log(
                $"[Dialogue] Saved pre-dialogue state for {npc.Name}: {dialogAgent.StateMachine.CurrentStateFlag}");
        }

        // 直接打开原生 DialogueBox，无问候语。
        // 注意：不能用空字符串——Game1.drawDialogue 会检查新 DialogueBox 的 dialogueFinished，
        // 空对话会立即 finished 导致菜单被置 null，玩家点击后什么都看不到（ISSUE-1）。
        // 用 "..." 占位：NPC 安静地等着玩家开口，输入提示由 DialogueBoxInputPatch 绘制。
        var dialogue = new StardewValley.Dialogue(npc, null, "...");
        npc.setNewDialogue(dialogue);
        Game1.drawDialogue(npc);

        // 标记当前活跃的 Agent 对话，供 DialogueBoxInputPatch 使用
        DialogueBoxInputPatch.SetActiveAgentNpc(npc.Name);
    }

    /// <summary>
    ///     手持可赠送物品时弹出原版风格问题对话框：送礼 / 交易 / 取消。
    /// </summary>
    private static void ShowGiftTradeMenu(NPC npc, Farmer who, GameLocation location, bool isThinClient)
    {
        // 菜单打开期间玩家可能切换手持物，物品名/数量在此时定格，用于交易意图文案
        var heldObj = who.ActiveObject!;
        var itemDisplayName = heldObj.DisplayName;
        var stack = heldObj.Stack;

        var responses = new[]
        {
            new Response(GiftTradeMenuLogic.ResponseKeyGift, "送礼"),
            new Response(GiftTradeMenuLogic.ResponseKeyTrade, "交易"),
            new Response(GiftTradeMenuLogic.ResponseKeyCancel, "取消")
        };

        // EnableTrade=false 时移除交易选项，仅保留送礼/取消
        if (!(Config?.EnableTrade ?? true))
        {
            responses = new[]
            {
                new Response(GiftTradeMenuLogic.ResponseKeyGift, "送礼"),
                new Response(GiftTradeMenuLogic.ResponseKeyCancel, "取消")
            };
        }

        Monitor?.Log($"[Dialogue] Agent {npc.Name}: held {itemDisplayName}×{stack}, showing gift/trade menu",
            LogLevel.Debug);

        // 注意：玩家 ESC 关闭问题框不消费 afterQuestion，回调会残留到下一个问题框。
        // 靠 ResponseKey 不匹配走默认分支（无操作）兜底，见 OnGiftTradeMenuAnswer。
        location.createQuestionDialogue(
            GiftTradeMenuLogic.BuildMenuQuestion(npc.displayName, itemDisplayName),
            responses,
            (farmer, whichAnswer) =>
                OnGiftTradeMenuAnswer(npc, farmer, whichAnswer, itemDisplayName, stack, isThinClient),
            npc);
    }

    /// <summary>送礼/交易菜单的回答处理。</summary>
    private static void OnGiftTradeMenuAnswer(NPC npc, Farmer farmer, string whichAnswer, string itemDisplayName,
        int stack, bool isThinClient)
    {
        switch (whichAnswer)
        {
            case GiftTradeMenuLogic.ResponseKeyGift:
                // 直接调原版入口，走 NPCGiftPatch 管道：
                // E3-3 待成交单命中 → 交易结算；否则送礼评估 + 好感度 + 记忆/情绪。
                npc.tryToReceiveActiveObject(farmer);
                break;

            case GiftTradeMenuLogic.ResponseKeyTrade:
                // 防御性检查：EnableTrade=false 时菜单已隐藏交易选项，
                // 但残留回调可能触发此分支——直接按取消处理。
                if (!(Config?.EnableTrade ?? true))
                {
                    break;
                }

                // 打开 AI 对话并以玩家口吻预注入交易意图，让 NPC 表态开价；
                // 后续还价/成交流程全部走已有对话管道，不在菜单里另起逻辑。
                // M3：房客侧 OpenAgentDialogue 需要 isThinClient=true 才不会去摸本地
                // AgentService（房客没有），此前硬编码 false —— 房客原本走不到这里。
                OpenAgentDialogue(npc, isThinClient);
                DialogueBoxInputPatch.QueueExternalInput(
                    GiftTradeMenuLogic.BuildTradeIntentMessage(itemDisplayName, stack));
                break;
        }
    }

    /// <summary>
    ///     原版对话关闭后，若存在待追加 AI 对话的非 Agent 村民，自动打开 AI 输入框。
    ///     挂在 DialogueBox.closeDialogue 上（所有对话框彻底关闭的唯一出口）。
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DialogueBox), nameof(DialogueBox.closeDialogue))]
    public static void CloseDialoguePostfix(DialogueBox __instance)
    {
        var pending = _pendingVanillaDialogueNpc;
        _pendingVanillaDialogueNpc = null; // 任何对话框关闭都消费一次，避免陈旧标记误触发
        if (pending == null)
        {
            return;
        }

        // AI 对话已在进行中（Agent 对话/上一次追加）→ 不重复打开
        if (DialogueBoxInputPatch.GetActiveAgentNpc() != null)
        {
            return;
        }

        // 校验关闭的确实是该 NPC 的角色对话（防陈旧标记）
        var speaker = __instance.characterDialogue?.speaker;
        if (speaker == null || !speaker.Name.Equals(pending, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Game1.eventUp || Game1.CurrentEvent != null || Game1.isFestival())
        {
            return;
        }

        // 房客侧 AgentServerProvider 恒为 null，对话通道存在性由 transport 判定：
        // 不按 transport 放行的话，房客播完原版台词后 AI 输入框永不弹出。
        if (AgentServerProvider == null && !DialogueBoxInputPatch.HasDialogueTransport)
        {
            return;
        }

        var aiDialogue = new StardewValley.Dialogue(speaker, null, "...");
        speaker.setNewDialogue(aiDialogue);
        Game1.drawDialogue(speaker);
        DialogueBoxInputPatch.SetActiveAgentNpc(speaker.Name);
        Monitor?.Log($"[Dialogue] Non-agent {speaker.Name}: vanilla dialogue ended, AI chat opened", LogLevel.Debug);
    }

    /// <summary>
    ///     结束 Agent 对话，恢复 NPC 对话前状态。
    /// </summary>
    private static void EndTopicConversation(string npcName)
    {
        var preState = GetPreDialogueState(npcName);
        if (preState.HasValue && AgentService != null)
        {
            var api = new ValleyAgentApi(AgentService, AgentService.AllocationManager);
            _ = api.TrySetAgentState(npcName, preState.Value.ToString());
            ClearPreDialogueState(npcName);
        }

        Monitor?.Log($"[Dialogue] Agent {npcName}: conversation ended", LogLevel.Debug);
    }

    /// <summary>
    ///     供 DialogueBoxInputPatch 调用的公开入口。
    /// </summary>
    public static void EndTopicConversationExternal(string npcName) => EndTopicConversation(npcName);

    /// <summary>玩家每发送一条消息计一次对话频率（供动态分配优先级）。</summary>
    public static void IncrementConversationCount(string npcName)
    {
        if (string.IsNullOrEmpty(npcName))
        {
            return;
        }

        _conversationCounts.AddOrUpdate(npcName, 1, (_, count) => count + 1);
    }

    /// <summary>读取会话级对话频率计数。</summary>
    public static int GetConversationCount(string npcName)
        => _conversationCounts.TryGetValue(npcName, out var count) ? count : 0;

    public static AgentState? GetPreDialogueState(string npcName) =>
        _preDialogueStates.TryGetValue(npcName, out var state) ? state : null;

    public static void ClearPreDialogueState(string npcName) => _ = _preDialogueStates.Remove(npcName);

    /// <summary>
    ///     对话中 LLM 通过 set_state 切换状态后，更新 preState 为新状态。
    ///     这样对话结束时 EndTopicConversation 恢复的是 LLM 决策的状态，
    ///     而不是对话前的旧状态——尊重 TS 端的状态转换决策。
    /// </summary>
    public static void UpdatePreDialogueState(string npcName, AgentState newState)
    {
        if (_preDialogueStates.ContainsKey(npcName))
        {
            _preDialogueStates[npcName] = newState;
        }
    }

    public static void ClearCache()
    {
        _preDialogueStates.Clear();
        _pendingVanillaDialogueNpc = null;
    }

    /// <summary>
    ///     P1-10: 清除所有活跃对话标记。返回标题画面时调用。
    /// </summary>
    public static void ClearAllConversations()
    {
        ClearCache();
        _conversationCounts.Clear();
    }
}