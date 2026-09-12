using System;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ValleyAgent.Autopilot.Capture;
using ValleyAgent.Autopilot.HttpServer;
using ValleyAgent.Autopilot.Input;
using ValleyAgent.Autopilot.Speed;

namespace ValleyAgent.Autopilot
{
    public class ModEntry : Mod
    {
        private VirtualInputState? _virtualInput;
        private SpeedController? _speedController;
        private InputHijacker? _inputHijacker;
        private ScreenCapture? _screenCapture;
        private AutopilotHttpServer? _httpServer;
        private Harmony? _harmony;

        public override void Entry(IModHelper helper)
        {
            ArgumentNullException.ThrowIfNull(helper);
            _instance = this;
            _virtualInput = new VirtualInputState();
            _speedController = new SpeedController();
            _inputHijacker = new InputHijacker(_virtualInput);
            _screenCapture = new ScreenCapture(Monitor);

            _harmony = new Harmony(ModManifest.UniqueID);
            _speedController.ApplyPatches(_harmony);
            _inputHijacker.ApplyPatches(_harmony);

            // 调试埋点：记录右键/动作键路径是否被触发，用于诊断虚拟鼠标右键问题
            _harmony.Patch(
                original: AccessTools.Method(typeof(Game1), nameof(Game1.pressActionButton)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(BeforePressActionButton))
            );
            _harmony.Patch(
                original: AccessTools.Method(typeof(Game1), nameof(Game1.tryToCheckAt)),
                prefix: new HarmonyMethod(typeof(ModEntry), nameof(BeforeTryToCheckAt))
            );

            helper.ConsoleCommands.Add("autopilot_stop", "Stop autopilot and restore player input.", (_, _) =>
            {
                if (_virtualInput == null) return;
                _virtualInput.IsActive = false;
                _virtualInput.ReleaseAll();
                Monitor.Log("Autopilot stopped via console command.", LogLevel.Info);
            });

            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;

            Monitor.Log("ValleyAgent.Autopilot loaded.", LogLevel.Info);
        }

        private static ModEntry? _instance;

        private static void BeforePressActionButton(
            Microsoft.Xna.Framework.Input.KeyboardState currentKBState,
            Microsoft.Xna.Framework.Input.MouseState currentMouseState,
            Microsoft.Xna.Framework.Input.GamePadState currentPadState)
        {
            var old = Game1.oldMouseState;
            _instance?.Monitor.Log(
                $"[Diag] pressActionButton: cur=({currentMouseState.X},{currentMouseState.Y},L={currentMouseState.LeftButton},R={currentMouseState.RightButton}) " +
                $"old=({old.X},{old.Y},R={old.RightButton}) viewport=({Game1.viewport.X},{Game1.viewport.Y})",
                LogLevel.Trace);
        }

        private static void BeforeTryToCheckAt(Microsoft.Xna.Framework.Vector2 grabTile)
        {
            _instance?.Monitor.Log(
                $"[Diag] tryToCheckAt: grabTile=({grabTile.X:F1},{grabTile.Y:F1}) " +
                $"playerTile=({Game1.player?.TilePoint.X},{Game1.player?.TilePoint.Y})",
                LogLevel.Trace);
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            ArgumentNullException.ThrowIfNull(_virtualInput);
            ArgumentNullException.ThrowIfNull(_speedController);
            ArgumentNullException.ThrowIfNull(_screenCapture);

            // 联机 farmhand 端不需要本地 HTTP 控制接口；避免与主机 5555 端口冲突。
            var testInstance = Environment.GetEnvironmentVariable("VALLEY_TEST_INSTANCE");
            if (testInstance == "farmhand")
            {
                Monitor.Log("Autopilot HTTP server disabled for farmhand instance.", LogLevel.Debug);
                return;
            }

            var routeHandlers = new RouteHandlers(_virtualInput, _speedController, _screenCapture);
            _httpServer = new AutopilotHttpServer(Monitor, routeHandlers);
            try
            {
                _httpServer.Start();
            }
            catch (System.Net.HttpListenerException ex)
            {
                Monitor.Log($"Failed to start HTTP Server: {ex.Message}. Autopilot API will not be available.", LogLevel.Error);
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                Monitor.Log($"Failed to start HTTP Server: {ex.Message}. Autopilot API will not be available.", LogLevel.Error);
            }
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            _screenCapture?.OnUpdateTicked();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _httpServer?.Dispose();
                _harmony?.UnpatchAll(ModManifest.UniqueID);
            }
            base.Dispose(disposing);
        }
    }
}
