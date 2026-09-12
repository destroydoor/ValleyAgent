import { test, expect } from "bun:test";
import { CircuitBreaker, CircuitState } from "../src/circuit-breaker";

test("starts in CLOSED state", () => {
  const cb = new CircuitBreaker({ threshold: 3, recoveryTime: 1000 });
  expect(cb.getState()).toBe(CircuitState.CLOSED);
  expect(cb.canExecute()).toBe(true);
});

test("opens after threshold failures", () => {
  const cb = new CircuitBreaker({ threshold: 3, recoveryTime: 1000 });
  cb.recordFailure();
  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.CLOSED);
  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.OPEN);
  expect(cb.canExecute()).toBe(false);
});

test("CLOSED state allows execution", () => {
  const cb = new CircuitBreaker({ threshold: 5, recoveryTime: 1000 });
  expect(cb.canExecute()).toBe(true);
});

test("OPEN state blocks execution", () => {
  const cb = new CircuitBreaker({ threshold: 2, recoveryTime: 1000 });
  cb.recordFailure();
  cb.recordFailure();
  expect(cb.canExecute()).toBe(false);
});

test("transitions to HALF_OPEN after recoveryTime", async () => {
  const cb = new CircuitBreaker({ threshold: 2, recoveryTime: 50, halfOpenMaxCalls: 1 });
  cb.recordFailure();
  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.OPEN);

  await new Promise((resolve) => setTimeout(resolve, 60));

  expect(cb.canExecute()).toBe(true);
  expect(cb.getState()).toBe(CircuitState.HALF_OPEN);
});

test("HALF_OPEN success closes the circuit", async () => {
  const cb = new CircuitBreaker({ threshold: 2, recoveryTime: 50, halfOpenMaxCalls: 1 });
  cb.recordFailure();
  cb.recordFailure();

  await new Promise((resolve) => setTimeout(resolve, 60));
  cb.canExecute();
  expect(cb.getState()).toBe(CircuitState.HALF_OPEN);

  cb.recordSuccess();
  expect(cb.getState()).toBe(CircuitState.CLOSED);
});

test("HALF_OPEN failure reopens the circuit", async () => {
  const cb = new CircuitBreaker({ threshold: 2, recoveryTime: 50, halfOpenMaxCalls: 1 });
  cb.recordFailure();
  cb.recordFailure();

  await new Promise((resolve) => setTimeout(resolve, 60));
  cb.canExecute();
  expect(cb.getState()).toBe(CircuitState.HALF_OPEN);

  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.OPEN);
});

test("recordSuccess resets failure count in CLOSED", () => {
  const cb = new CircuitBreaker({ threshold: 3, recoveryTime: 1000 });
  cb.recordFailure();
  cb.recordFailure();
  cb.recordSuccess();
  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.CLOSED);
});

test("reset returns to CLOSED", () => {
  const cb = new CircuitBreaker({ threshold: 2, recoveryTime: 1000 });
  cb.recordFailure();
  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.OPEN);

  cb.reset();
  expect(cb.getState()).toBe(CircuitState.CLOSED);
});

test("forceOpen sets state to OPEN", () => {
  const cb = new CircuitBreaker({ threshold: 5, recoveryTime: 1000 });
  cb.forceOpen();
  expect(cb.getState()).toBe(CircuitState.OPEN);
  expect(cb.canExecute()).toBe(false);
});

test("isOpen returns true only in OPEN state", () => {
  const cb = new CircuitBreaker({ threshold: 2, recoveryTime: 1000 });
  expect(cb.isOpen()).toBe(false);
  cb.recordFailure();
  cb.recordFailure();
  expect(cb.isOpen()).toBe(true);
});
