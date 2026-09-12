import { test, expect } from "bun:test";
import { EventStream } from "../src/event-stream";
import type { AgentEvent } from "../src/types";

test("subscribe receives emitted events", async () => {
  const stream = new EventStream<AgentEvent>();
  const received: AgentEvent[] = [];
  stream.subscribe((event) => received.push(event));

  const event: AgentEvent = { type: "agent_start", timestamp: Date.now() };
  stream.emit(event);

  expect(received).toHaveLength(1);
  expect(received[0]!.type).toBe("agent_start");
});

test("multiple subscribers all receive events", () => {
  const stream = new EventStream<AgentEvent>();
  const received1: AgentEvent[] = [];
  const received2: AgentEvent[] = [];
  stream.subscribe((e) => received1.push(e));
  stream.subscribe((e) => received2.push(e));

  stream.emit({ type: "agent_end", timestamp: Date.now() });

  expect(received1).toHaveLength(1);
  expect(received2).toHaveLength(1);
});

test("unsubscribe stops receiving events", () => {
  const stream = new EventStream<AgentEvent>();
  const received: AgentEvent[] = [];
  const unsub = stream.subscribe((e) => received.push(e));

  stream.emit({ type: "turn_start", timestamp: Date.now(), turnIndex: 0 });
  unsub();
  stream.emit({ type: "turn_end", timestamp: Date.now(), turnIndex: 0 });

  expect(received).toHaveLength(1);
});

test("awaitAll resolves when done() is called", async () => {
  const stream = new EventStream<AgentEvent>();
  const promise = stream.awaitAll();

  stream.emit({ type: "agent_start", timestamp: Date.now() });
  stream.done();

  const events = await promise;
  expect(events).toHaveLength(1);
  expect(events[0]!.type).toBe("agent_start");
});

test("awaitAll collects all events emitted before done", async () => {
  const stream = new EventStream<AgentEvent>();
  const promise = stream.awaitAll();

  stream.emit({ type: "agent_start", timestamp: 1 });
  stream.emit({ type: "turn_start", timestamp: 2, turnIndex: 0 });
  stream.emit({ type: "agent_end", timestamp: 3 });
  stream.done();

  const events = await promise;
  expect(events).toHaveLength(3);
});

test("isDone returns true after done() is called", () => {
  const stream = new EventStream<AgentEvent>();
  expect(stream.isDone()).toBe(false);
  stream.done();
  expect(stream.isDone()).toBe(true);
});

test("emit after done throws", () => {
  const stream = new EventStream<AgentEvent>();
  stream.done();
  expect(() => stream.emit({ type: "agent_start", timestamp: 1 })).toThrow(
    "EventStream is done"
  );
});
