import { test, expect } from "bun:test";
import { OutputValidator } from "../src/output-validator";

test("accepts Chinese-dominant text", () => {
  const v = new OutputValidator();
  const result = v.validate("你好，我是 Abigail。今天天气真好。");
  expect(result.valid).toBe(true);
});

test("rejects text with no CJK characters", () => {
  const v = new OutputValidator();
  const result = v.validate("Hello, I am Abigail. The weather is nice today.");
  expect(result.valid).toBe(false);
  expect(result.reason).toMatch(/CJK/i);
});

test("rejects empty text", () => {
  const v = new OutputValidator();
  const result = v.validate("");
  expect(result.valid).toBe(false);
  expect(result.reason).toMatch(/empty/i);
});

test("rejects whitespace-only text", () => {
  const v = new OutputValidator();
  const result = v.validate("   \n\t  ");
  expect(result.valid).toBe(false);
});

test("accepts mixed Chinese + English with CJK majority", () => {
  const v = new OutputValidator();
  const result = v.validate("我喜欢 Amethyst，它是最美的水晶。");
  expect(result.valid).toBe(true);
});

test("rejects English-dominant text (CJK ratio < 0.3)", () => {
  const v = new OutputValidator();
  const result = v.validate("Hello Abigail, this is a test of the dialogue system.");
  expect(result.valid).toBe(false);
});

test("buildRetryPrompt returns prompt asking for Chinese", () => {
  const v = new OutputValidator();
  const retry = v.buildRetryPrompt("Hello world");
  expect(retry).toContain("中文");
  expect(retry).toContain("Chinese");
});

test("validateSpeechAndActions accepts speak tool call with valid Chinese text", () => {
  const v = new OutputValidator();
  const result = v.validateSpeechAndActions(
    "你好",
    [{ tool: "speak", args: { text: "你好" } }]
  );
  expect(result.valid).toBe(true);
});

test("validateSpeechAndActions uses speech when no speak tool call", () => {
  const v = new OutputValidator();
  const result = v.validateSpeechAndActions(
    "你好",
    [{ tool: "emote", args: { emote_id: "happy" } }]
  );
  expect(result.valid).toBe(true);
});

test("validateSpeechAndActions rejects when both speech and tool text are invalid", () => {
  const v = new OutputValidator();
  const result = v.validateSpeechAndActions(
    "Hello",
    [{ tool: "speak", args: { text: "Hello" } }]
  );
  expect(result.valid).toBe(false);
});
