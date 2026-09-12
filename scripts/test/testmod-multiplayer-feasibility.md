# TestMod 双端联机测试可行性调研

> **调研日期**：2026-07-18  
> **分支**：`fix/multiplayer-farmhand-chain`  
> **调研范围**：`src/ValleyAgent.TestMod` 现有框架是否支持双端联机实测

---

## 1. 结论（TL;DR）

当前 `ValleyAgent.TestMod` 是**单机架构**，不能直接用于双端协同自动化测试。建议：

- **P0（本次交付）**：使用 `scripts/test/multiplayer-farmhand-checklist.md` 手动双端验收 + `scripts/test/analyze-multiplayer-logs.ps1` 自动分析日志。
- **P1（后续迭代）**：扩展 TestMod，使其能识别 `ThinClient`/`Host` 模式，分别在两端执行断言，并通过 ModMessage 或共享文件实现双端协同。

---

## 2. 现有 TestMod 架构分析

### 2.1 核心类职责

| 类/文件 | 职责 | 联机相关性 |
|---|---|---|
| `V3TestRunner` | 单实例 tick 驱动，串行执行 `_tests` 列表 | 单机：直接操作 `Game1.player`、`Game1.warpFarmer` |
| `V3TestBase` | 断言、结果保存、视觉断言队列、Input/Screenshot/MockLLM 钩子 | 无模式感知 |
| `TestConfig` / `TestFilter` | JSON 配置与分组过滤 | 无联机相关字段 |
| `manifest.json` | 依赖 `dandm1.ValleyAgent` | 在 farmhand 端也能加载 |
| `Input/InputSimulator.cs` | 模拟鼠标右键/键盘输入 | 可用于 farmhand 端自动触发点击/输入 |
| `Input/ScreenshotCapture.cs` | 截图/录屏 | 双端均可使用 |
| `Runners/ExperienceTestRunner.cs` | 体验类测试包装 | 单机 |
| `Tests/Experience/EXP001_ClickNpcOpensDialogue.cs` | 单机点击 NPC 测试 | 未区分 Host/ThinClient |
| `Tests/Functional/Func_DialogueActions.cs` | 单机对话 action 测试 | 直接调用 `TryGenerateDialogue` |

### 2.2 单机假设的证据

1. `V3TestRunner.Setup()` 中直接调用 `Game1.warpFarmer("Farm", ...)`、`Game1.getCharacterFromName("Haley")`、`api.TryAllocateAgent("Haley")`。
   - 在 **farmhand** 端，`TryAllocateAgent` 可能不可用或行为不同（ThinClient 无完整 `AgentService`）。
2. `V3TestRunner` 通过 `Game1.quit = true` 在测试结束后退出游戏。
   - 双端测试中不能直接退出 farmhand，否则主机端也无法继续。
3. `V3TestBase` 没有 `IsHost` / `IsFarmhand` 运行时判断。
   - 现有测试默认自己是主机/单机玩家。
4. `TestGroup` 枚举只有 `Module/Fuzzy/Edge/Functional/Real/Visual/Experience/Pipeline`，**没有 `Multiplayer` 分组**。

---

## 3. 双端测试的挑战

### 3.1 运行时模式差异

- **Host 端**：`ModEntry` 进入 `Host` 模式，拥有完整 `ServiceContainer`、`AgentService`、`CommandExecutor`。
- **ThinClient 端**：`ModEntry` 进入 `ThinClient` 模式，只有 `AgentRemoteRenderer`、`FarmhandDialogueTransport`、`FarmhandGiftTransport`。
- **Inert 端**：farmhand 主机未装 mod 或版本不兼容时进入 Inert，TestMod 不应运行任何断言。

### 3.2 TestMod 在 farmhand 端能加载吗？

- `manifest.json` 的 `Dependencies` 中只有 `dandm1.ValleyAgent`。
- `ValleyAgent` 在 ThinClient 模式下仍然会加载并初始化（只是功能受限）。
- 因此 **TestMod 可以在 farmhand 端加载**，但大部分基于 `ModEntry.API` 的断言会失败或行为异常。

### 3.3 需要新增的能力

| 能力 | 说明 | 影响文件 |
|---|---|---|
| 运行时模式检测 | 测试需要知道自己是 Host/Farmhand/Inert | `V3TestBase` 或新增 `MultiplayerTestContext` |
| 双端协同同步 | 需要让 farmhand 等待主机就绪，或主机等待 farmhand 操作完成 | 新增 `MultiplayerSyncSignal`（文件锁/ModMessage/命名管道） |
| 条件化 Setup | farmhand 端不能 `TryAllocateAgent`，只能等待主机广播 | 改造 `V3TestRunner` Setup |
| 条件化断言 | Host 断言 NPC 状态变化；farmhand 断言远程渲染/transport 行为 | 改造 `V3TestBase` |
| 不退出游戏 | 双端测试结束时只保存结果，不调用 `Game1.quit` | 新增配置 `AutoExitOnComplete=false` |

---

## 4. 推荐方案

### 4.1 P0：手动 + 日志自动化（已交付）

- 使用 `multiplayer-farmhand-checklist.md` 手动执行 C1/C2/C3。
- 使用 `analyze-multiplayer-logs.ps1` 对双端 SMAPI 日志做自动 verdict。
- 优点：无需改动 TestMod，当天即可验收；覆盖真实玩家操作路径。
- 缺点：依赖人工执行，无法回归测试。

P0 实测补充建议：
- **截图/录像**：建议用 OBS/GeForce Experience 录制 farmhand 视角全程，重点保留点击 NPC、送礼、输入对话三段画面，便于事后复盘。
- **Mock LLM**：C3 对 LLM 返回 actions 的稳定性要求高。若使用真实 LLM 频繁不返回 action，可临时启用 mock LLM 让 `帮我挖矿` 固定返回 `{ "tool": "set_state", "parameters": { "state": "Mining" } }`，降低测试噪音。
- **日志备份**：双端测试开始前删除或重命名 `%APPDATA%\StardewValley\ErrorLogs\SMAPI-latest.txt`，确保日志只包含本次测试内容。

### 4.2 P1：扩展 TestMod 支持双端（未来迭代）

建议新增以下文件：

```
src/ValleyAgent.TestMod/
  Infrastructure/
    MultiplayerTestContext.cs      # 检测 Host/Farmhand/Inert
    MultiplayerSyncSignal.cs       # 双端同步信号（文件锁或 ModMessage）
  Tests/Multiplayer/
    MP01_FarmhandDialogueOpens.cs  # C1
    MP02_FarmhandGiftTransport.cs  # C2
    MP03_FarmhandActionExecution.cs # C3
  V3TestRunner.cs                  # 改造：条件化 setup、不退出、支持 MP 分组
  TestConfig.cs                    # 新增 multiplayer 相关配置
  TestFilter.cs                    # 新增 TestGroup.Multiplayer
```

实现要点：

1. **模式检测**：
   ```csharp
   public static class MultiplayerTestContext
   {
       public static bool IsHost => !Context.IsMultiplayer || Context.IsMainPlayer;
       public static bool IsFarmhand => Context.IsMultiplayer && !Context.IsMainPlayer;
       public static bool IsInert => ModEntry.Instance?.GetApi() == null; // 近似
   }
   ```

2. **C1 测试（farmhand 端）**：
   - Setup：等待 `AgentRemoteRenderer` 收到 `FullSync` 且目标 NPC 存在。
   - Update tick 60：用 `InputSimulator` 右键 NPC。
   - Update tick 120：断言 `Game1.activeClickableMenu is DialogueBox` + 底部输入条可见（视觉断言）。
   - 主机端同步断言：收到 `DialogueRequest` 或 `NPCDialoguePatch.InterceptCount` 增加。

3. **C2 测试（farmhand 端）**：
   - Setup：给 farmhand 背包添加礼物。
   - Update：右键 NPC 送礼。
   - 断言：物品从背包扣除；`FarmhandGiftTransport` 发出请求；主机收到 `GiftRequest`。

4. **C3 测试（双端）**：
   - farmhand 端：输入 `帮我挖矿`。
   - 主机端：断言 `HostRequestHandlers` 执行了 action，`CommandExecutor` 改变了 NPC 状态。
   - farmhand 端：断言数秒内看到 `NpcAction` 广播（emote/气泡）。

5. **同步机制**：
   - 简单方案：用共享文件锁 + JSON 状态文件（`logs/mp_sync.json`）。
   - 高级方案：用 SMAPI `ModMessage` 发送测试专用消息。

---

## 5. 风险与建议

| 风险 | 影响 | 建议 |
|---|---|---|
| DetermineRuntimeMode 使用 `Game1.MasterPlayer` 可能在联机加入时序上不稳定 | P0 手动测试也可能遇到 | 在 checklist 中增加“客机重进一次”兜底步骤 |
| FarmhandDialogueTransport 按 npcName 前缀 FIFO 匹配 pending，同 NPC 并发请求可能错配 | C1/C3 若快速连续点击可能不稳定 | 手动测试中每次操作后等待 3-5 秒；P1 自动化时单次请求 |
| LLM 响应不稳定，可能不会返回 actions | C3 可能因 LLM 而不通过 | 使用 mock LLM 或明确指令（如 `笑一个`）提高 action 返回概率 |
| TestMod 直接退出游戏会中断双端 | P1 必须改造 | 增加 `AutoExitOnComplete=false` 时只保存结果不退出 |

---

## 6. 下一步行动

1. **本次**：使用 `multiplayer-farmhand-checklist.md` + `analyze-multiplayer-logs.ps1` 完成 P0 双端验收。
2. **后续**：根据 P0 验收结果，决定是否投入 P1 TestMod 双端自动化扩展。
