export enum CircuitState {
  CLOSED = "CLOSED",
  OPEN = "OPEN",
  HALF_OPEN = "HALF_OPEN",
}

export interface CircuitBreakerConfig {
  // Primary names (spec-aligned)
  failureThreshold?: number;
  openDurationMs?: number;
  // Legacy aliases (backwards compat)
  threshold?: number;
  recoveryTime?: number;
  halfOpenMaxCalls?: number;
  // Callbacks
  onSuccess?: () => void;
  onOpen?: () => void;
  onClose?: () => void;
}

export class CircuitBreaker {
  private state: CircuitState = CircuitState.CLOSED;
  private failureCount = 0;
  private lastFailureTime = 0;
  private halfOpenCalls = 0;
  private readonly failureThreshold: number;
  private readonly openDurationMs: number;
  private readonly halfOpenMaxCalls: number;
  private readonly onSuccess: (() => void) | undefined;
  private readonly onOpen: (() => void) | undefined;
  private readonly onClose: (() => void) | undefined;

  constructor(config: CircuitBreakerConfig) {
    this.failureThreshold = config.failureThreshold ?? config.threshold ?? 5;
    this.openDurationMs = config.openDurationMs ?? config.recoveryTime ?? 30_000;
    this.halfOpenMaxCalls = config.halfOpenMaxCalls ?? 1;
    this.onSuccess = config.onSuccess;
    this.onOpen = config.onOpen;
    this.onClose = config.onClose;
  }

  getState(): CircuitState {
    this.checkRecovery();
    return this.state;
  }

  canExecute(): boolean {
    this.checkRecovery();
    if (this.state === CircuitState.CLOSED) return true;
    if (this.state === CircuitState.HALF_OPEN) {
      if (this.halfOpenCalls < this.halfOpenMaxCalls) {
        this.halfOpenCalls++;
        return true;
      }
      return false;
    }
    return false;
  }

  recordSuccess(): void {
    const wasHalfOpen = this.state === CircuitState.HALF_OPEN;
    if (wasHalfOpen) {
      this.state = CircuitState.CLOSED;
      this.failureCount = 0;
      this.halfOpenCalls = 0;
      this.onSuccess?.();
      this.onClose?.();
      return;
    }
    if (this.state === CircuitState.CLOSED) {
      this.failureCount = 0;
    }
  }

  recordFailure(): void {
    this.failureCount++;
    this.lastFailureTime = Date.now();

    if (this.state === CircuitState.HALF_OPEN) {
      this.state = CircuitState.OPEN;
      this.halfOpenCalls = 0;
      this.onOpen?.();
      return;
    }

    if (this.failureCount >= this.failureThreshold && this.state === CircuitState.CLOSED) {
      this.state = CircuitState.OPEN;
      this.onOpen?.();
    }
  }

  isOpen(): boolean {
    return this.getState() === CircuitState.OPEN;
  }

  reset(): void {
    this.state = CircuitState.CLOSED;
    this.failureCount = 0;
    this.halfOpenCalls = 0;
  }

  forceOpen(): void {
    this.state = CircuitState.OPEN;
    this.lastFailureTime = Date.now();
  }

  private checkRecovery(): void {
    if (this.state === CircuitState.OPEN) {
      const elapsed = Date.now() - this.lastFailureTime;
      if (elapsed >= this.openDurationMs) {
        this.state = CircuitState.HALF_OPEN;
        this.halfOpenCalls = 0;
      }
    }
  }
}
