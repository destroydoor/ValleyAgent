import { test, expect } from "bun:test";
import type { MemoryBackend } from "../src/memory-backend";
// Side-effect import ensures the module physically exists at runtime.
// `import type` alone is erased, which would hide a missing module from RED verification.
import "../src/memory-backend";

test("MemoryBackend interface is structurally compatible with a minimal impl", () => {
  const fakeMemory: MemoryBackend = {
    conversationHistory: [],
    shortTermMemories: [],
    significantMemories: [],
    friendship: 0,
    addConversation(role, text) { this.conversationHistory.push({ role, text }); },
    addMemory(text, importance = 1, entryType = "generic", location = "", tags = []) {
      this.shortTermMemories.push({ text, timestamp: 0, importance, entryType, location, tags, count: 1 });
    },
    addSignificantMemory(text, category = "life_event", weight = "joy", relatedNpcs = [], location = "") {
      if (this.significantMemories.some(m => m.text === text)) return false;
      this.significantMemories.push({ text, timestamp: 0, category, emotionalWeight: weight, relatedNpcs, location });
      return true;
    },
    getConversationContext(_count = 10) { return ""; },
    getRecentMemories(_count = 5) { return ""; },
    getSignificantMemoriesText() { return ""; },
    async load() { /* no-op */ },
    async save() { /* no-op */ },
  };

  fakeMemory.addConversation("player", "hello");
  expect(fakeMemory.conversationHistory.length).toBe(1);
  expect(fakeMemory.friendship).toBe(0);
});
