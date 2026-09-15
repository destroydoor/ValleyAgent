import { test, expect } from "bun:test";
import { startServer } from "../src/server";
import type { ServerHandle } from "../src/server";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { resolve } from "path";

const DATA_PATH = resolve(import.meta.dir, "../data/npc_prompts.json");

async function withServer<T>(fn: (port: number) => Promise<T>): Promise<T> {
  const dir = mkdtempSync(join(tmpdir(), "valley-e2e-"));
  const handle: ServerHandle = await startServer({
    port: 0,
    hostname: "127.0.0.1",
    dataPath: DATA_PATH,
    agentsDir: dir,
    llmConfig: {
      provider: "minimax",
      apiKey: "fake",
      model: "fake",
      baseUrl: "http://localhost:9999",
    },
    llmCallOverride: async () => ({
      content: "",
      toolCalls: [{ id: "tc-1", name: "speak", args: { text: "你好啊，新来的农夫。" } }],
    }),
  });
  try {
    return await fn(handle.port);
  } finally {
    await handle.stop();
    rmSync(dir, { recursive: true, force: true });
  }
}

test("server starts and accepts WebSocket connections", async () => {
  await withServer(async (port) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    await new Promise<void>((resolve, reject) => {
      ws.onopen = () => resolve();
      ws.onerror = () => reject(new Error("WebSocket connection failed"));
    });
    ws.close();
  });
});

test("server responds to hello message", async () => {
  await withServer(async (port) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    await new Promise<void>((resolve) => { ws.onopen = () => resolve(); });

    const response = await new Promise<unknown>((resolve) => {
      ws.onmessage = (ev) => resolve(JSON.parse(ev.data as string));
      ws.send(JSON.stringify({ type: "hello", requestId: "h-1", modVersion: "1.0.0" }));
    });

    expect((response as { type: string }).type).toBe("hello");
    expect((response as { status: string }).status).toBe("ok");
    ws.close();
  });
});

test("server responds to ping with pong", async () => {
  await withServer(async (port) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    await new Promise<void>((resolve) => { ws.onopen = () => resolve(); });

    const response = await new Promise<unknown>((resolve) => {
      ws.onmessage = (ev) => resolve(JSON.parse(ev.data as string));
      ws.send(JSON.stringify({ type: "ping", requestId: "p-1" }));
    });

    expect((response as { type: string }).type).toBe("pong");
    ws.close();
  });
});

test("server handles full dialogue flow: hello → dialogue → response", async () => {
  await withServer(async (port) => {
    const ws = new WebSocket(`ws://127.0.0.1:${port}`);
    await new Promise<void>((resolve) => { ws.onopen = () => resolve(); });

    await new Promise<void>((resolve) => {
      ws.onmessage = () => resolve();
      ws.send(JSON.stringify({ type: "hello", requestId: "h-1", modVersion: "1.0.0" }));
    });

    const dialogueResp = await new Promise<unknown>((resolve) => {
      ws.onmessage = (ev) => resolve(JSON.parse(ev.data as string));
      ws.send(JSON.stringify({
        type: "dialogue",
        requestId: "d-1",
        npcName: "Abigail",
        playerInput: "你好",
        worldSnapshot: {
          season: "summer", day: 28, time: "14:30", weather: "sunny",
          location: "Town", npcTile: { x: 32, y: 18 },
          nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
          inventory: [], farmerName: "新来的农夫",
        },
      }));
    });

    const resp = dialogueResp as { type: string; speech: string; npcName: string };
    expect(resp.type).toBe("dialogue_response");
    expect(resp.npcName).toBe("Abigail");
    expect(resp.speech).toBe("你好啊，新来的农夫。");
    ws.close();
  });
});

test("server persists memory after dialogue", async () => {
  const dir = mkdtempSync(join(tmpdir(), "valley-e2e-mem-"));
  try {
    const handle = await startServer({
      port: 0,
      hostname: "127.0.0.1",
      dataPath: DATA_PATH,
      agentsDir: dir,
      llmConfig: {
        provider: "minimax", apiKey: "fake", model: "fake",
        baseUrl: "http://localhost:9999",
      },
      llmCallOverride: async () => ({
        content: "",
        toolCalls: [{ id: "tc-1", name: "speak", args: { text: "你好。" } }],
      }),
    });

    const ws = new WebSocket(`ws://127.0.0.1:${handle.port}`);
    await new Promise<void>((resolve) => { ws.onopen = () => resolve(); });

    await new Promise<void>((resolve) => {
      ws.onmessage = () => resolve();
      ws.send(JSON.stringify({
        type: "dialogue", requestId: "d-1", npcName: "Abigail",
        playerInput: "你好",
        worldSnapshot: {
          season: "summer", day: 28, time: "14:30", weather: "sunny",
          location: "Town", npcTile: { x: 32, y: 18 },
          nearbyObjects: "2 villagers", friendship: 250, npcState: "IDLE",
          inventory: [], farmerName: "新来的农夫",
        },
      }));
    });

    await new Promise((r) => setTimeout(r, 200));

    ws.close();
    await handle.stop();

    const { existsSync, readFileSync } = await import("fs");
    const memPath = join(dir, "Abigail_memory.json");
    expect(existsSync(memPath)).toBe(true);
    const raw = readFileSync(memPath, "utf-8");
    const parsed = JSON.parse(raw);
    expect(parsed.npcName).toBe("Abigail");
    expect(parsed.conversationHistory.length).toBeGreaterThanOrEqual(2);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});