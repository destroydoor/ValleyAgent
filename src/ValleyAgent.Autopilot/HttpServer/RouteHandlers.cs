using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using StardewValley;
using ValleyAgent.Autopilot.Capture;
using ValleyAgent.Autopilot.Input;
using ValleyAgent.Autopilot.Speed;
using ValleyAgent.Autopilot.State;

namespace ValleyAgent.Autopilot.HttpServer
{
    public sealed class RouteHandlers
    {
        private readonly VirtualInputState _virtualInput;
        private readonly SpeedController _speedController;
        private readonly ScreenCapture _screenCapture;

        public RouteHandlers(
            VirtualInputState virtualInput,
            SpeedController speedController,
            ScreenCapture screenCapture)
        {
            _virtualInput = virtualInput;
            _speedController = speedController;
            _screenCapture = screenCapture;
        }

        public (string body, int statusCode) Handle(string method, string path, System.Net.HttpListenerRequest request)
        {
            return path switch
            {
                "/autopilot/start" => HandleAutopilotStart(),
                "/autopilot/stop" => HandleAutopilotStop(),
                "/autopilot/status" => HandleAutopilotStatus(),
                "/speed" when method == "POST" => HandleSetSpeed(request),
                "/input/key" when method == "POST" => HandleInputKey(request),
                "/input/mouse" when method == "POST" => HandleInputMouse(request),
                "/input/click" when method == "POST" => HandleInputClick(request),
                "/input/release_all" when method == "POST" => HandleReleaseAll(),
                "/screenshot" => HandleScreenshot(),
                "/state" => HandleState(),
                "/state/npcs" => HandleNpcsState(),
                "/ui/open_chat" when method == "POST" => HandleOpenChat(request),
                "/ui/debug_chat" => HandleDebugChat(),
                "/ui/dialogue_state" => HandleDialogueState(),
                "/player/warp" when method == "POST" => HandlePlayerWarp(request),
                "/action/interact" when method == "POST" => HandleInteract(request),
                _ => ("{\"error\":\"Not found\"}", 404)
            };
        }

        /// <summary>
        /// Opens the ValleyAgent AgentChatMenu for a given NPC.
        /// This is a test-only endpoint that uses reflection to find AgentChatMenu
        /// in the ValleyAgent mod assembly and invokes its static Show() method.
        /// </summary>
        private static (string, int) HandleOpenChat(System.Net.HttpListenerRequest request)
        {
            var body = ReadBody(request);
            string npcName;
            try
            {
                var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("npc", out var npcEl))
                {
                    return ("{\"error\":\"Missing 'npc' field\"}", 400);
                }
                npcName = npcEl.GetString() ?? "";
            }
            catch (JsonException ex)
            {
                return ($"{{\"error\":\"Invalid JSON: {ex.Message}\"}}", 400);
            }
            if (string.IsNullOrWhiteSpace(npcName))
            {
                return ("{\"error\":\"Empty npc name\"}", 400);
            }

            try
            {
                // Find AgentChatMenu type via reflection
                var menuType = FindType("ValleyAgent.UI.AgentChatMenu");
                if (menuType == null)
                {
                    return ("{\"error\":\"AgentChatMenu type not found\"}", 500);
                }

                // Find static Show(string, Action<string>, Action) method
                var showMethod = menuType.GetMethod(
                    "Show",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(string), typeof(Action<string>), typeof(Action) },
                    modifiers: null);

                if (showMethod == null)
                {
                    return ("{\"error\":\"AgentChatMenu.Show method not found\"}", 500);
                }

                // Invoke Show with no-op callbacks
                var result = showMethod.Invoke(null, new object?[] { npcName, null, null });
                if (result == null)
                {
                    return ("{\"error\":\"AgentChatMenu.Show returned null\"}", 500);
                }
                return ($"{{\"status\":\"opened\",\"npc\":\"{npcName}\"}}", 200);
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                return ($"{{\"error\":\"{EscapeJson(tie.InnerException.Message)}\"}}", 500);
            }
            catch (ArgumentException ex)
            {
                return ($"{{\"error\":\"{EscapeJson(ex.Message)}\"}}", 400);
            }
        }

        private static Type? FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, throwOnError: false);
                if (t != null) return t;
            }
            return null;
        }

        /// <summary>
        /// Returns debug info about the active AgentChatMenu (dialog/textbox coords, screen size).
        /// Used by tests to verify input box position.
        /// </summary>
        private static (string, int) HandleDebugChat()
        {
            try
            {
                var menuType = FindType("ValleyAgent.UI.AgentChatMenu");
                if (menuType == null) return ("{\"error\":\"AgentChatMenu type not found\"}", 500);

                var activeMenu = Game1.activeClickableMenu;
                if (activeMenu == null) return ("{\"error\":\"No active menu\"}", 404);
                if (!menuType.IsInstanceOfType(activeMenu)) return ("{\"error\":\"Active menu is not AgentChatMenu\"}", 404);

                var inst = menuType.GetProperty("DebugDrawTransitionBounds", BindingFlags.Public | BindingFlags.Static);
                // Inspect DialogueBox fields
                var dbType = typeof(StardewValley.Menus.DialogueBox);
                var xField = dbType.GetField("x", BindingFlags.Public | BindingFlags.Instance);
                var yField = dbType.GetField("y", BindingFlags.Public | BindingFlags.Instance);
                var wField = dbType.GetField("width", BindingFlags.Public | BindingFlags.Instance);
                var hField = dbType.GetField("height", BindingFlags.Public | BindingFlags.Instance);
                var transXField = dbType.GetField("transitionX", BindingFlags.Public | BindingFlags.Instance);
                var transYField = dbType.GetField("transitionY", BindingFlags.Public | BindingFlags.Instance);
                var transWField = dbType.GetField("transitionWidth", BindingFlags.Public | BindingFlags.Instance);
                var transHField = dbType.GetField("transitionHeight", BindingFlags.Public | BindingFlags.Instance);
                var transField = dbType.GetField("transitioning", BindingFlags.Public | BindingFlags.Instance);

                int x = xField != null ? (xField.GetValue(activeMenu) as int? ?? 0) : 0;
                int y = yField != null ? (yField.GetValue(activeMenu) as int? ?? 0) : 0;
                int w = wField != null ? (wField.GetValue(activeMenu) as int? ?? 0) : 0;
                int h = hField != null ? (hField.GetValue(activeMenu) as int? ?? 0) : 0;
                int tX = transXField != null ? (transXField.GetValue(activeMenu) as int? ?? 0) : 0;
                int tY = transYField != null ? (transYField.GetValue(activeMenu) as int? ?? 0) : 0;
                int tW = transWField != null ? (transWField.GetValue(activeMenu) as int? ?? 0) : 0;
                int tH = transHField != null ? (transHField.GetValue(activeMenu) as int? ?? 0) : 0;
                bool transitioning = transField != null && transField.GetValue(activeMenu) is bool b && b;

                // Inspect textbox via reflection on private field
                var tbField = menuType.GetField("_textBox", BindingFlags.NonPublic | BindingFlags.Instance);
                var tb = tbField?.GetValue(activeMenu);
                int tbX = 0, tbY = 0, tbW = 0, tbH = 0;
                string tbState = "null";
                if (tb != null)
                {
                    var tbType = tb.GetType();
                    var xProp = tbType.GetProperty("X");
                    var yProp = tbType.GetProperty("Y");
                    var wProp = tbType.GetProperty("Width");
                    var hProp = tbType.GetProperty("Height");
                    var selProp = tbType.GetProperty("Selected");
                    tbX = xProp != null ? (xProp.GetValue(tb) as int? ?? 0) : 0;
                    tbY = yProp != null ? (yProp.GetValue(tb) as int? ?? 0) : 0;
                    tbW = wProp != null ? (wProp.GetValue(tb) as int? ?? 0) : 0;
                    tbH = hProp != null ? (hProp.GetValue(tb) as int? ?? 0) : 0;
                    var sel = selProp != null ? selProp.GetValue(tb) : null;
                    tbState = $"selected={sel}";
                }

                int sw = Game1.viewport.Width;
                int sH = Game1.viewport.Height;

                var json = $"{{\"screen\":{{\"w\":{sw},\"h\":{sH}}},\"dialog\":{{\"x\":{x},\"y\":{y},\"w\":{w},\"h\":{h}}},\"transition\":{{\"x\":{tX},\"y\":{tY},\"w\":{tW},\"h\":{tH},\"active\":{transitioning.ToString().ToLowerInvariant()}}},\"textbox\":{{\"x\":{tbX},\"y\":{tbY},\"w\":{tbW},\"h\":{tbH},\"state\":\"{tbState}\"}}}}";
                return (json, 200);
            }
            catch (TargetInvocationException tie)
            {
                return ($"{{\"error\":\"{EscapeJson(tie.InnerException?.Message ?? tie.Message)}\"}}", 500);
            }
            catch (InvalidOperationException ex)
            {
                return ($"{{\"error\":\"{EscapeJson(ex.Message)}\"}}", 500);
            }
        }

        /// <summary>
        /// Reflects ValleyAgent.Patches.DialogueBoxInputPatch static state so tests can
        /// observe the agent dialogue flow: active npc, player input, waiting flag,
        /// and the LLM reply typewriter (visible/full text, complete flag).
        /// </summary>
        private static (string, int) HandleDialogueState()
        {
            try
            {
                var patchType = FindType("ValleyAgent.Patches.DialogueBoxInputPatch");
                if (patchType == null) return ("{\"error\":\"DialogueBoxInputPatch type not found\"}", 500);

                const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;
                string npc = patchType.GetField("_activeAgentNpc", Flags)?.GetValue(null) as string ?? "";
                string input = "";
                if (patchType.GetField("_inputText", Flags)?.GetValue(null) is StringBuilder sb)
                    input = sb.ToString();
                bool waiting = patchType.GetField("_isWaitingForResponse", Flags)?.GetValue(null) is true;

                // 回复内容：原版渲染后，LLM 回复被 setNewDialogue 写入 currentSpeaker 的 CurrentDialogue。
                // 占位符 "..." 不算回复；replyComplete 近似为“回复已到达且 LLM 调用已结束”。
                string replyVisible = "", replyFull = "";
                var speaker = Game1.currentSpeaker;
                if (speaker != null && speaker.CurrentDialogue.Count > 0)
                {
                    try { replyFull = speaker.CurrentDialogue.Peek().getCurrentDialogue() ?? ""; }
                    catch (InvalidOperationException) { }
                }
                replyVisible = replyFull;
                bool hasReply = !string.IsNullOrEmpty(replyFull) && replyFull.Trim() != "...";
                bool replyComplete = hasReply && !waiting;

                string menu = Game1.activeClickableMenu?.GetType().Name ?? "";
                string boxGeom = "null";
                if (Game1.activeClickableMenu is StardewValley.Menus.DialogueBox db)
                {
                    boxGeom = "{\"x\":" + db.xPositionOnScreen + ",\"y\":" + db.yPositionOnScreen
                        + ",\"w\":" + db.width + ",\"h\":" + db.height + "}";
                }
                var json = "{"
                    + "\"npc\":\"" + EscapeJson(npc) + "\","
                    + "\"input\":\"" + EscapeJson(input) + "\","
                    + "\"waiting\":" + (waiting ? "true" : "false") + ","
                    + "\"hasReply\":" + (hasReply ? "true" : "false") + ","
                    + "\"replyComplete\":" + (replyComplete ? "true" : "false") + ","
                    + "\"replyVisible\":\"" + EscapeJson(replyVisible) + "\","
                    + "\"replyFull\":\"" + EscapeJson(replyFull) + "\","
                    + "\"menu\":\"" + EscapeJson(menu) + "\","
                    + "\"dialogueUp\":" + (Game1.dialogueUp ? "true" : "false") + ","
                    + "\"box\":" + boxGeom
                    + "}";
                return (json, 200);
            }
            catch (InvalidOperationException ex)
            {
                return ($"{{\"error\":\"{EscapeJson(ex.Message)}\"}}", 500);
            }
        }

        /// <summary>
        /// Test-only: warp the player. {"x":tileX,"y":tileY,"location":optional name}.
        /// Without "location" warps within the current map (pixel-exact tile target).
        /// </summary>
        private static (string, int) HandlePlayerWarp(System.Net.HttpListenerRequest request)
        {
            try
            {
                var doc = JsonDocument.Parse(ReadBody(request));
                int x = doc.RootElement.GetProperty("x").GetInt32();
                int y = doc.RootElement.GetProperty("y").GetInt32();
                string? loc = doc.RootElement.TryGetProperty("location", out var le) ? le.GetString() : null;
                if (string.IsNullOrEmpty(loc))
                {
                    if (Game1.player == null) return ("{\"error\":\"no player\"}", 500);
                    Game1.player.Position = new Microsoft.Xna.Framework.Vector2(x * 64f, y * 64f);
                }
                else
                {
                    var location = Game1.getLocationFromName(loc);
                    if (location == null) return ($"{{\"error\":\"location not found: {EscapeJson(loc)}\"}}", 404);
                    Game1.warpFarmer(loc, x, y, false);
                }
                return ("{\"status\":\"warped\"}", 200);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return ($"{{\"error\":\"{EscapeJson(ex.Message)}\"}}", 400);
            }
        }

        /// <summary>
        /// 玩家等效抽象操作：右键点击 NPC 说话。直接调用原版 NPC.checkAction，
        /// 与真实玩家右键点击走完全相同的代码路径（含所有 Harmony 补丁），
        /// 但不依赖鼠标坐标/窗口焦点等易碎环节。
        /// </summary>
        private static (string, int) HandleInteract(System.Net.HttpListenerRequest request)
        {
            try
            {
                var doc = JsonDocument.Parse(ReadBody(request));
                string name = doc.RootElement.GetProperty("npc").GetString() ?? "";
                var location = Game1.player?.currentLocation;
                if (location == null) return ("{\"error\":\"no location\"}", 500);
                StardewValley.NPC? target = null;
                foreach (var c in location.characters)
                {
                    if (c.Name == name) { target = c; break; }
                }
                if (target == null) return ($"{{\"error\":\"npc not in location: {EscapeJson(name)}\"}}", 404);
                bool result = target.checkAction(Game1.player, location);
                return ($"{{\"status\":\"ok\",\"result\":{(result ? "true" : "false")}}}", 200);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or NullReferenceException)
            {
                return ($"{{\"error\":\"{EscapeJson(ex.Message)}\"}}", 400);
            }
        }

        private static Type? FindType2(string fullName) => null;

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private (string, int) HandleAutopilotStart()
        {
            _virtualInput.IsActive = true;
            return ("{\"status\":\"autopilot_active\"}", 200);
        }

        private (string, int) HandleAutopilotStop()
        {
            _virtualInput.IsActive = false;
            _virtualInput.ReleaseAll();
            return ("{\"status\":\"autopilot_inactive\"}", 200);
        }

        private (string, int) HandleAutopilotStatus()
        {
            return ($"{{\"active\":{_virtualInput.IsActive.ToString().ToLowerInvariant()}}}", 200);
        }

        private (string, int) HandleSetSpeed(System.Net.HttpListenerRequest request)
        {
            var body = ReadBody(request);
            var doc = JsonDocument.Parse(body);
            var factor = doc.RootElement.GetProperty("factor").GetSingle();
            _speedController.SpeedFactor = factor;
            return ($"{{\"speedFactor\":{_speedController.SpeedFactor}}}", 200);
        }

        private (string, int) HandleInputKey(System.Net.HttpListenerRequest request)
        {
            var body = ReadBody(request);
            var doc = JsonDocument.Parse(body);
            var key = doc.RootElement.GetProperty("key").GetString() ?? "";
            var pressed = doc.RootElement.GetProperty("pressed").GetBoolean();
            _virtualInput.SetKey(key, pressed);
            return ("{\"status\":\"ok\"}", 200);
        }

        private (string, int) HandleInputMouse(System.Net.HttpListenerRequest request)
        {
            var body = ReadBody(request);
            var doc = JsonDocument.Parse(body);
            var x = doc.RootElement.GetProperty("x").GetInt32();
            var y = doc.RootElement.GetProperty("y").GetInt32();
            var left = doc.RootElement.TryGetProperty("leftButton", out var lb) && lb.GetBoolean();
            var right = doc.RootElement.TryGetProperty("rightButton", out var rb) && rb.GetBoolean();
            _virtualInput.SetMouse(x, y, left, right);
            return ("{\"status\":\"ok\"}", 200);
        }

        private (string, int) HandleInputClick(System.Net.HttpListenerRequest request)
        {
            var body = ReadBody(request);
            var doc = JsonDocument.Parse(body);
            var x = doc.RootElement.GetProperty("x").GetInt32();
            var y = doc.RootElement.GetProperty("y").GetInt32();
            var isRight = doc.RootElement.TryGetProperty("button", out var btn) && btn.GetString() == "right";
            _virtualInput.SetMouse(x, y, !isRight, isRight);
            return ("{\"status\":\"ok\"}", 200);
        }

        private (string, int) HandleReleaseAll()
        {
            _virtualInput.ReleaseAll();
            return ("{\"status\":\"ok\"}", 200);
        }

        private (string, int) HandleScreenshot()
        {
            _screenCapture.RequestCapture();
            var cached = _screenCapture.WaitForScreenshot(2000);
            if (cached != null)
            {
                var (png, w, h) = cached.Value;
                var base64 = Convert.ToBase64String(png);
                return ($"{{\"image\":\"{base64}\",\"width\":{w},\"height\":{h}}}", 200);
            }
            return ("{\"error\":\"Screenshot timeout\"}", 504);
        }

        private static (string, int) HandleState()
        {
            return (GameStateProvider.GetFullState(), 200);
        }

        private static (string, int) HandleNpcsState()
        {
            return (GameStateProvider.GetNpcsState(), 200);
        }

        private static string ReadBody(System.Net.HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            return reader.ReadToEnd();
        }
    }
}
