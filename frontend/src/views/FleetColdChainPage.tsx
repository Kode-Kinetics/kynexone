'use client';

import { useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { BellRing, BatteryCharging, FlaskConical, Gauge, Layers3, Thermometer, Truck } from 'lucide-react';
import { Logo } from '../components/Logo';
import { notifyApiError } from '../api/client';
import { fleetApi, fleetColdChainApi, type ColdChainReport, type TemperatureAlert, type TemperatureDevice, type TemperatureZone } from '../api/fleet';

type SummaryState = {
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
};

export function FleetColdChainPage() {
  const [summary, setSummary] = useState<SummaryState | null>(null);
  const [devices, setDevices] = useState<TemperatureDevice[]>([]);
  const [alerts, setAlerts] = useState<TemperatureAlert[]>([]);
  const [shipments, setShipments] = useState<Array<{ id: string; shipmentNumber: string; status: string; customerName: string; mode: string }>>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [selectedShipmentId, setSelectedShipmentId] = useState('');
  const [selectedZoneId, setSelectedZoneId] = useState('');
  const [form, setForm] = useState({
    deviceCode: '',
    name: '',
    vehicleNumber: '',
    temperature: '4.0',
    batteryPercent: '92',
    notes: '',
    readingNotes: '',
    alertNotes: '',
  });

  const refresh = async () => {
    const [summaryRes, devicesRes, alertsRes, shipmentsRes] = await Promise.all([
      fleetColdChainApi.summary(),
      fleetColdChainApi.devices(),
      fleetColdChainApi.alerts(),
      fleetApi.shipments({ pageSize: 8 }),
    ]);
    setSummary(summaryRes);
    setDevices(devicesRes.items);
    setAlerts(alertsRes.items);
    setShipments(shipmentsRes.items as Array<{ id: string; shipmentNumber: string; status: string; customerName: string; mode: string }>);
    setSelectedZoneId(summaryRes.zones[0]?.id ?? '');
    if (!selectedShipmentId && shipmentsRes.items[0]) {
      setSelectedShipmentId(shipmentsRes.items[0].id);
    }
  };

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    (async () => {
      try {
        const [summaryRes, devicesRes, alertsRes, shipmentsRes] = await Promise.all([
          fleetColdChainApi.summary(),
          fleetColdChainApi.devices(),
          fleetColdChainApi.alerts(),
          fleetApi.shipments({ pageSize: 8 }),
        ]);
        if (cancelled) return;
        setSummary(summaryRes);
        setDevices(devicesRes.items);
        setAlerts(alertsRes.items);
        setShipments(shipmentsRes.items as Array<{ id: string; shipmentNumber: string; status: string; customerName: string; mode: string }>);
        setSelectedZoneId(summaryRes.zones[0]?.id ?? '');
        setSelectedShipmentId(shipmentsRes.items[0]?.id ?? '');
      } catch (err) {
        if (!cancelled) notifyApiError(err, 'Unable to load cold-chain workspace.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const metrics = useMemo(() => {
    if (!summary) return [];
    return [
      { label: 'Active devices', value: summary.summary.activeDevices, icon: Thermometer },
      { label: 'Readings today', value: summary.summary.readingsToday, icon: Gauge },
      { label: 'Open alerts', value: summary.summary.openAlerts, icon: BellRing },
      { label: 'Compliance', value: `${summary.summary.compliancePercent}%`, icon: Layers3 },
    ];
  }, [summary]);

  const createDevice = async () => {
    if (!selectedZoneId) return;
    setSaving(true);
    try {
      await fleetColdChainApi.createDevice({
        deviceCode: form.deviceCode,
        name: form.name,
        zoneId: selectedZoneId,
        shipmentId: selectedShipmentId || undefined,
        vehicleNumber: form.vehicleNumber,
        status: 'Active',
        lastReportedTemperatureCelsius: Number(form.temperature),
        batteryPercent: Number(form.batteryPercent),
        notes: form.notes,
      });
      setForm((current) => ({ ...current, deviceCode: '', name: '', vehicleNumber: '', notes: '' }));
      await refresh();
    } catch (err) {
      notifyApiError(err, 'Unable to create device.');
    } finally {
      setSaving(false);
    }
  };

  const logReading = async (deviceId: string) => {
    const device = devices.find((item) => item.id === deviceId);
    if (!device) return;
    try {
      await fleetColdChainApi.createReading({
        deviceId,
        shipmentId: selectedShipmentId || device.shipmentId || undefined,
        zoneId: selectedZoneId || device.zoneId || undefined,
        temperatureCelsius: Number(form.temperature),
        humidityPercent: 51,
        source: 'Sensor',
        status: 'Normal',
        notes: form.readingNotes || 'Manual telemetry sample created from the cold-chain console.',
      });
      await refresh();
    } catch (err) {
      notifyApiError(err, 'Unable to log temperature reading.');
    }
  };

  const resolveAlert = async (alertId: string) => {
    try {
      await fleetColdChainApi.resolveAlert(alertId, { resolutionNotes: form.alertNotes || 'Reviewed by operations.' });
      await refresh();
    } catch (err) {
      notifyApiError(err, 'Unable to resolve alert.');
    }
  };

  const generateReport = async (shipmentId: string) => {
    try {
      await fleetColdChainApi.report(shipmentId);
      await refresh();
    } catch (err) {
      notifyApiError(err, 'Unable to generate report.');
    }
  };

  if (loading || !summary) {
    return <div className="min-h-screen bg-slate-950 text-white" />;
  }

  return (
    <main className="relative min-h-screen overflow-hidden bg-[radial-gradient(circle_at_top_left,_rgba(59,130,246,0.25),_transparent_42%),radial-gradient(circle_at_80%_10%,_rgba(34,211,238,0.18),_transparent_28%),linear-gradient(135deg,_#f8fbff_0%,_#ecf5ff_55%,_#e9f0ff_100%)] text-slate-900">
      <div className="pointer-events-none absolute inset-0 overflow-hidden">
        <div className="absolute left-[-6rem] top-24 h-72 w-72 rounded-full bg-cyan-300/30 blur-3xl animate-pulse" />
        <div className="absolute right-0 top-14 h-80 w-80 rounded-full bg-blue-400/25 blur-3xl animate-pulse [animation-delay:1.5s]" />
        <div className="absolute bottom-0 left-1/4 h-64 w-64 rounded-full bg-sky-300/25 blur-3xl animate-pulse [animation-delay:3s]" />
      </div>

      <section className="relative mx-auto flex w-full max-w-7xl flex-col gap-6 px-6 py-8 lg:px-10">
        <div className="flex flex-wrap items-center justify-between gap-4">
          <Logo size="lg" />
          <Link href="/fleet" className="rounded-full border border-slate-200/80 bg-white/70 px-4 py-2 text-sm font-semibold text-slate-700 shadow-sm backdrop-blur">
            Back to Fleet Command
          </Link>
        </div>

        <div className="grid gap-6 lg:grid-cols-[1.15fr_0.85fr]">
          <div className="space-y-6">
            <div className="rounded-[30px] border border-white/70 bg-white/55 p-7 shadow-[0_30px_80px_rgba(37,99,235,0.14)] backdrop-blur-xl">
              <div className="inline-flex items-center gap-2 rounded-full border border-cyan-200 bg-cyan-50 px-4 py-1.5 text-xs font-bold uppercase tracking-[0.26em] text-cyan-700">
                <FlaskConical className="h-3.5 w-3.5" />
                Cold-chain control room
              </div>
              <h1 className="mt-5 max-w-3xl text-4xl font-black tracking-tight text-slate-950 md:text-6xl">
                Temperature-sensitive freight should feel governed, live, and calm.
              </h1>
              <p className="mt-4 max-w-2xl text-base leading-7 text-slate-600 md:text-lg">
                Track zones, devices, alerts, and shipment compliance from one operational surface. This page is tied to real tenant data, seeded telemetry, and reviewable temperature breach history.
              </p>
              <div className="mt-6 flex flex-wrap gap-3 text-xs font-semibold text-slate-600">
                {['Live telemetry', 'Breach alerts', 'Zone control', 'Audit trail'].map((tag) => (
                  <span key={tag} className="rounded-full border border-slate-200 bg-white/80 px-3 py-1.5 shadow-sm">
                    {tag}
                  </span>
                ))}
              </div>
            </div>

            <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
              {metrics.map((metric) => {
                const Icon = metric.icon;
                return (
                  <article key={metric.label} className="rounded-3xl border border-white/80 bg-white/75 p-5 shadow-lg backdrop-blur">
                    <div className="flex items-center justify-between">
                      <p className="text-xs font-bold uppercase tracking-[0.24em] text-slate-500">{metric.label}</p>
                      <Icon className="h-4 w-4 text-cyan-600" />
                    </div>
                    <p className="mt-4 text-3xl font-black tracking-tight text-slate-950">{metric.value}</p>
                  </article>
                );
              })}
            </div>

            <div className="grid gap-6 xl:grid-cols-[1fr_0.95fr]">
              <section className="rounded-[28px] border border-white/75 bg-white/75 p-6 shadow-[0_24px_50px_rgba(15,23,42,0.08)] backdrop-blur">
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-xs font-bold uppercase tracking-[0.24em] text-slate-500">Temperature devices</p>
                    <h2 className="mt-2 text-2xl font-black text-slate-950">Operational sensors</h2>
                  </div>
                  <BatteryCharging className="h-5 w-5 text-emerald-500" />
                </div>
                <div className="mt-5 space-y-3">
                  {devices.slice(0, 5).map((device) => (
                    <div key={device.id} className="rounded-2xl border border-slate-200/80 bg-white/80 p-4">
                      <div className="flex flex-wrap items-center justify-between gap-2">
                        <div>
                          <p className="font-bold text-slate-950">{device.deviceCode} · {device.name}</p>
                          <p className="text-sm text-slate-500">{device.zoneCode || 'Unzoned'} · {device.vehicleNumber || 'No vehicle linked'}</p>
                        </div>
                        <span className="rounded-full bg-cyan-50 px-3 py-1 text-xs font-bold text-cyan-700">{device.status}</span>
                      </div>
                      <div className="mt-3 grid grid-cols-3 gap-3 text-sm text-slate-600">
                        <div>
                          <p className="text-[11px] uppercase tracking-[0.2em] text-slate-400">Temp</p>
                          <p className="font-bold text-slate-900">{device.lastReportedTemperatureCelsius.toFixed(1)}°C</p>
                        </div>
                        <div>
                          <p className="text-[11px] uppercase tracking-[0.2em] text-slate-400">Battery</p>
                          <p className="font-bold text-slate-900">{device.batteryPercent.toFixed(0)}%</p>
                        </div>
                        <button onClick={() => logReading(device.id)} className="rounded-full bg-slate-950 px-3 py-2 text-xs font-bold text-white transition hover:bg-slate-800">
                          Log reading
                        </button>
                      </div>
                    </div>
                  ))}
                </div>
              </section>

              <section className="rounded-[28px] border border-white/75 bg-white/75 p-6 shadow-[0_24px_50px_rgba(15,23,42,0.08)] backdrop-blur">
                <p className="text-xs font-bold uppercase tracking-[0.24em] text-slate-500">Control inputs</p>
                <h2 className="mt-2 text-2xl font-black text-slate-950">Add device and sample reading</h2>
                <div className="mt-5 space-y-4">
                  <div className="grid gap-4 sm:grid-cols-2">
                    <input value={form.deviceCode} onChange={(e) => setForm((current) => ({ ...current, deviceCode: e.target.value }))} placeholder="Device code" className="rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none transition focus:border-cyan-400" />
                    <input value={form.name} onChange={(e) => setForm((current) => ({ ...current, name: e.target.value }))} placeholder="Device name" className="rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none transition focus:border-cyan-400" />
                    <input value={form.vehicleNumber} onChange={(e) => setForm((current) => ({ ...current, vehicleNumber: e.target.value }))} placeholder="Vehicle number" className="rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none transition focus:border-cyan-400" />
                    <select value={selectedZoneId} onChange={(e) => setSelectedZoneId(e.target.value)} className="rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none transition focus:border-cyan-400">
                      {summary.zones.map((zone) => <option key={zone.id} value={zone.id}>{zone.name}</option>)}
                    </select>
                    <input value={form.temperature} onChange={(e) => setForm((current) => ({ ...current, temperature: e.target.value }))} placeholder="Temperature °C" className="rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none transition focus:border-cyan-400" />
                    <input value={form.batteryPercent} onChange={(e) => setForm((current) => ({ ...current, batteryPercent: e.target.value }))} placeholder="Battery %" className="rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none transition focus:border-cyan-400" />
                  </div>
                  <textarea value={form.notes} onChange={(e) => setForm((current) => ({ ...current, notes: e.target.value }))} rows={3} placeholder="Device notes" className="w-full rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none transition focus:border-cyan-400" />
                  <textarea value={form.readingNotes} onChange={(e) => setForm((current) => ({ ...current, readingNotes: e.target.value }))} rows={3} placeholder="Reading notes" className="w-full rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none transition focus:border-cyan-400" />
                  <button disabled={saving} onClick={createDevice} className="inline-flex w-full items-center justify-center rounded-2xl bg-gradient-to-r from-cyan-600 to-blue-600 px-4 py-3 font-bold text-white shadow-lg transition hover:from-cyan-500 hover:to-blue-500 disabled:opacity-60">
                    {saving ? 'Saving...' : 'Create device'}
                  </button>
                </div>
              </section>
            </div>
          </div>

          <aside className="space-y-6">
            <section className="rounded-[28px] border border-white/75 bg-slate-950/95 p-6 text-white shadow-[0_28px_60px_rgba(15,23,42,0.32)]">
              <div className="flex items-center justify-between">
                <div>
                  <p className="text-xs font-bold uppercase tracking-[0.24em] text-cyan-200/70">Zones</p>
                  <h2 className="mt-2 text-2xl font-black">Temperature bands</h2>
                </div>
                <Truck className="h-5 w-5 text-cyan-300" />
              </div>
              <div className="mt-5 space-y-3">
                {summary.zones.map((zone) => (
                  <div key={zone.id} className="rounded-2xl border border-white/10 bg-white/5 p-4">
                    <div className="flex items-center justify-between gap-3">
                      <div>
                        <p className="font-bold">{zone.name}</p>
                        <p className="text-sm text-slate-300">{zone.code} · {zone.minCelsius}°C to {zone.maxCelsius}°C</p>
                      </div>
                      <span className="h-3 w-3 rounded-full" style={{ backgroundColor: zone.color }} />
                    </div>
                    <p className="mt-2 text-sm text-slate-400">{zone.notes}</p>
                  </div>
                ))}
              </div>
            </section>

            <section className="rounded-[28px] border border-white/75 bg-white/80 p-6 shadow-[0_24px_50px_rgba(15,23,42,0.08)] backdrop-blur">
              <div className="flex items-center justify-between">
                <div>
                  <p className="text-xs font-bold uppercase tracking-[0.24em] text-slate-500">Alerts</p>
                  <h2 className="mt-2 text-2xl font-black text-slate-950">Open breaches</h2>
                </div>
                <BellRing className="h-5 w-5 text-rose-500" />
              </div>
              <div className="mt-5 space-y-3">
                {alerts.slice(0, 4).map((alert) => (
                  <div key={alert.id} className="rounded-2xl border border-slate-200 bg-white p-4">
                    <div className="flex items-center justify-between gap-2">
                      <div>
                        <p className="font-bold text-slate-950">{alert.alertType}</p>
                        <p className="text-sm text-slate-500">{alert.deviceCode || 'Sensor'} · {alert.shipmentNumber || 'No shipment'}</p>
                      </div>
                      <span className="rounded-full bg-rose-50 px-3 py-1 text-xs font-bold text-rose-700">{alert.severity}</span>
                    </div>
                    <p className="mt-2 text-sm text-slate-600">Measured {alert.measuredTemperature.toFixed(1)}°C against {alert.thresholdMin.toFixed(1)}°C to {alert.thresholdMax.toFixed(1)}°C.</p>
                    <div className="mt-3 flex items-center justify-between gap-3">
                      <input value={form.alertNotes} onChange={(e) => setForm((current) => ({ ...current, alertNotes: e.target.value }))} placeholder="Resolution notes" className="min-w-0 flex-1 rounded-full border border-slate-200 bg-white px-3 py-2 text-sm outline-none" />
                      <button onClick={() => resolveAlert(alert.id)} className="rounded-full bg-slate-950 px-3 py-2 text-xs font-bold text-white transition hover:bg-slate-800">
                        Resolve
                      </button>
                    </div>
                  </div>
                ))}
              </div>
            </section>

            <section className="rounded-[28px] border border-white/75 bg-white/80 p-6 shadow-[0_24px_50px_rgba(15,23,42,0.08)] backdrop-blur">
              <div className="flex items-center justify-between">
                <div>
                  <p className="text-xs font-bold uppercase tracking-[0.24em] text-slate-500">Reports</p>
                  <h2 className="mt-2 text-2xl font-black text-slate-950">Shipment compliance</h2>
                </div>
                <Layers3 className="h-5 w-5 text-cyan-600" />
              </div>
              <div className="mt-5 space-y-3">
                {shipments.slice(0, 4).map((shipment) => (
                  <button key={shipment.id} onClick={() => generateReport(shipment.id)} className="w-full rounded-2xl border border-slate-200 bg-gradient-to-r from-white to-slate-50 p-4 text-left transition hover:border-cyan-300 hover:shadow-md">
                    <div className="flex items-center justify-between">
                      <p className="font-bold text-slate-950">{shipment.shipmentNumber}</p>
                      <span className="text-xs font-bold text-cyan-700">{shipment.mode}</span>
                    </div>
                    <p className="mt-1 text-sm text-slate-500">{shipment.customerName} · {shipment.status}</p>
                  </button>
                ))}
              </div>
            </section>
          </aside>
        </div>
      </section>
    </main>
  );
}

export default FleetColdChainPage;
