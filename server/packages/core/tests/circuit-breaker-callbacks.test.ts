import { test, expect } from "bun:test";
import { CircuitBreaker, CircuitState } from "../src/circuit-breaker";

test("accepts failureThreshold alias for threshold", () => {
  const cb = new CircuitBreaker({ failureThreshold: 3, openDurationMs: 1000 });
  cb.recordFailure();
  cb.recordFailure();
  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.OPEN);
});

test("accepts openDurationMs alias for recoveryTime", async () => {
  const cb = new CircuitBreaker({ failureThreshold: 2, openDurationMs: 50 });
  cb.recordFailure();
  cb.recordFailure();
  expect(cb.isOpen()).toBe(true);
  await new Promise(r => setTimeout(r, 60));
  expect(cb.canExecute()).toBe(true);
});

test("fires onSuccess callback when HALF_OPEN transitions to CLOSED", () => {
  let onSuccessCalled = 0;
  const cb = new CircuitBreaker({
    failureThreshold: 2,
    openDurationMs: 50,
    onSuccess: () => { onSuccessCalled++; },
  });
  cb.recordFailure();
  cb.recordFailure();
  expect(onSuccessCalled).toBe(0);

  // Force recovery + success
  cb.forceOpen();
  // Wait for HALF_OPEN transition via canExecute
  return new Promise<void>((resolve) => {
    setTimeout(() => {
      cb.canExecute(); // triggers HALF_OPEN
      cb.recordSuccess(); // triggers CLOSED + onSuccess
      expect(onSuccessCalled).toBe(1);
      resolve();
    }, 60);
  });
});

test("fires onOpen callback when threshold reached", () => {
  let onOpenCalled = 0;
  const cb = new CircuitBreaker({
    failureThreshold: 2,
    openDurationMs: 1000,
    onOpen: () => { onOpenCalled++; },
  });
  cb.recordFailure();
  expect(onOpenCalled).toBe(0);
  cb.recordFailure();
  expect(onOpenCalled).toBe(1);
});

test("fires onClose callback when circuit recovers", async () => {
  let onCloseCalled = 0;
  const cb = new CircuitBreaker({
    failureThreshold: 1,
    openDurationMs: 50,
    onClose: () => { onCloseCalled++; },
  });
  cb.recordFailure();
  expect(cb.isOpen()).toBe(true);
  await new Promise(r => setTimeout(r, 60));
  cb.canExecute(); // HALF_OPEN
  cb.recordSuccess(); // CLOSED
  expect(onCloseCalled).toBe(1);
});

test("backwards compatible with old threshold/recoveryTime fields", () => {
  const cb = new CircuitBreaker({ threshold: 3, recoveryTime: 1000 });
  cb.recordFailure();
  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.CLOSED);
  cb.recordFailure();
  expect(cb.getState()).toBe(CircuitState.OPEN);
});
