#!/usr/bin/env bun
/**
 * ValleyAgent 终端对话工具。
 * 通过 WebSocket 连接 ValleyAI Agent Server，无需启动游戏即可与 NPC 对话并测试工具调用。
 *
 * 用法: bun run scripts/chat-cli.ts [--npc Haley] [--port 8765] [--host 127.0.0.1]
 */

import { readFileSync } from "fs";
import { resolve } from "path";

// ── 常量 ──────────────────────────────────────────────────────────────────────

const VERSION = "1.0.0";
const DEFAULT_PORT = 8765;
const DEFAULT_HOST = "127.0.0.1";
const DEFAULT_NPC = "Haley";
const RECV_TIMEOUT_MS = 60_000; // 单次响应 60s 超时

// ── 协议类型（以 messages.json 为准） ─────────────────────────────────────────

interface ToolAction {
  tool: string;
  args: Record<string, unknown>;
  callId?: string;
}

interface WorldSnapshot {
  season: string;
  day: number;
  time: string;
  weather: string;
  location: string;
  npcTile: { x: number; y: number };
  nearbyObjects: string;
  friendship: number;
  npcState: string;
  inventory: Array<{ name: string; quantity: number }>;
  farmerName: string;
  playerMoney?: number;
  npcLocation?: string;
  npcMoney?: number;
  npcInventory?: Array<{ name: string; quantity: number }>;
  playerHeldItem?: { itemId: string; name: string; qty: number; marketPrice: number } | null;
  currentGoal?: { type: string; params: Record<string, unknown>; progress: string; status: string } | null;
  npcMood?: string;
  npcRecentEvents?: string[];
  npcWorkingOn?: string | null;
}

// ── 本地模拟状态（仅 CLI 显示用，server 侧以自身记忆为准） ────────────────────

interface SimState {
  npcName: string;
  snapshot: WorldSnapshot;
}

function createDefaultSnapshot(): WorldSnapshot {
  return {
    season: "summer",
    day: 28,
    time: "14:30",
    weather: "sunny",
    location: "Farm",
    npcTile: { x: 32, y: 30 },
    nearbyObjects: "玩家农场",
    friendship: 500,
    npcState: "IDLE",
    inventory: [{ name: "木", quantity: 10 }],
    farmerName: "Farmer",
    playerMoney: 5000,
    npcLocation: "Farm",
    npcMoney: 1000,
    npcInventory: [],
    playerHeldItem: null,
    currentGoal: null,
    npcMood: "neutral",
    npcRecentEvents: [],
    npcWorkingOn: null,
  };
}

// ── 工具函数 ──────────────────────────────────────────────────────────────────

/** 生成简短 request ID */
function rid(): string {
  return `cli-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

/** 读取 NPC 人设列表 */
function loadNpcNames(): string[] {
  try {
    const dataPath = resolve(import.meta.dir, "../packages/stardew/data/npc_prompts.json");
    const raw = readFileSync(dataPath, "utf-8");
    return Object.keys(JSON.parse(raw));
  } catch {
    return [];
  }
}

/** 格式化工具参数摘要（截断过长的值） */
function summarizeArgs(args: Record<string, unknown>): string {
  const parts: string[] = [];
  for (const [k, v] of Object.entries(args)) {
    const s = typeof v === "string" ? v : JSON.stringify(v);
    parts.push(`${k}=${s.length > 40 ? s.slice(0, 37) + "..." : s}`);
  }
  return parts.join(", ");
}

/** 打印一行分隔线 */
function hr(char = "─", len = 60): void {
  console.log(char.repeat(len));
}

// ── CLI 参数解析 ──────────────────────────────────────────────────────────────

interface CliArgs {
  npc: string;
  port: number;
  host: string;
  help: boolean;
}

function parseArgs(): CliArgs {
  const args: CliArgs = { npc: DEFAULT_NPC, port: DEFAULT_PORT, host: DEFAULT_HOST, help: false };
  const argv = process.argv.slice(2);
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i]!;
    switch (a) {
      case "--npc": case "-n": { const n = argv[++i]; if (n) args.npc = n; break; }
      case "--port": case "-p": { const p = argv[++i]; if (p) args.port = parseInt(p, 10); break; }
      case "--host": { const h = argv[++i]; if (h) args.host = h; break; }
      case "--help": args.help = true; break;
    }
  }
  return args;
}

function printHelp(npcNames: string[]): void {
  console.log(`
ValleyAgent Chat CLI v${VERSION} — 终端对话工具

用法:
  bun run scripts/chat-cli.ts [options]

选项:
  --npc, -n <name>     初始 NPC 名 (默认: ${DEFAULT_NPC})
  --port, -p <number>  WebSocket 端口 (默认: ${DEFAULT_PORT})
  --host <addr>        服务器地址 (默认: ${DEFAULT_HOST})
  --help               显示此帮助

环境变量 (server 端):
  LLM_API_KEY          LLM API 密钥 (必需)
  LLM_MODEL            模型名 (默认: MiniMax-M2)
  LLM_BASE_URL         API base URL
  LLM_PROVIDER         提供者 (minimax|openai|deepseek)

启动 server:
  cd <ValleyAI 仓库根目录>
  LLM_API_KEY=your_key bun run packages/stardew/src/cli.ts

对话中可用命令:
  /status              显示当前模拟快照
  /money <n>           设置 NPC 钱包余额
  /item <名> <qty>     NPC 背包增减物品 (负数减少)
  /pitem <名> <qty>    玩家背包增减物品
  /held <名> <qty>     设置玩家手持物
  /mood <标签>         设置 NPC 心情
  /state <状态>        设置 NPC 状态 (IDLE/TALKING/WORKING...)
  /loc <地图名>        设置玩家位置
  /weather <w>         设置天气
  /time <hh:mm>        设置时间
  /friend <点数>       设置好感度 (0-2500)
  /events <事件;...>   设置近期事件 (分号分隔)
  /working <描述>      设置工作标记
  /goal <type> <qty>   设置当前目标
  /npc <名字>          切换 NPC (server 端按 npcName 独立记忆)
  /clear               提示如何重置 server 侧记忆
  /help                显示命令列表
  /bye                 退出

可用 NPC: ${npcNames.length > 0 ? npcNames.join(", ") : "(启动 server 后自动加载)"}
`);
}

// ── WebSocket 通信 ────────────────────────────────────────────────────────────

/** 发送 fire-and-forget 消息 */
function sendOnly(ws: WebSocket, payload: Record<string, unknown>): void {
  ws.send(JSON.stringify(payload));
}

/** 发送 hello 握手 */
async function doHello(ws: WebSocket): Promise<Record<string, unknown>> {
  const reqId = rid();
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      ws.removeEventListener("message", onMsg);
      reject(new Error("握手超时"));
    }, 10_000);
    const onMsg = (event: MessageEvent) => {
      try {
        const data = JSON.parse(typeof event.data === "string" ? event.data : String(event.data));
        if (data.type === "hello" && data.requestId === reqId) {
          clearTimeout(timer);
          ws.removeEventListener("message", onMsg);
          resolve(data);
        }
      } catch { /* ignore non-JSON */ }
    };
    ws.addEventListener("message", onMsg);
    ws.send(JSON.stringify({ type: "hello", requestId: reqId, modVersion: `chat-cli/${VERSION}` }));
  });
}

/** 发送 dialogue 并等待 dialogue_response */
async function doDialogue(ws: WebSocket, req: Record<string, unknown>): Promise<Record<string, unknown>> {
  const reqId = req.requestId as string;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      ws.removeEventListener("message", onMsg);
      reject(new Error(`响应超时 (${RECV_TIMEOUT_MS / 1000}s)`));
    }, RECV_TIMEOUT_MS);
    const onMsg = (event: MessageEvent) => {
      try {
        const data = JSON.parse(typeof event.data === "string" ? event.data : String(event.data));
        if (data.type === "dialogue_response" && data.requestId === reqId) {
          clearTimeout(timer);
          ws.removeEventListener("message", onMsg);
          resolve(data);
        }
      } catch { /* ignore */ }
    };
    ws.addEventListener("message", onMsg);
    ws.send(JSON.stringify(req));
  });
}

/** 建立 WebSocket 连接 */
function connectWs(url: string): Promise<WebSocket> {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(url);
    const timer = setTimeout(() => {
      ws.close();
      reject(new Error("连接超时"));
    }, 5000);
    ws.addEventListener("open", () => { clearTimeout(timer); resolve(ws); });
    ws.addEventListener("error", () => { clearTimeout(timer); reject(new Error("WebSocket 连接失败")); });
  });
}

/** 读取一行用户输入 */
function readLine(): Promise<string> {
  return new Promise((resolve) => {
    process.stdout.write("\x1b[36m  [You]\x1b[0m ");
    process.stdin.setEncoding("utf-8");
    process.stdin.resume();
    process.stdin.once("data", (data: string) => resolve(data.trim()));
  });
}

/** 询问 y/n（默认 y） */
function askYesNo(question: string): Promise<boolean> {
  return new Promise((resolve) => {
    process.stdout.write(`${question} (Y/n) `);
    process.stdin.setEncoding("utf-8");
    process.stdin.resume();
    process.stdin.once("data", (data: string) => {
      const answer = data.trim().toLowerCase();
      resolve(answer !== "n" && answer !== "no");
    });
  });
}

/** 询问输入（有默认值） */
function askInput(question: string, defaultVal: string): Promise<string> {
  return new Promise((resolve) => {
    process.stdout.write(`${question}: `);
    process.stdin.setEncoding("utf-8");
    process.stdin.resume();
    process.stdin.once("data", (data: string) => {
      const answer = data.trim();
      resolve(answer || defaultVal);
    });
  });
}

// ── 命令处理 ──────────────────────────────────────────────────────────────────

type CommandResult = { action: "ok" } | { action: "exit" } | { action: "switchNpc"; name: string };

function handleCommand(input: string, sim: SimState, npcNames: string[]): CommandResult {
  const parts = input.slice(1).split(/\s+/);
  const cmd = parts[0]?.toLowerCase();
  const args = parts.slice(1);

  switch (cmd) {
    case "bye": case "quit": case "exit":
      return { action: "exit" };

    case "help": {
      console.log(`
  命令列表:
    /status              显示当前模拟快照
    /money <n>           设置 NPC 钱包余额
    /item <名> <qty>     NPC 背包增减 (负数减少)
    /pitem <名> <qty>    玩家背包增减
    /held <名> <qty>     设置玩家手持物 (0 取消)
    /mood <标签>         设置 NPC 心情
    /state <状态>        设置 NPC 状态
    /loc <地图名>        设置玩家位置
    /weather <w>         设置天气
    /time <hh:mm>        设置时间
    /friend <点数>       设置好感度 (0-2500)
    /events <事件;...>   设置近期事件 (分号分隔)
    /working <描述>      设置工作标记 (空串清除)
    /goal <type> <qty>   设置当前目标
    /npc <名字>          切换 NPC
    /clear               提示如何重置记忆
    /help                此帮助
    /bye                 退出
`);
      break;
    }

    case "status": {
      const s = sim.snapshot;
      console.log(`
  当前快照 (${sim.npcName}):
    季节: ${s.season}  日: ${s.day}  时间: ${s.time}  天气: ${s.weather}
    玩家位置: ${s.location}  NPC 位置: ${s.npcLocation ?? "?"}
    NPC 坐标: (${s.npcTile.x}, ${s.npcTile.y})
    附近: ${s.nearbyObjects}
    好感度: ${s.friendship}
    NPC 状态: ${s.npcState}  心情: ${s.npcMood ?? "neutral"}
    NPC 钱: ${s.npcMoney ?? 0}  玩家钱: ${s.playerMoney ?? 0}
    玩家背包: ${s.inventory.map(i => `${i.name}x${i.quantity}`).join(", ") || "(空)"}
    NPC 背包: ${(s.npcInventory ?? []).map(i => `${i.name}x${i.quantity}`).join(", ") || "(空)"}
    玩家手持: ${s.playerHeldItem ? `${s.playerHeldItem.name}x${s.playerHeldItem.qty}` : "无"}
    工作: ${s.npcWorkingOn ?? "无"}
    目标: ${s.currentGoal ? `${s.currentGoal.type} (${s.currentGoal.status})` : "无"}
    近期事件: ${(s.npcRecentEvents ?? []).join("; ") || "无"}
    农夫名: ${s.farmerName}
`);
      break;
    }

    case "money": {
      const n = parseInt(args[0] ?? "", 10);
      if (isNaN(n)) { console.log("  用法: /money <数字>"); break; }
      sim.snapshot.npcMoney = n;
      console.log(`  NPC 钱包: ${n}`);
      break;
    }

    case "item": {
      const name = args[0];
      const qty = parseInt(args[1] ?? "", 10);
      if (!name || isNaN(qty)) { console.log("  用法: /item <物品名> <数量>"); break; }
      const inv = sim.snapshot.npcInventory ?? (sim.snapshot.npcInventory = []);
      const existing = inv.find(i => i.name === name);
      if (existing) {
        existing.quantity += qty;
        if (existing.quantity <= 0) {
          sim.snapshot.npcInventory = inv.filter(i => i !== existing);
          console.log(`  NPC 背包: ${name} 已移除`);
        } else {
          console.log(`  NPC 背包: ${name} x${existing.quantity}`);
        }
      } else if (qty > 0) {
        inv.push({ name, quantity: qty });
        console.log(`  NPC 背包: +${name} x${qty}`);
      } else {
        console.log(`  NPC 背包: ${name} 不存在，无法减少`);
      }
      break;
    }

    case "pitem": {
      const name = args[0];
      const qty = parseInt(args[1] ?? "", 10);
      if (!name || isNaN(qty)) { console.log("  用法: /pitem <物品名> <数量>"); break; }
      const inv = sim.snapshot.inventory;
      const existing = inv.find(i => i.name === name);
      if (existing) {
        existing.quantity += qty;
        if (existing.quantity <= 0) {
          sim.snapshot.inventory = inv.filter(i => i !== existing);
          console.log(`  玩家背包: ${name} 已移除`);
        } else {
          console.log(`  玩家背包: ${name} x${existing.quantity}`);
        }
      } else if (qty > 0) {
        inv.push({ name, quantity: qty });
        console.log(`  玩家背包: +${name} x${qty}`);
      } else {
        console.log(`  玩家背包: ${name} 不存在，无法减少`);
      }
      break;
    }

    case "held": {
      const name = args[0];
      const qty = parseInt(args[1] ?? "0", 10);
      if (!name || qty <= 0) {
        sim.snapshot.playerHeldItem = null;
        console.log("  玩家手持: 无");
      } else {
        sim.snapshot.playerHeldItem = { itemId: `custom_${name}`, name, qty, marketPrice: 100 };
        console.log(`  玩家手持: ${name} x${qty}`);
      }
      break;
    }

    case "mood": {
      const mood = args.join(" ") || "neutral";
      sim.snapshot.npcMood = mood;
      console.log(`  NPC 心情: ${mood}`);
      break;
    }

    case "state": {
      const state = args[0] ?? "IDLE";
      sim.snapshot.npcState = state.toUpperCase();
      console.log(`  NPC 状态: ${sim.snapshot.npcState}`);
      break;
    }

    case "loc": {
      const loc = args.join(" ") || "Farm";
      sim.snapshot.location = loc;
      console.log(`  玩家位置: ${loc}`);
      break;
    }

    case "weather": {
      const w = args[0] ?? "sunny";
      sim.snapshot.weather = w;
      console.log(`  天气: ${w}`);
      break;
    }

    case "time": {
      const t = args[0] ?? "12:00";
      sim.snapshot.time = t;
      console.log(`  时间: ${t}`);
      break;
    }

    case "friend": {
      const n = parseInt(args[0] ?? "", 10);
      if (isNaN(n)) { console.log("  用法: /friend <0-2500>"); break; }
      sim.snapshot.friendship = Math.max(0, Math.min(2500, n));
      console.log(`  好感度: ${sim.snapshot.friendship}`);
      break;
    }

    case "events": {
      const evts = input.slice("/events ".length + 1).split(";").map(s => s.trim()).filter(Boolean);
      sim.snapshot.npcRecentEvents = evts;
      console.log(`  近期事件: ${evts.join("; ") || "(无)"}`);
      break;
    }

    case "working": {
      const desc = input.slice("/working ".length + 1).trim();
      sim.snapshot.npcWorkingOn = desc || null;
      console.log(`  工作标记: ${desc || "(无)"}`);
      break;
    }

    case "goal": {
      const type = args[0];
      const qty = parseInt(args[1] ?? "1", 10);
      if (!type) {
        sim.snapshot.currentGoal = null;
        console.log("  目标: 已清除");
      } else {
        sim.snapshot.currentGoal = { type, params: { quantity: qty }, progress: "0/" + qty, status: "Executing" };
        console.log(`  目标: ${type} x${qty}`);
      }
      break;
    }

    case "npc": {
      const name = args[0];
      if (!name) {
        console.log(`  当前 NPC: ${sim.npcName}`);
        if (npcNames.length > 0) console.log(`  可选: ${npcNames.join(", ")}`);
        break;
      }
      return { action: "switchNpc", name };
    }

    case "clear": {
      console.log(`  [提示] server 侧记忆存储在 agents/ 目录下。`);
      console.log(`  删除对应的 rel 文件可重置 NPC 记忆:`);
      console.log(`    rm -rf ./agents/${sim.npcName}_players/`);
      console.log(`  然后重连 (/npc ${sim.npcName}) 即可获得全新对话。`);
      break;
    }

    default:
      console.log(`  未知命令: /${cmd}。输入 /help 查看命令列表。`);
  }

  return { action: "ok" };
}

// ── 工具执行模拟（按语义更新本地状态） ────────────────────────────────────────

function applyToolToSim(sim: SimState, tool: string, args: Record<string, unknown>) {
  const s = sim.snapshot;
  switch (tool) {
    case "give_item": {
      // NPC 给玩家物品：NPC 背包减少，玩家背包增加
      const name = String(args.itemName ?? args.name ?? args.item ?? "物品");
      const qty = Number(args.quantity ?? args.qty ?? 1);
      const npcInv = s.npcInventory ?? [];
      const npcItem = npcInv.find(i => i.name === name);
      if (npcItem) {
        npcItem.quantity = Math.max(0, npcItem.quantity - qty);
        if (npcItem.quantity === 0) s.npcInventory = npcInv.filter(i => i !== npcItem);
      }
      const pItem = s.inventory.find(i => i.name === name);
      if (pItem) pItem.quantity += qty;
      else s.inventory.push({ name, quantity: qty });
      break;
    }
    case "trade": {
      // 交易：涉及钱和物品
      const money = Number(args.money ?? args.price ?? 0);
      if (money > 0) {
        // NPC 卖给玩家：玩家钱减少，NPC 钱增加
        s.playerMoney = (s.playerMoney ?? 0) - money;
        s.npcMoney = (s.npcMoney ?? 0) + money;
      } else if (money < 0) {
        // NPC 买玩家的：玩家钱增加，NPC 钱减少
        s.playerMoney = (s.playerMoney ?? 0) - money;
        s.npcMoney = (s.npcMoney ?? 0) + money;
      }
      break;
    }
    case "give_gift": {
      const name = String(args.itemName ?? args.name ?? args.item ?? "礼物");
      const npcInv = s.npcInventory ?? [];
      const npcItem = npcInv.find(i => i.name === name);
      if (npcItem) {
        npcItem.quantity = Math.max(0, npcItem.quantity - 1);
        if (npcItem.quantity === 0) s.npcInventory = npcInv.filter(i => i !== npcItem);
      }
      break;
    }
    case "receive_payment": {
      const amount = Number(args.amount ?? args.money ?? 0);
      s.playerMoney = (s.playerMoney ?? 0) - amount;
      s.npcMoney = (s.npcMoney ?? 0) + amount;
      break;
    }
    case "set_state": {
      const state = String(args.state ?? args.newState ?? "");
      if (state) s.npcState = state;
      break;
    }
    case "set_goal": {
      const type = String(args.type ?? args.goal ?? "");
      const qty = Number(args.quantity ?? args.qty ?? 1);
      if (type) s.currentGoal = { type, params: { quantity: qty }, progress: "0/" + qty, status: "NotStarted" };
      break;
    }
    case "set_npc_mood": {
      const mood = String(args.mood ?? args.emotion ?? "");
      if (mood) s.npcMood = mood;
      break;
    }
    case "remember": {
      const text = String(args.text ?? args.content ?? args.memory ?? "");
      if (text) {
        const evts = s.npcRecentEvents ?? [];
        evts.push(text);
        s.npcRecentEvents = evts;
      }
      break;
    }
    case "emote": {
      const emotion = String(args.emotion ?? args.emote ?? "");
      if (emotion) s.npcMood = emotion;
      break;
    }
    // speak, show_dialogue, get_info, forget, accept_job, evaluate_friendship 不更新本地状态
  }
}

// ── 主逻辑 ────────────────────────────────────────────────────────────────────

async function main() {
  const cliArgs = parseArgs();
  const npcNames = loadNpcNames();

  if (cliArgs.help) {
    printHelp(npcNames);
    process.exit(0);
  }

  const wsUrl = `ws://${cliArgs.host}:${cliArgs.port}`;
  let sim: SimState = { npcName: cliArgs.npc, snapshot: createDefaultSnapshot() };

  // ── 欢迎横幅 ──
  console.log();
  hr("=");
  console.log(`  ValleyAgent Chat CLI v${VERSION}`);
  hr("=");
  console.log(`  NPC:     ${sim.npcName}`);
  console.log(`  Server:  ${wsUrl}`);
  console.log(`  命令:    /help 查看所有命令, /bye 退出`);
  hr("=");
  console.log();

  // ── 连接（外层循环：NPC 切换时重连） ──
  let ws: WebSocket;
  try {
    ws = await connectWs(wsUrl);
  } catch (err: unknown) {
    const msg = err instanceof Error ? err.message : String(err);
    console.error(`  [错误] 无法连接到 ${wsUrl}`);
    console.error(`  原因: ${msg}`);
    console.error();
    console.error(`  请先启动 ValleyAI Agent Server:`);
    console.error(`    cd <ValleyAI 仓库根目录>`);
    console.error(`    LLM_API_KEY=your_key bun run packages/stardew/src/cli.ts`);
    console.error();
    process.exit(1);
  }

  // ── hello 握手 ──
  try {
    const helloResp = await doHello(ws);
    console.log(`  [握手成功] serverVersion=${helloResp.serverVersion ?? "?"}`);
  } catch (err: unknown) {
    console.warn(`  [握手失败] ${err instanceof Error ? err.message : err}`);
    console.warn(`  继续尝试对话...`);
  }

  // ── 交互循环（NPC 切换时跳出重连） ──
  outer: while (true) {
    const result = await chatRound(ws, sim);
    switch (result.action) {
      case "exit":
        break outer;
      case "switchNpc":
        // 切换 NPC：关闭旧连接，重连，重新握手
        ws.close();
        sim.npcName = result.name;
        sim.snapshot = createDefaultSnapshot();
        console.log(`  切换 NPC: ${result.name}，重新连接...`);
        try {
          ws = await connectWs(wsUrl);
          try { await doHello(ws); } catch { /* hello 失败不阻塞 */ }
          console.log(`  [已重连] NPC=${sim.npcName}`);
        } catch (err: unknown) {
          console.error(`  [重连失败] ${err instanceof Error ? err.message : err}`);
          break outer;
        }
        break;
      // "ok" → 继续循环
    }
  }

  ws.close();
  console.log("\n  再见!");
}

/** 一轮交互：读输入 → 处理命令或发 dialogue → 返回控制信号 */
async function chatRound(ws: WebSocket, sim: SimState): Promise<CommandResult> {
  // 显示快照摘要
  const s = sim.snapshot;
  const goalInfo = s.currentGoal ? ` goal=${s.currentGoal.type}` : "";
  console.log(`  \x1b[90m快照: ${sim.npcName} | 状态=${s.npcState} | 心情=${s.npcMood ?? "neutral"} | 好感=${s.friendship} | NPC钱=${s.npcMoney ?? 0} | 玩家钱=${s.playerMoney ?? 0}${goalInfo}\x1b[0m`);

  let input: string;
  try {
    input = await readLine();
  } catch {
    return { action: "exit" }; // EOF / Ctrl+C
  }
  if (!input) return { action: "ok" };

  // ── 命令处理 ──
  if (input.startsWith("/")) {
    return handleCommand(input, sim, []);
  }

  // ── 发送 dialogue ──
  const requestId = rid();
  console.log(`  \x1b[90m${sim.npcName} 思考中...\x1b[0m`);

  const dialogueReq = {
    type: "dialogue",
    requestId,
    npcName: sim.npcName,
    playerInput: input,
    worldSnapshot: { ...sim.snapshot },
  };

  try {
    const resp = await doDialogue(ws, dialogueReq);

    // 清除 "思考中" 提示
    process.stdout.write("\r\x1b[K");

    // ── 显示响应 ──
    const speech = (resp.speech as string) ?? "...";
    const emotion = (resp.emotion as string) ?? "";
    const fallback = resp.fallback === true;
    const friendshipDelta = resp.friendshipDelta as number | undefined;
    const actions = (resp.actions as ToolAction[] | undefined) ?? [];

    const fallbackTag = fallback ? " \x1b[33m[降级]\x1b[0m" : "";
    console.log(`  \x1b[32m[${sim.npcName}]\x1b[0m${fallbackTag} ${speech}`);
    if (emotion) console.log(`  \x1b[90m情绪: ${emotion}\x1b[0m`);
    if (friendshipDelta && friendshipDelta !== 0) {
      const sign = friendshipDelta > 0 ? "+" : "";
      console.log(`  \x1b[35m好感度变化: ${sign}${friendshipDelta}\x1b[0m`);
      sim.snapshot.friendship = Math.max(0, Math.min(2500, sim.snapshot.friendship + friendshipDelta));
    }

    // ── 处理工具调用 actions ──
    if (actions.length > 0) {
      console.log();
      console.log(`  \x1b[33m[工具调用] ${actions.length} 个动作:\x1b[0m`);
      for (let i = 0; i < actions.length; i++) {
        const action = actions[i]!;
        const callId = action.callId || rid();
        const summary = summarizeArgs(action.args);
        console.log(`    ${i + 1}. \x1b[1m${action.tool}\x1b[0m(${summary})`);

        // 询问是否模拟执行成功（默认 y）
        const success = await askYesNo(`    模拟执行 ${action.tool} 成功?`);
        let reason: string | undefined;

        if (!success) {
          reason = await askInput("    失败原因/失败码 (回车=internalError)", "internalError");
        }

        // 发送 action_result 回执
        // 以 messages.json schema 为准：type, requestId, npcName, action(=tool名), callId, tool, success, result
        // types.ts 额外有 reason 字段（C# ActionResultReason camelCase），一并发送
        sendOnly(ws, {
          type: "action_result",
          requestId: rid(),
          npcName: sim.npcName,
          action: action.tool,        // messages.json required: 工具名
          callId,                     // messages.json required: LLM tool_call_id
          tool: action.tool,          // TS routeToolResult 优先读此字段
          success,
          result: success ? `${action.tool} executed successfully` : undefined,
          reason: success ? undefined : reason,
        });

        // 按语义更新本地模拟状态
        if (success) {
          applyToolToSim(sim, action.tool, action.args);
          console.log(`    \x1b[32m[ok] 回执已发送 (success=true)\x1b[0m`);
        } else {
          console.log(`    \x1b[31m[fail] 回执已发送 (success=false, reason=${reason})\x1b[0m`);
        }
      }
      console.log();
    }

    // 显示 memorySideEffect
    if (resp.memorySideEffect === "recorded") {
      console.log(`  \x1b[90m[记忆已记录]\x1b[0m`);
    }

  } catch (err: unknown) {
    process.stdout.write("\r\x1b[K");
    console.error(`  [错误] ${err instanceof Error ? err.message : err}`);
  }

  return { action: "ok" };
}

// ── 启动 ──────────────────────────────────────────────────────────────────────

main().catch((err) => {
  console.error("[chat-cli] fatal:", err);
  process.exit(1);
});
