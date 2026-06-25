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

  routes: (params: { status?: string } = {}) =>
    client.get<{ items: LogisticsRoute[] }>('/api/logistics/routes', { params }).then((r) => r.data),

  lastMile: (params: { status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; page: number; pageSize: number; items: LogisticsStop[] }>('/api/logistics/last-mile', { params }).then((r) => r.data),

  dispatchOrder: (id: string, body: { routeCode?: string; driverName?: string; vehicleNumber?: string; notes?: string }) =>
    client.post<LogisticsOrder>(`/api/logistics/orders/${id}/dispatch`, body).then((r) => r.data),

  progressRoute: (id: string, body: { completedStopsDelta?: number; currentStop?: string; nextStop?: string; etaCompleteUtc?: string; notes?: string }) =>
    client.post<LogisticsRoute>(`/api/logistics/routes/${id}/progress`, body).then((r) => r.data),

  confirmDelivery: (id: string, body: { recipientName?: string; proofStatus?: string; exceptionReason?: string }) =>
    client.post<LogisticsStop>(`/api/logistics/stops/${id}/deliver`, body).then((r) => r.data),
};
