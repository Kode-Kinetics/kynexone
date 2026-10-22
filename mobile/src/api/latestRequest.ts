export interface LatestRequestGate {
  begin(): number;
  isLatest(id: number): boolean;
  invalidate(): void;
}

export function createLatestRequestGate(): LatestRequestGate {
  let latestRequestId = 0;
  return {
    begin() {
      latestRequestId += 1;
      return latestRequestId;
    },
    isLatest(id) {
      return id === latestRequestId;
    },
    invalidate() {
      latestRequestId += 1;
    },
  };
}
