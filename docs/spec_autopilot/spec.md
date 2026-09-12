# ValleyAgent.Autopilot 设计规格

## 概述

ValleyAgent.Autopilot 是一个 SMAPI 模组 + Python MCP Server，让外部 LLM（如 Claude/GPT）能够通过 MCP 协议操控星露谷游戏，作为 AI 测试员验证 ValleyAgent NPC 模组的功能可用性。

核心能力：
1. **全局慢动作** — 降低游戏整体速度（帧率、动画、移动全部变慢）
2. **输入劫持** — 屏蔽玩家输入，注入 AI 控制的键盘鼠标操作
3. **截图** — 返回当前游戏画面供 AI 视觉分析
4. **游戏状态查询** — 返回玩家位置、NPC、可交互对象等结构化数据

## 架构

```
┌─────────────────┐     MCP/stdio      ┌──────────────────────┐
│   AI 客户端      │ ◄──────────────► │  Python MCP Server    │
│ (Claude/GPT)    │   JSON-RPC        │  (autopilot_mcp)      │
└─────────────────┘                    └──────────┬───────────┘
                                                  │ HTTP (localhost)
                                                  ▼
                                       ┌──────────────────────┐
                                       │  SMAPI 模组           │
                                       │  ValleyAgent.Autopilot│
                                       │  ┌────────────────┐  │
                                       │  │ HTTP Server     │  │
                                       │  │ (接收指令)       │  │
                                       │  ├────────────────┤  │
                                       │  │ Input Hijacker  │  │
                                       │  │ (键盘鼠标劫持)   │  │
                                       │  ├────────────────┤  │
                                       │  │ Speed Control   │  │
                                       │  │ (全局慢动作)     │  │
                                       │  ├────────────────┤  │
                                       │  │ Screen Capture  │  │
                                       │  │ (截图返回)       │  │
                                       │  ├────────────────┤  │
                                       │  │ Game State      │  │
                                       │  │ (状态查询)       │  │
                                       │  └────────────────┘  │
                                       └──────────────────────┘
```

数据流：
1. AI 客户端通过 MCP 协议调用工具（如 `screenshot`、`press_key`）
2. Python MCP Server 将 MCP 请求转为 HTTP 请求发送到游戏模组
3. 模组执行操作（截图/按键/移动/速度控制）并返回 JSON 结果
4. Python MCP Server 将结果封装为 MCP 响应返回给 AI

## SMAPI 模组设计 — ValleyAgent.Autopilot

### 项目结构

```
src/ValleyAgent.Autopilot/
├── manifest.json
├── ValleyAgent.Autopilot.csproj
├── ModEntry.cs                    # 模组入口，初始化各子系统
├── HttpServer/
│   ├── AutopilotHttpServer.cs     # 轻量 HTTP Server
│   └── RouteHandlers.cs          # 路由处理
├── Input/
│   ├── InputHijacker.cs          # Harmony 补丁，劫持输入
│   └── VirtualInputState.cs      # 虚拟输入状态管理
├── Speed/
│   └── SpeedController.cs        # Harmony 补丁，全局慢动作
├── Capture/
│   └── ScreenCapture.cs          # 截图功能
└── State/
    └── GameStateProvider.cs      # 游戏状态查询
```

### 全局慢动作 — SpeedController

通过 Harmony Prefix 补丁 `Game1.Update(GameTime gameTime)` 方法，修改 `ElapsedGameTime`：

```csharp
[HarmonyPrefix]
[HarmonyPatch(typeof(Game1), nameof(Game1.Update))]
static void BeforeUpdate(ref GameTime gameTime)
{
    if (_speedFactor < 1.0f)
    {
        var original = gameTime.ElapsedGameTime;
        gameTime.ElapsedGameTime = TimeSpan.FromTicks((long)(original.Ticks * _speedFactor));
    }
}
```

- 速度系数范围：0.1 ~ 1.0（0.1 = 10 倍慢动作，1.0 = 正常速度）
- API：`POST /speed` `{"factor": 0.3}`

### 输入劫持 — InputHijacker

两层实现：

**屏蔽层**：当 AI 接管时，Harmony 补丁拦截输入读取：
- `Microsoft.Xna.Framework.Input.Keyboard.GetState()` → 返回空 KeyboardState
- `Microsoft.Xna.Framework.Input.Mouse.GetState()` → 返回 VirtualInputState 中的鼠标状态
- SMAPI `InputHelper.GetState(SButton)` → 返回 Released

**注入层**：维护 `VirtualInputState` 单例，AI 通过 API 设置：
- `POST /input/key` — `{"key": "W", "pressed": true}`
- `POST /input/mouse` — `{"x": 400, "y": 300, "leftButton": true, "rightButton": false}`
- `POST /input/click` — `{"x": 400, "y": 300, "button": "left"}` — 模拟一次点击（按下+释放）
- `POST /input/release_all` — 释放所有按键和鼠标

控制流：
- `POST /autopilot/start` — 启动 AI 接管，屏蔽玩家输入
- `POST /autopilot/stop` — 停止 AI 接管，恢复玩家输入
- `GET /autopilot/status` — 返回当前接管状态

### 截图 — ScreenCapture

利用 XNA/MonoGame 的 `GraphicsDevice.GetBackBufferData()` 获取当前帧像素数据，编码为 PNG：

- `GET /screenshot?resolution=low` — 640x360，适合 AI 视觉分析
- `GET /screenshot?resolution=high` — 原始分辨率
- 返回：`{"image": "<base64 png>", "width": 640, "height": 360}`

实现要点：
- 在 `UpdateTicked` 事件中缓存当前帧，避免跨线程访问 GraphicsDevice
- 使用 `System.Drawing.Common` 或手动 PNG 编码，避免重度依赖

### 游戏状态查询 — GameStateProvider

- `GET /state` — 返回玩家位置、当前场景、时间、季节、金钱、体力
- `GET /state/npcs` — 返回当前场景可见 NPC 列表及位置
- `GET /state/tiles` — 返回玩家周围可交互对象（宝箱、门、家具等）

返回格式示例：
```json
{
  "player": {
    "name": "Player",
    "position": {"x": 1024, "y": 512, "map": "Town"},
    "facing": 2,
    "health": 100,
    "stamina": 270,
    "money": 5000
  },
  "time": {"day": 5, "season": "spring", "year": 1, "timeOfDay": 800},
  "currentLocation": "Town"
}
```

### HTTP Server

使用 `System.Net.HttpListener` 实现轻量 HTTP Server，监听 `http://localhost:5555/`（端口可配置）。

- 在模组 `Entry()` 中启动，`Dispose()` 中停止
- 所有路由返回 JSON
- 请求超时 5 秒

## Python MCP Server 设计 — autopilot_mcp

### 项目结构

```
src/autopilot_mcp/
├── __init__.py
├── server.py          # MCP Server 入口 + 工具注册
├── game_client.py     # HTTP 客户端，与游戏模组通信
├── config.py          # 配置管理
└── pyproject.toml     # 项目元数据 + 依赖
```

### MCP 工具清单

| 工具名 | 参数 | 描述 |
|--------|------|------|
| `screenshot` | `resolution: "low"\|"high"` | 截取游戏画面，返回 base64 图片 |
| `press_key` | `key: str, duration_ms: int=100` | 按下并释放按键 |
| `hold_key` | `key: str` | 持续按住按键 |
| `release_key` | `key: str` | 释放按住的按键 |
| `move_mouse` | `x: int, y: int` | 移动鼠标到指定位置 |
| `click` | `x: int, y: int, button: "left"\|"right"` | 在指定位置点击 |
| `release_all` | 无 | 释放所有按键和鼠标 |
| `set_speed` | `factor: float` | 设置游戏速度（0.1~1.0） |
| `get_game_state` | `include: list[str]` | 查询游戏状态 |
| `start_autopilot` | 无 | 启动 AI 接管模式 |
| `stop_autopilot` | 无 | 停止 AI 接管模式 |

### 技术选型

- MCP 框架：`mcp` Python SDK（FastMCP），stdio 传输
- HTTP 客户端：`httpx`（异步）
- 配置：环境变量 `AUTOPILOT_HOST`（默认 `localhost`）、`AUTOPILOT_PORT`（默认 `5555`）

### MCP Server 配置

在 AI 客户端的 MCP 配置中添加：
```json
{
  "mcpServers": {
    "stardew-autopilot": {
      "command": "python",
      "args": ["-m", "autopilot_mcp.server"],
      "cwd": "d:/Source/ValleyTalk/src/autopilot_mcp"
    }
  }
}
```

## 错误处理

### 模组侧

| 场景 | 处理 |
|------|------|
| HTTP Server 启动失败 | SMAPI 控制台输出错误，模组仍可加载但不提供 API |
| 请求超时（5s） | 返回 HTTP 504 |
| 无效参数 | 返回 HTTP 400 + 错误描述 |
| 游戏未加载完成 | 返回 HTTP 503 "Game not ready" |

### MCP Server 侧

| 场景 | 处理 |
|------|------|
| 游戏模组不可达 | 返回 MCP 错误 "Game mod not responding. Is the game running?" |
| 截图失败 | 返回文字描述的游戏状态作为降级 |
| 按键操作失败 | 返回错误，不静默忽略 |

## 安全考虑

- MCP Server 仅通过 stdio 通信，不暴露网络端口
- 游戏模组 HTTP Server 仅监听 localhost
- 提供 `stop_autopilot` 工具，AI 可随时释放控制权
- SMAPI 控制台命令 `autopilot_stop` 作为紧急停止手段

## 测试策略

1. **模组单元测试**：在 ValleyAgent.UnitTests 中添加 VirtualInputState、SpeedController 的逻辑测试
2. **集成测试**：启动游戏后通过 curl 调用 HTTP API 验证截图、按键、速度控制
3. **端到端测试**：AI 客户端通过 MCP Server 完成一次完整的「走到 NPC 旁边并对话」流程

## 关键风险

1. **Harmony 输入劫持稳定性** — 不同游戏场景（菜单、对话、钓鱼小游戏）下的输入拦截行为可能不一致
2. **截图性能** — `GetBackBufferData` 在高分辨率下可能较慢，需限制截图分辨率
3. **慢动作兼容性** — 某些游戏机制依赖固定时间步长，慢动作可能导致物理异常
4. **线程安全** — HTTP Server 在后台线程运行，访问 Game1 状态需确保线程安全
