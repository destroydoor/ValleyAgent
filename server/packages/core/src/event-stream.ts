type Subscriber<T> = (event: T) => void;

export class EventStream<T> {
  private subscribers: Set<Subscriber<T>> = new Set();
  private collectedEvents: T[] = [];
  private _isDone = false;
  private doneResolve: ((events: T[]) => void) | null = null;
  private donePromise: Promise<T[]> | null = null;

  subscribe(fn: Subscriber<T>): () => void {
    if (this._isDone) {
      throw new Error("EventStream is done");
    }
    this.subscribers.add(fn);
    return () => {
      this.subscribers.delete(fn);
    };
  }

  emit(event: T): void {
    if (this._isDone) {
      throw new Error("EventStream is done");
    }
    this.collectedEvents.push(event);
    for (const sub of this.subscribers) {
      try {
        sub(event);
      } catch (err) {
        // 订阅者隔离（issue #23，审计 §3.8）：单播抛异常只记错不传播——
        // 此处抛出会打进生产者执行体（agentLoop 的 emit 调用点），若发生在
        // 收尾 emit（agent_end/done 前的 error 事件）处，流就永远不再 done()。
        console.error(
          `[event-stream] subscriber ${sub.name || "(anonymous)"} threw during emit — isolated:`,
          err,
        );
      }
    }
  }

  done(): void {
    if (this._isDone) return;
    this._isDone = true;
    this.subscribers.clear();
    if (this.doneResolve) {
      this.doneResolve([...this.collectedEvents]);
    }
  }

  isDone(): boolean {
    return this._isDone;
  }

  awaitAll(): Promise<T[]> {
    if (this._isDone) {
      return Promise.resolve([...this.collectedEvents]);
    }
    if (!this.donePromise) {
      this.donePromise = new Promise<T[]>((resolve) => {
        this.doneResolve = resolve;
      });
    }
    return this.donePromise;
  }
}
