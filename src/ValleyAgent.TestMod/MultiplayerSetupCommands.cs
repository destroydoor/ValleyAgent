#nullable enable
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;

namespace ValleyAgent.TestMod;

/// <summary>
///     自动多人联机设置命令：程序化开启主机服务器 / 客机加入 LAN 游戏。
/// </summary>
public static class MultiplayerSetupCommands
{
    private const string DefaultJoinAddress = "127.0.0.1:24642";
    private const string ServerMode = "friends";
    private const double JoinTimeoutSeconds = 30.0;

    private static IMonitor s_monitor = null!;
    private static object? s_client;
    private static bool s_isJoining;
    private static DateTime s_joinStartTime;
    private static bool s_pendingHost;

    public static void Register(IModHelper helper, IMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(helper);
        ArgumentNullException.ThrowIfNull(monitor);

        s_monitor = monitor;

        _ = helper.ConsoleCommands.Add("va_mp_host",
            "Host a multiplayer server.\n" +
            "Usage: va_mp_host [min_cabins]  (default 1; soak 三房客场景传 3)",
            (_, args) => HostServer(args));

        _ = helper.ConsoleCommands.Add("va_mp_join",
            "Join a LAN multiplayer game as a farmhand.\n" +
            "Usage: va_mp_join [address]  (default: 127.0.0.1:24642)",
            (_, args) => JoinGame(args.Length > 0 ? args[0] : DefaultJoinAddress));

        _ = helper.ConsoleCommands.Add("va_test_sleep",
            "Trigger a day change (same as vanilla debug sleep, but executed on the main thread).\n" +
            "Usage: va_test_sleep",
            (_, _) => s_pendingSleep = true);

        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
    }

    private static int s_minCabins = 1;
    private static bool s_pendingSleep;

    private static void HostServer(string[] args)
    {
        try
        {
            if (args.Length > 0 && int.TryParse(args[0], out var requested))
            {
                s_minCabins = Math.Clamp(requested, 1, 8);
            }

            if (!Game1.hasLoadedGame)
            {
                s_pendingHost = true;
                s_monitor.Log("[MP] Game not loaded yet; will host server once save is ready.", LogLevel.Info);
                return;
            }

            DoHostServer();
        }
        catch (Exception ex)
        {
            s_monitor.Log($"[MP] Failed to host server: {ex}", LogLevel.Error);
        }
    }

    private static void DoHostServer()
    {
        // 主机端处理 farmhand 加入请求时会访问 Game1.hooks，若为 null 会抛 NRE 导致 farmhand 被断开。
        EnsureHooks();

        var options = Game1.options;
        if (options == null)
        {
            s_monitor.Log("[MP] Cannot host server: Game1.options is null.", LogLevel.Error);
            return;
        }

        // 1. 必须先将 multiplayerMode 设为 2（server/host），否则游戏的 updatePendingConnections
        // 不会调用 Multiplayer.StartServer() / GameServer.initializeHost()，导致 farmhand
        // 加入请求在 GameServer.checkFarmhandRequest 中 NRE。
        var multiplayerModeField = typeof(Game1).GetField("multiplayerMode",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (multiplayerModeField != null)
        {
            multiplayerModeField.SetValue(null, (byte)2);
            s_monitor.Log("[MP] Set Game1.multiplayerMode = 2.", LogLevel.Debug);
        }
        else
        {
            s_monitor.Log("[MP] WARN: Cannot find Game1.multiplayerMode field.", LogLevel.Warn);
        }

        var multiplayer = GetMultiplayer();
        if (multiplayer == null)
        {
            s_monitor.Log("[MP] Cannot host server: Game1.multiplayer is null or inaccessible.", LogLevel.Error);
            return;
        }

        // 2. 服务器状态判定：存档 options.enableServer 可能已为 true（上次测试保存过）——
        // updatePendingConnections 会在 DoHostServer 之前自动启动服务器。已运行时直接复用；
        // 否则清理旧 server 后显式启动（stop 后端口未释放立即重启会 SocketException 10048）。
        if (Game1.server != null && Game1.server.connected())
        {
            s_monitor.Log("[MP] Server already running (auto-started by updatePendingConnections), reusing.",
                LogLevel.Info);
        }
        else
        {
            // 2a. 如果存在旧的 Game1.server（未正确初始化），先停止并丢弃它。
            // 否则 LidgrenServer.server 为 null 时 receiveMessages() 会每秒抛 NRE。
            if (Game1.server != null)
            {
                s_monitor.Log(
                    $"[MP] Existing Game1.server detected (servers={Game1.server.connectionsCount}); stopping to ensure clean initialization.",
                    LogLevel.Debug);
                try
                {
                    Game1.server.stopServer();
                }
                catch (Exception ex)
                {
                    s_monitor.Log($"[MP] stopServer() threw while cleaning up existing server (ignored): {ex.Message}",
                        LogLevel.Debug);
                }

                Game1.server = null;
            }

            // 3. 直接启动服务器，避免 setServerMode 与 updatePendingConnections 之间的竞态导致
            // LidgrenServer 在初始化完成前被 receiveMessages() 访问而 NRE。
            var startServerMethod = multiplayer.GetType().GetMethod("StartServer",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (startServerMethod != null)
            {
                startServerMethod.Invoke(multiplayer, null);
                s_monitor.Log("[MP] Explicitly invoked Multiplayer.StartServer().", LogLevel.Debug);
            }
            else
            {
                s_monitor.Log("[MP] WARN: Cannot find Multiplayer.StartServer method.", LogLevel.Warn);
            }
        }

        // 4. 设置 options.enableServer / serverPrivacy，使后续 updatePendingConnections 保持服务器开启。
        SetField(options, "enableServer", true);
        SetServerPrivacy(options, ServerPrivacy.FriendsOnly);

        // 4.5 确保有可加入的 farmhand 角色：单机存档无木屋（farmhands 为空）时，服务器
        // 广播的 availableFarmhands 列表为空，farmhand 加入报 "No available farmhands on server"。
        // 等价 vanilla "添加农场主"（GameLocation.BuildStartingCabins 同款）：在农场放一座
        // Cabin 建筑（Building("Cabin") → GetIndoors() 是 Cabin 室内）并 CreateFarmhand。
        // 2026-09-10 soak：va_mp_host [min_cabins] 循环补建到 N 个角色（3 房客场景）；
        // 随机落点可能与已有建筑/地形冲突，预留 3N 次重试。
        try
        {
            var netWorldState = Game1.netWorldState.Value;
            var retries = Math.Max(s_minCabins * 3, 3);
            while (netWorldState.farmhandData.Count() < s_minCabins && retries-- > 0)
            {
                var farm = Game1.getFarm();
                var cabinBuilding = new StardewValley.Buildings.Building("Cabin", farm.getRandomTile())
                {
                    magical = { Value = true },
                    daysOfConstructionLeft = { Value = 0 }
                };
                cabinBuilding.load();
                if (farm.buildStructure(cabinBuilding, new Microsoft.Xna.Framework.Vector2(cabinBuilding.tileX.Value, cabinBuilding.tileY.Value), Game1.player, skipSafetyChecks: true)
                    && cabinBuilding.GetIndoors() is Cabin cabin)
                {
                    cabin.CreateFarmhand();
                    s_monitor.Log(
                        $"[MP] Created farmhand role via Cabin.CreateFarmhand (farmhandData={netWorldState.farmhandData.Count()}).",
                        LogLevel.Info);
                }
                else
                {
                    s_monitor.Log("[MP] WARN: cabin buildStructure failed; retrying with another tile.", LogLevel.Warn);
                }
            }

            s_monitor.Log(
                $"[MP] farmhand roles now {netWorldState.farmhandData.Count()} (requested {s_minCabins}).",
                netWorldState.farmhandData.Count() >= s_minCabins ? LogLevel.Info : LogLevel.Warn);
        }
        catch (Exception ex)
        {
            s_monitor.Log($"[MP] Failed to ensure farmhand roles: {ex.Message}", LogLevel.Warn);
        }

        // 5. 显式初始化 host，避免 farmhand 加入请求在 checkFarmhandRequest 中 NRE。
        if (Game1.server != null && Game1.serverHost == null)
        {
            Game1.server.initializeHost();
            s_monitor.Log("[MP] Explicitly invoked GameServer.initializeHost().", LogLevel.Debug);
        }

        // 6. 将时间设为白天 09:00，避免玩家躺在床上导致 farmhand 端交互测试在视频上看不到动作。
        // 主机时间生效后所有 farmhand 玩家加入时也会处于起床状态。
        try
        {
            Game1.timeOfDay = 900;
            s_monitor.Log("[MP] Set timeOfDay = 0900 so players wake up before farmhand tests.", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            s_monitor.Log($"[MP] Failed to set timeOfDay: {ex.Message}", LogLevel.Warn);
        }

        s_pendingHost = false;
        s_monitor.Log($"[MP] Hosted multiplayer server in '{ServerMode}' mode.", LogLevel.Info);
    }

    private static void SetServerPrivacy(Options options, ServerPrivacy privacy)
    {
        try
        {
            var field = typeof(Options).GetField("serverPrivacy",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(options, privacy);
                s_monitor.Log($"[MP] Set Options.serverPrivacy = {privacy}.", LogLevel.Debug);
            }
            else
            {
                s_monitor.Log("[MP] WARN: Cannot find Options.serverPrivacy field.", LogLevel.Warn);
            }
        }
        catch (Exception ex)
        {
            s_monitor.Log($"[MP] Failed to set Options.serverPrivacy: {ex.Message}", LogLevel.Warn);
        }
    }

    private static void SetField(object target, string fieldName, object value)
    {
        try
        {
            var field = target.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(target, value);
            }
            else
            {
                s_monitor.Log($"[MP] WARN: Cannot find {target.GetType().Name}.{fieldName} field.", LogLevel.Warn);
            }
        }
        catch (Exception ex)
        {
            s_monitor.Log($"[MP] Failed to set {target.GetType().Name}.{fieldName}: {ex.Message}", LogLevel.Warn);
        }
    }

    private static void JoinGame(string address)
    {
        try
        {
            if (s_isJoining)
            {
                s_monitor.Log("[MP] Already attempting to join a game.", LogLevel.Warn);
                return;
            }

            var client = CreateLidgrenClient(address);
            if (client == null)
            {
                s_monitor.Log("[MP] Failed to create LidgrenClient.", LogLevel.Error);
                return;
            }

            var multiplayer = GetMultiplayer();
            if (multiplayer == null)
            {
                s_monitor.Log("[MP] Cannot join game: Game1.multiplayer is null or inaccessible.", LogLevel.Error);
                return;
            }

            var initClientMethod = multiplayer.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "InitClient" && m.GetParameters().Length == 1)
                .FirstOrDefault(m => m.GetParameters()[0].ParameterType.IsAssignableFrom(client.GetType()));
            if (initClientMethod == null)
            {
                s_monitor.Log("[MP] Cannot find Multiplayer.InitClient method.", LogLevel.Error);
                return;
            }

            var initializedClient = initClientMethod.Invoke(multiplayer, new[] { client });
            if (initializedClient == null)
            {
                s_monitor.Log("[MP] InitClient returned null.", LogLevel.Error);
                return;
            }

            EnsureHooks();

            var menu = CreateFarmhandMenu(initializedClient);
            if (menu == null)
            {
                s_monitor.Log("[MP] Failed to create FarmhandMenu.", LogLevel.Error);
                return;
            }

            Game1.activeClickableMenu = menu;
            s_client = initializedClient;
            s_isJoining = true;
            s_joinStartTime = DateTime.UtcNow;
            s_monitor.Log($"[MP] Joining multiplayer game at {address}...", LogLevel.Info);
        }
        catch (Exception ex)
        {
            s_monitor.Log($"[MP] Failed to join game: {ex}", LogLevel.Error);
            StopJoining();
        }
    }

    private static object? CreateLidgrenClient(string address)
    {
        var lidgrenType = typeof(Game1).Assembly.GetType("StardewValley.Network.LidgrenClient");
        if (lidgrenType == null)
        {
            s_monitor.Log("[MP] Cannot find StardewValley.Network.LidgrenClient type.", LogLevel.Error);
            return null;
        }

        var ctor = lidgrenType.GetConstructor(new[] { typeof(string) });
        if (ctor == null)
        {
            s_monitor.Log("[MP] Cannot find LidgrenClient(string) constructor.", LogLevel.Error);
            return null;
        }

        return ctor.Invoke(new object[] { address });
    }

    private static IClickableMenu? CreateFarmhandMenu(object client)
    {
        var menuType = typeof(Game1).Assembly.GetType("StardewValley.Menus.FarmhandMenu");
        if (menuType == null)
        {
            s_monitor.Log("[MP] Cannot find StardewValley.Menus.FarmhandMenu type.", LogLevel.Error);
            return null;
        }

        var ctor = menuType.GetConstructors()
            .FirstOrDefault(c =>
                c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType.IsAssignableFrom(client.GetType()));
        if (ctor == null)
        {
            s_monitor.Log("[MP] Cannot find FarmhandMenu constructor accepting the client type.", LogLevel.Error);
            return null;
        }

        var instance = ctor.Invoke(new[] { client });
        return instance as IClickableMenu;
    }

    private static void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        // 换日必须在主线程执行（answerDialogueAction 会动菜单/流程状态）；
        // console 命令线程只置位。逻辑等价原版 DebugCommands.Sleep。
        if (s_pendingSleep && Game1.hasLoadedGame)
        {
            s_pendingSleep = false;
            try
            {
                s_monitor.Log("[MP] va_test_sleep: triggering day change (Sleep_Yes)...", LogLevel.Info);
                Game1.player.isInBed.Value = true;
                Game1.player.sleptInTemporaryBed.Value = true;
                Game1.currentLocation.answerDialogueAction("Sleep_Yes", null);
            }
            catch (Exception ex)
            {
                s_monitor.Log($"[MP] va_test_sleep failed: {ex}", LogLevel.Error);
            }
        }

        if (s_pendingHost && Game1.hasLoadedGame)
        {
            try
            {
                DoHostServer();
            }
            catch (Exception ex)
            {
                s_monitor.Log($"[MP] Failed to host server (deferred): {ex}", LogLevel.Error);
                s_pendingHost = false;
            }
        }

        if (!s_isJoining || s_client == null)
        {
            return;
        }

        try
        {
            if ((DateTime.UtcNow - s_joinStartTime).TotalSeconds > JoinTimeoutSeconds)
            {
                s_monitor.Log("[MP] Join timeout: connection failed or no available farmhands received.",
                    LogLevel.Error);
                StopJoining();
                return;
            }

            var availableField = s_client.GetType().GetField("availableFarmhands",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (availableField == null)
            {
                return;
            }

            var available = availableField.GetValue(s_client) as IList;
            if (available == null)
            {
                return;
            }

            if (available.Count == 0)
            {
                s_monitor.Log("[MP] No available farmhands on server.", LogLevel.Error);
                StopJoining();
                return;
            }

            var first = available[0];
            if (first == null)
            {
                s_monitor.Log("[MP] First available farmhand is null.", LogLevel.Error);
                StopJoining();
                return;
            }

            ActivateFarmhand(first);
        }
        catch (Exception ex)
        {
            s_monitor.Log($"[MP] Error polling farmhands: {ex}", LogLevel.Error);
            StopJoining();
        }
    }

    private static void ActivateFarmhand(object farmerObj)
    {
        try
        {
            var farmer = farmerObj as Farmer;
            if (farmer == null)
            {
                var valueProp = farmerObj.GetType().GetProperty("Value",
                    BindingFlags.Instance | BindingFlags.Public);
                farmer = valueProp?.GetValue(farmerObj) as Farmer;
            }

            if (farmer == null)
            {
                s_monitor.Log("[MP] First available farmhand is not a Farmer.", LogLevel.Error);
                StopJoining();
                return;
            }

            Game1.game1?.loadForNewGame();
            SetPlayer(farmer);

            var availableField = s_client!.GetType().GetField("availableFarmhands",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            availableField?.SetValue(s_client, null);

            var sendMethod = s_client.GetType().GetMethod("sendPlayerIntroduction",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (sendMethod == null)
            {
                s_monitor.Log("[MP] Cannot find sendPlayerIntroduction method.", LogLevel.Error);
                StopJoining();
                return;
            }

            sendMethod.Invoke(s_client, null);
            Game1.gameMode = 6;
            s_isJoining = false;
            s_monitor.Log("[MP] Joined game and activated farmhand.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            s_monitor.Log($"[MP] Failed to activate farmhand: {ex}", LogLevel.Error);
            StopJoining();
        }
    }

    private static void EnsureHooks()
    {
        try
        {
            var field = typeof(Game1).GetField("hooks",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
            {
                s_monitor.Log("[MP] Cannot find Game1.hooks field.", LogLevel.Warn);
                return;
            }

            if (field.GetValue(null) != null)
            {
                return;
            }

            var hooksType = typeof(Game1).Assembly.GetType("StardewValley.Mods.ModHooks");
            if (hooksType == null)
            {
                s_monitor.Log("[MP] Cannot find StardewValley.Mods.ModHooks type.", LogLevel.Warn);
                return;
            }

            var instance = Activator.CreateInstance(hooksType);
            field.SetValue(null, instance);
            s_monitor.Log("[MP] Initialized Game1.hooks (was null).", LogLevel.Info);
        }
        catch (Exception ex)
        {
            s_monitor.Log($"[MP] Failed to initialize Game1.hooks: {ex.Message}", LogLevel.Warn);
        }
    }

    private static object? GetMultiplayer()
    {
        try
        {
            var field = typeof(Game1).GetField("multiplayer",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(null);
        }
        catch (Exception ex)
        {
            s_monitor.Log($"[MP] Failed to access Game1.multiplayer: {ex.Message}", LogLevel.Error);
            return null;
        }
    }

    private static void SetPlayer(Farmer farmer)
    {
        try
        {
            var property = typeof(Game1).GetProperty("player",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null)
            {
                s_monitor.Log("[MP] Cannot find Game1.player property.", LogLevel.Error);
                return;
            }

            property.SetValue(null, farmer);
        }
        catch (Exception ex)
        {
            s_monitor.Log($"[MP] Failed to set Game1.player: {ex.Message}", LogLevel.Error);
        }
    }

    private static void StopJoining()
    {
        s_isJoining = false;
        s_client = null;
        s_joinStartTime = DateTime.MinValue;
    }
}