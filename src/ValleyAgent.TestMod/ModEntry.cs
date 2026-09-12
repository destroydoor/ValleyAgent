#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Tools;
using ValleyAgent.Navigation;
using ValleyAgent.TestMod.Runners;
using ValleyAgent.Utils;

namespace ValleyAgent.TestMod;

public class ModEntry : Mod
{
    // 通过 SMAPI ModRegistry 跨 mod 获取 ValleyAgent API，避免直接引用 ValleyAgent.csproj。
    // 不能在 Entry() 中获取：SMAPI 此时还未完成所有 mod 的初始化，会抛出
    // "Tried to access a mod-provided API before all mods were initialized" 错误。
    // 正确做法是延迟到 GameLaunched 事件中获取（此时所有 mod 已完成 Entry()）。
    // 同时提供惰性获取回退：如果 GameLaunched 尚未触发但有代码访问 API，尝试获取一次。
    private static IValleyAgentApi? s_api;
    private static bool s_apiAcquired;
    private readonly HashSet<SButton> _pressedHotkeys = new();
    private int _autoTestDelayTicks;
    private CommandFileWatcher? _commandFileWatcher;
    private ExperienceTestRunner? _experienceRunner;

    private int _hotkeyDiagCounter;
    private V3TestRunner _v3Runner = null!;
    private VisualCaptureRunner? _visualRunner;

    // 主线程卡死看门狗（Phase 4 猎捕房主随机无日志卡死）：>3s 停滞自动落 .dmp 线程栈
    private Infrastructure.MainThreadWatchdog? _watchdog;

    public static ModEntry? Instance { get; private set; }

    public static IValleyAgentApi? API
    {
        get
        {
            if (s_apiAcquired)
            {
                return s_api;
            }

            // 惰性获取：GameLaunched 未触发时的回退路径
            s_apiAcquired = true;
            if (Instance != null)
            {
                try
                {
                    s_api = Instance.Helper.ModRegistry.GetApi<IValleyAgentApi>("dandm1.ValleyAgent");
                    if (s_api != null)
                    {
                        Instance.Monitor.Log("[TestMod] ValleyAgent API acquired via ModRegistry (lazy).",
                            LogLevel.Info);
                    }
                    else
                    {
                        Instance.Monitor.Log("[TestMod] Failed to acquire ValleyAgent API via ModRegistry (lazy).",
                            LogLevel.Error);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    Instance.Monitor.Log($"[TestMod] API acquisition failed (lazy): {ex.Message}", LogLevel.Error);
                }
            }

            return s_api;
        }
    }

    public static IMovementService? MovementService
    {
        get => API?.GetMovementService();
    }

    public override void Entry(IModHelper helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        Instance = this;

        // 不在 Entry() 中获取 API — SMAPI 此时未完成所有 mod 初始化，会抛错。
        // 改为在 GameLaunched 事件中获取（此时所有 mod 的 Entry() 已完成）。
        // 同时 API 属性的 getter 有惰性获取回退。

        // ── 加载统一测试配置 ──
        TestConfig.Load(helper, Monitor);
        TestFilter.ReloadFromConfig();
        Monitor.Log($"[TestMod] test_config.json 已加载: runner={TestConfig.Runner}, group={TestFilter.ActiveGroup}",
            LogLevel.Info);

        // 根据配置初始化 Runner
        _v3Runner = new V3TestRunner(helper, Monitor, true);

        _ = helper.ConsoleCommands.Add("vat_scene_fight",
            "Manually spawn monsters near the player for combat testing.\n" +
            "Usage: vat_scene_fight [count]",
            (cmd, args) =>
            {
                var count = ParseCount(args, 3);
                _ = TestScenes.SpawnFightScene(helper, Monitor, count);
                Monitor.Log($"Spawned {count} monsters. Use 'vat_clear' to remove them.", LogLevel.Info);
            });

        _ = helper.ConsoleCommands.Add("vat_scene_farm",
            "Manually spawn mature crops near the player for farming testing.\n" +
            "Usage: vat_scene_farm [count]",
            (cmd, args) =>
            {
                var count = ParseCount(args, 3);
                _ = TestScenes.SpawnFarmScene(helper, Monitor, count);
                Monitor.Log($"Spawned {count} crops. Use 'vat_clear' to remove them.", LogLevel.Info);
            });

        _ = helper.ConsoleCommands.Add("vat_scene_mine",
            "Manually spawn breakable rocks near the player for mining testing.\n" +
            "Usage: vat_scene_mine [count]",
            (cmd, args) =>
            {
                var count = ParseCount(args, 3);
                _ = TestScenes.SpawnMineScene(helper, Monitor, count);
                Monitor.Log($"Spawned {count} rocks. Use 'vat_clear' to remove them.", LogLevel.Info);
            });

        _ = helper.ConsoleCommands.Add("vat_scene_forage",
            "Manually spawn forageables near the player for foraging testing.\n" +
            "Usage: vat_scene_forage [count]",
            (cmd, args) =>
            {
                var count = ParseCount(args, 3);
                _ = TestScenes.SpawnForageScene(helper, Monitor, count);
                Monitor.Log($"Spawned {count} forageables. Use 'vat_clear' to remove them.", LogLevel.Info);
            });

        _ = helper.ConsoleCommands.Add("vat_clear",
            "Remove all entities spawned by this test mod.\n" +
            "Usage: vat_clear",
            (_, _) => TestScenes.ClearAll(helper, Monitor));

        _ = helper.ConsoleCommands.Add("vat_give_tools",
            "Give the player a rusty sword and pickaxe for manual scene testing.\n" +
            "Usage: vat_give_tools",
            (_, _) => GivePlayerTools());

        _ = helper.ConsoleCommands.Add("vat_status",
            "Display the current test runner status.\n" +
            "Usage: vat_status",
            (_, _) =>
            {
                Monitor.Log($"V3 runner running: {_v3Runner.IsRunning}", LogLevel.Info);
                Monitor.Log($"Experience runner running: {_experienceRunner?.IsRunning ?? false}", LogLevel.Info);
                Monitor.Log($"Visual runner running: {_visualRunner?.IsRunning ?? false}", LogLevel.Info);
                if (_experienceRunner?.IsRunning == true)
                {
                    Monitor.Log($"  Current test: {_experienceRunner.CurrentTestName}", LogLevel.Info);
                }

                if (_visualRunner?.IsRunning == true)
                {
                    Monitor.Log($"  Current test: {_visualRunner.CurrentTestName}", LogLevel.Info);
                }
            });

        _ = helper.ConsoleCommands.Add("vat_speed",
            "Set game speed multiplier for faster testing.\n" +
            "Usage: vat_speed <multiplier>\n" +
            "  1 = normal, 5 = 5x speed, 10 = 10x speed, 0 = reset to 1x\n" +
            "Affects: game time, decision interval, movement thresholds, NPC speed.",
            (_, args) =>
            {
                if (args.Length == 0 || !int.TryParse(args[0], out var mult))
                {
                    Monitor.Log($"Current speed: {DebugFlags.GameSpeedMultiplier}x", LogLevel.Info);
                    return;
                }

                mult = Math.Clamp(mult, 0, 20);
                if (mult == 0)
                {
                    mult = 1;
                }

                DebugFlags.GameSpeedMultiplier = mult;
                Monitor.Log($"Game speed set to {mult}x", LogLevel.Info);
                Monitor.Log($"  Decision interval: {1800 / mult} ticks (was 1800)", LogLevel.Info);
                Monitor.Log($"  NPC pathfind speed: {Math.Min(20, 2 * mult)} (was 2)", LogLevel.Info);
                Monitor.Log($"  Stuck threshold: {Math.Max(10, 120 / mult)} ticks (was 120)", LogLevel.Info);
            });

        _ = helper.ConsoleCommands.Add("vat_run",
            "Run a test group using the new orchestration runners.\n" +
            "Usage: vat_run <experience|pipeline|visual>\n" +
            "  experience — Run Experience tests (EXP001-EXP012) with mock LLM\n" +
            "  pipeline   — Run Pipeline tests (PIPE001-PIPE006) with mock LLM\n" +
            "  visual     — Run Visual capture tests (VIS001-VIS008) without mock LLM",
            (_, args) =>
            {
                if (args.Length == 0)
                {
                    Monitor.Log("Usage: vat_run <experience|pipeline|visual>", LogLevel.Warn);
                    return;
                }

                var group = args[0].ToLowerInvariant();
                switch (group)
                {
                    case "experience":
                        StartExperienceRunner("Experience");
                        break;
                    case "pipeline":
                        StartExperienceRunner("Pipeline");
                        break;
                    case "complex":
                        StartExperienceRunner("Complex");
                        break;
                    case "visual":
                        StartVisualRunner();
                        break;
                    default:
                        Monitor.Log($"Unknown group '{args[0]}'. Valid: experience, pipeline, visual", LogLevel.Warn);
                        break;
                }
            });

        _ = helper.ConsoleCommands.Add("vat_abort",
            "Abort the currently running experience/pipeline/visual test runner.\n" +
            "Usage: vat_abort",
            (_, _) =>
            {
                if (_experienceRunner?.IsRunning == true)
                {
                    _experienceRunner.Abort();
                    Monitor.Log("Experience runner aborted.", LogLevel.Info);
                }
                else if (_visualRunner?.IsRunning == true)
                {
                    _visualRunner.Abort();
                    Monitor.Log("Visual runner aborted.", LogLevel.Info);
                }
                else
                {
                    Monitor.Log("No new-style runner is currently active.", LogLevel.Info);
                }
            });

        // 联机 farmhand C1/C2/C3 测试命令
        MultiplayerTestCommands.Register(helper, Monitor);

        // 自动多人联机设置命令（主机 / 客机）
        MultiplayerSetupCommands.Register(helper, Monitor);

        // soak 测试驱动命令（3 人联机主机卡死复刻，2026-09-10）
        SoakCommands.Register(helper, Monitor);

        // 文件命令触发器：外部自动化可通过 test_commands.txt 触发任意 SMAPI 控制台命令
        _commandFileWatcher = CommandFileWatcher.Start(helper, Monitor);

        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        // 生产 mod 已自备看门狗（2026-09-11 生产化）：两个 dbghelp MiniDumpWriteDump 并发写同一进程
        // 会互相干扰，TestMod 侧让位。manifest 依赖 dandm1.ValleyAgent 保证生产 Entry 先执行，
        // Current 已赋值即可信。
        if (ValleyAgent.Infrastructure.MainThreadWatchdog.Current != null)
        {
            Monitor.Log("[Watchdog] Production watchdog armed — TestMod watchdog skipped (avoid concurrent MiniDumpWriteDump)",
                LogLevel.Info);
        }
        else
        {
            _watchdog = Infrastructure.MainThreadWatchdog.Start(helper.DirectoryPath, Monitor);
        }
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.Display.RenderedWorld += OnRenderedWorld;
        helper.Events.Display.RenderedHud += OnRenderedHud;

        Monitor.Log("ValleyAgent TestMod loaded. Type 'help vat_status' for usage.", LogLevel.Info);
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        // GameLaunched 在所有 mod 的 Entry() 完成后触发，是获取 mod-provided API 的正确时机
        s_apiAcquired = true;
        try
        {
            s_api = Helper.ModRegistry.GetApi<IValleyAgentApi>("dandm1.ValleyAgent");
            if (s_api != null)
            {
                Monitor.Log("[TestMod] ValleyAgent API acquired via ModRegistry (GameLaunched).", LogLevel.Info);
            }
            else
            {
                Monitor.Log(
                    "[TestMod] Failed to acquire ValleyAgent API via ModRegistry (GameLaunched). Tests will be skipped.",
                    LogLevel.Error);
            }
        }
        catch (InvalidOperationException ex)
        {
            Monitor.Log($"[TestMod] API acquisition failed (GameLaunched): {ex.Message}", LogLevel.Error);
        }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        _watchdog?.Beat();

        ForceActiveIfInactive();

        // 2026-08-17：文件命令在主线程执行（CommandFileWatcher 在 Timer 线程 Trigger，
        // va_mp_host 等改游戏状态的命令必须主线程——后台线程设 multiplayerMode 会与
        // updatePendingConnections 竞态导致开服 10048）。
        _commandFileWatcher?.DrainPendingCommands();

        var runnerName = TestConfig.Runner.ToLowerInvariant();

        switch (runnerName)
        {
            case "v3":
                _v3Runner.Update();
                break;
            case "orchestrator":
                _v3Runner.Update(); // orchestrator not fully wired yet — maps to v3 for now
                break;
            case "manual":
                // 手动模式：不运行任何自动测试引擎，等待玩家通过控制台命令触发场景。
                break;
            default:
                _v3Runner.Update();
                break;
        }

        // ── Manual hotkeys for scene spawning (used by PlayerInputDriver) ──
        UpdateManualHotkeys();

        if (_autoTestDelayTicks > 0)
        {
            _autoTestDelayTicks--;
            if (_autoTestDelayTicks == 0)
            {
                Monitor.Log($"Starting V3 test suite (groups: {TestFilter.ActiveGroup})...", LogLevel.Info);
            }
        }

        // ── V3TestRunner 自身已完成退出，无需重复处理 ──

        // 驱动新的 Experience/Pipeline/Visual runner（如果它们已被 vat_run 启动）
        try
        {
            if (_experienceRunner?.IsRunning == true)
            {
                _experienceRunner.Update();
            }
            else if (_visualRunner?.IsRunning == true)
            {
                _visualRunner.Update();
            }
        }
        catch (InvalidOperationException ex)
        {
            Monitor.Log($"[TestMod] Runner update error: {ex.Message}", LogLevel.Error);
        }
        catch (NullReferenceException ex)
        {
            Monitor.Log($"[TestMod] Runner update error: {ex.Message}", LogLevel.Error);
        }
    }

    // 让窗口失焦时游戏继续运行（替代失效的 IsActive 反射方案）。
    //
    // 根因（2026-08-02）：release build 下 Game1.Update 在窗口失焦时提前 return
    // （Game1.cs ~3902：`(!IsActiveNoOverlay && Program.releaseBuild) && options.pauseWhenOutOfFocus`），
    // 导致 fade-to-black 冻结——warpFarmer 永不完成、Game1.quit 永不处理。
    // 反射强制 IsActive 失败：此 MonoGame 构建的 Game 类无 _isActive 后备字段，IsActive 属性不可写。
    // 正确做法：每 tick 强制 options.pauseWhenOutOfFocus=false，直接消除该提前 return 的条件。
    private void ForceActiveIfInactive()
    {
        try
        {
            if (Game1.game1 == null)
            {
                return;
            }

            if (Game1.options != null && Game1.options.pauseWhenOutOfFocus)
            {
                Game1.options.pauseWhenOutOfFocus = false;
            }

            if (Game1.paused)
            {
                Game1.paused = false;
            }
        }
        catch (InvalidOperationException ex)
        {
            Monitor.Log($"[TestMod] ForceActive error: {ex.Message}", LogLevel.Warn);
        }
        catch (NullReferenceException ex)
        {
            Monitor.Log($"[TestMod] ForceActive error: {ex.Message}", LogLevel.Warn);
        }
        catch (ArgumentException ex)
        {
            Monitor.Log($"[TestMod] ForceActive error: {ex.Message}", LogLevel.Warn);
        }
    }

    private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
    {
        // No-op: Visual capture is driven by VisualCaptureRunner.Update() now.
    }

    private void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        // No-op: Visual capture is driven by VisualCaptureRunner.Update() now.
    }

    private void UpdateManualHotkeys()
    {
        if (!Game1.hasLoadedGame || Game1.player == null)
        {
            _pressedHotkeys.Clear();
            return;
        }

        // Diagnostic: log keyboard state every 60 ticks (~1s) to verify SMAPI receives input
        _hotkeyDiagCounter++;
        if (_hotkeyDiagCounter >= 60)
        {
            _hotkeyDiagCounter = 0;
            var f8 = Helper.Input.IsDown(SButton.F8);
            var f9 = Helper.Input.IsDown(SButton.F9);
            var w = Helper.Input.IsDown(SButton.W);
            var s = Helper.Input.IsDown(SButton.S);
            var c = Helper.Input.IsDown(SButton.C);
            var e = Helper.Input.IsDown(SButton.E);
            Monitor.Log($"[InputDiag] F8={f8} F9={f9} W={w} S={s} C={c} E={e} IsActive={Game1.game1?.IsActive}");
        }

        CheckManualHotkey(SButton.F9, () =>
        {
            GivePlayerTools();
            _ = TestScenes.SpawnFightScene(Helper, Monitor, 5);
            Monitor.Log("[ManualHotkey] F9: gave tools and spawned 5 monsters.", LogLevel.Info);
        });

        CheckManualHotkey(SButton.F10, () =>
        {
            _ = TestScenes.SpawnFarmScene(Helper, Monitor, 5);
            Monitor.Log("[ManualHotkey] F10: spawned 5 crops.", LogLevel.Info);
        });

        CheckManualHotkey(SButton.F11, () =>
        {
            _ = TestScenes.SpawnMineScene(Helper, Monitor, 5);
            Monitor.Log("[ManualHotkey] F11: spawned 5 rocks.", LogLevel.Info);
        });

        CheckManualHotkey(SButton.F12, () =>
        {
            _ = TestScenes.SpawnForageScene(Helper, Monitor, 5);
            Monitor.Log("[ManualHotkey] F12: spawned 5 forageables.", LogLevel.Info);
        });

        CheckManualHotkey(SButton.F8, () =>
        {
            TestScenes.ClearAll(Helper, Monitor);
            Monitor.Log("[ManualHotkey] F8: cleared all test entities.", LogLevel.Info);
        });

        CheckManualHotkey(SButton.F1, () =>
        {
            var api = API;
            if (api == null)
            {
                Monitor.Log("[TestTool] F1: API not available", LogLevel.Warn);
                return;
            }

            var agents = api.GetActiveAgentNames();
            if (agents.Length == 0)
            {
                Monitor.Log("[TestTool] F1: no active agents", LogLevel.Warn);
                return;
            }

            var npc = agents[0];
            var ok = api.TryTriggerGift(npc, out var itemName);
            Monitor.Log($"[TestTool] F1: TryTriggerGift({npc}) -> {ok}, item={itemName}",
                ok ? LogLevel.Info : LogLevel.Warn);
        });

        CheckManualHotkey(SButton.F2, () =>
        {
            var api = API;
            if (api == null)
            {
                Monitor.Log("[TestTool] F2: API not available", LogLevel.Warn);
                return;
            }

            var agents = api.GetActiveAgentNames();
            if (agents.Length == 0)
            {
                Monitor.Log("[TestTool] F2: no active agents", LogLevel.Warn);
                return;
            }

            var npc = agents[0];
            var ok = api.TrySpeak(npc, "Hello from pre_speak test!", 3000);
            Monitor.Log($"[TestTool] F2: TrySpeak({npc}) -> {ok}", ok ? LogLevel.Info : LogLevel.Warn);
        });

        CheckManualHotkey(SButton.F3, () =>
        {
            var api = API;
            if (api == null)
            {
                Monitor.Log("[TestTool] F3: API not available", LogLevel.Warn);
                return;
            }

            var agents = api.GetActiveAgentNames();
            if (agents.Length == 0)
            {
                Monitor.Log("[TestTool] F3: no active agents", LogLevel.Warn);
                return;
            }

            var npc = agents[0];
            var ok = api.TrySetAgentState(npc, "FARM");
            Monitor.Log($"[TestTool] F3: TrySetAgentState({npc}, FARM) -> {ok}", ok ? LogLevel.Info : LogLevel.Warn);
        });

        CheckManualHotkey(SButton.F4, () =>
        {
            var api = API;
            if (api == null)
            {
                Monitor.Log("[TestTool] F4: API not available", LogLevel.Warn);
                return;
            }

            var agents = api.GetActiveAgentNames();
            if (agents.Length == 0)
            {
                Monitor.Log("[TestTool] F4: no active agents", LogLevel.Warn);
                return;
            }

            var npc = agents[0];
            var ok = api.TrySetAgentState(npc, "MINE");
            Monitor.Log($"[TestTool] F4: TrySetAgentState({npc}, MINE) -> {ok}", ok ? LogLevel.Info : LogLevel.Warn);
        });

        CheckManualHotkey(SButton.F5, () =>
        {
            var api = API;
            if (api == null)
            {
                Monitor.Log("[TestTool] F5: API not available", LogLevel.Warn);
                return;
            }

            var agents = api.GetActiveAgentNames();
            if (agents.Length == 0)
            {
                Monitor.Log("[TestTool] F5: no active agents", LogLevel.Warn);
                return;
            }

            var npc = agents[0];
            var ok = api.TrySetAgentState(npc, "FORAGE");
            Monitor.Log($"[TestTool] F5: TrySetAgentState({npc}, FORAGE) -> {ok}", ok ? LogLevel.Info : LogLevel.Warn);
        });

        CheckManualHotkey(SButton.F6, () =>
        {
            var api = API;
            if (api == null)
            {
                Monitor.Log("[TestTool] F6: API not available", LogLevel.Warn);
                return;
            }

            var agents = api.GetActiveAgentNames();
            if (agents.Length == 0)
            {
                Monitor.Log("[TestTool] F6: no active agents", LogLevel.Warn);
                return;
            }

            var npc = agents[0];
            var ok = api.TrySetAgentState(npc, "FOLLOW");
            Monitor.Log($"[TestTool] F6: TrySetAgentState({npc}, FOLLOW) -> {ok}", ok ? LogLevel.Info : LogLevel.Warn);
        });
    }

    private void CheckManualHotkey(SButton button, Action action)
    {
        // 优先用 SMAPI 输入（真实按键），回退到 XNA Keyboard.GetState()（支持 Autopilot 虚拟按键）
        var isDown = Helper.Input.IsDown(button);
        if (!isDown && Enum.TryParse(button.ToString(), out Keys xnaKey))
        {
            // SMAPI 的 Helper.Input 不读取 InputHijacker 注入的虚拟按键，
            // 额外检查 XNA Keyboard.GetState() 以支持 Autopilot HTTP /input/key 端点。
            var ks = Keyboard.GetState();
            isDown = ks.IsKeyDown(xnaKey);
        }

        if (isDown && _pressedHotkeys.Add(button))
        {
            action();
        }
        else if (!isDown)
        {
            _ = _pressedHotkeys.Remove(button);
        }
    }

    private void GivePlayerTools()
    {
        var player = Game1.player;
        if (player == null)
        {
            Monitor.Log("Player not available.", LogLevel.Warn);
            return;
        }

        var sword = new MeleeWeapon("0"); // Rusty Sword
        var pickaxe = new Pickaxe();

        if (player.Items.Count > 0)
        {
            player.Items[0] = sword;
        }
        else
        {
            player.addItemToInventoryBool(sword);
        }

        if (player.Items.Count > 1)
        {
            player.Items[1] = pickaxe;
        }
        else
        {
            player.addItemToInventoryBool(pickaxe);
        }

        player.CurrentToolIndex = 0;
        Monitor.Log("Gave player rusty sword (slot 1) and pickaxe (slot 2).", LogLevel.Info);
    }

    private void StartExperienceRunner(string group)
    {
        // 如果已有 runner 在运行，先停止
        if (_experienceRunner?.IsRunning == true)
        {
            Monitor.Log($"Aborting current Experience runner (group={group}) to start new one.", LogLevel.Warn);
            _experienceRunner.Abort();
        }

        _experienceRunner?.Dispose();
        _experienceRunner = new ExperienceTestRunner(Helper, Monitor, true);
        try
        {
            _experienceRunner.Start(group);
        }
        catch (ArgumentException ex)
        {
            Monitor.Log($"Failed to start Experience runner: {ex.Message}", LogLevel.Error);
        }
        catch (InvalidOperationException ex)
        {
            Monitor.Log($"Failed to start Experience runner: {ex.Message}", LogLevel.Error);
        }
    }

    private void StartVisualRunner()
    {
        if (_visualRunner?.IsRunning == true)
        {
            Monitor.Log("Aborting current Visual runner to start new one.", LogLevel.Warn);
            _visualRunner.Abort();
        }

        _visualRunner?.Dispose();
        _visualRunner = new VisualCaptureRunner(Helper, Monitor, true);
        try
        {
            _visualRunner.Start();
        }
        catch (InvalidOperationException ex)
        {
            Monitor.Log($"Failed to start Visual runner: {ex.Message}", LogLevel.Error);
        }
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e) => Monitor.Log(
        "Save loaded. Tests available via 'vat_run <experience|pipeline|visual>' command.", LogLevel.Info);

    private static int ParseCount(string[] args, int defaultValue) =>
        args.Length > 0 && int.TryParse(args[0], out var count) && count > 0 ? count : defaultValue;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _experienceRunner?.Dispose();
            _visualRunner?.Dispose();
            _commandFileWatcher?.Dispose();
        }
    }
}