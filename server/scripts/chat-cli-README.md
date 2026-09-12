# ValleyAgent Chat CLI

终端对话工具：无需启动游戏，通过 WebSocket 直连 Agent Server 与 NPC 对话，测试 Agent 的工具调用能力。

## 快速开始

### 1. 启动 Agent Server

```bash
cd D:/Source/ValleyAI

# 最简启动（需要 LLM API key）
LLM_API_KEY=your_key bun run packages/stardew/src/cli.ts

# 完整参数示例（MiniMax）
LLM_API_KEY=your_key LLM_MODEL=MiniMax-M2 LLM_BASE_URL=https://api.minimax.chat/v1 LLM_PROVIDER=minimax \
  bun run packages/stardew/src/cli.ts

# OpenAI 兼容
LLM_API_KEY=sk-xxx LLM_MODEL=gpt-4o-mini LLM_BASE_URL=https://api.openai.com/v1 LLM_PROVIDER=openai \
  bun run packages/stardew/src/cli.ts

# DeepSeek
LLM_API_KEY=your_key LLM_MODEL=deepseek-chat LLM_BASE_URL=https://api.deepseek.com LLM_PROVIDER=deepseek \
  bun run packages/stardew/src/cli.ts

# 多 provider 模式（用 JSON 配置文件）
bun run packages/stardew/src/cli.ts --llm-config path/to/config.json
```

### 2. 启动 Chat CLI

```bash
cd D:/Source/ValleyAI

# 默认连接 Haley
bun run scripts/chat-cli.ts

# 指定 NPC
bun run scripts/chat-cli.ts --npc Abigail

# 指定端口/地址
bun run scripts/chat-cli.ts --port 8765 --host 127.0.0.1
```

## 测试对话示例

### 测试基本对话

```
[You] 你好啊，今天天气不错
[Haley] 嗯，还行吧。我在考虑要不要去海滩拍几张照片。
```

### 测试 give_item（NPC 给玩家物品）

先用 `/item` 让 NPC 背包里有东西，再引导 NPC 送出：

```
/item 紫水晶 3
你有紫水晶吗？能给我一个吗？
→ NPC 应该调用 give_item 工具，CLI 会提示确认
```

### 测试 trade（交易）

```
/held 铜矿 5
我想用 5 个铜矿跟你换 200 金
→ NPC 应该调用 trade 工具
```

### 测试 set_goal（NPC 自主目标）

```
你能去帮我砍几棵树吗？砍 5 棵。
→ NPC 应该调用 set_goal 工具
```

### 测试 set_state（状态变更）

```
别站着发呆了，去工作吧。
→ NPC 可能调用 set_state 切换到 WORKING
```

### 测试 remember（记忆）

```
记住，我最喜欢吃披萨。
→ NPC 应该调用 remember 工具
```

### 测试 give_gift（送礼）

```
今天是我生日，你有什么想送我的吗？
→ NPC 可能调用 give_gift 工具
```

### 测试情绪和好感度

```
/money 0
你最近手头紧吗？我借你 500 金吧。
→ 观察 friendshipDelta 和 emotion 变化
```

### 测试降级（fallback）

断开 LLM（或用无效 API key 重启 server），对话应显示 `[降级]` 标签。

## 命令速查

| 命令 | 说明 |
|------|------|
| `/status` | 显示当前模拟快照完整信息 |
| `/money <n>` | 设置 NPC 钱包余额 |
| `/item <名> <qty>` | NPC 背包增减（负数减少） |
| `/pitem <名> <qty>` | 玩家背包增减 |
| `/held <名> <qty>` | 设置玩家手持物（0 取消） |
| `/mood <标签>` | 设置 NPC 心情 |
| `/state <状态>` | 设置 NPC 状态（IDLE/TALKING/WORKING...） |
| `/loc <地图名>` | 设置玩家位置 |
| `/weather <w>` | 设置天气（sunny/rainy/snowy...） |
| `/time <hh:mm>` | 设置游戏时间 |
| `/friend <点数>` | 设置好感度（0-2500） |
| `/events <事件;...>` | 设置近期事件（分号分隔） |
| `/working <描述>` | 设置工作标记 |
| `/goal <type> <qty>` | 设置当前目标 |
| `/npc <名字>` | 切换 NPC（server 端按 npcName 独立记忆） |
| `/clear` | 提示如何重置 server 侧记忆 |
| `/help` | 显示命令列表 |
| `/bye` | 退出 |

## 工作原理

1. **连接**：CLI 通过 WebSocket 连接 Agent Server（默认 ws://127.0.0.1:8765）
2. **握手**：发送 `hello` 消息验证连通性
3. **对话**：每轮发送 `dialogue` 消息（携带 worldSnapshot），收到 `dialogue_response`
4. **模拟回执**：收到 `actions` 后，CLI 逐个询问是否模拟执行成功，然后发送 `action_result` 回执给 server——这样 NPC 下一轮对话能感知"刚才的动作成功/失败了"
5. **本地状态**：成功执行的工具会按语义更新 CLI 侧的模拟快照（背包/钱/心情等），但 server 侧以自己的记忆为准

## 常见问题

### Q: 连接失败 "WebSocket 连接失败"

确保 Agent Server 已启动。运行 `bun run packages/stardew/src/cli.ts` 前必须设置 `LLM_API_KEY` 环境变量。

### Q: NPC 回复很慢

LLM 响应时间取决于 provider。MiniMax 通常 2-5 秒，OpenAI 1-3 秒。CLI 默认 60 秒超时。

### Q: 想重置 NPC 记忆从头开始

server 将记忆存储在 `agents/` 目录下。删除对应 NPC 的 rel 文件：

```bash
rm -rf D:/Source/ValleyAI/agents/Haley_players/
```

然后用 `/npc Haley` 重连即可获得全新对话。

### Q: 如何测试 fallback（降级响应）

重启 server 时不设置有效的 LLM API key（或设置一个无法访问的 base URL），NPC 会走规则引擎降级响应，CLI 会显示 `[降级]` 标签。

### Q: `--npc` 指定的 NPC 名不在 npc_prompts.json 里

server 会用默认模板处理未知 NPC，对话仍可进行，但人设不会被加载。
