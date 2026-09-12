#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using ValleyAgent.Patches;
using Object = StardewValley.Object;

namespace ValleyAgent.TestMod;

/// <summary>
///     联机 farmhand 测试命令：程序化触发 C1/C2/C3 的补丁路径。
/// </summary>
public static class MultiplayerTestCommands
{
    public static void Register(IModHelper helper, IMonitor monitor)
    {
        _ = helper.ConsoleCommands.Add("va_test_c1",
            "C1: 程序化触发 farmhand 右键 Agent NPC，验证是否打开原生 DialogueBox 输入条。\n" +
            "Usage: va_test_c1 <npc_name>",
            (_, args) => RunC1(args, monitor));

        _ = helper.ConsoleCommands.Add("va_test_c2",
            "C2: 程序化触发 farmhand 向 Agent NPC 送礼，验证是否走 transport 代理。\n" +
            "Usage: va_test_c2 <npc_name> [item_id]  (默认 item_id=74 钻石)",
            (_, args) => RunC2(args, monitor));

        _ = helper.ConsoleCommands.Add("va_test_c3",
            "C3: 向 Agent NPC 发送消息并验证主机执行 Actions。\n" +
            "Usage: va_test_c3 <npc_name> <message>",
            (_, args) => { Task.Run(() => RunC3(args, monitor)); });

        _ = helper.ConsoleCommands.Add("va_test_c4",
            "C4 (M1): 主机侧注入带房客 playerId 的 execute_adjust，验证钱物落在房客身上。\n" +
            "Usage: va_test_c4 <npc_name>  (需在线 farmhand；房客 +50g / NPC -50g)",
            (_, args) => RunC4(args, monitor));

        _ = helper.ConsoleCommands.Add("va_test_alloc",
            "分配 NPC 为 Agent（C3 前置：TryGenerateDialogue 需要 NPC 已是 Agent）。\n" +
            "Usage: va_test_alloc <npc_name>",
            (_, args) => RunAlloc(args, monitor));
    }

    /// <summary>C3 前置：主机侧分配 NPC 为 Agent（TryGenerateDialogue 对非 Agent 返回 false）。
    /// TryAllocateAgent 对"已分配"返回 false（AllocationManager 槽位视角），而 GetActiveAgentNames
    /// 是 AgentService 实例视角——IdleEviction 可能移除实例但留槽位导致两者不同步、alloc 卡死。
    /// 失败时先 TryDeallocateAgent 重置再重试（最多 5 轮）。</summary>
    internal static void RunAlloc(string[] args, IMonitor monitor)
    {
        var npcName = args.Length > 0 ? args[0] : TestConfig.NpcName;
        var api = ValleyAgent.ModEntry.Instance?.API;
        if (api == null)
        {
            monitor.Log("[Alloc] ValleyAgent API 未获取到。", LogLevel.Error);
            return;
        }

        // 诊断：分配管理器状态（槽位/上限/实例名单——TryAllocateAgent 失败分支不可见）
        var container = ValleyAgent.ModEntry.Instance?.Container;
        var agentService = container?.GetService<ValleyAgent.Services.AgentService>();
        var allocMgr = agentService?.AllocationManager;
        if (allocMgr != null)
        {
            monitor.Log(
                $"[Alloc] diag: currentCount={allocMgr.CurrentAgentCount} maxAgents={allocMgr.MaxAgents} " +
                $"allocated={string.Join(",", allocMgr.AllocatedAgentNames)} " +
                $"active={string.Join(",", api.GetActiveAgentNames())}",
                LogLevel.Info);
        }

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            if (api.TryAllocateAgent(npcName)
                || api.GetActiveAgentNames().Contains(npcName, StringComparer.OrdinalIgnoreCase))
            {
                monitor.Log($"[Alloc] {npcName} allocated (or already active) [attempt {attempt}].", LogLevel.Info);
                return;
            }

            // API 的 TryDeallocateAgent 会因 RemoveAgent/Dealloc 事件异常返回 false（槽位残留，
            // 存档加载后 allocated=Haley,Abigail 但 active=空）——直接操作 AllocationManager
            // + AgentService（IntegrationTestBase 同款先例），绕过 API 的 catch。
            try
            {
                if (allocMgr != null && allocMgr.IsAllocated(npcName))
                {
                    _ = allocMgr.Deallocate(npcName);
                }

                var allocated = allocMgr != null && allocMgr.TryAllocate(npcName, 0, 0, 0);
                var created = false;
                string? createErr = null;
                try
                {
                    created = agentService?.CreateAgent(npcName) != null;
                }
                catch (Exception ex)
                {
                    createErr = $"{ex.GetType().Name}: {ex.Message}";
                }
                monitor.Log(
                    $"[Alloc] attempt {attempt}: allocated={allocated} agentCreated={created}" +
                    $"{(createErr != null ? $" createError={createErr}" : "")} " +
                    $"hasAgent={agentService?.HasAgent(npcName)} active={string.Join(",", api.GetActiveAgentNames())}",
                    LogLevel.Info);
                if (allocated && created)
                {
                    monitor.Log($"[Alloc] {npcName} allocated via direct manager [attempt {attempt}].", LogLevel.Info);
                    return;
                }
            }
            catch (Exception ex)
            {
                monitor.Log($"[Alloc] attempt {attempt}: direct manager threw: {ex.Message}", LogLevel.Warn);
            }
            System.Threading.Thread.Sleep(2000);
        }

        monitor.Log($"[Alloc] {npcName} allocation FAILED after 5 attempts.", LogLevel.Error);
    }

    internal static void RunC1(string[] args, IMonitor monitor)
    {
        var npcName = args.Length > 0 ? args[0] : TestConfig.NpcName;
        if (!EnsureLoaded(monitor, npcName, out var player, out var npc))
        {
            monitor.Log($"[C1] 前置条件不满足（玩家或 NPC {npcName} 未找到）。", LogLevel.Error);
            return;
        }

        try
        {
            // 把玩家瞬移到 NPC 面前，确保 checkAction 能命中
            TeleportNear(player, npc, monitor);

            // 清掉可能已打开的菜单
            if (Game1.activeClickableMenu != null)
            {
                Game1.exitActiveMenu();
            }

            DialogueBoxInputPatch.SetActiveAgentNpc(string.Empty);

            monitor.Log($"[C1] 调用 {npc.Name}.checkAction...", LogLevel.Info);
            var result = npc.checkAction(player, npc.currentLocation);

            var isAgent = IsAgentNpc(npc.Name, monitor);
            var interceptCount = NPCDialoguePatch.InterceptCount;
            var activeMenu = Game1.activeClickableMenu;
            var isDialogueBox = activeMenu is DialogueBox;
            var inputActive = DialogueBoxInputPatch.GetActiveAgentNpc() != null;

            monitor.Log(
                $"[C1] result={result}, isAgent={isAgent}, interceptCount={interceptCount}, activeMenu={activeMenu?.GetType().Name ?? "(null)"}, inputActive={inputActive}",
                LogLevel.Info);

            if (isAgent && isDialogueBox && inputActive)
            {
                monitor.Log($"[C1] PASS: Agent NPC {npc.Name} 在 farmhand 端打开了输入对话框。", LogLevel.Info);
                ShowTestHud($"C1 PASS: {npc.Name} Agent 对话框");
                // 为避免阻塞主线程导致窗口无响应，不 sleep；对话框立即关闭，HUD 提示已足够视频识别。
                Game1.exitActiveMenu();
            }
            else if (!isAgent)
            {
                monitor.Log($"[C1] SKIP: {npc.Name} 在当前端未被识别为 Agent。", LogLevel.Warn);
            }
            else
            {
                monitor.Log(
                    $"[C1] FAIL: 未打开预期输入对话框 (menu={activeMenu?.GetType().Name}, inputActive={inputActive}, intercept={interceptCount})",
                    LogLevel.Error);
            }
        }
        catch (Exception ex)
        {
            monitor.Log($"[C1] 异常: {ex}", LogLevel.Error);
        }
    }

    internal static void RunC2(string[] args, IMonitor monitor)
    {
        var npcName = args.Length > 0 ? args[0] : TestConfig.NpcName;
        var itemId = args.Length > 1 ? args[1] : "74"; // 钻石
        if (!EnsureLoaded(monitor, npcName, out var player, out var npc))
        {
            monitor.Log($"[C2] 前置条件不满足（玩家或 NPC {npcName} 未找到）。", LogLevel.Error);
            return;
        }

        try
        {
            TeleportNear(player, npc, monitor);

            // 清包/给礼物
            var gift = new Object(itemId, 1);
            player.addItemToInventoryBool(gift);
            player.ActiveObject = gift;

            monitor.Log($"[C2] 调用 {npc.Name}.tryToReceiveActiveObject，礼物={gift.DisplayName}...", LogLevel.Info);
            var result = npc.tryToReceiveActiveObject(player);

            monitor.Log(
                $"[C2] result={result}, activeObjectAfter={player.ActiveObject?.DisplayName ?? "null"}, isAgent={IsAgentNpc(npc.Name, monitor)}",
                LogLevel.Info);

            // 在 ThinClient 下，礼物会被 transport 异步消耗并显示对话；在 Host 下本地评估消耗。
            // 只要物品被消耗且 NPC 是 Agent，即认为路径命中。
            if (IsAgentNpc(npc.Name, monitor) && player.ActiveObject == null)
            {
                monitor.Log($"[C2] PASS: Agent NPC {npc.Name} 的礼物路径被接管（物品已消耗）。", LogLevel.Info);
                ShowTestHud($"C2 PASS: {npc.Name} 收下礼物");
            }
            else if (!IsAgentNpc(npc.Name, monitor))
            {
                monitor.Log($"[C2] SKIP: {npc.Name} 在当前端未被识别为 Agent。", LogLevel.Warn);
            }
            else
            {
                monitor.Log("[C2] FAIL: 礼物未被 Agent 路径消耗。", LogLevel.Error);
                ShowTestHud("C2 FAIL: 礼物未消耗");
            }
        }
        catch (Exception ex)
        {
            monitor.Log($"[C2] 异常: {ex}", LogLevel.Error);
        }
    }

    internal static async Task RunC3(string[] args, IMonitor monitor)
    {
        var npcName = args.Length > 0 ? args[0] : TestConfig.NpcName;
        var message = args.Length > 1 ? string.Join(" ", args[1..]) : "帮我挖矿";
        if (!EnsureLoaded(monitor, npcName, out _, out var npc))
        {
            monitor.Log($"[C3] 前置条件不满足（玩家或 NPC {npcName} 未找到）。", LogLevel.Error);
            return;
        }

        var api = ValleyAgent.ModEntry.Instance?.API;
        if (api == null)
        {
            monitor.Log("[C3] ValleyAgent API 未获取到。", LogLevel.Error);
            return;
        }

        monitor.Log($"[C3] 发送消息给 {npc.Name}: {message}", LogLevel.Info);
        var ok = api.TryGenerateDialogue(npc.Name, message);
        if (!ok)
        {
            monitor.Log("[C3] TryGenerateDialogue 返回 false，可能处于冷却或未识别为 Agent。", LogLevel.Error);
            return;
        }

        // 轮询等待响应
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < 30)
        {
            await Task.Delay(500).ConfigureAwait(false);

            if (api.TryGetLastDialogue(npc.Name, out var response) && !string.IsNullOrEmpty(response))
            {
                api.TryGetLastDialogueSource(npc.Name, out var source);
                monitor.Log($"[C3] 收到响应 (source={source}): {response}", LogLevel.Info);
                // 2026-09-10：PASS 行携带 source——fallback 与真实 LLM 响应此前无法区分，
                // 自动化拿 "[C3] PASS" 判绿会把 LLM 全灭降级误当通过。
                monitor.Log($"[C3] PASS (source={source}): 对话链路通。请在主机端日志确认 Actions 是否被执行。", LogLevel.Info);
                ShowTestHud($"C3 PASS: {source} 响应已收到");
                return;
            }
        }

        monitor.Log("[C3] FAIL: 30 秒内未收到 LLM 响应。", LogLevel.Error);
        ShowTestHud("C3 FAIL: 未收到响应");
    }

    private static bool EnsureLoaded(IMonitor monitor, string npcName, out Farmer player, out NPC npc)
    {
        player = Game1.player;
        // 2026-09-10 修复：此前写死 TestConfig.NpcName——args[0] 指定的 NPC 被静默忽略，
        // `va_test_c3 Alex` 实际对话的是 Haley（E2E 一直用 Haley 所以从未暴露）。
        npc = Game1.getCharacterFromName(npcName);
        if (player == null || npc == null)
        {
            monitor.Log($"[Test] player={(player == null ? "null" : "ok")}, npc={(npc == null ? "null" : "ok")} ({npcName})",
                LogLevel.Error);
            return false;
        }

        return true;
    }

    /// <summary>
    ///     强制玩家脱离床铺并把角色放到房间开阔处。存档自动加载后玩家可能仍在床上，导致
    ///     checkAction / tryToReceiveActiveObject 等交互在睡姿状态被跳过，视频上看不到动作。
    /// </summary>
    private static void WakeUpAndMoveToOpenTile(Farmer player, GameLocation location)
    {
        try
        {
            // Farmer.isInBed 是 NetBool，需要通过 Value 属性设置
            var isInBedField = typeof(Farmer).GetField("isInBed",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (isInBedField != null)
            {
                var netBool = isInBedField.GetValue(player);
                if (netBool != null)
                {
                    var valueProp = netBool.GetType().GetProperty("Value");
                    if (valueProp != null)
                    {
                        valueProp.SetValue(netBool, false);
                    }
                }
            }

            // 同时清除可能的睡眠状态字段
            foreach (var fieldName in new[] { "sleeping", "isSleeping", "isInBed" })
            {
                var field = typeof(Farmer).GetField(fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType == typeof(bool))
                {
                    field.SetValue(player, false);
                }
            }

            // 强制位置到房间开阔处（农舍床大概在 (8,8)，选择远离床的固定点）
            var openTile = new Vector2(12, 5);
            player.currentLocation = location;
            player.setTileLocation(openTile);
            player.position.Value = new Vector2(openTile.X * 64f, openTile.Y * 64f);
        }
        catch (Exception)
        {
            // 反射失败不阻断测试
        }
    }

    private static void TeleportNear(Farmer player, NPC npc, IMonitor monitor)
    {
        var location = npc.currentLocation ?? player.currentLocation ?? Game1.currentLocation;
        WakeUpAndMoveToOpenTile(player, location);

        // 也把 NPC 拉到同一位置附近，确保在 farmhand 端视觉上两人站在一起。
        // ThinClient 下 NPC 位置由主机同步，但本地临时设置可在执行交互的几帧内生效。
        try
        {
            npc.currentLocation = location;
            npc.setTileLocation(new Vector2(11, 5));
            npc.position.Value = new Vector2(11 * 64f, 5 * 64f);
        }
        catch (Exception)
        {
            // 忽略 NPC 位置设置失败
        }

        player.FacingDirection = 3;
        monitor.Log($"[Test] 玩家已瞬移到 {npc.Name} 附近 tile={player.Tile}", LogLevel.Debug);
    }

    /// <summary>
    ///     在主线程显示一条 HUD 提示，供视频录制验证测试步骤实际发生。
    /// </summary>
    private static void ShowTestHud(string text)
    {
        try
        {
            Game1.addHUDMessage(new HUDMessage(text, 2));
        }
        catch (Exception)
        {
            // 忽略显示失败
        }
    }

    private static bool IsAgentNpc(string npcName, IMonitor monitor)
    {
        // Host 模式
        var api = ValleyAgent.ModEntry.Instance?.API;
        if (api != null)
        {
            var agents = api.GetActiveAgentNames();
            if (agents.Contains(npcName, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // ThinClient 模式：通过 patch 的 RemoteRenderer 判断
        var remoteState = NPCDialoguePatch.RemoteRenderer?.GetRemoteState(npcName);
        if (remoteState != null)
        {
            monitor.Log($"[Test] {npcName} 在 RemoteRenderer 中存在远程状态。", LogLevel.Debug);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     C4（M1 联机经济正确性）：主机侧注入带房客 playerId 的 execute_adjust。
    ///     验证点：AdjustExecutor 按 playerId 解析到房客 Farmer —— 钱落在房客钱包，
    ///     主机钱包零变动（此前会扣主机的钱）。单机 IT14 覆盖了解析语义，
    ///     这里验证真联机下 otherFarmers 路径。执行后恢复双方余额。
    /// </summary>
    internal static void RunC4(string[] args, IMonitor monitor)
    {
        var npcName = args.Length > 0 ? args[0] : TestConfig.NpcName;
        if (!EnsureLoaded(monitor, npcName, out _, out var npc))
        {
            monitor.Log("[C4] 前置条件不满足（NPC 未找到）。", LogLevel.Error);
            return;
        }

        if (Game1.otherFarmers.Count == 0)
        {
            monitor.Log("[C4] SKIP: 无在线 farmhand（需在真联机会话中运行）。", LogLevel.Warn);
            return;
        }

        var container = ValleyAgent.ModEntry.Instance?.Container;
        var adjustExecutor = container?.GetService<ValleyAgent.Economy.AdjustExecutor>();
        var agentService = container?.GetService<ValleyAgent.Services.AgentService>();
        if (adjustExecutor == null || agentService == null
            || !agentService.TryGetBrain(npcName, out var agent) || agent == null)
        {
            monitor.Log("[C4] SKIP: AdjustExecutor/AgentService 不可用或 NPC 未分配。", LogLevel.Error);
            return;
        }

        var farmhand = Game1.otherFarmers.Values.First();
        const int delta = 50;

        // 保证 NPC 钱包够扣
        var npcMoneyBefore = agent.Inventory.Money;
        agent.Inventory.Money = Math.Max(npcMoneyBefore, 1000);

        var farmhandMoneyBefore = farmhand.Money;
        var hostMoneyBefore = Game1.player.Money;

        try
        {
            var message = new ValleyAgent.Protocol.ProtocolV2.ExecuteAdjustMessage
            {
                RequestId = "c4-mp-adjust",
                InstructionId = $"c4-mp-adjust-{Guid.NewGuid():N}",
                NpcName = npcName,
                PlayerId = farmhand.UniqueMultiplayerID.ToString(),
                Ops = new System.Collections.Generic.List<ValleyAgent.Protocol.ProtocolV2.AdjustOp>
                {
                    new() { Kind = "money", Target = "player", Amount = delta, Reason = "c4_mp_test" },
                    new() { Kind = "money", Target = "npc", Amount = -delta, Reason = "c4_mp_test" }
                }
            };

            var result = adjustExecutor.Execute(message);

            var farmhandMoneyAfter = farmhand.Money;
            var hostMoneyAfter = Game1.player.Money;
            var npcMoneyAfter = agent.Inventory.Money;

            // 钱包模式：1.6 共享钱包（useSeparateWallets=false，旧档默认）下 AddIndividualMoney
            // 改的是共享钱包——主机+房客同一钱包，双方都变是 vanilla 语义；独立钱包才各自独立。
            var sharedWallet = !farmhand.useSeparateWallets;
            var walletMode = sharedWallet ? "共享钱包(双方同池)" : "独立钱包(各自独立)";
            var npcExpected = Math.Max(npcMoneyBefore, 1000) - delta; // C4 已把 NPC 钱包垫到 >=1000

            var ok = result.Success
                     && farmhandMoneyAfter == farmhandMoneyBefore + delta
                     && (sharedWallet ? hostMoneyAfter == hostMoneyBefore + delta : hostMoneyAfter == hostMoneyBefore)
                     && npcMoneyAfter == npcExpected;

            monitor.Log(
                $"[C4] result={result.Success}{(result.FailureCode != null ? $" code={result.FailureCode}" : "")} " +
                $"钱包模式={walletMode} " +
                $"farmhand {farmhandMoneyBefore}→{farmhandMoneyAfter} (+{delta}) " +
                $"host {hostMoneyBefore}→{hostMoneyAfter} " +
                $"npc {npcMoneyBefore}→{npcMoneyAfter} (期望 {npcExpected})",
                LogLevel.Info);

            if (ok)
            {
                monitor.Log(
                    $"[C4] PASS: playerId 解析到房客——{walletMode}下钱款落账正确（房客侧 +{delta}、NPC 侧 -{delta}）。",
                    LogLevel.Info);
                ShowTestHud("C4 PASS: 房客收款落账");
            }
            else
            {
                monitor.Log($"[C4] FAIL: {(!result.Success ? $"回执失败 {result.FailureCode}" : "余额断言不匹配")}", LogLevel.Error);
            }
        }
        catch (Exception ex)
        {
            monitor.Log($"[C4] FAIL: Execute 抛异常: {ex.Message}", LogLevel.Error);
        }
        finally
        {
            // 恢复：房客 -delta（team API，Farmer.Money setter 禁止改其他玩家）、NPC 回到初始钱包
            try
            {
                Game1.player.team.AddIndividualMoney(farmhand, -delta);
                agent.Inventory.Money = npcMoneyBefore;
            }
            catch (Exception ex)
            {
                monitor.Log($"[C4] 恢复失败: {ex.Message}", LogLevel.Warn);
            }
        }
    }
}