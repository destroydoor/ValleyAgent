import { test } from "bun:test";
import type {
  AgentMessage,
  LlmMessage,
  AgentEvent,
} from "../src/types";

// 本文件是纯编译期类型契约检查（审计 D5）。
// 旧版每个用例都在"构造字面量 → 断言字面量属性"——对象是我自己写的，断言必然成立，
// 属于自证测试（删掉类型字段也不会挂，因为断言的是字面量本身）。
// 保留真实价值：字面量构造必须通过类型检查——类型里删/改字段会在这里编译失败。
// 运行时仅保留一个结构完整性兜底（事件类型无重复），不做任何自证断言。

test("AgentMessage 三种 role 均可构造（编译期契约）", () => {
  const user: AgentMessage = { role: "user", content: "hello" };
  const assistant: AgentMessage = { role: "assistant", content: "hi" };
  const tool: AgentMessage = { role: "tool", content: "result", toolCallId: "c1", toolName: "speak" };
  const all: AgentMessage[] = [user, assistant, tool];
  if (all.length === 0) throw new Error("unreachable");
});

test("LlmMessage 可构造（编译期契约）", () => {
  const system: LlmMessage = { role: "system", content: "you are an NPC" };
  const user: LlmMessage = { role: "user", content: "hello" };
  const assistant: LlmMessage = { role: "assistant", content: "hi" };
  const tool: LlmMessage = { role: "tool", content: "result", toolCallId: "c1", toolName: "speak" };
  const all: LlmMessage[] = [system, user, assistant, tool];
  if (all.length === 0) throw new Error("unreachable");
});

test("AgentEvent 全部事件类型可构造且互不重复（编译期契约）", () => {
  const events: AgentEvent[] = [
    { type: "agent_start", timestamp: 0 },
    { type: "agent_end", timestamp: 0 },
    { type: "turn_start", timestamp: 0, turnIndex: 0 },
    { type: "turn_end", timestamp: 0, turnIndex: 0 },
    { type: "message_start", timestamp: 0 },
    { type: "message_update", timestamp: 0, delta: "d" },
    { type: "message_end", timestamp: 0, content: "c" },
    { type: "tool_call_start", timestamp: 0, toolName: "speak", toolCallId: "c1", args: {} },
    { type: "tool_call_end", timestamp: 0, toolName: "speak", toolCallId: "c1", result: {}, isError: false },
    { type: "error", timestamp: 0, message: "m" },
  ];
  // 唯一的结构性断言：所有事件类型齐全且无重复（判别联合的完整性靠编译期保证）
  const typeCount = new Set(events.map((e) => e.type)).size;
  if (typeCount !== events.length) throw new Error("duplicate event types");
});
