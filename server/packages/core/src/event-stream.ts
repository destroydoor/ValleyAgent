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
      sub(event);
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
