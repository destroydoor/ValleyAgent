import { test, expect } from "bun:test";
import { PerformanceMonitor } from "../src/performance-monitor";

test("recordOperation stores timing", () => {
  const monitor = new PerformanceMonitor();
  monitor.recordOperation("llm_call", 150);
  monitor.recordOperation("llm_call", 250);

  const stats = monitor.getStats("llm_call");
  expect(stats.count).toBe(2);
  expect(stats.totalMs).toBe(400);
  expect(stats.avgMs).toBe(200);
  expect(stats.maxMs).toBe(250);
  expect(stats.minMs).toBe(150);
});

test("getStats returns zeros for unknown operation", () => {
  const monitor = new PerformanceMonitor();
  const stats = monitor.getStats("unknown");
  expect(stats.count).toBe(0);
  expect(stats.avgMs).toBe(0);
});

test("getSlowOperations returns operations above threshold", () => {
  const monitor = new PerformanceMonitor();
  monitor.recordOperation("llm_call", 50);
  monitor.recordOperation("llm_call", 500);
  monitor.recordOperation("tool_exec", 300);

  const slow = monitor.getSlowOperations(200);
  expect(slow).toHaveLength(2);
  const names = slow.map((s) => s.name);
  expect(names).toContain("llm_call");
  expect(names).toContain("tool_exec");
});

test("getSlowOperations on specific operation", () => {
  const monitor = new PerformanceMonitor();
  monitor.recordOperation("llm_call", 50);
  monitor.recordOperation("llm_call", 500);

  const slow = monitor.getSlowOperations(200, "llm_call");
  expect(slow).toHaveLength(1);
  expect(slow[0]!.name).toBe("llm_call");
  expect(slow[0]!.maxMs).toBe(500);
});

test("reset clears all stats", () => {
  const monitor = new PerformanceMonitor();
  monitor.recordOperation("llm_call", 100);
  monitor.reset();
  expect(monitor.getStats("llm_call").count).toBe(0);
});

test("measure wraps async function and records timing", async () => {
  const monitor = new PerformanceMonitor();
  const result = await monitor.measure("test_op", async () => {
    await new Promise((r) => setTimeout(r, 10));
    return 42;
  });
  expect(result).toBe(42);
  const stats = monitor.getStats("test_op");
  expect(stats.count).toBe(1);
  expect(stats.avgMs).toBeGreaterThanOrEqual(8);
});
