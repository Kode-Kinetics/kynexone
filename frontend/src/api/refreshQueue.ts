type RefreshWaiter = {
  resolve: (token: string) => void;
  reject: (error: unknown) => void;
};

/**
 * Coordinates requests that receive 401 while one refresh is already in flight.
 * Both settlement paths are explicit: a failed refresh must reject every waiter,
 * otherwise callers remain pending forever and screens keep spinning after logout.
 */
export class RefreshQueue {
  private waiters: RefreshWaiter[] = [];

  wait(): Promise<string> {
    return new Promise<string>((resolve, reject) => {
      this.waiters.push({ resolve, reject });
    });
  }

  resolve(token: string): void {
    const waiters = this.takeAll();
    waiters.forEach((waiter) => waiter.resolve(token));
  }

  reject(error: unknown): void {
    const waiters = this.takeAll();
    waiters.forEach((waiter) => waiter.reject(error));
  }

  get size(): number {
    return this.waiters.length;
  }

  private takeAll(): RefreshWaiter[] {
    const waiters = this.waiters;
    this.waiters = [];
    return waiters;
  }
}
