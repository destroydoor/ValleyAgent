# ValleyAgent 双端联机手动验收 Checklist

> **分支**：`fix/multiplayer-farmhand-chain`  
> **验收范围**：C1、C2、C3 三个 bug 修复 + 双端通用联机检查  
> **术语**：主机 = 创建/加载存档的玩家（Host 模式）；客机 = 远程加入的 farmhand（ThinClient 模式）  
> **TS 服务器**：`<VALLEYAI_ROOT>\packages\stardew\bin\valley-ai-server.exe`（Bun 编译产物，源码在 `<VALLEYAI_ROOT>\packages\stardew\src`）  
> **日志路径**（Windows）：`%appdata%/StardewValley/ErrorLogs/SMAPI-latest.txt`，或在 SMAPI 控制台中实时过滤。  
> **自动分析脚本**：`scripts/test/analyze-multiplayer-logs.ps1`

---

## 1. 环境准备

### 1.0 TS 服务器准备（在主机端执行）

```powershell
# 1. 确保 Bun 已安装并可用
bun --version

# 2. 重新编译可执行文件（源码修改后必须执行）
cd <VALLEYAI_ROOT>\packages\stardew
bun build --compile --target=bun-windows-x64 src/cli.ts --outfile bin/valley-ai-server.exe

# 3. 配置 ValleyAgent config.json（主机与客机相同）
#    设置 ServerExecutablePath 指向 TS 服务器 exe，并填入 LlmApiKey
```

`Stardew Valley\Mods\ValleyAgent\config.json` 关键字段示例：

```json
{
  "Provider": "MiniMax",
  "ServerExecutablePath": "<VALLEYAI_ROOT>\\packages\\stardew\\bin\\valley-ai-server.exe",
  "ServerDirectory": "<VALLEYAI_ROOT>\\packages\\stardew",
  "AgentServerUri": "ws://127.0.0.1:8765",
  "UseAgentServer": true,
  "AutoStartServer": true,
  "LlmApiKey": "your-api-key-here",
  "LlmModel": "MiniMax-M2",
  "ServerPort": 8765
}
```

> 注意：TS 服务器与 C# Mod 之间的 WebSocket 协议为 `ws://127.0.0.1:8765`；客机 farmhand 不直接连接服务器，所有请求通过 ModMessage 转发到主机，由主机侧统一连接服务器。

| 序号 | 准备项 | 完成标准 |
|---|---|---|
| 1.1 | 两端安装同一构建产物 | 主机与客机的 `ValleyAgent` mod 文件、manifest 版本完全一致（主版本号必须相同）。 |
| 1.2 | 启动 Agent 服务器 | 主机端 TypeScript 服务器已启动并可连接，无 `connection refused`/`timeout`。服务器可执行文件位于 `<VALLEYAI_ROOT>\packages\stardew\bin\valley-ai-server.exe`。 |
| 1.3 | SMAPI 版本一致 | 两端 SMAPI 版本相同，且均启用控制台/日志输出。 |
| 1.4 | 创建/加载联机存档 | 主机进入农场，客机通过 Steam/邀请码/LAN 成功加入。 |
| 1.5 | 准备 Agent NPC | 主机确保至少一名村民为 Agent NPC（如 Haley/Abigail），且客机当前地图可见该 NPC。 |
| 1.6 | 准备礼物 | 客机背包中至少准备 1 个该 Agent NPC **喜爱**的礼物和 1 个 **不喜欢/讨厌**的礼物。 |
| 1.7 | 准备测试指令 | 客机准备好输入文字，例如：`帮我挖矿`、`去矿洞`、`笑一个`、`做个开心的表情`。 |
| 1.8 | 日志可访问 | 两端均可实时查看 SMAPI 日志，建议使用 `grep`/`findstr` 过滤关键字。 |

---

## 2. 主机端步骤

### C1 — Farmhand 点击 Agent NPC 走原版，对话功能完全失效

| 字段 | 内容 |
|---|---|
| **ID** | C1-H |
| **目标** | 验证客机点击 Agent NPC 时，请求被 ValleyAgent 接管，主机侧不会错误地让客机执行原版对话。 |
| **前置条件** | 两端已联机；Agent NPC 在客机视野内；客机未手持礼物。 |
| **操作步骤** | 1. 主机保持正常 Host 模式运行，无需额外输入。<br>2. 主机靠近 Agent NPC，确认 NPC 处于正常活动状态。<br>3. 主机观察客机是否打开对话框。 |
| **预期结果** | 主机侧无异常；NPC 未被客机原版 `checkAction` 触发普通村民问候。 |
| **失败判断标准** | 主机日志出现 `checkAction`/`tryToReceiveActiveObject` 异常；或客机报告只出现原版对话。 |
| **关键 SMAPI 日志（grep）** | `ValleyAgent initialized successfully (Host mode)`<br>`\[Multiplayer\] Sent full sync to player` |

### C2 — Farmhand 送礼代理是死代码

| 字段 | 内容 |
|---|---|
| **ID** | C2-H |
| **目标** | 验证客机送礼请求被 ModMessage 转发到主机，由 `HostGiftTransport` 评估并回包。 |
| **前置条件** | Agent NPC 存在；客机背包有待赠送物品；两端联机。 |
| **操作步骤** | 1. 主机保持 Host 模式，确保 Agent 服务器在线。<br>2. 主机打开 Social 页，记录该 Agent NPC 当前好感度作为基准。<br>3. 通知客机执行送礼。 |
| **预期结果** | 主机收到 `GiftRequest` 消息；`HostGiftTransport` 完成礼物评估；`SendGiftResponse` 向客机回包。 |
| **失败判断标准** | 主机日志无 `HandleGiftRequest`；出现 `AgentService is null` 或 `NullReferenceException`；客机报告物品未消耗。 |
| **关键 SMAPI 日志（grep）** | `HandleGiftRequest`<br>`HostGiftTransport`<br>`SendGiftResponse`<br>`\[Gift\]` |

### C3 — Farmhand 对话的 Actions 主机不执行、客机跳过

| 字段 | 内容 |
|---|---|
| **ID** | C3-H |
| **目标** | 验证客机发送含行动指令的对话后，主机侧执行 `response.Actions`，并通过广播让客机看到变化。 |
| **前置条件** | Agent NPC 存在；两端联机；Agent 服务器能够返回 actions。 |
| **操作步骤** | 1. 主机保持 Host 模式。<br>2. 主机记录 NPC 当前状态/位置作为基准（可截图）。<br>3. 通知客机输入触发动作的对话，例如 `帮我挖矿` 或 `笑一个`。 |
| **预期结果** | 主机执行动作后 NPC 状态改变或产生 emote；`BroadcastNpcAction` 将视觉动作广播给客机。 |
| **失败判断标准** | 主机日志无 `ExecuteAction`/`ExecuteSetState`；NPC 状态/位置无变化；客机看不到 emote/气泡。 |
| **关键 SMAPI 日志（grep）** | `HandleDialogueRequest`<br>`\[HostRequestHandlers\] Action '`<br>`ExecuteAction:`<br>`ExecuteSetState:`<br>`BroadcastNpcAction` |

### MP-GEN — 双端通用检查（主机侧）

| 字段 | 内容 |
|---|---|
| **ID** | MP-GEN-H |
| **目标** | 确认主机以 Host 模式初始化，并向客机正确广播状态。 |
| **前置条件** | 客机已加入农场。 |
| **操作步骤** | 1. 主机加载/创建农场。<br>2. 等待客机加入并稳定 5-10 秒。 |
| **预期结果** | 主机日志显示 Host 模式；向客机发送 `FullSync`；后续周期性 `AgentState` 广播。 |
| **失败判断标准** | 主机未进入 Host 模式；`SendFullSync` 失败或报错。 |
| **关键 SMAPI 日志（grep）** | `ValleyAgent initialized successfully (Host mode)`<br>`\[Multiplayer\] Sent full sync to player`<br>`BroadcastAgentStates` |

---

## 3. 客机（Farmhand）端步骤

### C1 — Farmhand 点击 Agent NPC 走原版，对话功能完全失效

| 字段 | 内容 |
|---|---|
| **ID** | C1-F |
| **目标** | 验证客机点击 Agent NPC 后打开原生 `DialogueBox` 并出现输入条，而非原版问候对话。 |
| **前置条件** | 已加入主机农场；Agent NPC 在当前地图可见；未手持礼物。 |
| **操作步骤** | 1. 确认客机 SMAPI 日志显示 `ThinClient mode`。<br>2. 面对 Agent NPC，按右键对话。<br>3. 观察对话框底部是否有输入条。<br>4. 尝试按 `T` 键，确认未打开原版聊天框。<br>5. 按 `ESC` 关闭对话框。 |
| **预期结果** | 打开对话框文本为 `...`，底部出现输入条提示 `输入消息后按 Enter 发送，ESC 关闭`；可按 `T` 输入文字；`ESC` 关闭。 |
| **失败判断标准** | 出现原版 NPC 问候语；无输入条；按 `T` 打开原版聊天框；左键/Enter 直接推进原版对话；日志中出现原版 `checkAction` 相关调用且没有 ValleyAgent 拦截痕迹。 |
| **关键 SMAPI 日志（grep）** | `ValleyAgent initialized successfully (ThinClient mode)`<br>`ThinClient Harmony patches applied`<br>`\[Dialogue\] Agent .* opened native DialogueBox \(mode=ThinClient\)`<br>`DialogueBoxInputPatch` |

### C2 — Farmhand 送礼代理是死代码

| 字段 | 内容 |
|---|---|
| **ID** | C2-F |
| **目标** | 验证客机送礼时代理到主机，消耗物品并显示 LLM 反馈，不走原版好感度跳字。 |
| **前置条件** | 已加入主机农场；背包有待赠送物品；Agent NPC 可交互。 |
| **操作步骤** | 1. 在工具栏选中礼物物品。<br>2. 面对 Agent NPC，按右键赠送。<br>3. 观察画面与背包变化。<br>4. 等待数秒查看 LLM 反馈对话。 |
| **预期结果** | 礼物从背包扣除（或堆叠数 -1）；未出现原版爱心/碎心图标和好感度跳字；出现 LLM 生成的反馈对话。 |
| **失败判断标准** | 物品未消耗；出现原版送礼反馈（爱心、讨厌图标、`+X 好感` 等）；无 LLM 反馈；日志出现 `Object reference not set` 等异常。 |
| **关键 SMAPI 日志（grep）** | `\[Gift\] .* received gift via transport`<br>`FarmhandGiftTransport`<br>`GiftResponse`<br>`\[Gift\] Transport main-thread display` |

### C3 — Farmhand 对话的 Actions 主机不执行、客机跳过

| 字段 | 内容 |
|---|---|
| **ID** | C3-F |
| **目标** | 验证客机输入含行动指令的对话后，主机侧 NPC 真的改变状态/移动/执行动作，且客机能看到广播。 |
| **前置条件** | 已加入主机农场；能打开 Agent NPC 输入对话；Agent 服务器可返回 actions。 |
| **操作步骤** | 1. 右键 Agent NPC 打开输入对话框。<br>2. 输入触发动作的对话，例如：`帮我挖矿`、`去矿洞`、`笑一个`、`做个开心的表情`。<br>3. 按 `Enter` 发送。<br>4. 等待主机处理并回包。<br>5. 观察 NPC 是否有 emote/气泡/状态变化/移动。 |
| **预期结果** | 客机收到 LLM 回复；随后看到 NPC 执行动作（如 `emote`、`set_state`、`move_to` 等，具体以 LLM 返回为准）；Social/地图状态与主机一致。 |
| **失败判断标准** | 仅收到文字回复但 NPC 无任何动作；主机日志无 `ExecuteAction`；客机日志无 `NpcAction` 处理；NPC 位置/状态未同步。 |
| **关键 SMAPI 日志（grep）** | `FarmhandDialogueTransport`<br>`DialogueResponse`<br>`\[Chat\] LLM response`<br>`NpcAction`<br>`doEmote` |

### MP-GEN — 双端通用检查（客机侧）

| 字段 | 内容 |
|---|---|
| **ID** | MP-GEN-F |
| **目标** | 确认客机以 ThinClient 模式初始化，收到主机状态同步，并能持续接收广播。 |
| **前置条件** | 已成功加入主机农场。 |
| **操作步骤** | 1. 客机加入后等待 5-10 秒。<br>2. 观察 Agent NPC 头顶是否有 ValleyAgent 状态 UI（血条、情绪图标等）。<br>3. 等待主机 NPC 移动或改变状态，观察客机是否同步。 |
| **预期结果** | 客机日志显示 ThinClient 模式；收到 `FullSync`；能看到 Agent NPC 状态；NPC 动作/位置与主机同步。 |
| **失败判断标准** | 客机进入 `Inert` 模式；看不到 Agent UI；收到 `version incompatible` 警告；状态长时间不同步。 |
| **关键 SMAPI 日志（grep）** | `ValleyAgent initialized successfully (ThinClient mode)`<br>`\[Multiplayer\] Received full sync:`<br>`AgentState`<br>`HandleAgentStateMessage` |

---

## 4. 共同观察步骤

### C1 — 点击后打开输入条、不是原版对话

| 字段 | 内容 |
|---|---|
| **ID** | C1-OBS |
| **目标** | 两端共同确认点击 Agent NPC 后进入 ValleyAgent 输入对话流程。 |
| **前置条件** | 客机已完成 C1 操作。 |
| **操作步骤** | 1. 客机右键 Agent NPC。<br>2. 两端同时观察屏幕与日志。 |
| **预期结果** | 客机屏幕：对话框底部有输入条；主机屏幕：该 NPC 未显示被原版对话占用；两端均无异常报错。 |
| **失败判断标准** | 客机看到原版 NPC 头像与问候语；输入条未出现；按 `T` 弹出聊天框。 |
| **关键 SMAPI 日志（grep）** | 客机：`opened native DialogueBox (mode=ThinClient)`<br>主机：无异常 `Error`/`Warn` 相关 `checkAction` |

### C2 — 送礼消耗物品并显示 LLM 反馈、不走原版好感度跳字

| 字段 | 内容 |
|---|---|
| **ID** | C2-OBS |
| **目标** | 两端共同确认送礼走 ValleyAgent 代理路径，物品被消耗并出现 LLM 反馈。 |
| **前置条件** | 客机已完成 C2 操作。 |
| **操作步骤** | 1. 客机送礼前后截图背包。<br>2. 主机记录 Social 页好感度变化。<br>3. 两端观察是否出现原版跳字/图标。 |
| **预期结果** | 客机物品减少；画面未出现原版爱心/碎心图标与好感度跳字；出现 LLM 反馈文本；主机侧好感度按 `FriendshipDelta` 更新。 |
| **失败判断标准** | 物品未消耗；出现原版 `+80` / `-40` 好感度跳字；LLM 反馈未出现；主机未收到 `GiftRequest`。 |
| **关键 SMAPI 日志（grep）** | 客机：`FarmhandGiftTransport`、`<Gift>.*received gift via transport`<br>主机：`HandleGiftRequest`、`SendGiftResponse` |

### C3 — farmhand 说"帮我挖矿"后主机执行动作并广播

| 字段 | 内容 |
|---|---|
| **ID** | C3-OBS |
| **目标** | 验证客机触发 actions 后，主机权威执行并广播，两端视觉一致。 |
| **前置条件** | 客机已完成 C3 操作；Agent 服务器返回了 actions。 |
| **操作步骤** | 1. 客机发送触发动作的对话。<br>2. 主机观察 NPC 是否改变状态/移动/执行动作。<br>3. 客机观察是否看到 emote/气泡/状态变化。<br>4. 若 LLM 未返回 actions，改用更明确指令（如 `笑一个，做个开心的表情`）重试一次。 |
| **预期结果** | 主机：NPC 状态变化（如 `ExecuteSetState: X → Mining`）或产生 emote；客机：数秒内看到 NPC emote/气泡/位置变化。 |
| **失败判断标准** | 主机无 `ExecuteAction`/`ExecuteSetState` 日志；客机看不到任何动作广播；两端 NPC 状态不一致。 |
| **关键 SMAPI 日志（grep）** | 主机：`HandleDialogueRequest`、`\[HostRequestHandlers\] Action '`、`ExecuteAction:`、`ExecuteSetState:`、`BroadcastNpcAction`<br>客机：`FarmhandDialogueTransport`、`NpcAction`、`doEmote`、`ApplyNpcAction` |

### MP-GEN — 双端联机通用检查

| 字段 | 内容 |
|---|---|
| **ID** | MP-GEN-OBS |
| **目标** | 确认版本兼容、ModMessage 连通性、状态同步。 |
| **前置条件** | 两端均已进入游戏并稳定联机。 |
| **操作步骤** | 1. 检查两端日志中的初始化模式。<br>2. 检查客机加入后是否收到 `FullSync`。<br>3. 观察 1 分钟以上，确认周期性 `AgentState` 广播无报错。<br>4. 主机移动 Agent NPC，确认客机位置平滑同步。 |
| **预期结果** | 主机 = Host 模式；客机 = ThinClient 模式；版本无冲突；FullSync 成功；状态/位置持续同步。 |
| **失败判断标准** | 客机进入 Inert 模式；版本不兼容警告；`Failed to send full sync`/`Failed to handle AgentState` 报错；NPC 位置长时间不同步或瞬移。 |
| **关键 SMAPI 日志（grep）** | `ValleyAgent initialized successfully (Host mode)`<br>`ValleyAgent initialized successfully (ThinClient mode)`<br>`host version .* incompatible`<br>`Sent full sync to player`<br>`Received full sync:`<br>`AgentState`<br>`Failed to handle .* ModMessage` |

---

## 附录：日志关键字速查表

| 测试项 | 主机端关键字 | 客机端关键字 |
|---|---|---|
| 模式初始化 | `ValleyAgent initialized successfully (Host mode)` | `ValleyAgent initialized successfully (ThinClient mode)` |
| 版本/兼容 | `host version .* incompatible` | `ValleyAgent is inactive on this farmhand` |
| 状态同步 | `[Multiplayer] Sent full sync to player` | `[Multiplayer] Received full sync:` |
| C1 对话接管 | — | `[Dialogue] Agent .* opened native DialogueBox \(mode=ThinClient\)` |
| C2 送礼代理 | `HandleGiftRequest`、`SendGiftResponse` | `FarmhandGiftTransport`、`<Gift>.*received gift via transport` |
| C3 动作执行 | `HandleDialogueRequest`、`[HostRequestHandlers] Action '`、`ExecuteAction:`、`ExecuteSetState:`、`BroadcastNpcAction` | `FarmhandDialogueTransport`、`NpcAction`、`doEmote` |
| 通用错误 | `Failed to handle .* ModMessage`、`[HostRequestHandlers] .* failed` | `No pending request for .* dropping response` |

> **注意**：所有 grep 关键字均来自当前分支源码日志文本，可直接在 SMAPI 日志中使用。若某关键字未出现，请先确认日志级别是否包含 `Debug`/`Trace`。
