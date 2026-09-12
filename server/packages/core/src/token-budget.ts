export interface TokenBudgetConfig {
  budget: number; // 0 = unlimited
  windowMs: number;
}

export class TokenBudgetManager {
  private used = 0;
  private windowStart = 0;
  private readonly config: TokenBudgetConfig;

  constructor(config: TokenBudgetConfig) {
    this.config = config;
    this.windowStart = Date.now();
  }

  consume(amount: number): boolean {
    if (this.config.budget === 0) return true;

    this.checkWindowReset();

    if (this.used + amount > this.config.budget) {
      return false;
    }
    this.used += amount;
    return true;
  }

  getRemaining(): number {
    if (this.config.budget === 0) return Infinity;
    this.checkWindowReset();
    return Math.max(0, this.config.budget - this.used);
  }

  getUsed(): number {
    this.checkWindowReset();
    return this.used;
  }

  reset(): void {
    this.used = 0;
    this.windowStart = Date.now();
  }

  private checkWindowReset(): void {
    const elapsed = Date.now() - this.windowStart;
    if (elapsed >= this.config.windowMs) {
      this.used = 0;
      this.windowStart = Date.now();
    }
  }
}
