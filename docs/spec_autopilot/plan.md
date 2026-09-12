# ValleyAgent.Autopilot 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 构建一个 SMAPI 模组 + Python MCP Server，让外部 LLM 通过 MCP 协议操控星露谷游戏，作为 AI 测试员。

**Architecture:** 独立 SMAPI 模组 ValleyAgent.Autopilot 内嵌 HTTP Server，Python MCP Server 通过 HTTP 桥接 AI 客户端和游戏模组。模组通过 Harmony 补丁实现全局慢动作和输入劫持，通过 GraphicsDevice 实现截图，通过 Game1 API 实现状态查询。

**Tech Stack:** C# / SMAPI / Harmony (模组侧), Python / mcp SDK / httpx (MCP Server 侧)

---

## 文件结构

### 新建文件

| 文件 | 职责 |
|------|------|
| `src/ValleyAgent.Autopilot/ValleyAgent.Autopilot.csproj` | 项目定义 |
| `src/ValleyAgent.Autopilot/manifest.json` | SMAPI 模组清单 |
| `src/ValleyAgent.Autopilot/ModEntry.cs` | 模组入口，初始化各子系统 |
| `src/ValleyAgent.Autopilot/HttpServer/AutopilotHttpServer.cs` | 轻量 HTTP Server |
| `src/ValleyAgent.Autopilot/HttpServer/RouteHandlers.cs` | 路由处理 |
| `src/ValleyAgent.Autopilot/Input/VirtualInputState.cs` | 虚拟输入状态管理 |
| `src/ValleyAgent.Autopilot/Input/InputHijacker.cs` | Harmony 补丁劫持输入 |
| `src/ValleyAgent.Autopilot/Speed/SpeedController.cs` | Harmony 补丁全局慢动作 |
| `src/ValleyAgent.Autopilot/Capture/ScreenCapture.cs` | 截图功能 |
| `src/ValleyAgent.Autopilot/State/GameStateProvider.cs` | 游戏状态查询 |
| `src/autopilot_mcp/__init__.py` | Python 包标记 |
| `src/autopilot_mcp/server.py` | MCP Server 入口 + 工具注册 |
| `src/autopilot_mcp/game_client.py` | HTTP 客户端 |
| `src/autopilot_mcp/config.py` | 配置管理 |
| `src/autopilot_mcp/pyproject.toml` | 项目元数据 + 依赖 |

### 修改文件

| 文件 | 变更 |
|------|------|
| `scripts/build/build-all.ps1` | 添加 Autopilot 模组构建步骤 |
| `scripts/build/deploy.ps1` | 添加 Autopilot 模组部署步骤 |

---

## Task 1: 项目脚手架 — csproj + manifest

**Files:**
- Create: `src/ValleyAgent.Autopilot/ValleyAgent.Autopilot.csproj`
- Create: `src/ValleyAgent.Autopilot/manifest.json`
- Create: `src/ValleyAgent.Autopilot/ModEntry.cs` (最小入口)

- [ ] **Step 1: 创建 csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
        <AssemblyName>ValleyAgent.Autopilot</AssemblyName>
        <RootNamespace>ValleyAgent.Autopilot</RootNamespace>
        <Version>1.0.0</Version>
        <TargetFramework>net6.0</TargetFramework>
        <EnableHarmony>true</EnableHarmony>
        <Nullable>enable</Nullable>
        <GenerateDocumentationFile>true</GenerateDocumentationFile>
    </PropertyGroup>
    <ItemGroup>
        <PackageReference Include="Pathoschild.Stardew.ModBuildConfig" Version="4.4.0" />
    </ItemGroup>
    <ItemGroup>
        <None Update="manifest.json">
            <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
        </None>
    </ItemGroup>
</Project>
```

- [ ] **Step 2: 创建 manifest.json**

```json
{
    "Name": "ValleyAgent Autopilot",
    "Author": "dandm1",
    "Version": "1.0.0",
    "Description": "AI-driven autopilot for Stardew Valley — lets external LLMs control the game via MCP protocol for testing.",
    "UniqueID": "dandm1.ValleyAgent.Autopilot",
    "EntryDll": "ValleyAgent.Autopilot.dll",
    "MinimumApiVersion": "4.1.0"
}
```

- [ ] **Step 3: 创建最小 ModEntry.cs**

```csharp
using StardewModdingAPI;

namespace ValleyAgent.Autopilot
{
    public class ModEntry : Mod
    {
        public override void Entry(IModHelper helper)
        {
            Monitor.Log("ValleyAgent.Autopilot loaded.", LogLevel.Info);
        }
    }
}
```

- [ ] **Step 4: 构建验证**

Run: `dotnet build src/ValleyAgent.Autopilot/ValleyAgent.Autopilot.csproj -c Debug --verbosity quiet`
Expected: BUILD SUCCEEDED, 0 warnings, 0 errors

- [ ] **Step 5: Commit**

```bash
git add src/ValleyAgent.Autopilot/
git commit -m "feat(autopilot): scaffold project with csproj, manifest, and minimal ModEntry"
```

---

## Task 2: VirtualInputState — 虚拟输入状态管理

**Files:**
- Create: `src/ValleyAgent.Autopilot/Input/VirtualInputState.cs`

- [ ] **Step 1: 实现 VirtualInputState**

```csharp
using System.Collections.Generic;
using Microsoft.Xna.Framework.Input;

namespace ValleyAgent.Autopilot.Input
{
    /// <summary>
    /// 管理 AI 注入的虚拟输入状态。当 Autopilot 激活时，
    /// InputHijacker 从这里读取状态替代真实输入。
    /// </summary>
    public sealed class VirtualInputState
    {
        private readonly HashSet<Keys> _heldKeys = new();
        private int _mouseX;
        private int _mouseY;
        private bool _mouseLeftPressed;
        private bool _mouseRightPressed;

        public bool IsActive { get; set; }

        public void SetKey(string keyName, bool pressed)
        {
            if (Enum.TryParse<Keys>(keyName, ignoreCase: true, out var key))
            {
                if (pressed)
                    _heldKeys.Add(key);
                else
                    _heldKeys.Remove(key);
            }
        }

        public void SetMouse(int x, int y, bool leftButton, bool rightButton)
        {
            _mouseX = x;
            _mouseY = y;
            _mouseLeftPressed = leftButton;
            _mouseRightPressed = rightButton;
        }

        public void ReleaseAll()
        {
            _heldKeys.Clear();
            _mouseLeftPressed = false;
            _mouseRightPressed = false;
        }

        public KeyboardState GetKeyboardState()
        {
            return _heldKeys.Count > 0 ? new KeyboardState(_heldKeys.ToArray()) : new KeyboardState();
        }

        public MouseState GetMouseState()
        {
            return new MouseState(
                _mouseX, _mouseY, 0,
                _mouseLeftPressed ? ButtonState.Pressed : ButtonState.Released,
                _mouseRightPressed ? ButtonState.Pressed : ButtonState.Released,
                ButtonState.Released, ButtonState.Released, 0
            );
        }
    }
}
```

- [ ] **Step 2: 构建验证**

Run: `dotnet build src/ValleyAgent.Autopilot/ValleyAgent.Autopilot.csproj -c Debug --verbosity quiet`
Expected: BUILD SUCCEEDED, 0 warnings, 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/ValleyAgent.Autopilot/Input/VirtualInputState.cs
git commit -m "feat(autopilot): add VirtualInputState for AI-injected input management"
```

---

## Task 3: SpeedController — 全局慢动作

**Files:**
- Create: `src/ValleyAgent.Autopilot/Speed/SpeedController.cs`

- [ ] **Step 1: 实现 SpeedController**

```csharp
using System;
using HarmonyLib;
using StardewValley;

namespace ValleyAgent.Autopilot.Speed
{
    /// <summary>
    /// 通过 Harmony 补丁修改 Game1.Update 的 ElapsedGameTime，
    /// 实现全局慢动作效果。
    /// </summary>
    public sealed class SpeedController
    {
        private float _speedFactor = 1.0f;

        public float SpeedFactor
        {
            get => _speedFactor;
            set => _speedFactor = Math.Clamp(value, 0.1f, 1.0f);
        }

        public void ApplyPatches(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Game1), nameof(Game1.Update)),
                prefix: new HarmonyMethod(typeof(SpeedController), nameof(BeforeUpdate))
            );
        }

        // Harmony 会将 _speedFactor 字段注入到此静态方法的 this 上下文中，
        // 但因为 Harmony prefix 是静态的，我们需要通过 Instance 访问。
        public static SpeedController? Instance { get; private set; }

        public SpeedController()
        {
            Instance = this;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Game1), nameof(Game1.Update))]
        private static void BeforeUpdate(ref Microsoft.Xna.Framework.GameTime gameTime)
        {
            if (Instance == null) return;
            var factor = Instance._speedFactor;
            if (factor < 1.0f)
            {
                var original = gameTime.ElapsedGameTime;
                gameTime.ElapsedGameTime = TimeSpan.FromTicks((long)(original.Ticks * factor));
            }
        }
    }
}
```

- [ ] **Step 2: 构建验证**

Run: `dotnet build src/ValleyAgent.Autopilot/ValleyAgent.Autopilot.csproj -c Debug --verbosity quiet`
Expected: BUILD SUCCEEDED, 0 warnings, 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/ValleyAgent.Autopilot/Speed/SpeedController.cs
git commit -m "feat(autopilot): add SpeedController with Harmony patch for slow-motion"
```

---

## Task 4: InputHijacker — 输入劫持

**Files:**
- Create: `src/ValleyAgent.Autopilot/Input/InputHijacker.cs`

- [ ] **Step 1: 实现 InputHijacker**

```csharp
using HarmonyLib;
using Microsoft.Xna.Framework.Input;

namespace ValleyAgent.Autopilot.Input
{
    /// <summary>
    /// 当 Autopilot 激活时，劫持 Keyboard.GetState() 和 Mouse.GetState()，
    /// 返回 VirtualInputState 中的虚拟输入，屏蔽真实玩家输入。
    /// </summary>
    public sealed class InputHijacker
    {
        private readonly VirtualInputState _virtualInput;

        public InputHijacker(VirtualInputState virtualInput)
        {
            _virtualInput = virtualInput;
            Instance = this;
        }

        public static InputHijacker? Instance { get; private set; }

        public void ApplyPatches(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Keyboard), nameof(Keyboard.GetState), new System.Type[0]),
                prefix: new HarmonyMethod(typeof(InputHijacker), nameof(BeforeKeyboardGetState))
            );
            harmony.Patch(
                original: AccessTools.Method(typeof(Mouse), nameof(Mouse.GetState)),
                prefix: new HarmonyMethod(typeof(InputHijacker), nameof(BeforeMouseGetState))
            );
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Keyboard), nameof(Keyboard.GetState))]
        private static bool BeforeKeyboardGetState(ref KeyboardState __result)
        {
            if (Instance == null || !Instance._virtualInput.IsActive) return true;
            __result = Instance._virtualInput.GetKeyboardState();
            return false; // skip original
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Mouse), nameof(Mouse.GetState))]
        private static bool BeforeMouseGetState(ref MouseState __result)
        {
            if (Instance == null || !Instance._virtualInput.IsActive) return true;
            __result = Instance._virtualInput.GetMouseState();
            return false; // skip original
        }
    }
}
```

- [ ] **Step 2: 构建验证**

Run: `dotnet build src/ValleyAgent.Autopilot/ValleyAgent.Autopilot.csproj -c Debug --verbosity quiet`
Expected: BUILD SUCCEEDED, 0 warnings, 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/ValleyAgent.Autopilot/Input/InputHijacker.cs
git commit -m "feat(autopilot): add InputHijacker to intercept keyboard/mouse input"
```

---

## Task 5: ScreenCapture — 截图

**Files:**
- Create: `src/ValleyAgent.Autopilot/Capture/ScreenCapture.cs`

- [ ] **Step 1: 实现 ScreenCapture**

```csharp
using System;
using System.IO;
using System.IO.Compression;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace ValleyAgent.Autopilot.Capture
{
    /// <summary>
    /// 截取当前游戏画面，编码为 PNG base64 返回。
    /// 在 UpdateTicked 中缓存帧数据，避免跨线程访问 GraphicsDevice。
    /// </summary>
    public sealed class ScreenCapture
    {
        private readonly IMonitor _monitor;
        private byte[]? _cachedPng;
        private int _cachedWidth;
        private int _cachedHeight;
        private bool _captureRequested;

        public ScreenCapture(IMonitor monitor)
        {
            _monitor = monitor;
        }

        /// <summary>
        /// 在游戏 tick 中调用，在主线程上安全地执行截图。
        /// </summary>
        public void OnUpdateTicked()
        {
            if (!_captureRequested) return;
            _captureRequested = false;

            try
            {
                var gd = Game1.graphics?.GraphicsDevice;
                if (gd == null) return;

                var pp = gd.PresentationParameters;
                int w = pp.BackBufferWidth;
                int h = pp.BackBufferHeight;

                var data = new Microsoft.Xna.Framework.Color[w * h];
                gd.GetBackBufferData(data);

                // 缩放到 640x360 用于 low 模式
                int targetW = 640;
                int targetH = 360;
                var resized = ResizeNearestNeighbor(data, w, h, targetW, targetH);

                _cachedPng = EncodePng(resized, targetW, targetH);
                _cachedWidth = targetW;
                _cachedHeight = targetH;
            }
            catch (Exception ex)
            {
                _monitor.Log($"ScreenCapture failed: {ex.Message}", LogLevel.Warn);
                _cachedPng = null;
            }
        }

        /// <summary>
        /// 请求截图。下一 tick 会执行实际截图，之后调用 GetCachedScreenshot 获取结果。
        /// </summary>
        public void RequestCapture()
        {
            _captureRequested = true;
        }

        public (byte[] png, int width, int height)? GetCachedScreenshot()
        {
            if (_cachedPng == null) return null;
            return (_cachedPng, _cachedWidth, _cachedHeight);
        }

        private static Microsoft.Xna.Framework.Color[] ResizeNearestNeighbor(
            Microsoft.Xna.Framework.Color[] src, int srcW, int srcH, int dstW, int dstH)
        {
            var dst = new Microsoft.Xna.Framework.Color[dstW * dstH];
            float xRatio = (float)srcW / dstW;
            float yRatio = (float)srcH / dstH;

            for (int y = 0; y < dstH; y++)
            {
                for (int x = 0; x < dstW; x++)
                {
                    int sx = (int)(x * xRatio);
                    int sy = (int)(y * yRatio);
                    dst[y * dstW + x] = src[sy * srcW + sx];
                }
            }
            return dst;
        }

        /// <summary>
        /// 将像素数据编码为 PNG（手动实现，无外部依赖）。
        /// </summary>
        private static byte[] EncodePng(Microsoft.Xna.Framework.Color[] pixels, int width, int height)
        {
            // PNG 格式: Signature + IHDR + IDAT + IEND
            using var ms = new MemoryStream();

            // PNG signature
            ms.WriteByte(0x89);
            ms.Write("PNG\r\n\x1a\n"u8);

            // IHDR chunk
            WriteChunk(ms, "IHDR", ihdr =>
            {
                WriteBigEndian32(ihdr, (uint)width);
                WriteBigEndian32(ihdr, (uint)height);
                ihdr.WriteByte(8);  // bit depth
                ihdr.WriteByte(2);  // color type: RGB
                ihdr.WriteByte(0);  // compression
                ihdr.WriteByte(0);  // filter
                ihdr.WriteByte(0);  // interlace
            });

            // IDAT chunk — raw pixel data with filter byte 0 (None) per row, then deflate
            using var rawMs = new MemoryStream();
            for (int y = 0; y < height; y++)
            {
                rawMs.WriteByte(0); // filter: None
                for (int x = 0; x < width; x++)
                {
                    var c = pixels[y * width + x];
                    rawMs.WriteByte(c.R);
                    rawMs.WriteByte(c.G);
                    rawMs.WriteByte(c.B);
                }
            }

            var compressed = DeflateCompress(rawMs.ToArray());
            WriteChunk(ms, "IDAT", idat => idat.Write(compressed));

            // IEND chunk
            WriteChunk(ms, "IEND", _ => { });

            return ms.ToArray();
        }

        private static void WriteChunk(Stream ms, string type, Action<Stream> writeData)
        {
            using var dataMs = new MemoryStream();
            writeData(dataMs);
            var data = dataMs.ToArray();

            WriteBigEndian32(ms, (uint)data.Length);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            ms.Write(typeBytes);

            // CRC covers type + data
            using var crcMs = new MemoryStream();
            crcMs.Write(typeBytes);
            crcMs.Write(data);
            uint crc = Crc32(crcMs.ToArray());
            WriteBigEndian32(ms, crc);

            ms.Write(data);
        }

        private static byte[] DeflateCompress(byte[] data)
        {
            using var output = new MemoryStream();
            // Write zlib header
            output.WriteByte(0x78); // CMF
            output.WriteByte(0x01); // FLG
            using (var ds = new System.IO.Compression.DeflateStream(output, CompressionMode.Compress, leaveOpen: true))
            {
                ds.Write(data);
            }
            // Write Adler-32 checksum
            uint adler = Adler32(data);
            WriteBigEndian32(output, adler);
            return output.ToArray();
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte bt in data)
            {
                a = (a + bt) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    crc = (crc >> 1) ^ (crc & 1) * 0xEDB88320;
                }
            }
            return ~crc;
        }

        private static void WriteBigEndian32(Stream s, uint value)
        {
            s.WriteByte((byte)((value >> 24) & 0xFF));
            s.WriteByte((byte)((value >> 16) & 0xFF));
            s.WriteByte((byte)((value >> 8) & 0xFF));
            s.WriteByte((byte)(value & 0xFF));
        }
    }
}
```

- [ ] **Step 2: 构建验证**

Run: `dotnet build src/ValleyAgent.Autopilot/ValleyAgent.Autopilot.csproj -c Debug --verbosity quiet`
Expected: BUILD SUCCEEDED, 0 warnings, 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/ValleyAgent.Autopilot/Capture/ScreenCapture.cs
git commit -m "feat(autopilot): add ScreenCapture with PNG encoding and nearest-neighbor resize"
```

---

## Task 6: GameStateProvider — 游戏状态查询

**Files:**
- Create: `src/ValleyAgent.Autopilot/State/GameStateProvider.cs`

- [ ] **Step 1: 实现 GameStateProvider**

```csharp
using System.Collections.Generic;
using System.Text.Json;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace ValleyAgent.Autopilot.State
{
    /// <summary>
    /// 提供游戏状态查询，返回结构化 JSON 数据。
    /// </summary>
    public sealed class GameStateProvider
    {
        public string GetPlayerState()
        {
            var farmer = Game1.player;
            if (farmer == null) return "{}";

            var state = new Dictionary<string, object>
            {
                ["name"] = farmer.Name ?? "",
                ["position"] = new Dictionary<string, object>
                {
                    ["x"] = (int)farmer.Position.X,
                    ["y"] = (int)farmer.Position.Y,
                    ["map"] = farmer.currentLocation?.Name ?? ""
                },
                ["facing"] = farmer.FacingDirection,
                ["health"] = farmer.health,
                ["stamina"] = (int)farmer.stamina,
                ["money"] = farmer.Money,
                ["tileX"] = farmer.TilePoint.X,
                ["tileY"] = farmer.TilePoint.Y
            };
            return JsonSerializer.Serialize(state);
        }

        public string GetNpcsState()
        {
            var npcs = new List<Dictionary<string, object>>();
            var location = Game1.player?.currentLocation;
            if (location == null) return "[]";

            foreach (var npc in location.characters)
            {
                if (npc is Child or Pet or Horse) continue;
                npcs.Add(new Dictionary<string, object>
                {
                    ["name"] = npc.Name ?? "",
                    ["x"] = (int)npc.Position.X,
                    ["y"] = (int)npc.Position.Y,
                    ["facing"] = npc.FacingDirection,
                    ["isTalking"] = Game1.currentSpeaker == npc
                });
            }
            return JsonSerializer.Serialize(npcs);
        }

        public string GetFullState()
        {
            var state = new Dictionary<string, object>
            {
                ["player"] = JsonSerializer.Deserialize<Dictionary<string, object>>(GetPlayerState())!,
                ["time"] = new Dictionary<string, object>
                {
                    ["day"] = Game1.dayOfMonth,
                    ["season"] = Game1.currentSeason ?? "",
                    ["year"] = Game1.year,
                    ["timeOfDay"] = Game1.timeOfDay
                },
                ["currentLocation"] = Game1.player?.currentLocation?.Name ?? ""
            };
            return JsonSerializer.Serialize(state);
        }
    }
}
```

- [ ] **Step 2: 构建验证**

Run: `dotnet build src/ValleyAgent.Autopilot/ValleyAgent.Autopilot.csproj -c Debug --verbosity quiet`
Expected: BUILD SUCCEEDED, 0 warnings, 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/ValleyAgent.Autopilot/State/GameStateProvider.cs
git commit -m "feat(autopilot): add GameStateProvider for structured game state queries"
```

---

## Task 7: AutopilotHttpServer + RouteHandlers — HTTP API

**Files:**
- Create: `src/ValleyAgent.Autopilot/HttpServer/AutopilotHttpServer.cs`
- Create: `src/ValleyAgent.Autopilot/HttpServer/RouteHandlers.cs`

- [ ] **Step 1: 实现 AutopilotHttpServer**

```csharp
using System;
using System.Net;
using System.Text;
using System.Threading;
using StardewModdingAPI;

namespace ValleyAgent.Autopilot.HttpServer
{
    /// <summary>
    /// 轻量 HTTP Server，监听 localhost:5555，接收 AI 控制指令。
    /// </summary>
    public sealed class AutopilotHttpServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly IMonitor _monitor;
        private readonly RouteHandlers _handlers;
        private Thread? _listenerThread;
        private volatile bool _running;

        public AutopilotHttpServer(IMonitor monitor, RouteHandlers handlers, int port = 5555)
        {
            _monitor = monitor;
            _handlers = handlers;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
        }

        public void Start()
        {
            _running = true;
            _listener.Start();
            _listenerThread = new Thread(ListenLoop) { IsBackground = true };
            _listenerThread.Start();
            _monitor.Log("Autopilot HTTP Server started on localhost:5555", LogLevel.Info);
        }

        private void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var context = _listener.GetContext();
                    ProcessRequest(context);
                }
                catch (HttpListenerException) when (!_running)
                {
                    // 正常关闭
                }
                catch (Exception ex)
                {
                    _monitor.Log($"HTTP Server error: {ex.Message}", LogLevel.Warn);
                }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var method = context.Request.HttpMethod;

            string responseBody;
            int statusCode;

            try
            {
                (responseBody, statusCode) = _handlers.Handle(method, path, context.Request);
            }
            catch (Exception ex)
            {
                responseBody = $"{{\"error\":\"{ex.Message}\"}}";
                statusCode = 500;
            }

            var buffer = Encoding.UTF8.GetBytes(responseBody);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = buffer.Length;
            context.Response.Close(buffer, false);
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { /* ignore */ }
            try { _listener.Close(); } catch { /* ignore */ }
        }
    }
}
```

- [ ] **Step 2: 实现 RouteHandlers**

```csharp
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using ValleyAgent.Autopilot.Capture;
using ValleyAgent.Autopilot.Input;
using ValleyAgent.Autopilot.Speed;
using ValleyAgent.Autopilot.State;

namespace ValleyAgent.Autopilot.HttpServer
{
    /// <summary>
    /// HTTP 路由处理，将请求分派到各子系统。
    /// </summary>
    public sealed class RouteHandlers
    {
        private readonly VirtualInputState _virtualInput;
        private readonly SpeedController _speedController;
        private readonly ScreenCapture _screenCapture;
        private readonly GameStateProvider _stateProvider;

        public RouteHandlers(
            VirtualInputState virtualInput,
            SpeedController speedController,
            ScreenCapture screenCapture,
            GameStateProvider stateProvider)
        {
            _virtualInput = virtualInput;
            _speedController = speedController;
            _screenCapture = screenCapture;
            _stateProvider = stateProvider;
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
                _ => ("{\"error\":\"Not found\"}", 404)
            };
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
            return ($"{{\"active\":{_virtualInput.IsActive.ToString().ToLower()}}}", 200);
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

            // 模拟点击：按下 → 释放（下一帧自动释放通过 release_all 语义）
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
            // 截图在下一 tick 执行，需要等待
            // 返回提示客户端稍后获取
            var cached = _screenCapture.GetCachedScreenshot();
            if (cached != null)
            {
                var (png, w, h) = cached.Value;
                var base64 = Convert.ToBase64String(png);
                return ($"{{\"image\":\"{base64}\",\"width\":{w},\"height\":{h}}}", 200);
            }
            return ("{\"error\":\"No screenshot available yet. Request one first and retry.\"}", 503);
        }

        private (string, int) HandleState()
        {
            return (_stateProvider.GetFullState(), 200);
        }

        private (string, int) HandleNpcsState()
        {
            return (_stateProvider.GetNpcsState(), 200);
        }

        private static string ReadBody(System.Net.HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            return reader.ReadToEnd();
        }
    }
}
```

- [ ] **Step 3: 构建验证**

Run: `dotnet build src/ValleyAgent.Autopilot/ValleyAgent.Autopilot.csproj -c Debug --verbosity quiet`
Expected: BUILD SUCCEEDED, 0 warnings, 0 errors

- [ ] **Step 4: Commit**

```bash
git add src/ValleyAgent.Autopilot/HttpServer/
git commit -m "feat(autopilot): add HTTP Server and route handlers for all API endpoints"
```

---

## Task 8: ModEntry 集成 — 连接所有子系统

**Files:**
- Modify: `src/ValleyAgent.Autopilot/ModEntry.cs`

- [ ] **Step 1: 重写 ModEntry.cs 集成所有子系统**

```csharp
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using ValleyAgent.Autopilot.Capture;
using ValleyAgent.Autopilot.HttpServer;
using ValleyAgent.Autopilot.Input;
using ValleyAgent.Autopilot.Speed;
using ValleyAgent.Autopilot.State;

namespace ValleyAgent.Autopilot
{
    public class ModEntry : Mod
    {
        private VirtualInputState? _virtualInput;
        private SpeedController? _speedController;
        private InputHijacker? _inputHijacker;
        private ScreenCapture? _screenCapture;
        private GameStateProvider? _stateProvider;
        private AutopilotHttpServer? _httpServer;
        private Harmony? _harmony;

        public override void Entry(IModHelper helper)
        {
            _virtualInput = new VirtualInputState();
            _speedController = new SpeedController();
            _inputHijacker = new InputHijacker(_virtualInput);
            _screenCapture = new ScreenCapture(Monitor);
            _stateProvider = new GameStateProvider();

            // Apply Harmony patches
            _harmony = new Harmony(ModManifest.UniqueID);
            _speedController.ApplyPatches(_harmony);
            _inputHijacker.ApplyPatches(_harmony);

            // Register SMAPI console command for emergency stop
            helper.ConsoleCommands.Add("autopilot_stop", "Stop autopilot and restore player input.", (_, _) =>
            {
                _virtualInput.IsActive = false;
                _virtualInput.ReleaseAll();
                Monitor.Log("Autopilot stopped via console command.", LogLevel.Info);
            });

            // Subscribe to update tick for screen capture
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

            // Start HTTP Server after game is loaded
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;

            Monitor.Log("ValleyAgent.Autopilot loaded.", LogLevel.Info);
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            var routeHandlers = new RouteHandlers(
                _virtualInput!, _speedController!, _screenCapture!, _stateProvider!);
            _httpServer = new AutopilotHttpServer(Monitor, routeHandlers);
            try
            {
                _httpServer.Start();
            }
            catch (System.Exception ex)
            {
                Monitor.Log($"Failed to start HTTP Server: {ex.Message}. Autopilot API will not be available.", LogLevel.Error);
            }
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            _screenCapture?.OnUpdateTicked();
        }

        public override void Dispose()
        {
            _httpServer?.Dispose();
            _harmony?.UnpatchAll(ModManifest.UniqueID);
            base.Dispose();
        }
    }
}
```

- [ ] **Step 2: 构建验证**

Run: `dotnet build src/ValleyAgent.Autopilot/ValleyAgent.Autopilot.csproj -c Debug --verbosity quiet`
Expected: BUILD SUCCEEDED, 0 warnings, 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/ValleyAgent.Autopilot/ModEntry.cs
git commit -m "feat(autopilot): integrate all subsystems in ModEntry"
```

---

## Task 9: 构建脚本更新

**Files:**
- Modify: `scripts/build/build-all.ps1`
- Modify: `scripts/build/deploy.ps1`

- [ ] **Step 1: 在 build-all.ps1 中添加 Autopilot 构建步骤**

在 Step 3（Build TestMod）之后添加：

```powershell
# Step 4: Build Autopilot
$AutopilotDir = Join-Path $RepoRoot "src\ValleyAgent.Autopilot"
Write-Log "Building ValleyAgent.Autopilot..." "Info"
dotnet build "$AutopilotDir\ValleyAgent.Autopilot.csproj" -c $Configuration --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Log "ValleyAgent.Autopilot build FAILED (exit code $LASTEXITCODE)" "Error"
    exit 1
}
Write-Log "ValleyAgent.Autopilot build succeeded." "Success"
```

- [ ] **Step 2: 在 deploy.ps1 中添加 Autopilot 部署步骤**

在 TestMod 部署之后添加：

```powershell
# Copy Autopilot mod
$AutopilotBuildDir = Join-Path $RepoRoot "src\ValleyAgent.Autopilot\bin\$Configuration\net6.0"
$AutopilotTargetDir = Join-Path $ModsDir "ValleyAgent.Autopilot"
if (Test-Path $AutopilotBuildDir) {
    if (-not (Test-Path $AutopilotTargetDir)) {
        New-Item -ItemType Directory -Path $AutopilotTargetDir -Force | Out-Null
    }
    $autopilotFiles = @("ValleyAgent.Autopilot.dll", "ValleyAgent.Autopilot.pdb", "manifest.json")
    foreach ($f in $autopilotFiles) {
        $src = Join-Path $AutopilotBuildDir $f
        $dst = Join-Path $AutopilotTargetDir $f
        if (Test-Path $src) {
            Copy-Item $src $dst -Force
            Write-Log "Copied Autopilot/$f" "Info"
        }
    }
}
```

- [ ] **Step 3: 构建验证**

Run: `powershell -File scripts\build\build-all.ps1`
Expected: All 3 projects build successfully

- [ ] **Step 4: Commit**

```bash
git add scripts/build/build-all.ps1 scripts/build/deploy.ps1
git commit -m "feat(autopilot): add Autopilot to build and deploy scripts"
```

---

## Task 10: Python MCP Server — autopilot_mcp

**Files:**
- Create: `src/autopilot_mcp/__init__.py`
- Create: `src/autopilot_mcp/config.py`
- Create: `src/autopilot_mcp/game_client.py`
- Create: `src/autopilot_mcp/server.py`
- Create: `src/autopilot_mcp/pyproject.toml`

- [ ] **Step 1: 创建 __init__.py**

```python
"""ValleyAgent Autopilot MCP Server — lets AI control Stardew Valley via MCP protocol."""
```

- [ ] **Step 2: 创建 config.py**

```python
"""Configuration for the autopilot MCP server."""
import os

AUTOPILOT_HOST = os.environ.get("AUTOPILOT_HOST", "localhost")
AUTOPILOT_PORT = int(os.environ.get("AUTOPILOT_PORT", "5555"))
AUTOPILOT_BASE_URL = f"http://{AUTOPILOT_HOST}:{AUTOPILOT_PORT}"
```

- [ ] **Step 3: 创建 game_client.py**

```python
"""HTTP client for communicating with the ValleyAgent.Autopilot SMAPI mod."""
import httpx
from .config import AUTOPILOT_BASE_URL


async def _request(method: str, path: str, json_data: dict | None = None) -> dict:
    """Send an HTTP request to the game mod and return the JSON response."""
    url = f"{AUTOPILOT_BASE_URL}{path}"
    async with httpx.AsyncClient(timeout=10.0) as client:
        if method == "GET":
            resp = await client.get(url)
        elif method == "POST":
            resp = await client.post(url, json=json_data)
        else:
            raise ValueError(f"Unsupported HTTP method: {method}")
        resp.raise_for_status()
        return resp.json()


async def start_autopilot() -> dict:
    return await _request("POST", "/autopilot/start")


async def stop_autopilot() -> dict:
    return await _request("POST", "/autopilot/stop")


async def get_autopilot_status() -> dict:
    return await _request("GET", "/autopilot/status")


async def set_speed(factor: float) -> dict:
    return await _request("POST", "/speed", {"factor": factor})


async def press_key(key: str, duration_ms: int = 100) -> dict:
    # Press key, wait, then release
    await _request("POST", "/input/key", {"key": key, "pressed": True})
    import asyncio
    await asyncio.sleep(duration_ms / 1000.0)
    return await _request("POST", "/input/key", {"key": key, "pressed": False})


async def hold_key(key: str) -> dict:
    return await _request("POST", "/input/key", {"key": key, "pressed": True})


async def release_key(key: str) -> dict:
    return await _request("POST", "/input/key", {"key": key, "pressed": False})


async def move_mouse(x: int, y: int) -> dict:
    return await _request("POST", "/input/mouse", {"x": x, "y": y, "leftButton": False, "rightButton": False})


async def click(x: int, y: int, button: str = "left") -> dict:
    return await _request("POST", "/input/click", {"x": x, "y": y, "button": button})


async def release_all() -> dict:
    return await _request("POST", "/input/release_all")


async def screenshot() -> dict:
    return await _request("GET", "/screenshot")


async def get_game_state() -> dict:
    return await _request("GET", "/state")


async def get_npcs_state() -> dict:
    return await _request("GET", "/state/npcs")
```

- [ ] **Step 4: 创建 server.py**

```python
"""MCP Server entry point — registers tools and starts the server."""
from mcp.server.fastmcp import FastMCP
from . import game_client

mcp = FastMCP("stardew-autopilot")


@mcp.tool()
async def start_autopilot() -> str:
    """Start AI autopilot mode — blocks player input and enables AI control."""
    result = await game_client.start_autopilot()
    return f"Autopilot started: {result}"


@mcp.tool()
async def stop_autopilot() -> str:
    """Stop AI autopilot mode — restores player input control."""
    result = await game_client.stop_autopilot()
    return f"Autopilot stopped: {result}"


@mcp.tool()
async def set_speed(factor: float) -> str:
    """Set game speed factor (0.1=10x slow motion, 1.0=normal speed).

    Args:
        factor: Speed factor between 0.1 and 1.0
    """
    result = await game_client.set_speed(factor)
    return f"Speed set to {factor}: {result}"


@mcp.tool()
async def press_key(key: str, duration_ms: int = 100) -> str:
    """Press and release a keyboard key.

    Args:
        key: Key name (e.g. 'W', 'A', 'S', 'D', 'Space', 'Enter', 'Escape')
        duration_ms: How long to hold the key in milliseconds
    """
    result = await game_client.press_key(key, duration_ms)
    return f"Pressed {key} for {duration_ms}ms: {result}"


@mcp.tool()
async def hold_key(key: str) -> str:
    """Hold a keyboard key down (until release_key is called).

    Args:
        key: Key name (e.g. 'W', 'A', 'S', 'D')
    """
    result = await game_client.hold_key(key)
    return f"Holding {key}: {result}"


@mcp.tool()
async def release_key(key: str) -> str:
    """Release a held keyboard key.

    Args:
        key: Key name to release
    """
    result = await game_client.release_key(key)
    return f"Released {key}: {result}"


@mcp.tool()
async def move_mouse(x: int, y: int) -> str:
    """Move the mouse cursor to a specific position.

    Args:
        x: X coordinate in pixels
        y: Y coordinate in pixels
    """
    result = await game_client.move_mouse(x, y)
    return f"Mouse moved to ({x}, {y}): {result}"


@mcp.tool()
async def click(x: int, y: int, button: str = "left") -> str:
    """Click at a specific position.

    Args:
        x: X coordinate in pixels
        y: Y coordinate in pixels
        button: 'left' or 'right'
    """
    result = await game_client.click(x, y, button)
    return f"Clicked {button} at ({x}, {y}): {result}"


@mcp.tool()
async def release_all() -> str:
    """Release all held keys and mouse buttons."""
    result = await game_client.release_all()
    return f"All inputs released: {result}"


@mcp.tool()
async def screenshot() -> str:
    """Take a screenshot of the current game screen. Returns base64-encoded PNG image."""
    result = await game_client.screenshot()
    if "image" in result:
        return f"Screenshot captured: {result['width']}x{result['height']}, base64 image data available"
    return f"Screenshot failed: {result}"


@mcp.tool()
async def get_game_state() -> str:
    """Get current game state including player position, time, season, money, etc."""
    result = await game_client.get_game_state()
    return f"Game state: {result}"


@mcp.tool()
async def get_npcs_state() -> str:
    """Get list of visible NPCs in current location with their positions."""
    result = await game_client.get_npcs_state()
    return f"NPCs: {result}"


if __name__ == "__main__":
    mcp.run()
```

- [ ] **Step 5: 创建 pyproject.toml**

```toml
[project]
name = "autopilot-mcp"
version = "1.0.0"
description = "MCP Server for AI-driven Stardew Valley autopilot"
requires-python = ">=3.10"
dependencies = [
    "mcp>=1.0.0",
    "httpx>=0.27.0",
]

[project.scripts]
autopilot-mcp = "autopilot_mcp.server:main"
```

- [ ] **Step 6: 安装依赖并验证**

Run: `cd src/autopilot_mcp && pip install -e .`
Expected: Successfully installed autopilot-mcp

- [ ] **Step 7: 验证 MCP Server 可启动**

Run: `cd src/autopilot_mcp && python -c "from autopilot_mcp.server import mcp; print('MCP server module loads OK')"`
Expected: "MCP server module loads OK"

- [ ] **Step 8: Commit**

```bash
git add src/autopilot_mcp/
git commit -m "feat(autopilot): add Python MCP Server with 11 tools for AI game control"
```

---

## Task 11: 截图异步修复 — 两步截图协议

截图需要两个步骤：请求截图（在主线程 tick 中执行）→ 获取结果。当前 HTTP API 的 `/screenshot` 端点需要改为两步模式。

**Files:**
- Modify: `src/ValleyAgent.Autopilot/HttpServer/RouteHandlers.cs`
- Modify: `src/ValleyAgent.Autopilot/Capture/ScreenCapture.cs`

- [ ] **Step 1: 在 ScreenCapture 中添加截图等待机制**

在 `ScreenCapture.cs` 中添加：

```csharp
private readonly System.Threading.ManualResetEventSlim _captureReady = new(false);

// 在 OnUpdateTicked 中，截图完成后：
_captureReady.Set();

// 在 RequestCapture 中：
_captureReady.Reset();

// 新增方法：等待截图完成
public (byte[] png, int width, int height)? WaitForScreenshot(int timeoutMs = 2000)
{
    if (!_captureReady.Wait(timeoutMs)) return null;
    return GetCachedScreenshot();
}
```

- [ ] **Step 2: 更新 RouteHandlers 中的 HandleScreenshot**

```csharp
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
```

- [ ] **Step 3: 构建验证**

Run: `dotnet build src/ValleyAgent.Autopilot/ValleyAgent.Autopilot.csproj -c Debug --verbosity quiet`
Expected: BUILD SUCCEEDED, 0 warnings, 0 errors

- [ ] **Step 4: Commit**

```bash
git add src/ValleyAgent.Autopilot/Capture/ScreenCapture.cs src/ValleyAgent.Autopilot/HttpServer/RouteHandlers.cs
git commit -m "fix(autopilot): add two-step screenshot protocol with wait mechanism"
```

---

## Task 12: 端到端构建验证

**Files:** 无新文件

- [ ] **Step 1: 完整构建**

Run: `powershell -File scripts\build\build-all.ps1`
Expected: ValleyAgent, ValleyAgent.TestMod, ValleyAgent.Autopilot 全部构建成功

- [ ] **Step 2: 部署验证**

Run: `powershell -File scripts\build\deploy.ps1 -SkipBuild`
Expected: 所有模组文件复制到 Mods 目录

- [ ] **Step 3: Python MCP Server 验证**

Run: `cd src/autopilot_mcp && python -c "from autopilot_mcp.server import mcp; print('OK')"`
Expected: OK

- [ ] **Step 4: 最终 Commit（如有修复）**

```bash
git add -A
git commit -m "chore(autopilot): final build verification fixes"
```

---

## 自检

### Spec 覆盖率
- 全局慢动作 → Task 3 (SpeedController) ✓
- 输入劫持（屏蔽+注入）→ Task 2 (VirtualInputState) + Task 4 (InputHijacker) ✓
- 截图 → Task 5 (ScreenCapture) + Task 11 (异步修复) ✓
- 游戏状态查询 → Task 6 (GameStateProvider) ✓
- HTTP Server → Task 7 ✓
- MCP Server → Task 10 ✓
- 构建脚本 → Task 9 ✓
- 紧急停止 → Task 8 (console command) ✓

### Placeholder 扫描
无 TBD/TODO/占位符。

### 类型一致性
- `VirtualInputState` 在 Task 2 定义，Task 4/7/8 使用 — 一致
- `SpeedController` 在 Task 3 定义，Task 7/8 使用 — 一致
- `ScreenCapture` 在 Task 5 定义，Task 7/8/11 使用 — 一致
- `GameStateProvider` 在 Task 6 定义，Task 7/8 使用 — 一致
