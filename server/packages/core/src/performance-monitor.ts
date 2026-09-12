interface OperationStats {
  count: number;
  totalMs: number;
  minMs: number;
  maxMs: number;
}

interface SlowOperation {
  name: string;
  count: number;
  totalMs: number;
  minMs: number;
  maxMs: number;
  avgMs: number;
}

export class PerformanceMonitor {
  private stats: Map<string, OperationStats> = new Map();

  recordOperation(name: string, durationMs: number): void {
    const existing = this.stats.get(name);
    if (existing) {
      existing.count++;
      existing.totalMs += durationMs;
      existing.minMs = Math.min(existing.minMs, durationMs);
      existing.maxMs = Math.max(existing.maxMs, durationMs);
    } else {
      this.stats.set(name, {
        count: 1,
        totalMs: durationMs,
        minMs: durationMs,
        maxMs: durationMs,
      });
    }
  }

  getStats(name: string): OperationStats & { avgMs: number } {
    const s = this.stats.get(name);
    if (!s) {
      return { count: 0, totalMs: 0, minMs: 0, maxMs: 0, avgMs: 0 };
    }
    return { ...s, avgMs: s.totalMs / s.count };
  }

  getSlowOperations(thresholdMs: number, operationName?: string): SlowOperation[] {
    const result: SlowOperation[] = [];
    const entries = operationName
      ? [[operationName, this.stats.get(operationName)] as const].filter(([, v]) => v !== undefined)
      : [...this.stats.entries()];

    for (const [name, s] of entries) {
      if (s && s.maxMs >= thresholdMs) {
        result.push({
          name,
          count: s.count,
          totalMs: s.totalMs,
          minMs: s.minMs,
          maxMs: s.maxMs,
          avgMs: s.totalMs / s.count,
        });
      }
    }
    return result;
  }

  reset(): void {
    this.stats.clear();
  }

  async measure<T>(name: string, fn: () => Promise<T>): Promise<T> {
    const start = performance.now();
    try {
      return await fn();
    } finally {
      const duration = performance.now() - start;
      this.recordOperation(name, duration);
    }
  }
}
