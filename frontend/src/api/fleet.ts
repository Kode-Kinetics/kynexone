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
  customerVATNumber: string;
  customerCommercialRegistrationNo: string;
  customerNationalAddressBuildingNo: string;
  customerNationalAddressAdditionalNo: string;
  customerNationalAddressDistrict: string;
  customerNationalAddressCity: string;
  customerNationalAddressRegion: string;
  customerNationalAddressPostalCode: string;
  customerNationalAddressCountry: string;
  driverName: string;
  vehicleNumber: string;
  routeCode: string;
  podStatus: string;
  temperatureRange: string;
  notes: string;
  isInvoiceReady?: boolean;
  invoiceReadyAtUtc?: string | null;
  invoiceReadinessNotes?: string;
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

export interface ShipmentStop {
  id: string;
  shipmentId: string;
  stopType: string;
  sequenceNo: number;
  locationName: string;
  contactName: string;
  contactPhone: string;
  addressLine1: string;
  addressLine2: string;
  city: string;
  region: string;
  postalCode: string;
  country: string;
  saudiNationalAddressBuildingNo: string;
  saudiNationalAddressAdditionalNo: string;
  saudiNationalAddressDistrict: string;
  latitude?: number | null;
  longitude?: number | null;
  plannedArrivalAt: string;
  actualArrivalAt?: string | null;
  completedAt?: string | null;
  status: string;
  notes: string;
  createdAt: string;
  updatedAt: string;
}

export interface ProofOfDelivery {
  id: string;
  shipmentId: string;
  stopId: string;
  capturedByUserId?: string | null;
  driverId?: string | null;
  vehicleId?: string | null;
  recipientName: string;
  recipientPhone: string;
  signatureUrl: string;
  photoUrl: string;
  documentUrl: string;
  notes: string;
  deliveryCondition: string;
  capturedLatitude?: number | null;
  capturedLongitude?: number | null;
  capturedAt: string;
  verifiedAt?: string | null;
  verifiedByUserId?: string | null;
  status: string;
  createdAt: string;
  updatedAt: string;
}

export interface ShipmentEvent {
  id: string;
  shipmentId: string;
  eventType: string;
  message: string;
  actorName: string;
  visibility: string;
  occurredAtUtc: string;
}

export interface CustomerTrackingLink {
  id: string;
  shipmentId: string;
  token: string;
  expiresAtUtc: string;
  isRevoked: boolean;
  sharedBy: string;
  createdAtUtc: string;
  revokedAtUtc?: string | null;
  updatedAtUtc?: string | null;
}

export interface DriverTask {
  id: string;
  shipmentId: string;
  stopId?: string | null;
  taskType: string;
  title: string;
  description: string;
  status: string;
  driverName: string;
  vehicleNumber: string;
  dueAtUtc: string;
  completedAtUtc?: string | null;
  notes: string;
  createdAtUtc: string;
  updatedAtUtc?: string | null;
}

export interface PublicTrackingSummary {
  shipmentNumber: string;
  status: string;
  origin: string;
  destination: string;
  pickupScheduledAtUtc?: string | null;
  deliveredAtUtc?: string | null;
  stops: Array<{
    sequenceNo: number;
    stopType: string;
    locationName: string;
    city: string;
    status: string;
    plannedArrivalAt: string;
    actualArrivalAt?: string | null;
    completedAt?: string | null;
  }>;
  publicEvents: Array<{
    eventType: string;
    message: string;
    occurredAtUtc: string;
  }>;
  pod: Array<{
    recipientName: string;
    deliveryCondition: string;
    status: string;
    capturedAt: string;
    verifiedAt?: string | null;
    signatureUrl: string;
    photoUrl: string;
    documentUrl: string;
  }>;
}

export interface Carrier {
  id: string;
  name: string;
  code: string;
  status: string;
  region: string;
  serviceType: string;
  vatNumber: string;
  commercialRegistrationNo: string;
  transportDocumentNo: string;
  permitNo: string;
  nationalAddressBuildingNo: string;
  nationalAddressAdditionalNo: string;
  district: string;
  city: string;
  postalCode: string;
  country: string;
  documentStatus: string;
  expiryStatus: string;
  hijriExpiryDate?: string | null;
  gregorianExpiryDate?: string | null;
  onTimeScore: number;
  damageScore: number;
  costScore: number;
  notes: string;
  createdAtUtc: string;
  updatedAtUtc?: string | null;
}

export interface CarrierContact {
  id: string;
  carrierId: string;
  name: string;
  role: string;
  email: string;
  phone: string;
  notes: string;
  createdAtUtc: string;
  updatedAtUtc?: string | null;
}

export interface CarrierPerformanceScore {
  id: string;
  carrierId: string;
  onTimePct: number;
  damagePct: number;
  acceptancePct: number;
  overallScore: number;
  scoredAtUtc: string;
  notes: string;
}

export interface ShipmentCarrierAssignment {
  id: string;
  shipmentId: string;
  carrierId: string;
  status: string;
  quotedAmount: number;
  agreedAmount: number;
  notes: string;
  assignedAtUtc: string;
  updatedAtUtc?: string | null;
}

export interface BookingRequest {
  id: string;
  requestNumber: string;
  customerName: string;
  origin: string;
  destination: string;
  status: string;
  estimatedWeightKg: number;
  estimatedVolumeCbm: number;
  requestedAtUtc: string;
  notes: string;
}

export interface QuoteRequest {
  id: string;
  quoteNumber: string;
  customerName: string;
  origin: string;
  destination: string;
  status: string;
  estimatedAmount: number;
  marginPct: number;
  requestedAtUtc: string;
  notes: string;
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

export const publicTrackingApi = {
  track: (token: string) => client.get<PublicTrackingSummary>(`/api/public/shipments/track/${token}`).then((r) => r.data),
  events: (token: string) => client.get<{ items: PublicTrackingSummary['publicEvents'] }>(`/api/public/shipments/track/${token}/events`).then((r) => r.data),
  pod: (token: string) => client.get<{ items: PublicTrackingSummary['pod'] }>(`/api/public/shipments/track/${token}/pod`).then((r) => r.data),
};

export const fleetLifecycleApi = {
  getStops: (shipmentId: string) => client.get<{ items: ShipmentStop[] }>(`/api/fleet-tms/shipments/${shipmentId}/stops`).then((r) => r.data),
  getShipmentEvents: (shipmentId: string) => client.get<{ items: ShipmentEvent[] }>(`/api/fleet-tms/shipments/${shipmentId}/events`).then((r) => r.data),
  getTrackingLinks: (shipmentId: string) => client.get<{ items: CustomerTrackingLink[] }>(`/api/fleet-tms/shipments/${shipmentId}/tracking-link`).then((r) => r.data),
  createStop: (shipmentId: string, body: Partial<ShipmentStop> & { stopType: string; sequenceNo: number; locationName: string; plannedArrivalAt: string }) =>
    client.post<ShipmentStop>(`/api/fleet-tms/shipments/${shipmentId}/stops`, body).then((r) => r.data),
  updateStop: (shipmentId: string, stopId: string, body: Partial<ShipmentStop> & { stopType: string; sequenceNo: number; plannedArrivalAt: string }) =>
    client.put<ShipmentStop>(`/api/fleet-tms/shipments/${shipmentId}/stops/${stopId}`, body).then((r) => r.data),
  arriveStop: (shipmentId: string, stopId: string, body: { notes?: string } = {}) =>
    client.post<ShipmentStop>(`/api/fleet-tms/shipments/${shipmentId}/stops/${stopId}/arrive`, body).then((r) => r.data),
  completeStop: (shipmentId: string, stopId: string, body: { notes?: string } = {}) =>
    client.post<ShipmentStop>(`/api/fleet-tms/shipments/${shipmentId}/stops/${stopId}/complete`, body).then((r) => r.data),
  getPod: (shipmentId: string) => client.get<{ items: ProofOfDelivery[] }>(`/api/fleet-tms/shipments/${shipmentId}/pod`).then((r) => r.data),
  createPod: (shipmentId: string, body: {
    stopId: string;
    recipientName?: string;
    recipientPhone?: string;
    signatureUrl?: string;
    photoUrl?: string;
    documentUrl?: string;
    notes?: string;
    deliveryCondition?: string;
    capturedLatitude?: number | null;
    capturedLongitude?: number | null;
  }) => client.post<ProofOfDelivery>(`/api/fleet-tms/shipments/${shipmentId}/pod`, body).then((r) => r.data),
  updatePod: (shipmentId: string, podId: string, body: {
    recipientName?: string;
    recipientPhone?: string;
    signatureUrl?: string;
    photoUrl?: string;
    documentUrl?: string;
    notes?: string;
    deliveryCondition?: string;
    capturedLatitude?: number | null;
    capturedLongitude?: number | null;
  }) => client.put<ProofOfDelivery>(`/api/fleet-tms/shipments/${shipmentId}/pod/${podId}`, body).then((r) => r.data),
  submitPod: (shipmentId: string, podId: string) => client.post<ProofOfDelivery>(`/api/fleet-tms/shipments/${shipmentId}/pod/${podId}/submit`, {}).then((r) => r.data),
  verifyPod: (shipmentId: string, podId: string) => client.post<ProofOfDelivery>(`/api/fleet-tms/shipments/${shipmentId}/pod/${podId}/verify`, {}).then((r) => r.data),
  rejectPod: (shipmentId: string, podId: string, body: { notes?: string } = {}) => client.post<ProofOfDelivery>(`/api/fleet-tms/shipments/${shipmentId}/pod/${podId}/reject`, body).then((r) => r.data),
  createTrackingLink: (shipmentId: string, body: { token?: string; expiresAtUtc?: string }) =>
    client.post<CustomerTrackingLink>(`/api/fleet-tms/shipments/${shipmentId}/tracking-link`, body).then((r) => r.data),
  revokeTrackingLink: (shipmentId: string, linkId: string) =>
    client.delete(`/api/fleet-tms/shipments/${shipmentId}/tracking-link/${linkId}`).then((r) => r.data),
  getDriverTasks: (params: { driverName?: string } = {}) =>
    client.get<{ items: DriverTask[] }>('/api/fleet-tms/driver/tasks', { params }).then((r) => r.data),
  getDriverTask: (taskId: string) => client.get<DriverTask>(`/api/fleet-tms/driver/tasks/${taskId}`).then((r) => r.data),
  arriveDriverTask: (taskId: string) => client.post<DriverTask>(`/api/fleet-tms/driver/tasks/${taskId}/arrive`, {}).then((r) => r.data),
  completeDriverTask: (taskId: string, body: { notes?: string } = {}) => client.post<DriverTask>(`/api/fleet-tms/driver/tasks/${taskId}/complete`, body).then((r) => r.data),
  upsertDriverTaskPod: (taskId: string, body: { stopId?: string; recipientName?: string; recipientPhone?: string; signatureUrl?: string; photoUrl?: string; documentUrl?: string; notes?: string; deliveryCondition?: string; capturedLatitude?: number | null; capturedLongitude?: number | null }) =>
    client.post<ProofOfDelivery>(`/api/fleet-tms/driver/tasks/${taskId}/pod`, body).then((r) => r.data),
  markInvoiceReady: (shipmentId: string, body: { override?: boolean; notes?: string } = {}) =>
    client.post<FleetShipment>(`/api/fleet-tms/shipments/${shipmentId}/mark-invoice-ready`, body).then((r) => r.data),
  invoiceReady: () => client.get<{ items: FleetShipment[] }>('/api/fleet-tms/shipments/invoice-ready').then((r) => r.data),
};

export const fleetCommercialApi = {
  carriers: () => client.get<{ items: Carrier[] }>('/api/fleet-tms/carriers').then((r) => r.data),
  carrier: (id: string) => client.get<Carrier>(`/api/fleet-tms/carriers/${id}`).then((r) => r.data),
  createCarrier: (body: Partial<Carrier> & { name: string; code: string }) =>
    client.post<Carrier>('/api/fleet-tms/carriers', body).then((r) => r.data),
  updateCarrier: (id: string, body: Partial<Carrier>) =>
    client.put<Carrier>(`/api/fleet-tms/carriers/${id}`, body).then((r) => r.data),
  assignShipmentCarrier: (shipmentId: string, body: { carrierId: string; quotedAmount?: number; agreedAmount?: number; notes?: string }) =>
    client.post<ShipmentCarrierAssignment>(`/api/fleet-tms/shipments/${shipmentId}/carrier`, body).then((r) => r.data),
  bookingRequests: () => client.get<{ items: BookingRequest[] }>('/api/fleet-tms/booking-requests').then((r) => r.data),
  createBookingRequest: (body: Partial<BookingRequest> & { requestNumber: string; customerName: string; origin: string; destination: string }) =>
    client.post<BookingRequest>('/api/fleet-tms/booking-requests', body).then((r) => r.data),
  quoteRequests: () => client.get<{ items: QuoteRequest[] }>('/api/fleet-tms/quote-requests').then((r) => r.data),
  createQuoteRequest: (body: Partial<QuoteRequest> & { quoteNumber: string; customerName: string; origin: string; destination: string }) =>
    client.post<QuoteRequest>('/api/fleet-tms/quote-requests', body).then((r) => r.data),
};

export interface TemperatureZone {
  id: string;
  code: string;
  name: string;
  minCelsius: number;
  maxCelsius: number;
  color: string;
  isActive: boolean;
  notes: string;
  createdAtUtc: string;
  updatedAtUtc?: string | null;
}

export interface TemperatureDevice {
  id: string;
  deviceCode: string;
  name: string;
  zoneId?: string | null;
  zoneCode?: string | null;
  zoneName?: string | null;
  shipmentId?: string | null;
  shipmentNumber?: string | null;
  vehicleNumber: string;
  status: string;
  lastReportedTemperatureCelsius: number;
  batteryPercent: number;
  lastPingAtUtc?: string | null;
  notes: string;
  createdAtUtc?: string;
  updatedAtUtc?: string | null;
}

export interface TemperatureReading {
  id: string;
  deviceId: string;
  shipmentId?: string | null;
  zoneId?: string | null;
  temperatureCelsius: number;
  humidityPercent?: number | null;
  latitude?: number | null;
  longitude?: number | null;
  source: string;
  status: string;
  notes: string;
  recordedAtUtc: string;
  createdAtUtc: string;
}

export interface TemperatureAlert {
  id: string;
  deviceId: string;
  deviceCode?: string | null;
  shipmentId?: string | null;
  shipmentNumber?: string | null;
  readingId: string;
  alertType: string;
  severity: string;
  status: string;
  thresholdMin: number;
  thresholdMax: number;
  measuredTemperature: number;
  triggeredAtUtc: string;
  resolvedAtUtc?: string | null;
  resolvedBy: string;
  resolutionNotes: string;
  notes: string;
}

export interface ColdChainReport {
  id: string;
  shipmentId: string;
  shipmentNumber: string;
  generatedAtUtc: string;
  compliancePercent: number;
  minTemperatureCelsius: number;
  maxTemperatureCelsius: number;
  totalReadings: number;
  breachCount: number;
  summaryJson: string;
  notes: string;
}

export interface RefrigerationUnitHealth {
  id: string;
  vehicleNumber: string;
  unitSerial: string;
  status: string;
  compressorHours: number;
  lastServiceAtUtc?: string | null;
  nextServiceDueAtUtc?: string | null;
  temperatureDeviationCount: number;
  notes: string;
}

export interface AssetType {
  id: string;
  code: string;
  name: string;
  description: string;
  isReturnable: boolean;
  createdAtUtc: string;
  updatedAtUtc?: string | null;
}

export interface Asset {
  id: string;
  assetTypeId: string;
  assetTypeCode?: string | null;
  assetTypeName?: string | null;
  assetTag: string;
  name: string;
  status: string;
  currentLocation: string;
  condition: string;
  isReturnable: boolean;
  quantity: number;
  unitOfMeasure: string;
  notes: string;
  lastSeenAtUtc?: string | null;
  createdAtUtc: string;
  updatedAtUtc?: string | null;
  assignmentCount?: number;
}

export interface AssetAssignment {
  id: string;
  assetId: string;
  shipmentId?: string | null;
  shipmentNumber?: string | null;
  carrierId?: string | null;
  carrierName?: string | null;
  assigneeType: string;
  assigneeName: string;
  quantity: number;
  status: string;
  assignedAtUtc: string;
  releasedAtUtc?: string | null;
  notes: string;
}

export interface AssetEvent {
  id: string;
  type: string;
  eventType: string;
  quantity: number;
  location: string;
  actorName: string;
  occurredAtUtc: string;
  notes: string;
}

export interface BarcodeScanEvent {
  id: string;
  scannedValue: string;
  scannerId: string;
  eventType: string;
  status: string;
  recordedAtUtc: string;
  notes: string;
}

export interface RfidEvent {
  id: string;
  tagId: string;
  readerId: string;
  eventType: string;
  status: string;
  recordedAtUtc: string;
  notes: string;
}

export const fleetColdChainApi = {
  summary: () =>
    client.get<{
      generatedAtUtc: string;
      summary: {
        activeDevices: number;
        readingsToday: number;
        openAlerts: number;
        totalReadings: number;
        breachReadings: number;
        avgTemperatureCelsius: number;
        compliancePercent: number;
      };
      zones: TemperatureZone[];
      devices: Array<Pick<TemperatureDevice, 'id' | 'deviceCode' | 'name' | 'vehicleNumber' | 'status' | 'lastReportedTemperatureCelsius' | 'batteryPercent' | 'lastPingAtUtc' | 'notes'> & {
        zoneCode?: string | null;
        zoneName?: string | null;
      }>;
      alerts: Array<Pick<TemperatureAlert, 'id' | 'alertType' | 'severity' | 'status' | 'measuredTemperature' | 'thresholdMin' | 'thresholdMax' | 'triggeredAtUtc' | 'resolutionNotes'>>;
      reports: ColdChainReport[];
    }>('/api/fleet-tms/cold-chain/summary').then((r) => r.data),
  devices: () => client.get<{ items: TemperatureDevice[] }>('/api/fleet-tms/cold-chain/devices').then((r) => r.data),
  createDevice: (body: Partial<TemperatureDevice> & { deviceCode: string; name: string }) =>
    client.post<TemperatureDevice>('/api/fleet-tms/cold-chain/devices', body).then((r) => r.data),
  shipmentReadings: (shipmentId: string) => client.get<{ items: TemperatureReading[] }>(`/api/fleet-tms/cold-chain/shipments/${shipmentId}/readings`).then((r) => r.data),
  createReading: (body: {
    deviceId: string;
    shipmentId?: string | null;
    zoneId?: string | null;
    temperatureCelsius: number;
    humidityPercent?: number | null;
    latitude?: number | null;
    longitude?: number | null;
    source?: string;
    status?: string;
    notes?: string;
  }) => client.post<TemperatureReading>('/api/fleet-tms/cold-chain/readings', body).then((r) => r.data),
  alerts: (status?: string) => client.get<{ items: TemperatureAlert[] }>('/api/fleet-tms/cold-chain/alerts', { params: status ? { status } : undefined }).then((r) => r.data),
  resolveAlert: (id: string, body: { resolutionNotes?: string } = {}) =>
    client.post<TemperatureAlert>(`/api/fleet-tms/cold-chain/alerts/${id}/resolve`, body).then((r) => r.data),
  report: (shipmentId: string) => client.get<ColdChainReport>(`/api/fleet-tms/cold-chain/reports/${shipmentId}`).then((r) => r.data),
};

export const fleetAssetApi = {
  assetTypes: () => client.get<{ items: AssetType[] }>('/api/fleet-tms/assets/types').then((r) => r.data),
  createAssetType: (body: Partial<AssetType> & { code: string; name: string }) =>
    client.post<AssetType>('/api/fleet-tms/assets/types', body).then((r) => r.data),
  assets: () => client.get<{ items: Asset[] }>('/api/fleet-tms/assets').then((r) => r.data),
  asset: (id: string) => client.get<{ asset: Asset; assignments: AssetAssignment[]; events: AssetEvent[] }>(`/api/fleet-tms/assets/${id}`).then((r) => r.data),
  createAsset: (body: Partial<Asset> & { assetTypeId: string; assetTag: string; name: string }) =>
    client.post<Asset>('/api/fleet-tms/assets', body).then((r) => r.data),
  updateAsset: (id: string, body: Partial<Asset> & { assetTypeId?: string }) =>
    client.put<Asset>(`/api/fleet-tms/assets/${id}`, body).then((r) => r.data),
  assignAsset: (id: string, body: {
    shipmentId?: string | null;
    carrierId?: string | null;
    assigneeType?: string;
    assigneeName?: string;
    quantity?: number;
    status?: string;
    currentLocation?: string;
    releasedAtUtc?: string | null;
    notes?: string;
  }) => client.post<AssetAssignment>(`/api/fleet-tms/assets/${id}/assign`, body).then((r) => r.data),
  checkInAsset: (id: string, body: {
    location?: string;
    condition?: string;
    notes?: string;
    shipmentId?: string | null;
    carrierId?: string | null;
    assigneeType?: string;
    assigneeName?: string;
    quantity?: number;
  }) => client.post<Asset>(`/api/fleet-tms/assets/${id}/check-in`, body).then((r) => r.data),
  checkOutAsset: (id: string, body: {
    location?: string;
    condition?: string;
    notes?: string;
    shipmentId?: string | null;
    carrierId?: string | null;
    assigneeType?: string;
    assigneeName?: string;
    quantity?: number;
  }) => client.post<Asset>(`/api/fleet-tms/assets/${id}/check-out`, body).then((r) => r.data),
  events: (id: string) => client.get<{ items: AssetEvent[] }>(`/api/fleet-tms/assets/${id}/events`).then((r) => r.data),
  scan: (body: {
    kind?: string;
    assetId?: string | null;
    shipmentId?: string | null;
    scannedValue?: string;
    tagId?: string;
    scannerId?: string;
    readerId?: string;
    eventType?: string;
    status?: string;
    notes?: string;
  }) => client.post<BarcodeScanEvent | RfidEvent>('/api/fleet-tms/assets/scan', body).then((r) => r.data),
};

export interface SaudiRegionReference {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  countryCode: string;
  isGccReady: boolean;
  cities: string[];
}

export interface FleetReadinessDocument {
  id: string;
  kind: string;
  subjectType: string;
  subjectId: string;
  subjectName: string;
  documentType: string;
  documentNumber: string;
  transportDocumentNo: string;
  permitNo: string;
  vatNumber: string;
  commercialRegistrationNo: string;
  countryCode: string;
  nationalAddressBuildingNo: string;
  nationalAddressAdditionalNo: string;
  district: string;
  city: string;
  region: string;
  postalCode: string;
  documentStatus: string;
  expiryStatus: string;
  issueDate?: string | null;
  hijriExpiryDate?: string | null;
  gregorianExpiryDate?: string | null;
  notes: string;
  createdAtUtc: string;
  updatedAtUtc?: string | null;
}

export interface FleetReadinessExpiry {
  id: string;
  kind: string;
  subjectType: string;
  subjectName: string;
  documentType: string;
  documentNumber: string;
  documentStatus: string;
  expiryStatus: string;
  countryCode: string;
  gregorianExpiryDate?: string | null;
  hijriExpiryDate?: string | null;
  daysRemaining: number;
  notes: string;
}

export interface FleetInvoiceReadySummary {
  generatedAtUtc: string;
  summary: {
    readyCount: number;
    blockedCount: number;
    readinessPercent: number;
    carrierReady: number;
    branchReady: number;
  };
  readyShipments: Array<{
    id: string;
    shipmentNumber: string;
    customerName: string;
    status: string;
    customerVATNumber: string;
    customerCommercialRegistrationNo: string;
    invoiceReadyAtUtc?: string | null;
    invoiceReadinessNotes: string;
    origin: string;
    destination: string;
    carrierName: string;
    routeCode: string;
  }>;
  blockedShipments: Array<{
    id: string;
    shipmentNumber: string;
    customerName: string;
    status: string;
    invoiceReadinessNotes: string;
    origin: string;
    destination: string;
    carrierName: string;
    routeCode: string;
  }>;
}

export interface FleetReadinessDocumentRequest {
  kind: string;
  subjectType: string;
  subjectId?: string;
  subjectName: string;
  documentType: string;
  documentNumber?: string;
  transportDocumentNo?: string;
  permitNo?: string;
  vatNumber?: string;
  commercialRegistrationNo?: string;
  countryCode?: string;
  nationalAddressBuildingNo?: string;
  nationalAddressAdditionalNo?: string;
  district?: string;
  city?: string;
  region?: string;
  postalCode?: string;
  documentStatus?: string;
  issueDate?: string | null;
  hijriExpiryDate?: string | null;
  gregorianExpiryDate?: string | null;
  notes?: string;
  requiresExpiry?: boolean;
}

export const fleetReadinessApi = {
  regions: () => client.get<{ items: SaudiRegionReference[] }>('/api/fleet-tms/saudi/regions').then((r) => r.data),
  documents: (query?: { kind?: string; subjectType?: string }) =>
    client.get<{ items: FleetReadinessDocument[] }>('/api/fleet-tms/compliance/documents', { params: query }).then((r) => r.data),
  createDocument: (body: FleetReadinessDocumentRequest) =>
    client.post<FleetReadinessDocument>('/api/fleet-tms/compliance/documents', body).then((r) => r.data),
  updateDocument: (id: string, body: FleetReadinessDocumentRequest) =>
    client.put<FleetReadinessDocument>(`/api/fleet-tms/compliance/documents/${id}`, body).then((r) => r.data),
  expiries: () =>
    client.get<{ generatedAtUtc: string; items: FleetReadinessExpiry[]; summary: { totalDocuments: number; expiringSoon: number; expired: number; healthy: number; windowDays: number } }>('/api/fleet-tms/compliance/expiries').then((r) => r.data),
  invoiceReady: () => client.get<FleetInvoiceReadySummary>('/api/fleet-tms/vat/invoice-ready').then((r) => r.data),
};
