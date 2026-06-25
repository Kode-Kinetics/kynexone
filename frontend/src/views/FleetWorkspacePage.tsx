'use client';

import { useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { usePathname } from 'next/navigation';
import {
  AlertTriangle,
  ArrowRight,
  BadgeInfo,
  CheckCircle2,
  ChevronRight,
  Fuel,
  MapPinned,
  Package,
  Sparkles,
  Truck,
  Wrench,
} from 'lucide-react';
import { Logo } from '../components/Logo';
import { notifyApiError } from '../api/client';
import { fleetApi, type FleetFuelEvent, type FleetMaintenanceTicket, type FleetOverview, type FleetShipment, type FleetTrackingPoint, type FleetVehicle } from '../api/fleet';

type FleetMode = 'command' | 'shipments' | 'vehicles' | 'tracking' | 'maintenance' | 'fuel';

const MODULES: Record<FleetMode, {
  label: string;
  title: string;
  subtitle: string;
  accent: string;
  path: string;
  summary: string;
}> = {
  command: {
    label: 'Fleet Command Center',
    title: 'Run freight, fleets, and service from one premium control room.',
    subtitle: 'See shipment pressure, available fleet capacity, live tracking signals, maintenance risk, and fuel health in one executive cockpit.',
    accent: 'from-emerald-600 via-cyan-500 to-sky-400',
    path: '/fleet',
    summary: 'One place to coordinate load planning, dispatch pressure, route balance, and the health of the operation.',
  },
  shipments: {
    label: 'Shipments & Loads',
    title: 'Turn booked freight into an executable movement plan.',
    subtitle: 'Track shipments from booking to proof of delivery with weights, volumes, priorities, and carrier assignments visible at a glance.',
    accent: 'from-cyan-600 via-sky-500 to-blue-400',
    path: '/fleet/shipments',
    summary: 'Keep load planning grounded in real capacity, real urgency, and the actual DB-backed movement state.',
  },
  vehicles: {
    label: 'Vehicle Management',
    title: 'Keep the fleet visible, ready, and accounted for.',
    subtitle: 'Monitor vehicle status, driver assignment, capacity, fuel, odometer, and service readiness in one operational panel.',
    accent: 'from-sky-600 via-indigo-500 to-violet-400',
    path: '/fleet/vehicles',
    summary: 'Make fleet availability and maintenance posture obvious before the schedule gets tight.',
  },
  tracking: {
    label: 'Live Tracking',
    title: 'See every shipment move, pause, and arrive in real time.',
    subtitle: 'GPS-style tracking cards surface location labels, geofences, speed, ETA, and exception signals without making the page feel noisy.',
    accent: 'from-blue-600 via-cyan-500 to-emerald-400',
    path: '/fleet/tracking',
    summary: 'Shipping visibility is not a dashboard decoration here; it is the operational source of truth.',
  },
  maintenance: {
    label: 'Maintenance',
    title: 'Protect the fleet before the schedule gets expensive.',
    subtitle: 'Track open work orders, due dates, vendors, downtime, and cost posture so maintenance becomes planned instead of disruptive.',
    accent: 'from-orange-500 via-amber-500 to-yellow-400',
    path: '/fleet/maintenance',
    summary: 'A healthy fleet is a commercial advantage, not just a repair queue.',
  },
  fuel: {
    label: 'Fuel Analytics',
    title: 'Turn fuel spend into a governed operating signal.',
    subtitle: 'Review fuel events, anomalies, card usage, and vehicle trends to catch leakage early and keep spending explainable.',
    accent: 'from-emerald-600 via-lime-500 to-teal-400',
    path: '/fleet/fuel',
    summary: 'Fuel should read like business intelligence, not a monthly surprise.',
  },
};

const MODE_ORDER: FleetMode[] = ['command', 'shipments', 'vehicles', 'tracking', 'maintenance', 'fuel'];

export function FleetWorkspacePage({ mode }: { mode: FleetMode }) {
  const pathname = usePathname();
  const config = MODULES[mode];
  const [overview, setOverview] = useState<FleetOverview | null>(null);
  const [shipments, setShipments] = useState<FleetShipment[]>([]);
  const [vehicles, setVehicles] = useState<FleetVehicle[]>([]);
  const [tracking, setTracking] = useState<FleetTrackingPoint[]>([]);
  const [maintenance, setMaintenance] = useState<FleetMaintenanceTicket[]>([]);
  const [fuel, setFuel] = useState<FleetFuelEvent[]>([]);
  const [loading, setLoading] = useState(true);
  const [savingId, setSavingId] = useState<string | null>(null);

  const refreshAll = async () => {
    const [ov, shipmentRes, vehicleRes, trackingRes, maintenanceRes, fuelRes] = await Promise.all([
      fleetApi.overview(),
      fleetApi.shipments({ pageSize: 12 }),
      fleetApi.vehicles(),
      fleetApi.tracking({ pageSize: 12 }),
      fleetApi.maintenance({ pageSize: 12 }),
      fleetApi.fuel({ pageSize: 12 }),
    ]);
    setOverview(ov);
    setShipments(shipmentRes.items);
    setVehicles(vehicleRes.items);
    setTracking(trackingRes.items);
    setMaintenance(maintenanceRes.items);
    setFuel(fuelRes.items);
  };

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    (async () => {
      try {
        const [ov, shipmentRes, vehicleRes, trackingRes, maintenanceRes, fuelRes] = await Promise.all([
          fleetApi.overview(),
          fleetApi.shipments({ pageSize: 12 }),
          fleetApi.vehicles(),
          fleetApi.tracking({ pageSize: 12 }),
          fleetApi.maintenance({ pageSize: 12 }),
          fleetApi.fuel({ pageSize: 12 }),
        ]);
        if (cancelled) return;
        setOverview(ov);
        setShipments(shipmentRes.items);
        setVehicles(vehicleRes.items);
        setTracking(trackingRes.items);
        setMaintenance(maintenanceRes.items);
        setFuel(fuelRes.items);
      } catch (err) {
        if (!cancelled) notifyApiError(err, 'Unable to load fleet workspace.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const stats = useMemo(() => {
    if (!overview) return [];
    const s = overview.summary;
    return [
      { label: 'Active shipments', value: s.activeShipments.toString(), hint: 'Booked + moving', icon: Package },
      { label: 'Fleet available', value: s.activeVehicles.toString(), hint: 'Ready for assignment', icon: Truck },
      { label: 'Open maintenance', value: s.openMaintenance.toString(), hint: 'Needs planning', icon: Wrench },
      { label: 'Fuel alerts', value: s.fuelAlerts.toString(), hint: 'Review anomalies', icon: Fuel },
    ];
  }, [overview]);

  const liveRows = useMemo(() => {
    if (mode === 'vehicles') {
      return vehicles.slice(0, 5).map((vehicle) => ({
        id: vehicle.id,
        title: vehicle.vehicleNumber,
        subtitle: `${vehicle.type} · ${vehicle.driverName || 'No driver'}`,
        status: vehicle.status,
      }));
    }
    if (mode === 'tracking') {
      return tracking.slice(0, 5).map((point) => ({
        id: point.id,
        title: point.shipmentNumber,
        subtitle: `${point.locationLabel} · ${point.geofenceName}`,
        status: point.status,
      }));
    }
    if (mode === 'maintenance') {
      return maintenance.slice(0, 5).map((ticket) => ({
        id: ticket.id,
        title: ticket.workOrderNumber,
        subtitle: `${ticket.vehicleNumber} · ${ticket.vendorName}`,
        status: ticket.status,
      }));
    }
    if (mode === 'fuel') {
      return fuel.slice(0, 5).map((event) => ({
        id: event.id,
        title: event.vehicleNumber,
        subtitle: `${event.stationName} · ${event.city}`,
        status: event.anomalyFlag ? 'Review' : event.eventType,
      }));
    }
    return shipments.slice(0, 5).map((shipment) => ({
      id: shipment.id,
      title: shipment.shipmentNumber,
      subtitle: `${shipment.customerName} · ${shipment.destination}`,
      status: shipment.status,
    }));
  }, [fuel, maintenance, mode, shipments, tracking, vehicles]);

  const exceptionRows = useMemo(() => {
    const rows = [] as Array<{ id: string; label: string; detail: string; tone: string }>;
    if (overview?.summary.fuelAlerts) {
      rows.push({ id: 'fuel-alerts', label: 'Fuel alerts', detail: `${overview.summary.fuelAlerts} vehicle fueling events need review.`, tone: 'amber' });
    }
    if (overview?.summary.openMaintenance) {
      rows.push({ id: 'maintenance', label: 'Maintenance queue', detail: `${overview.summary.openMaintenance} work orders are open or in progress.`, tone: 'orange' });
    }
    if (overview?.summary.enRoute) {
      rows.push({ id: 'movement', label: 'Freight in motion', detail: `${overview.summary.enRoute} shipments are currently on the road.`, tone: 'blue' });
    }
    return rows.slice(0, 4);
  }, [overview]);

  const deliveredRateText = overview ? `${overview.summary.deliveredRate.toFixed(1)}%` : '0%';
  const avgFuelLevelText = overview ? `${overview.summary.avgFuelLevel.toFixed(1)}%` : '0%';

  const handleDispatch = async (shipment: FleetShipment) => {
    setSavingId(shipment.id);
    try {
      await fleetApi.dispatchShipment(shipment.id, {
        vehicleNumber: shipment.vehicleNumber,
        driverName: shipment.driverName,
        routeCode: shipment.routeCode,
        notes: shipment.notes,
      });
      await refreshAll();
    } catch (err) {
      notifyApiError(err, 'Unable to dispatch shipment.');
    } finally {
      setSavingId(null);
    }
  };

  const handleService = async (vehicle: FleetVehicle) => {
    setSavingId(vehicle.id);
    try {
      await fleetApi.serviceVehicle(vehicle.id, {
        status: 'Maintenance',
        healthStatus: 'Reviewing',
        nextServiceAtUtc: vehicle.nextServiceAtUtc,
        notes: 'Sent for planned service from fleet control.',
      });
      await refreshAll();
    } catch (err) {
      notifyApiError(err, 'Unable to update vehicle service state.');
    } finally {
      setSavingId(null);
    }
  };

  const handleCloseMaintenance = async (ticket: FleetMaintenanceTicket) => {
    setSavingId(ticket.id);
    try {
      await fleetApi.closeMaintenance(ticket.id, {
        status: 'Closed',
        actualCost: ticket.estimatedCost,
        notes: 'Closed after review from fleet operations.',
      });
      await refreshAll();
    } catch (err) {
      notifyApiError(err, 'Unable to close maintenance ticket.');
    } finally {
      setSavingId(null);
    }
  };

  const handleFlagFuel = async (eventRow: FleetFuelEvent) => {
    setSavingId(eventRow.id);
    try {
      await fleetApi.flagFuelEvent(eventRow.id, {
        anomalyFlag: true,
        notes: eventRow.notes || 'Flagged for manual review by fleet operations.',
      });
      await refreshAll();
    } catch (err) {
      notifyApiError(err, 'Unable to flag fuel event.');
    } finally {
      setSavingId(null);
    }
  };

  return (
    <>
      <style>{`
        @keyframes kx-float {
          0%, 100% { transform: translateY(0px) translateX(0px); opacity: 0.8; }
          50% { transform: translateY(-10px) translateX(8px); opacity: 1; }
        }
        @keyframes kx-scan {
          0% { transform: translateX(-28%); }
          100% { transform: translateX(28%); }
        }
        @keyframes kx-grid {
          0%, 100% { opacity: 0.12; }
          50% { opacity: 0.2; }
        }
        .kx-float { animation: kx-float 10s ease-in-out infinite; }
        .kx-scan { animation: kx-scan 18s linear infinite; }
        .kx-grid { animation: kx-grid 12s ease-in-out infinite; }
        @media (prefers-reduced-motion: reduce) {
          .kx-float, .kx-scan, .kx-grid { animation: none !important; }
        }
      `}</style>

      <div className="relative min-h-[100svh] overflow-hidden bg-[#eef6f1] text-slate-900 dark:bg-[#03070f] dark:text-white">
        <div className="pointer-events-none absolute inset-0 bg-[radial-gradient(circle_at_top_left,rgba(16,185,129,0.18),transparent_26%),radial-gradient(circle_at_80%_16%,rgba(14,165,233,0.12),transparent_22%),linear-gradient(180deg,rgba(255,255,255,0.55),transparent_32%)] dark:bg-[radial-gradient(circle_at_top_left,rgba(16,185,129,0.20),transparent_26%),radial-gradient(circle_at_80%_16%,rgba(14,165,233,0.10),transparent_22%),linear-gradient(180deg,rgba(6,11,20,0.95),rgba(3,7,15,0.98))]" />
        <div className="pointer-events-none absolute inset-0 opacity-[0.16] mix-blend-soft-light [background-image:linear-gradient(rgba(16,185,129,0.08)_1px,transparent_1px),linear-gradient(90deg,rgba(16,185,129,0.08)_1px,transparent_1px)] [background-size:74px_74px] kx-grid" />
        <div className="pointer-events-none absolute left-[9%] top-[16%] h-60 w-60 rounded-full bg-white/40 blur-3xl kx-float" />
        <div className="pointer-events-none absolute right-[10%] top-[20%] h-72 w-72 rounded-full bg-cyan-300/12 blur-3xl kx-float" />
        <div className="pointer-events-none absolute bottom-[-6rem] left-[18%] h-80 w-80 rounded-full bg-emerald-400/12 blur-3xl kx-float" />
        <div className="pointer-events-none absolute inset-x-0 top-1/2 h-px bg-[linear-gradient(90deg,transparent,rgba(16,185,129,0.18),rgba(14,165,233,0.26),transparent)] opacity-45 kx-scan" />

        <div className="relative z-10 mx-auto flex min-h-[100svh] w-full max-w-[1640px] flex-col px-4 py-3 sm:px-6 lg:px-8 lg:py-4">
          <div className="mb-4 flex items-center justify-between gap-4">
            <div className="flex items-center gap-3 rounded-[22px] border border-white/80 bg-white/74 px-4 py-3 shadow-[0_18px_40px_rgba(16,185,129,0.08)] backdrop-blur-xl dark:border-white/[0.08] dark:bg-white/[0.04]">
              <div className="rounded-[18px] bg-[radial-gradient(circle_at_top_left,rgba(255,255,255,0.45),transparent_45%),linear-gradient(160deg,rgba(4,120,87,0.98),rgba(8,47,73,0.98))] p-1.5 shadow-[0_12px_24px_rgba(16,185,129,0.22)]">
                <Logo size="xl" />
              </div>
              <div className="h-9 w-px bg-slate-300/60" />
              <div>
                <p className="text-[10px] font-bold tracking-[0.28em] uppercase text-emerald-600/70">Fleet & TMS</p>
                <p className="text-[11px] text-slate-500">{config.label}</p>
              </div>
            </div>

            <div className="hidden items-center gap-2 rounded-full border border-white/70 bg-white/72 p-1 shadow-[0_14px_30px_rgba(16,185,129,0.05)] backdrop-blur-xl md:flex dark:border-white/[0.08] dark:bg-white/[0.04]">
              {MODE_ORDER.map((item) => {
                const active = item === mode || pathname === MODULES[item].path;
                return (
                  <Link
                    key={item}
                    href={MODULES[item].path}
                    className={`rounded-full px-4 py-2 text-[11px] font-semibold uppercase tracking-[0.18em] transition ${
                      active
                        ? 'bg-slate-950 text-white shadow-lg dark:bg-white dark:text-slate-950'
                        : 'text-slate-500 hover:bg-slate-100 dark:text-slate-400 dark:hover:bg-white/[0.05]'
                    }`}
                  >
                    {MODULES[item].label}
                  </Link>
                );
              })}
            </div>
          </div>

          <section className="grid flex-1 gap-4 lg:grid-cols-[1.05fr_0.95fr]">
            <div className="rounded-[32px] border border-white/75 bg-[linear-gradient(160deg,rgba(251,253,255,0.93),rgba(236,247,244,0.75))] p-5 shadow-[0_24px_80px_rgba(16,185,129,0.12)] backdrop-blur-3xl dark:border-white/[0.08] dark:bg-[linear-gradient(160deg,rgba(9,16,29,0.96),rgba(4,9,18,0.92))]">
              <div className="flex flex-wrap items-center gap-2">
                <span className="inline-flex items-center gap-2 rounded-full border border-emerald-300/35 bg-white/80 px-3 py-1.5 text-[10px] font-semibold uppercase tracking-[0.24em] text-emerald-700 shadow-sm backdrop-blur">
                  <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" />
                  Live DB-backed operations
                </span>
                <span className={`inline-flex items-center gap-2 rounded-full bg-gradient-to-r ${config.accent} px-3 py-1.5 text-[10px] font-semibold uppercase tracking-[0.24em] text-white shadow-sm`}>
                  <Sparkles className="h-3.5 w-3.5" />
                  {config.label}
                </span>
              </div>

              <div className="mt-5 max-w-3xl">
                <h1 className="text-[40px] font-black leading-[1.02] tracking-tight text-slate-950 xl:text-[54px] dark:text-white">
                  {config.title}
                </h1>
                <p className="mt-4 max-w-2xl text-[15px] leading-relaxed text-slate-600 dark:text-slate-400">
                  {config.subtitle}
                </p>
                <p className="mt-3 max-w-2xl text-[13px] leading-relaxed text-slate-500 dark:text-slate-500">
                  {config.summary}
                </p>
              </div>

              <div className="mt-6 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                {loading ? (
                  Array.from({ length: 4 }).map((_, index) => (
                    <div key={index} className="rounded-[24px] border border-white/80 bg-white/78 p-4 shadow-[0_12px_26px_rgba(16,185,129,0.05)] backdrop-blur-xl dark:border-white/[0.06] dark:bg-white/[0.04]">
                      <div className="space-y-3">
                        <div className="h-3 w-20 animate-pulse rounded bg-slate-200/80 dark:bg-white/10" />
                        <div className="h-9 w-16 animate-pulse rounded bg-slate-200/80 dark:bg-white/10" />
                        <div className="h-3 w-28 animate-pulse rounded bg-slate-200/80 dark:bg-white/10" />
                      </div>
                    </div>
                  ))
                ) : (
                  stats.map((stat) => (
                    <div key={stat.label} className="rounded-[24px] border border-white/80 bg-white/78 p-4 shadow-[0_12px_26px_rgba(16,185,129,0.05)] backdrop-blur-xl dark:border-white/[0.06] dark:bg-white/[0.04]">
                      <div className="flex items-center justify-between">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.2em] text-slate-400">{stat.label}</p>
                        <stat.icon className="h-4 w-4 text-emerald-500/70" />
                      </div>
                      <p className="mt-2 text-[28px] font-black tracking-tight text-slate-950 dark:text-white">{stat.value}</p>
                      <p className="mt-1 text-[12px] text-slate-500 dark:text-slate-500">{stat.hint}</p>
                    </div>
                  ))
                )}
              </div>

              <div className="mt-4 grid gap-4 xl:grid-cols-[1.22fr_0.78fr]">
                <div className="rounded-[28px] border border-white/80 bg-white/74 p-4 shadow-[0_18px_40px_rgba(16,185,129,0.06)] backdrop-blur-xl dark:border-white/[0.06] dark:bg-white/[0.04]">
                  <div className="mb-3 flex items-center justify-between gap-3">
                    <div>
                      <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-slate-400">Operational board</p>
                      <p className="mt-1 text-[18px] font-black tracking-tight text-slate-950 dark:text-white">
                        {mode === 'vehicles'
                          ? 'Fleet health and readiness'
                          : mode === 'tracking'
                            ? 'Movement and live location'
                            : mode === 'maintenance'
                              ? 'Maintenance queue'
                              : mode === 'fuel'
                                ? 'Fuel spend and anomaly review'
                                : 'Shipments, loads, and proof'}
                      </p>
                    </div>
                    <span className="rounded-full border border-emerald-200/70 bg-emerald-50 px-3 py-1 text-[10px] font-semibold uppercase tracking-[0.18em] text-emerald-600">
                      Backend live
                    </span>
                  </div>

                  <div className="space-y-3">
                    {mode === 'vehicles' ? vehicles.slice(0, 5).map((vehicle) => (
                      <ActionVehicleCard key={vehicle.id} vehicle={vehicle} saving={savingId === vehicle.id} onService={() => handleService(vehicle)} />
                    )) : mode === 'tracking' ? tracking.slice(0, 5).map((point) => (
                      <ActionTrackingCard key={point.id} point={point} />
                    )) : mode === 'maintenance' ? maintenance.slice(0, 5).map((ticket) => (
                      <ActionMaintenanceCard key={ticket.id} ticket={ticket} saving={savingId === ticket.id} onClose={() => handleCloseMaintenance(ticket)} />
                    )) : mode === 'fuel' ? fuel.slice(0, 5).map((eventRow) => (
                      <ActionFuelCard key={eventRow.id} eventRow={eventRow} saving={savingId === eventRow.id} onFlag={() => handleFlagFuel(eventRow)} />
                    )) : shipments.slice(0, 5).map((shipment) => (
                      <ActionShipmentCard key={shipment.id} shipment={shipment} saving={savingId === shipment.id} onDispatch={() => handleDispatch(shipment)} />
                    ))}
                  </div>
                </div>

                <div className="rounded-[28px] border border-white/80 bg-white/74 p-4 shadow-[0_18px_40px_rgba(16,185,129,0.06)] backdrop-blur-xl dark:border-white/[0.06] dark:bg-white/[0.04]">
                  <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-slate-400">Operational cues</p>
                  <div className="mt-3 space-y-3">
                    {exceptionRows.length ? exceptionRows.map((item) => (
                      <div
                        key={item.id}
                        className={`rounded-2xl border p-3 ${
                          item.tone === 'amber'
                            ? 'border-amber-200/60 bg-amber-50/70 dark:border-amber-400/10 dark:bg-amber-400/8'
                            : item.tone === 'orange'
                              ? 'border-orange-200/60 bg-orange-50/70 dark:border-orange-400/10 dark:bg-orange-400/8'
                              : 'border-sky-200/60 bg-sky-50/70 dark:border-sky-400/10 dark:bg-sky-400/8'
                        }`}
                      >
                        <div className="flex items-center justify-between gap-3">
                          <p className="text-[12px] font-bold text-slate-800 dark:text-slate-100">{item.label}</p>
                          <AlertTriangle className="h-4 w-4 text-slate-400" />
                        </div>
                        <p className="mt-1 text-[11px] leading-relaxed text-slate-500 dark:text-slate-400">{item.detail}</p>
                      </div>
                    )) : (
                      <div className="rounded-2xl border border-slate-200/70 bg-slate-50/70 p-4 text-sm text-slate-500 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-400">
                        No pressing exceptions. The fleet is running clean.
                      </div>
                    )}
                  </div>

                  <div className="mt-4 rounded-2xl border border-slate-200/70 bg-[linear-gradient(180deg,rgba(255,255,255,0.88),rgba(245,249,255,0.78))] p-4 dark:border-white/10 dark:bg-[linear-gradient(180deg,rgba(255,255,255,0.05),rgba(255,255,255,0.02))]">
                    <div className="flex items-center justify-between">
                      <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-slate-400">Live DB records</p>
                      <ArrowRight className="h-4 w-4 text-emerald-500/70" />
                    </div>
                    <div className="mt-3 space-y-2">
                      {liveRows.map((item) => (
                        <div key={item.id} className="flex items-center justify-between gap-3 rounded-xl bg-white/70 px-3 py-2 dark:bg-white/[0.04]">
                          <div className="min-w-0">
                            <p className="truncate text-[12px] font-semibold text-slate-800 dark:text-slate-200">{item.title}</p>
                            <p className="truncate text-[10px] text-slate-400 dark:text-slate-500">{item.subtitle}</p>
                          </div>
                          <span className="rounded-full bg-slate-100 px-2 py-1 text-[9px] font-semibold uppercase tracking-[0.18em] text-slate-500 dark:bg-white/[0.05] dark:text-slate-300">
                            {item.status}
                          </span>
                          <ChevronRight className="h-4 w-4 shrink-0 text-slate-300" />
                        </div>
                      ))}
                    </div>
                  </div>
                </div>
              </div>
            </div>

            <div className="rounded-[32px] border border-white/75 bg-[linear-gradient(180deg,rgba(10,16,28,0.96),rgba(5,10,18,0.92))] p-5 shadow-[0_24px_80px_rgba(0,0,0,0.32)] backdrop-blur-3xl">
              <div className="flex items-center justify-between gap-3">
                <div className="rounded-full border border-white/10 bg-white/5 px-3 py-1.5 text-[10px] font-semibold uppercase tracking-[0.22em] text-white/70">
                  {config.label}
                </div>
                <div className="rounded-full border border-emerald-500/15 bg-emerald-500/10 px-3 py-1.5 text-[10px] font-semibold uppercase tracking-[0.22em] text-emerald-300">
                  Connected backend
                </div>
              </div>

              <div className="mt-5">
                <h2 className="text-[28px] font-black leading-tight text-white xl:text-[34px]">
                  Built for fleet leaders who need confidence in the first glance.
                </h2>
                <p className="mt-3 max-w-xl text-[14px] leading-relaxed text-slate-300/80">
                  This is a working control room and a client-facing product story at the same time: the visuals are premium, the movement is subtle, and the data round-trips to the database.
                </p>
              </div>

              <div className="mt-6 grid gap-3">
                <div className="rounded-[24px] border border-white/10 bg-white/[0.04] p-4">
                  <div className="flex items-center justify-between">
                    <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-white/50">Load planning</p>
                    <MapPinned className="h-4 w-4 text-cyan-300/80" />
                  </div>
                  <div className="mt-3 space-y-3">
                    {overview?.loadPlanCards?.slice(0, 4).map((plan) => (
                      <div key={plan.routeCode} className="rounded-2xl border border-white/10 bg-white/[0.03] p-3">
                        <div className="flex items-center justify-between gap-3">
                          <div>
                            <p className="text-[12px] font-bold text-white">{plan.routeCode || 'Unassigned route'}</p>
                            <p className="text-[11px] text-slate-400">{plan.shipmentCount} shipments · {plan.highPriority} high priority</p>
                          </div>
                          <span className="text-[10px] font-bold text-cyan-300">{plan.totalWeightKg.toFixed(0)} kg</span>
                        </div>
                        <div className="mt-2 h-1.5 rounded-full bg-white/10">
                          <div className="h-1.5 rounded-full bg-gradient-to-r from-emerald-400 via-cyan-400 to-sky-500" style={{ width: `${Math.min(100, plan.shipmentCount * 22)}%` }} />
                        </div>
                      </div>
                    ))}
                  </div>
                </div>

                <div className="rounded-[24px] border border-white/10 bg-white/[0.04] p-4">
                  <div className="flex items-center justify-between">
                    <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-white/50">Fleet health</p>
                    <Truck className="h-4 w-4 text-emerald-300/80" />
                  </div>
                  <div className="mt-3 space-y-2">
                    {vehicles.slice(0, 4).map((vehicle) => (
                      <div key={vehicle.id} className="rounded-2xl border border-white/10 bg-white/[0.03] px-3 py-2.5">
                        <div className="flex items-center justify-between gap-3">
                          <div className="min-w-0">
                            <p className="truncate text-[12px] font-bold text-white">{vehicle.vehicleNumber}</p>
                            <p className="truncate text-[10px] text-slate-400">{vehicle.type} · {vehicle.lastKnownLocation}</p>
                          </div>
                          <span className="rounded-full border border-white/10 px-2 py-1 text-[9px] font-semibold uppercase tracking-[0.18em] text-white/65">
                            {vehicle.status}
                          </span>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              </div>

              <div className="mt-6 rounded-[26px] border border-white/10 bg-gradient-to-r from-emerald-500/12 via-cyan-400/10 to-transparent p-4">
                <div className="flex items-center gap-2">
                  <CheckCircle2 className="h-5 w-5 text-emerald-300" />
                  <p className="text-[11px] font-semibold uppercase tracking-[0.22em] text-white/70">Operationally honest</p>
                </div>
                <p className="mt-2 text-[13px] leading-relaxed text-slate-300/80">
                  Shipments, vehicles, tracking, maintenance, and fuel all use the live tenant database. The interface is designed to feel like a premium landing page without sacrificing the working workflow underneath.
                </p>
              </div>

              <div className="mt-4 rounded-[26px] border border-white/10 bg-white/[0.04] p-4">
                <div className="flex items-center justify-between">
                  <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-white/50">Signal summary</p>
                  <BadgeInfo className="h-4 w-4 text-white/50" />
                </div>
                <div className="mt-3 space-y-2 text-[12px] text-slate-300/80">
                  <p>On-trip vehicles: <span className="font-bold text-white">{overview?.summary.onTripVehicles ?? 0}</span></p>
                  <p>Delivered today: <span className="font-bold text-white">{overview?.summary.deliveredToday ?? 0}</span></p>
                  <p>Delivery rate: <span className="font-bold text-white">{deliveredRateText}</span></p>
                  <p>Average fuel level: <span className="font-bold text-white">{avgFuelLevelText}</span></p>
                </div>
              </div>
            </div>
          </section>
        </div>
      </div>
    </>
  );
}

function ActionShipmentCard({ shipment, onDispatch, saving }: { shipment: FleetShipment; onDispatch: () => void; saving: boolean }) {
  return (
    <div className="rounded-[24px] border border-slate-200/70 bg-[linear-gradient(180deg,rgba(255,255,255,0.92),rgba(245,248,255,0.78))] p-4 shadow-[0_10px_24px_rgba(16,185,129,0.05)] dark:border-white/10 dark:bg-white/[0.04]">
      <div className="flex items-start justify-between gap-3">
        <div>
          <p className="text-[12px] font-black tracking-tight text-slate-950 dark:text-white">{shipment.shipmentNumber}</p>
          <p className="text-[11px] text-slate-500 dark:text-slate-400">{shipment.customerName} · {shipment.destination}</p>
        </div>
        <span className="rounded-full border border-slate-200/70 bg-white px-2.5 py-1 text-[9px] font-semibold uppercase tracking-[0.18em] text-slate-500 dark:border-white/10 dark:bg-white/[0.05] dark:text-slate-300">
          {shipment.status}
        </span>
      </div>
      <div className="mt-3 flex flex-wrap items-center gap-2 text-[10px] font-semibold uppercase tracking-[0.18em] text-slate-400">
        <span>{shipment.vehicleNumber}</span>
        <span>•</span>
        <span>{shipment.priority}</span>
        <span>•</span>
        <span>{shipment.mode}</span>
      </div>
      <button type="button" onClick={onDispatch} disabled={saving} className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-2xl bg-gradient-to-r from-emerald-600 via-cyan-500 to-sky-400 px-4 py-3 text-[12px] font-bold text-white shadow-[0_14px_30px_rgba(16,185,129,0.26)] transition hover:brightness-105 disabled:cursor-not-allowed disabled:opacity-60">
        {saving ? 'Dispatching...' : 'Dispatch shipment'}
        <ArrowRight className="h-4 w-4" />
      </button>
    </div>
  );
}

function ActionVehicleCard({ vehicle, onService, saving }: { vehicle: FleetVehicle; onService: () => void; saving: boolean }) {
  return (
    <div className="rounded-[24px] border border-slate-200/70 bg-[linear-gradient(180deg,rgba(255,255,255,0.92),rgba(245,248,255,0.78))] p-4 shadow-[0_10px_24px_rgba(16,185,129,0.05)] dark:border-white/10 dark:bg-white/[0.04]">
      <div className="flex items-start justify-between gap-3">
        <div>
          <p className="text-[12px] font-black tracking-tight text-slate-950 dark:text-white">{vehicle.vehicleNumber}</p>
          <p className="text-[11px] text-slate-500 dark:text-slate-400">{vehicle.type} · {vehicle.driverName || 'Unassigned'}</p>
        </div>
        <span className="rounded-full border border-slate-200/70 bg-white px-2.5 py-1 text-[9px] font-semibold uppercase tracking-[0.18em] text-slate-500 dark:border-white/10 dark:bg-white/[0.05] dark:text-slate-300">
          {vehicle.status}
        </span>
      </div>
      <div className="mt-3 grid grid-cols-3 gap-2 text-[10px] text-slate-400">
        <span>{vehicle.currentLoadKg.toFixed(0)} kg</span>
        <span>{vehicle.fuelLevelPercent.toFixed(0)}% fuel</span>
        <span>{vehicle.healthStatus}</span>
      </div>
      <button type="button" onClick={onService} disabled={saving} className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-2xl bg-gradient-to-r from-sky-600 via-indigo-500 to-violet-400 px-4 py-3 text-[12px] font-bold text-white shadow-[0_14px_30px_rgba(16,185,129,0.26)] transition hover:brightness-105 disabled:cursor-not-allowed disabled:opacity-60">
        {saving ? 'Updating...' : 'Send to service'}
        <Wrench className="h-4 w-4" />
      </button>
    </div>
  );
}

function ActionTrackingCard({ point }: { point: FleetTrackingPoint }) {
  return (
    <div className="rounded-[24px] border border-slate-200/70 bg-[linear-gradient(180deg,rgba(255,255,255,0.92),rgba(245,248,255,0.78))] p-4 shadow-[0_10px_24px_rgba(16,185,129,0.05)] dark:border-white/10 dark:bg-white/[0.04]">
      <div className="flex items-start justify-between gap-3">
        <div>
          <p className="text-[12px] font-black tracking-tight text-slate-950 dark:text-white">{point.shipmentNumber}</p>
          <p className="text-[11px] text-slate-500 dark:text-slate-400">{point.locationLabel} · {point.geofenceName}</p>
        </div>
        <span className="rounded-full border border-slate-200/70 bg-white px-2.5 py-1 text-[9px] font-semibold uppercase tracking-[0.18em] text-slate-500 dark:border-white/10 dark:bg-white/[0.05] dark:text-slate-300">
          {point.status}
        </span>
      </div>
      <div className="mt-3 flex flex-wrap items-center gap-2 text-[10px] font-semibold uppercase tracking-[0.18em] text-slate-400">
        <span>{point.vehicleNumber}</span>
        <span>•</span>
        <span>{point.speedKph.toFixed(0)} km/h</span>
        <span>•</span>
        <span>{point.alertType || 'No alert'}</span>
      </div>
    </div>
  );
}

function ActionMaintenanceCard({ ticket, onClose, saving }: { ticket: FleetMaintenanceTicket; onClose: () => void; saving: boolean }) {
  return (
    <div className="rounded-[24px] border border-slate-200/70 bg-[linear-gradient(180deg,rgba(255,255,255,0.92),rgba(245,248,255,0.78))] p-4 shadow-[0_10px_24px_rgba(16,185,129,0.05)] dark:border-white/10 dark:bg-white/[0.04]">
      <div className="flex items-start justify-between gap-3">
        <div>
          <p className="text-[12px] font-black tracking-tight text-slate-950 dark:text-white">{ticket.workOrderNumber}</p>
          <p className="text-[11px] text-slate-500 dark:text-slate-400">{ticket.vehicleNumber} · {ticket.vendorName}</p>
        </div>
        <span className="rounded-full border border-slate-200/70 bg-white px-2.5 py-1 text-[9px] font-semibold uppercase tracking-[0.18em] text-slate-500 dark:border-white/10 dark:bg-white/[0.05] dark:text-slate-300">
          {ticket.status}
        </span>
      </div>
      <div className="mt-3 grid grid-cols-3 gap-2 text-[10px] text-slate-400">
        <span>{ticket.priority}</span>
        <span>{ticket.downtimeHours.toFixed(1)} hrs</span>
        <span>{ticket.estimatedCost.toFixed(0)} cost</span>
      </div>
      <button type="button" onClick={onClose} disabled={saving} className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-2xl bg-gradient-to-r from-orange-500 via-amber-500 to-yellow-400 px-4 py-3 text-[12px] font-bold text-white shadow-[0_14px_30px_rgba(16,185,129,0.26)] transition hover:brightness-105 disabled:cursor-not-allowed disabled:opacity-60">
        {saving ? 'Closing...' : 'Close work order'}
        <CheckCircle2 className="h-4 w-4" />
      </button>
    </div>
  );
}

function ActionFuelCard({ eventRow, onFlag, saving }: { eventRow: FleetFuelEvent; onFlag: () => void; saving: boolean }) {
  return (
    <div className="rounded-[24px] border border-slate-200/70 bg-[linear-gradient(180deg,rgba(255,255,255,0.92),rgba(245,248,255,0.78))] p-4 shadow-[0_10px_24px_rgba(16,185,129,0.05)] dark:border-white/10 dark:bg-white/[0.04]">
      <div className="flex items-start justify-between gap-3">
        <div>
          <p className="text-[12px] font-black tracking-tight text-slate-950 dark:text-white">{eventRow.vehicleNumber}</p>
          <p className="text-[11px] text-slate-500 dark:text-slate-400">{eventRow.stationName} · {eventRow.city}</p>
        </div>
        <span className={`rounded-full border px-2.5 py-1 text-[9px] font-semibold uppercase tracking-[0.18em] ${
          eventRow.anomalyFlag
            ? 'border-amber-200/70 bg-amber-50 text-amber-700 dark:border-amber-400/10 dark:bg-amber-400/8 dark:text-amber-300'
            : 'border-slate-200/70 bg-white text-slate-500 dark:border-white/10 dark:bg-white/[0.05] dark:text-slate-300'
        }`}>
          {eventRow.anomalyFlag ? 'Review' : eventRow.eventType}
        </span>
      </div>
      <div className="mt-3 grid grid-cols-3 gap-2 text-[10px] text-slate-400">
        <span>{eventRow.liters.toFixed(0)} L</span>
        <span>{eventRow.cost.toFixed(0)} cost</span>
        <span>{eventRow.fuelCardNumber}</span>
      </div>
      <button type="button" onClick={onFlag} disabled={saving || eventRow.anomalyFlag} className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-2xl bg-gradient-to-r from-emerald-600 via-cyan-500 to-sky-400 px-4 py-3 text-[12px] font-bold text-white shadow-[0_14px_30px_rgba(16,185,129,0.26)] transition hover:brightness-105 disabled:cursor-not-allowed disabled:opacity-60">
        {saving ? 'Flagging...' : eventRow.anomalyFlag ? 'Flagged for review' : 'Flag anomaly'}
        <Fuel className="h-4 w-4" />
      </button>
    </div>
  );
}
