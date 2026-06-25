import client from './client';

export interface LogisticsSummary {
  activeOrders: number;
  inTransit: number;
  deliveredToday: number;
  exceptionOrders: number;
  activeRoutes: number;
  onTimeRate: number;
}

export interface LogisticsOverview {
  generatedAtUtc: string;
  summary: LogisticsSummary;
  alerts: Array<{
    orderNumber: string;
    customerName: string;
    routeCode: string;
    status: string;
    exceptionReason: string;
    attemptCount: number;
    riderName: string;
    etaUtc: string;
  }>;
  routeCards: LogisticsRoute[];
  orderCards: LogisticsOrder[];
  liveStops: LogisticsStop[];
}

export interface LogisticsOrder {
  id: string;
  orderNumber: string;
  customerName: string;
  customerSegment: string;
  salesChannel: string;
  city: string;
  area: string;
  status: string;
  priority: string;
  itemCount: number;
  orderValue: number;
  routeCode: string;
  driverName: string;
  vehicleNumber: string;
  dispatchNotes: string;
  createdAtUtc: string;
  promisedAtUtc?: string;
  dispatchedAtUtc?: string;
  deliveredAtUtc?: string;
}

export interface LogisticsRoute {
  id: string;
  routeCode: string;
  hub: string;
  territory: string;
  driverName: string;
  vehicleNumber: string;
  status: string;
  plannedStops: number;
  completedStops: number;
  distanceKm: number;
  completionPercent: number;
  currentStop: string;
  nextStop: string;
  plannedForDate: string;
  departureTimeUtc: string;
  etaCompleteUtc?: string;
  notes: string;
}

export interface LogisticsStop {
  id: string;
  orderNumber: string;
  routeCode: string;
  customerName: string;
  addressLine: string;
  city: string;
  status: string;
  proofStatus: string;
  recipientName: string;
  attemptCount: number;
  riderName: string;
  timeWindow: string;
  etaUtc: string;
  deliveredAtUtc?: string;
  exceptionReason: string;
}

export const logisticsApi = {
  overview: () =>
    client.get<LogisticsOverview>('/api/logistics/overview').then((r) => r.data),

  orders: (params: { status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; page: number; pageSize: number; items: LogisticsOrder[] }>('/api/logistics/orders', { params }).then((r) => r.data),

  order: (id: string) =>
    client.get<LogisticsOrder>(`/api/logistics/orders/${id}`).then((r) => r.data),

  createOrder: (body: Partial<LogisticsOrder> & { orderNumber: string; customerName: string }) =>
    client.post<LogisticsOrder>('/api/logistics/orders', body).then((r) => r.data),

  updateOrder: (id: string, body: Partial<LogisticsOrder>) =>
    client.put<LogisticsOrder>(`/api/logistics/orders/${id}`, body).then((r) => r.data),

  routes: (params: { status?: string } = {}) =>
    client.get<{ items: LogisticsRoute[] }>('/api/logistics/routes', { params }).then((r) => r.data),

  createRoute: (body: Partial<LogisticsRoute> & { routeCode: string }) =>
    client.post<LogisticsRoute>('/api/logistics/routes', body).then((r) => r.data),

  updateRoute: (id: string, body: Partial<LogisticsRoute>) =>
    client.put<LogisticsRoute>(`/api/logistics/routes/${id}`, body).then((r) => r.data),

  routeStops: (id: string) =>
    client.get<{ items: LogisticsStop[] }>(`/api/logistics/routes/${id}/stops`).then((r) => r.data),

  lastMile: (params: { status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; page: number; pageSize: number; items: LogisticsStop[] }>('/api/logistics/last-mile', { params }).then((r) => r.data),

  dispatchOrder: (id: string, body: { routeCode?: string; driverName?: string; vehicleNumber?: string; notes?: string }) =>
    client.post<LogisticsOrder>(`/api/logistics/orders/${id}/dispatch`, body).then((r) => r.data),

  progressRoute: (id: string, body: { completedStopsDelta?: number; currentStop?: string; nextStop?: string; etaCompleteUtc?: string; notes?: string }) =>
    client.post<LogisticsRoute>(`/api/logistics/routes/${id}/progress`, body).then((r) => r.data),

  confirmDelivery: (id: string, body: { recipientName?: string; proofStatus?: string; exceptionReason?: string }) =>
    client.post<LogisticsStop>(`/api/logistics/stops/${id}/deliver`, body).then((r) => r.data),

  recordAttempt: (id: string, body: { status?: string; proofStatus?: string; exceptionReason?: string; nextEtaUtc?: string; nextStop?: string }) =>
    client.post<LogisticsStop>(`/api/logistics/stops/${id}/attempt`, body).then((r) => r.data),

  rescheduleStop: (id: string, body: { nextEtaUtc?: string; timeWindow?: string; reason?: string }) =>
    client.post<LogisticsStop>(`/api/logistics/stops/${id}/reschedule`, body).then((r) => r.data),
};
