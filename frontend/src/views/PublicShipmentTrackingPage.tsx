'use client';

import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { AlertTriangle, CheckCircle2, MapPinned, Package, ShieldCheck, Sparkles, Truck } from 'lucide-react';
import { publicTrackingApi, type PublicTrackingSummary } from '../api/fleet';

interface PublicShipmentTrackingPageProps {
  token: string;
}

function formatDate(value?: string | null) {
  if (!value) return 'Pending';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return 'Pending';
  return new Intl.DateTimeFormat('en', {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(date);
}

export function PublicShipmentTrackingPage({ token }: PublicShipmentTrackingPageProps) {
  const [summary, setSummary] = useState<PublicTrackingSummary | null>(null);
  const [events, setEvents] = useState<PublicTrackingSummary['publicEvents']>([]);
  const [pod, setPod] = useState<PublicTrackingSummary['pod']>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    (async () => {
      try {
        const [track, eventRes, podRes] = await Promise.all([
          publicTrackingApi.track(token),
          publicTrackingApi.events(token),
          publicTrackingApi.pod(token),
        ]);
        if (cancelled) return;
        setSummary(track);
        setEvents(eventRes.items);
        setPod(podRes.items);
      } catch {
        if (!cancelled) setError('This tracking link is unavailable, expired, or revoked.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [token]);

  const eta = useMemo(() => {
    if (!summary) return 'Pending';
    const nextStop = summary.stops.find((stop) => !stop.completedAt) ?? summary.stops[summary.stops.length - 1];
    return formatDate(nextStop?.plannedArrivalAt ?? summary.pickupScheduledAtUtc ?? null);
  }, [summary]);

  return (
    <div className="relative min-h-screen overflow-hidden bg-[radial-gradient(circle_at_top_left,rgba(59,130,246,0.12),transparent_26%),radial-gradient(circle_at_80%_20%,rgba(16,185,129,0.12),transparent_24%),linear-gradient(180deg,#f5f8ff,#eef4ff_40%,#f8fbff)] text-slate-900">
      <div className="pointer-events-none absolute inset-0 opacity-60 [background-image:linear-gradient(rgba(59,130,246,0.05)_1px,transparent_1px),linear-gradient(90deg,rgba(59,130,246,0.05)_1px,transparent_1px)] [background-size:82px_82px]" />
      <div className="pointer-events-none absolute left-[-3rem] top-28 h-72 w-72 rounded-full bg-cyan-300/30 blur-3xl animate-pulse" />
      <div className="pointer-events-none absolute right-[-2rem] top-48 h-80 w-80 rounded-full bg-emerald-300/25 blur-3xl animate-pulse" />

      <div className="relative mx-auto flex min-h-screen w-full max-w-6xl flex-col px-4 py-6 sm:px-6 lg:px-8">
        <div className="mb-6 flex items-center justify-between rounded-[28px] border border-white/70 bg-white/80 px-5 py-4 shadow-[0_18px_40px_rgba(15,23,42,0.08)] backdrop-blur-xl">
          <div>
            <p className="text-[10px] font-bold uppercase tracking-[0.28em] text-cyan-600/70">Public shipment tracking</p>
            <h1 className="mt-1 text-[28px] font-black tracking-tight text-slate-950">Track shipment progress without exposing internal operations.</h1>
          </div>
          <div className="hidden items-center gap-2 rounded-full border border-emerald-200/70 bg-emerald-50 px-3 py-1.5 text-[10px] font-bold uppercase tracking-[0.18em] text-emerald-700 md:flex">
            <ShieldCheck className="h-4 w-4" />
            Customer-safe view
          </div>
        </div>

        {loading ? (
          <div className="grid gap-4 lg:grid-cols-[1.05fr_0.95fr]">
            <div className="rounded-[30px] border border-white/70 bg-white/85 p-6 shadow-[0_18px_40px_rgba(15,23,42,0.08)] backdrop-blur-xl">
              <div className="h-5 w-40 animate-pulse rounded-full bg-slate-200/80" />
              <div className="mt-4 h-12 w-72 animate-pulse rounded-2xl bg-slate-200/70" />
              <div className="mt-6 space-y-3">
                <div className="h-24 animate-pulse rounded-[22px] bg-slate-100" />
                <div className="h-24 animate-pulse rounded-[22px] bg-slate-100" />
                <div className="h-24 animate-pulse rounded-[22px] bg-slate-100" />
              </div>
            </div>
            <div className="rounded-[30px] border border-white/70 bg-white/85 p-6 shadow-[0_18px_40px_rgba(15,23,42,0.08)] backdrop-blur-xl">
              <div className="h-5 w-48 animate-pulse rounded-full bg-slate-200/80" />
              <div className="mt-4 space-y-3">
                <div className="h-28 animate-pulse rounded-[22px] bg-slate-100" />
                <div className="h-28 animate-pulse rounded-[22px] bg-slate-100" />
                <div className="h-28 animate-pulse rounded-[22px] bg-slate-100" />
              </div>
            </div>
          </div>
        ) : error ? (
          <div className="rounded-[30px] border border-amber-200/70 bg-amber-50/90 p-6 text-amber-900 shadow-[0_18px_40px_rgba(15,23,42,0.08)]">
            <div className="flex items-center gap-2">
              <AlertTriangle className="h-5 w-5" />
              <p className="text-lg font-bold">Tracking unavailable</p>
            </div>
            <p className="mt-2 text-sm leading-relaxed">{error}</p>
          </div>
        ) : summary ? (
          <div className="grid gap-4 lg:grid-cols-[1.05fr_0.95fr]">
            <section className="rounded-[30px] border border-white/70 bg-white/85 p-6 shadow-[0_18px_40px_rgba(15,23,42,0.08)] backdrop-blur-xl">
              <div className="flex items-center gap-2">
                <span className="rounded-full border border-cyan-200/70 bg-cyan-50 px-3 py-1 text-[10px] font-bold uppercase tracking-[0.18em] text-cyan-700">{summary.shipmentNumber}</span>
                <span className="rounded-full border border-emerald-200/70 bg-emerald-50 px-3 py-1 text-[10px] font-bold uppercase tracking-[0.18em] text-emerald-700">{summary.status}</span>
              </div>
              <h2 className="mt-4 text-[30px] font-black tracking-tight text-slate-950">{summary.origin} to {summary.destination}</h2>
              <p className="mt-2 max-w-2xl text-[15px] leading-relaxed text-slate-600">
                This view is intentionally limited to what a customer should see: shipment progress, ETA, public events, and proof of delivery if it has been shared.
              </p>

              <div className="mt-5 grid gap-3 sm:grid-cols-3">
                <InfoCard label="Reference" value={summary.shipmentNumber} icon={<Package className="h-4 w-4 text-cyan-500" />} />
                <InfoCard label="Status" value={summary.status} icon={<Truck className="h-4 w-4 text-emerald-500" />} />
                <InfoCard label="ETA" value={eta} icon={<MapPinned className="h-4 w-4 text-violet-500" />} />
              </div>

              <div className="mt-6">
                <div className="mb-3 flex items-center justify-between">
                  <div>
                    <p className="text-[10px] font-bold uppercase tracking-[0.24em] text-slate-400">Stop timeline</p>
                    <h3 className="mt-1 text-[18px] font-black tracking-tight text-slate-950">Milestones the customer can follow</h3>
                  </div>
                  <Sparkles className="h-5 w-5 text-cyan-500" />
                </div>
                <div className="space-y-3">
                  {summary.stops.map((stop) => (
                    <div key={`${stop.sequenceNo}-${stop.locationName}`} className="rounded-[22px] border border-slate-200/70 bg-slate-50/80 p-4">
                      <div className="flex items-start justify-between gap-3">
                        <div>
                          <p className="text-[12px] font-black text-slate-950">{stop.sequenceNo}. {stop.locationName}</p>
                          <p className="text-[11px] text-slate-500">{stop.stopType} · {stop.city} · {stop.status}</p>
                        </div>
                        <span className="rounded-full border border-slate-200/80 px-2.5 py-1 text-[9px] font-bold uppercase tracking-[0.18em] text-slate-500">
                          {formatDate(stop.plannedArrivalAt)}
                        </span>
                      </div>
                      <div className="mt-3 h-1.5 rounded-full bg-slate-200">
                        <div
                          className="h-1.5 rounded-full bg-gradient-to-r from-cyan-500 via-sky-500 to-emerald-400"
                          style={{ width: stop.completedAt ? '100%' : stop.actualArrivalAt ? '72%' : '38%' }}
                        />
                      </div>
                    </div>
                  ))}
                </div>
              </div>
            </section>

            <aside className="space-y-4">
              <section className="rounded-[30px] border border-white/70 bg-white/85 p-6 shadow-[0_18px_40px_rgba(15,23,42,0.08)] backdrop-blur-xl">
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-[10px] font-bold uppercase tracking-[0.24em] text-slate-400">Public events</p>
                    <h3 className="mt-1 text-[18px] font-black tracking-tight text-slate-950">Shipment activity feed</h3>
                  </div>
                  <CheckCircle2 className="h-5 w-5 text-emerald-500" />
                </div>
                <div className="mt-4 space-y-2">
                  {events.map((event) => (
                    <div key={`${event.eventType}-${event.occurredAtUtc}`} className="rounded-[20px] border border-slate-200/70 bg-slate-50/80 p-3">
                      <p className="text-[12px] font-bold text-slate-900">{event.eventType}</p>
                      <p className="mt-1 text-[11px] leading-relaxed text-slate-500">{event.message}</p>
                      <p className="mt-2 text-[10px] uppercase tracking-[0.18em] text-slate-400">{formatDate(event.occurredAtUtc)}</p>
                    </div>
                  ))}
                  {!events.length && <p className="rounded-[20px] border border-dashed border-slate-200/70 bg-slate-50/70 p-4 text-sm text-slate-500">No public events have been shared yet.</p>}
                </div>
              </section>

              <section className="rounded-[30px] border border-white/70 bg-white/85 p-6 shadow-[0_18px_40px_rgba(15,23,42,0.08)] backdrop-blur-xl">
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-[10px] font-bold uppercase tracking-[0.24em] text-slate-400">Proof of delivery</p>
                    <h3 className="mt-1 text-[18px] font-black tracking-tight text-slate-950">Status and available proof</h3>
                  </div>
                  <ShieldCheck className="h-5 w-5 text-cyan-500" />
                </div>
                <div className="mt-4 space-y-3">
                  {pod.map((item) => (
                    <div key={`${item.recipientName}-${item.capturedAt}`} className="rounded-[20px] border border-slate-200/70 bg-slate-50/80 p-4">
                      <div className="flex items-start justify-between gap-3">
                        <div>
                          <p className="text-[12px] font-black text-slate-900">{item.recipientName}</p>
                          <p className="text-[11px] text-slate-500">{item.deliveryCondition} · {item.status}</p>
                        </div>
                        <span className="rounded-full border border-slate-200/80 px-2.5 py-1 text-[9px] font-bold uppercase tracking-[0.18em] text-slate-500">
                          {formatDate(item.capturedAt)}
                        </span>
                      </div>
                      <div className="mt-3 grid gap-2 text-[11px] text-slate-500">
                        <p>Signature: {item.signatureUrl ? 'Available' : 'Not shared'}</p>
                        <p>Photo: {item.photoUrl ? 'Available' : 'Not shared'}</p>
                        <p>Document: {item.documentUrl ? 'Available' : 'Not shared'}</p>
                        <p>Verified: {item.verifiedAt ? formatDate(item.verifiedAt) : 'Pending'}</p>
                      </div>
                    </div>
                  ))}
                  {!pod.length && <p className="rounded-[20px] border border-dashed border-slate-200/70 bg-slate-50/70 p-4 text-sm text-slate-500">Proof has not been shared yet.</p>}
                </div>
              </section>
            </aside>
          </div>
        ) : null}
      </div>
    </div>
  );
}

function InfoCard({ label, value, icon }: { label: string; value: string; icon: ReactNode }) {
  return (
    <div className="rounded-[22px] border border-slate-200/70 bg-[linear-gradient(180deg,rgba(255,255,255,0.95),rgba(243,247,255,0.75))] p-4">
      <div className="flex items-center justify-between">
        <p className="text-[10px] font-bold uppercase tracking-[0.24em] text-slate-400">{label}</p>
        {icon}
      </div>
      <p className="mt-3 text-[14px] font-bold text-slate-900">{value}</p>
    </div>
  );
}
