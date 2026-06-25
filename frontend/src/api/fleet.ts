import client from './client';

export interface FleetSummary {
  activeShipments: number;
  enRoute: number;
  deliveredToday: number;
  activeVehicles: number;
  onTripVehicles: number;
  openMaintenance: number;
  fuelAlerts: number;
  trackingEvents: number;
  avgFuelLevel: number;
  deliveredRate: number;
}

export interface FleetOverview {
  generatedAtUtc: string;
  summary: FleetSummary;
  shipmentCards: FleetShipment[];
  vehicleCards: FleetVehicle[];
  trackingCards: FleetTrackingPoint[];
  maintenanceCards: FleetMaintenanceTicket[];
  fuelCards: FleetFuelEvent[];
  loadPlanCards: Array<{
    routeCode: string;
    shipmentCount: number;
    totalWeightKg: number;
    totalVolumeCbm: number;
    highPriority: number;
    delivered: number;
  }>;
}

export interface FleetShipment {
  id: string;
  shipmentNumber: string;
  customerName: string;
  customerSegment: string;
  origin: string;
  destination: string;
  city: string;
  status: string;
  priority: string;
  mode: string;
  pieceCount: number;
  weightKg: number;
  volumeCbm: number;
  declaredValue: number;
  carrierName: string;
  driverName: string;
  vehicleNumber: string;
  routeCode: string;
  podStatus: string;
  temperatureRange: string;
  notes: string;
  createdAtUtc: string;
  pickupScheduledAtUtc?: string;
  pickedUpAtUtc?: string;
  deliveredAtUtc?: string;
}

export interface FleetVehicle {
  id: string;
  vehicleNumber: string;
  plateNumber: string;
  type: string;
  status: string;
  driverName: string;
  capacityKg: number;
  capacityCbm: number;
  currentLoadKg: number;
  fuelLevelPercent: number;
  odometerKm: number;
  healthStatus: string;
  isRefrigerated: boolean;
  temperatureCelsius?: number;
  lastKnownLocation: string;
  lastPingAtUtc?: string;
  lastServiceAtUtc?: string;
  nextServiceAtUtc?: string;
  notes: string;
}

export interface FleetTrackingPoint {
  id: string;
  shipmentNumber: string;
  vehicleNumber: string;
  locationLabel: string;
  status: string;
  geofenceName: string;
  alertType: string;
  latitude: number;
  longitude: number;
  speedKph: number;
  recordedAtUtc: string;
  estimatedArrivalUtc?: string;
  notes: string;
}

export interface FleetMaintenanceTicket {
  id: string;
  workOrderNumber: string;
  vehicleNumber: string;
  type: string;
  status: string;
  priority: string;
  vendorName: string;
  description: string;
  estimatedCost: number;
  actualCost: number;
  downtimeHours: number;
  openedAtUtc: string;
  dueAtUtc?: string;
  closedAtUtc?: string;
  notes: string;
}

export interface FleetFuelEvent {
  id: string;
  vehicleNumber: string;
  fuelCardNumber: string;
  stationName: string;
  city: string;
  eventType: string;
  anomalyFlag: boolean;
  liters: number;
  cost: number;
  odometerKm: number;
  notes: string;
  recordedAtUtc: string;
}

export const fleetApi = {
  overview: () => client.get<FleetOverview>('/api/fleet/overview').then((r) => r.data),
  shipments: (params: { status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; page: number; pageSize: number; items: FleetShipment[] }>('/api/fleet/shipments', { params }).then((r) => r.data),
  vehicles: (params: { status?: string } = {}) =>
    client.get<{ items: FleetVehicle[] }>('/api/fleet/vehicles', { params }).then((r) => r.data),
  tracking: (params: { shipmentNumber?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; page: number; pageSize: number; items: FleetTrackingPoint[] }>('/api/fleet/tracking', { params }).then((r) => r.data),
  maintenance: (params: { status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; page: number; pageSize: number; items: FleetMaintenanceTicket[] }>('/api/fleet/maintenance', { params }).then((r) => r.data),
  fuel: (params: { anomaliesOnly?: boolean; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; page: number; pageSize: number; items: FleetFuelEvent[] }>('/api/fleet/fuel', { params }).then((r) => r.data),
  dispatchShipment: (id: string, body: { vehicleNumber?: string; driverName?: string; routeCode?: string; notes?: string }) =>
    client.post<FleetShipment>(`/api/fleet/shipments/${id}/dispatch`, body).then((r) => r.data),
  serviceVehicle: (id: string, body: { status?: string; healthStatus?: string; nextServiceAtUtc?: string; notes?: string }) =>
    client.post<FleetVehicle>(`/api/fleet/vehicles/${id}/service`, body).then((r) => r.data),
  closeMaintenance: (id: string, body: { status?: string; actualCost?: number; notes?: string }) =>
    client.post<FleetMaintenanceTicket>(`/api/fleet/maintenance/${id}/close`, body).then((r) => r.data),
  flagFuelEvent: (id: string, body: { anomalyFlag: boolean; notes?: string }) =>
    client.post<FleetFuelEvent>(`/api/fleet/fuel/${id}/flag`, body).then((r) => r.data),
};
