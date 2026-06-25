'use client';

import { useEffect, useMemo, useState } from 'react';
import {
  ArrowRight,
  CheckCircle2,
  ClipboardList,
  FileCheck2,
  Link2,
  MapPinned,
  Package,
  RefreshCcw,
  Route,
  ShieldCheck,
  Truck,
  UserRound,
  X,
} from 'lucide-react';
import { notifyApiError } from '@/src/api/client';
import { type Carrier, type CustomerTrackingLink, type DriverTask, type FleetShipment, fleetCommercialApi, fleetLifecycleApi, type ProofOfDelivery, type ShipmentEvent, type ShipmentStop } from '@/src/api/fleet';
import { SaudiAddressFields } from './SaudiAddressFields';

interface ShipmentLifecycleDrawerProps {
  shipment: FleetShipment;
  onClose: () => void;
}

const blankStop = {
  stopType: 'Pickup',
  sequenceNo: 1,
  locationName: '',
  contactName: '',
  contactPhone: '',
  addressLine1: '',
  addressLine2: '',
  city: '',
  region: '',
  postalCode: '',
  country: 'Saudi Arabia',
  saudiNationalAddressBuildingNo: '',
  saudiNationalAddressAdditionalNo: '',
  saudiNationalAddressDistrict: '',
  latitude: '',
  longitude: '',
  plannedArrivalAt: '',
  notes: '',
};

const blankPod = {
  stopId: '',
  recipientName: '',
  recipientPhone: '',
  signatureUrl: '',
  photoUrl: '',
  documentUrl: '',
  notes: '',
  deliveryCondition: 'Good',
  capturedLatitude: '',
  capturedLongitude: '',
};

function toLocalDateTime(value: string | undefined | null) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function toDateTimeString(value: string) {
  const date = new Date(value);
  return new Intl.DateTimeFormat('en', {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(date);
}

export function ShipmentLifecycleDrawer({ shipment, onClose }: ShipmentLifecycleDrawerProps) {
  const [stops, setStops] = useState<ShipmentStop[]>([]);
  const [pods, setPods] = useState<ProofOfDelivery[]>([]);
  const [events, setEvents] = useState<ShipmentEvent[]>([]);
  const [links, setLinks] = useState<CustomerTrackingLink[]>([]);
  const [tasks, setTasks] = useState<DriverTask[]>([]);
  const [carriers, setCarriers] = useState<Carrier[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const [stopForm, setStopForm] = useState(blankStop);
  const [podForm, setPodForm] = useState(blankPod);
  const [trackingDays, setTrackingDays] = useState('7');
  const [trackingLink, setTrackingLink] = useState<CustomerTrackingLink | null>(null);
  const [copiedToken, setCopiedToken] = useState<string | null>(null);
  const [noteDraft, setNoteDraft] = useState('');
  const [carrierId, setCarrierId] = useState('');
  const [quotedAmount, setQuotedAmount] = useState('');
  const [agreedAmount, setAgreedAmount] = useState('');

  const load = async () => {
    setLoading(true);
    try {
      const [stopRes, podRes, eventRes, linkRes, taskRes, carrierRes] = await Promise.all([
        fleetLifecycleApi.getStops(shipment.id),
        fleetLifecycleApi.getPod(shipment.id),
        fleetLifecycleApi.getShipmentEvents(shipment.id),
        fleetLifecycleApi.getTrackingLinks(shipment.id),
        fleetLifecycleApi.getDriverTasks({ driverName: shipment.driverName || undefined }),
        fleetCommercialApi.carriers().catch(() => ({ items: [] as Carrier[] })),
      ]);

      setStops(stopRes.items);
      setPods(podRes.items);
      setTasks(taskRes.items.filter((task) => task.shipmentId === shipment.id));
      setCarriers(carrierRes.items);
      setEvents(eventRes.items);
      setLinks(linkRes.items);
      const latest = podRes.items[0];
      if (latest) {
        setPodForm({
          stopId: latest.stopId,
          recipientName: latest.recipientName ?? '',
          recipientPhone: latest.recipientPhone ?? '',
          signatureUrl: latest.signatureUrl ?? '',
          photoUrl: latest.photoUrl ?? '',
          documentUrl: latest.documentUrl ?? '',
          notes: latest.notes ?? '',
          deliveryCondition: latest.deliveryCondition ?? 'Good',
          capturedLatitude: latest.capturedLatitude?.toString() ?? '',
          capturedLongitude: latest.capturedLongitude?.toString() ?? '',
        });
      } else if (stopRes.items[0]) {
        setPodForm((current) => ({ ...current, stopId: stopRes.items[0].id }));
      }
      setStopForm((current) => ({ ...current, sequenceNo: (stopRes.items.length ? Math.max(...stopRes.items.map((item) => item.sequenceNo)) : 0) + 1 }));
      setStopForm((current) => ({
        ...current,
        plannedArrivalAt: current.plannedArrivalAt || toLocalDateTime(new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString()),
      }));
      setTrackingLink(linkRes.items.find((link) => !link.isRevoked) ?? null);
      if (!carrierId && shipment.carrierName) {
        const matched = carrierRes.items.find((carrier) => carrier.name === shipment.carrierName);
        if (matched) setCarrierId(matched.id);
      }
    } catch (err) {
      notifyApiError(err, 'Unable to load shipment lifecycle.');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [shipment.id]);

  const latestPod = pods[0] ?? null;
  const carrierName = carriers.find((carrier) => carrier.id === carrierId)?.name ?? shipment.carrierName ?? 'Unassigned';
  const visibleEvents = useMemo(() => events.slice(0, 12), [events]);

  const handleCreateStop = async () => {
    if (!stopForm.locationName.trim()) return;
    if (!stopForm.stopType.trim()) return;
    setBusy('create-stop');
    try {
      const created = await fleetLifecycleApi.createStop(shipment.id, {
        stopType: stopForm.stopType,
        sequenceNo: Number(stopForm.sequenceNo),
        locationName: stopForm.locationName,
        contactName: stopForm.contactName,
        contactPhone: stopForm.contactPhone,
        addressLine1: stopForm.addressLine1,
        addressLine2: stopForm.addressLine2,
        city: stopForm.city,
        region: stopForm.region,
        postalCode: stopForm.postalCode,
        country: stopForm.country,
        saudiNationalAddressBuildingNo: stopForm.saudiNationalAddressBuildingNo,
        saudiNationalAddressAdditionalNo: stopForm.saudiNationalAddressAdditionalNo,
        saudiNationalAddressDistrict: stopForm.saudiNationalAddressDistrict,
        latitude: stopForm.latitude ? Number(stopForm.latitude) : undefined,
        longitude: stopForm.longitude ? Number(stopForm.longitude) : undefined,
        plannedArrivalAt: stopForm.plannedArrivalAt,
        notes: stopForm.notes,
      });
      setStops((current) => [...current, created].sort((a, b) => a.sequenceNo - b.sequenceNo));
      setStopForm((current) => ({ ...blankStop, sequenceNo: current.sequenceNo + 1, country: 'Saudi Arabia' }));
      await load();
    } catch (err) {
      notifyApiError(err, 'Unable to create stop.');
    } finally {
      setBusy(null);
    }
  };

  const handleStopAction = async (stopId: string, action: 'arrive' | 'complete') => {
    setBusy(`${action}-${stopId}`);
    try {
      const updated = action === 'arrive'
        ? await fleetLifecycleApi.arriveStop(shipment.id, stopId, { notes: noteDraft })
        : await fleetLifecycleApi.completeStop(shipment.id, stopId, { notes: noteDraft });
      setStops((current) => current.map((stop) => (stop.id === updated.id ? updated : stop)));
      setNoteDraft('');
      await load();
    } catch (err) {
      notifyApiError(err, `Unable to ${action} stop.`);
    } finally {
      setBusy(null);
    }
  };

  const handlePodSave = async (submit = false, verify = false, reject = false) => {
    if (!podForm.stopId) return;
    setBusy(submit ? 'submit-pod' : verify ? 'verify-pod' : reject ? 'reject-pod' : 'save-pod');
    try {
      const payload = {
        stopId: podForm.stopId,
        recipientName: podForm.recipientName,
        recipientPhone: podForm.recipientPhone,
        signatureUrl: podForm.signatureUrl,
        photoUrl: podForm.photoUrl,
        documentUrl: podForm.documentUrl,
        notes: podForm.notes,
        deliveryCondition: podForm.deliveryCondition,
        capturedLatitude: podForm.capturedLatitude ? Number(podForm.capturedLatitude) : undefined,
        capturedLongitude: podForm.capturedLongitude ? Number(podForm.capturedLongitude) : undefined,
      };
      const created = latestPod?.id
        ? await fleetLifecycleApi.updatePod(shipment.id, latestPod.id, payload)
        : await fleetLifecycleApi.createPod(shipment.id, payload);
      let next = created;
      if (submit) next = await fleetLifecycleApi.submitPod(shipment.id, created.id);
      if (verify) next = await fleetLifecycleApi.verifyPod(shipment.id, created.id);
      if (reject) next = await fleetLifecycleApi.rejectPod(shipment.id, created.id, { notes: podForm.notes });
      setPods((current) => [next, ...current.filter((pod) => pod.id !== next.id)]);
      await load();
    } catch (err) {
      notifyApiError(err, 'Unable to save POD.');
    } finally {
      setBusy(null);
    }
  };

  const handleCreateTrackingLink = async () => {
    setBusy('tracking-link');
    try {
      const created = await fleetLifecycleApi.createTrackingLink(shipment.id, {
        expiresAtUtc: new Date(Date.now() + Number(trackingDays || '7') * 24 * 60 * 60 * 1000).toISOString(),
      });
      setTrackingLink(created);
      setLinks((current) => [created, ...current.filter((link) => link.id !== created.id)]);
      await load();
    } catch (err) {
      notifyApiError(err, 'Unable to create tracking link.');
    } finally {
      setBusy(null);
    }
  };

  const handleAssignCarrier = async () => {
    if (!carrierId) return;
    setBusy('assign-carrier');
    try {
      await fleetCommercialApi.assignShipmentCarrier(shipment.id, {
        carrierId,
        quotedAmount: quotedAmount ? Number(quotedAmount) : undefined,
        agreedAmount: agreedAmount ? Number(agreedAmount) : undefined,
        notes: 'Assigned from the shipment lifecycle drawer.',
      });
      await load();
    } catch (err) {
      notifyApiError(err, 'Unable to assign carrier.');
    } finally {
      setBusy(null);
    }
  };

  const handleCopyTracking = async (link: CustomerTrackingLink) => {
    const url = `${window.location.origin}/track/${link.token}`;
    await navigator.clipboard.writeText(url);
    setCopiedToken(link.token);
    window.setTimeout(() => setCopiedToken(null), 1800);
  };

  const handleMarkInvoiceReady = async () => {
    setBusy('invoice-ready');
    try {
      await fleetLifecycleApi.markInvoiceReady(shipment.id, { notes: 'Reviewed from the shipment lifecycle drawer.' });
      await load();
    } catch (err) {
      notifyApiError(err, 'Unable to mark shipment invoice-ready.');
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="fixed inset-0 z-50">
      <div className="absolute inset-0 bg-slate-950/55 backdrop-blur-sm" onClick={onClose} />
      <div className="absolute inset-y-3 right-3 left-3 overflow-hidden rounded-[32px] border border-white/70 bg-[linear-gradient(180deg,rgba(251,253,255,0.97),rgba(238,245,255,0.92))] shadow-[0_30px_100px_rgba(15,23,42,0.32)] backdrop-blur-3xl dark:border-white/10 dark:bg-[linear-gradient(180deg,rgba(10,16,28,0.98),rgba(4,8,16,0.96))] lg:left-auto lg:w-[min(1240px,calc(100vw-24px))]">
        <div className="relative flex h-full flex-col">
          <div className="absolute inset-0 pointer-events-none bg-[radial-gradient(circle_at_top_left,rgba(34,197,94,0.14),transparent_24%),radial-gradient(circle_at_80%_0%,rgba(14,165,233,0.16),transparent_22%),linear-gradient(180deg,rgba(255,255,255,0.34),transparent_24%)] dark:bg-[radial-gradient(circle_at_top_left,rgba(34,197,94,0.12),transparent_24%),radial-gradient(circle_at_80%_0%,rgba(14,165,233,0.12),transparent_22%),linear-gradient(180deg,rgba(255,255,255,0.04),transparent_24%)]" />
          <div className="relative z-10 flex items-center justify-between border-b border-slate-200/70 px-5 py-4 dark:border-white/10">
            <div>
              <p className="text-[10px] font-bold uppercase tracking-[0.28em] text-emerald-600/70">Shipment lifecycle</p>
              <h2 className="mt-1 text-[22px] font-black tracking-tight text-slate-950 dark:text-white">{shipment.shipmentNumber}</h2>
              <p className="text-sm text-slate-500 dark:text-slate-400">{shipment.customerName} · {shipment.origin} to {shipment.destination}</p>
            </div>
            <div className="flex items-center gap-2">
              <button type="button" onClick={handleMarkInvoiceReady} disabled={busy === 'invoice-ready'} className="rounded-full border border-emerald-200/80 bg-emerald-50 px-4 py-2 text-[11px] font-bold uppercase tracking-[0.18em] text-emerald-700 transition hover:bg-emerald-100 disabled:opacity-60">
                {busy === 'invoice-ready' ? 'Marking...' : 'Mark invoice-ready'}
              </button>
              <button type="button" onClick={onClose} className="rounded-full border border-slate-200/80 bg-white/90 p-2 text-slate-500 transition hover:text-slate-900 dark:border-white/10 dark:bg-white/[0.04] dark:text-slate-300">
                <X className="h-5 w-5" />
              </button>
            </div>
          </div>

          <div className="relative z-10 grid flex-1 gap-4 overflow-hidden p-4 lg:grid-cols-[1.15fr_0.85fr]">
            <div className="flex min-h-0 flex-col gap-4 overflow-y-auto pr-1">
              <section className="rounded-[28px] border border-white/80 bg-white/80 p-4 shadow-[0_16px_36px_rgba(15,23,42,0.06)] backdrop-blur-xl dark:border-white/10 dark:bg-white/[0.04]">
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-[10px] font-bold uppercase tracking-[0.24em] text-slate-400">Execution board</p>
                    <h3 className="mt-1 text-[18px] font-black tracking-tight text-slate-950 dark:text-white">Stops, POD, and live control</h3>
                  </div>
                  <span className="rounded-full border border-cyan-200/70 bg-cyan-50 px-3 py-1 text-[10px] font-bold uppercase tracking-[0.18em] text-cyan-700">
                    {loading ? 'Loading live data' : `${stops.length} stops`}
                  </span>
                </div>

                <div className="mt-4 grid gap-3 xl:grid-cols-[1fr_1fr]">
                  <div className="rounded-[24px] border border-slate-200/70 bg-[linear-gradient(180deg,rgba(255,255,255,0.96),rgba(245,248,255,0.78))] p-4 dark:border-white/10 dark:bg-white/[0.03]">
                    <div className="flex items-center gap-2 text-slate-500">
                      <Route className="h-4 w-4 text-cyan-500" />
                      <p className="text-[10px] font-bold uppercase tracking-[0.22em]">Stops timeline</p>
                    </div>
                    <div className="mt-4 space-y-3">
                      {stops.map((stop) => (
                        <div key={stop.id} className="rounded-[22px] border border-slate-200/70 bg-white/85 p-3 dark:border-white/10 dark:bg-white/[0.04]">
                          <div className="flex items-start justify-between gap-3">
                            <div>
                              <p className="text-[12px] font-black text-slate-900 dark:text-white">{stop.sequenceNo}. {stop.locationName}</p>
                              <p className="text-[11px] text-slate-500 dark:text-slate-400">{stop.stopType} · {stop.city || stop.country} · {stop.status}</p>
                            </div>
                            <span className="rounded-full border border-slate-200/70 px-2 py-1 text-[9px] font-bold uppercase tracking-[0.18em] text-slate-500 dark:border-white/10 dark:text-slate-300">
                              {stop.plannedArrivalAt ? toDateTimeString(stop.plannedArrivalAt) : 'Scheduled'}
                            </span>
                          </div>
                          <div className="mt-3 flex flex-wrap gap-2">
                            <button type="button" onClick={() => handleStopAction(stop.id, 'arrive')} disabled={busy === `arrive-${stop.id}`} className="inline-flex items-center gap-2 rounded-full bg-slate-950 px-3 py-2 text-[11px] font-bold text-white disabled:opacity-60 dark:bg-white dark:text-slate-950">
                              <MapPinned className="h-4 w-4" />
                              Arrive
                            </button>
                            <button type="button" onClick={() => handleStopAction(stop.id, 'complete')} disabled={busy === `complete-${stop.id}`} className="inline-flex items-center gap-2 rounded-full border border-emerald-200 bg-emerald-50 px-3 py-2 text-[11px] font-bold text-emerald-700 disabled:opacity-60">
                              <CheckCircle2 className="h-4 w-4" />
                              Complete
                            </button>
                          </div>
                        </div>
                      ))}
                      {!stops.length && (
                        <div className="rounded-[22px] border border-dashed border-slate-200/80 bg-slate-50/70 p-4 text-sm text-slate-500 dark:border-white/10 dark:bg-white/[0.03]">
                          No stops yet. Add the first pickup or delivery stop to start the execution timeline.
                        </div>
                      )}
                    </div>
                  </div>

                  <div className="rounded-[24px] border border-slate-200/70 bg-[linear-gradient(180deg,rgba(255,255,255,0.96),rgba(245,248,255,0.78))] p-4 dark:border-white/10 dark:bg-white/[0.03]">
                    <div className="flex items-center gap-2 text-slate-500">
                      <FileCheck2 className="h-4 w-4 text-emerald-500" />
                      <p className="text-[10px] font-bold uppercase tracking-[0.22em]">POD panel</p>
                    </div>
                    <div className="mt-4 space-y-3">
                      <label className="space-y-1">
                        <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Stop</span>
                        <select className="w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm text-slate-900 outline-none focus:border-cyan-400 dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={podForm.stopId} onChange={(e) => setPodForm((current) => ({ ...current, stopId: e.target.value }))}>
                          <option value="">Select stop</option>
                          {stops.map((stop) => <option key={stop.id} value={stop.id}>{stop.sequenceNo} · {stop.locationName}</option>)}
                        </select>
                      </label>
                      <div className="grid gap-3 md:grid-cols-2">
                        <label className="space-y-1">
                          <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Recipient</span>
                          <input className="w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none focus:border-cyan-400 dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={podForm.recipientName} onChange={(e) => setPodForm((current) => ({ ...current, recipientName: e.target.value }))} placeholder="Name" />
                        </label>
                        <label className="space-y-1">
                          <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Condition</span>
                          <select className="w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none focus:border-cyan-400 dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={podForm.deliveryCondition} onChange={(e) => setPodForm((current) => ({ ...current, deliveryCondition: e.target.value }))}>
                            <option>Good</option>
                            <option>Damaged</option>
                            <option>Short</option>
                            <option>Rejected</option>
                            <option>Other</option>
                          </select>
                        </label>
                      </div>
                      <div className="grid gap-3 md:grid-cols-2">
                        <label className="space-y-1">
                          <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Signature URL</span>
                          <input className="w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none focus:border-cyan-400 dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={podForm.signatureUrl} onChange={(e) => setPodForm((current) => ({ ...current, signatureUrl: e.target.value }))} placeholder="https://..." />
                        </label>
                        <label className="space-y-1">
                          <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Photo URL</span>
                          <input className="w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none focus:border-cyan-400 dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={podForm.photoUrl} onChange={(e) => setPodForm((current) => ({ ...current, photoUrl: e.target.value }))} placeholder="https://..." />
                        </label>
                      </div>
                      <label className="space-y-1">
                        <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Notes</span>
                        <textarea className="min-h-[88px] w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none focus:border-cyan-400 dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={podForm.notes} onChange={(e) => setPodForm((current) => ({ ...current, notes: e.target.value }))} placeholder="Optional POD notes" />
                      </label>
                      <div className="flex flex-wrap gap-2">
                        <button type="button" onClick={() => handlePodSave(false, false, false)} disabled={busy === 'save-pod'} className="inline-flex items-center gap-2 rounded-full bg-gradient-to-r from-cyan-600 via-sky-500 to-emerald-400 px-4 py-2.5 text-[11px] font-bold text-white shadow-[0_16px_30px_rgba(14,165,233,0.24)] disabled:opacity-60">
                          <ClipboardList className="h-4 w-4" />
                          {latestPod ? 'Update POD' : 'Create POD'}
                        </button>
                        <button type="button" onClick={() => handlePodSave(true, false, false)} disabled={busy === 'submit-pod'} className="inline-flex items-center gap-2 rounded-full border border-slate-200/80 bg-white px-4 py-2.5 text-[11px] font-bold text-slate-700 disabled:opacity-60 dark:border-white/10 dark:bg-white/[0.04] dark:text-slate-200">
                          <ArrowRight className="h-4 w-4" />
                          Submit
                        </button>
                        <button type="button" onClick={() => handlePodSave(false, true, false)} disabled={busy === 'verify-pod'} className="inline-flex items-center gap-2 rounded-full border border-emerald-200 bg-emerald-50 px-4 py-2.5 text-[11px] font-bold text-emerald-700 disabled:opacity-60">
                          <ShieldCheck className="h-4 w-4" />
                          Verify
                        </button>
                        <button type="button" onClick={() => handlePodSave(false, false, true)} disabled={busy === 'reject-pod'} className="inline-flex items-center gap-2 rounded-full border border-rose-200 bg-rose-50 px-4 py-2.5 text-[11px] font-bold text-rose-700 disabled:opacity-60">
                          <X className="h-4 w-4" />
                          Reject
                        </button>
                      </div>
                    </div>
                  </div>
                </div>
              </section>

              <section className="grid gap-4 xl:grid-cols-2">
                <div className="rounded-[28px] border border-white/80 bg-white/80 p-4 backdrop-blur-xl dark:border-white/10 dark:bg-white/[0.04]">
                  <div className="flex items-center justify-between">
                    <div>
                      <p className="text-[10px] font-bold uppercase tracking-[0.24em] text-slate-400">Customer tracking</p>
                      <h3 className="mt-1 text-[18px] font-black tracking-tight text-slate-950 dark:text-white">Shared link and public view</h3>
                    </div>
                    <Link2 className="h-5 w-5 text-cyan-500" />
                  </div>

                  <div className="mt-4 flex items-center gap-2">
                    <input className="w-24 rounded-2xl border border-slate-200/80 bg-white/85 px-3 py-2.5 text-sm outline-none dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={trackingDays} onChange={(e) => setTrackingDays(e.target.value)} />
                    <span className="text-sm text-slate-500">days before expiry</span>
                    <button type="button" onClick={handleCreateTrackingLink} disabled={busy === 'tracking-link'} className="ml-auto rounded-full bg-slate-950 px-4 py-2.5 text-[11px] font-bold uppercase tracking-[0.16em] text-white disabled:opacity-60 dark:bg-white dark:text-slate-950">
                      {busy === 'tracking-link' ? 'Creating...' : 'Create link'}
                    </button>
                  </div>

                  <div className="mt-4 space-y-2">
                    {links.map((link) => {
                      const href = `${typeof window !== 'undefined' ? window.location.origin : ''}/track/${link.token}`;
                      return (
                        <div key={link.id} className="rounded-[20px] border border-slate-200/70 bg-slate-50/80 p-3 dark:border-white/10 dark:bg-white/[0.03]">
                          <div className="flex items-center justify-between gap-3">
                            <div className="min-w-0">
                              <p className="truncate text-[12px] font-bold text-slate-900 dark:text-white">{link.token}</p>
                              <p className="text-[11px] text-slate-500 dark:text-slate-400">{link.isRevoked ? 'Revoked' : `Expires ${toDateTimeString(link.expiresAtUtc)}`}</p>
                            </div>
                            <div className="flex items-center gap-2">
                              <button type="button" onClick={() => handleCopyTracking(link)} className="rounded-full border border-slate-200/80 bg-white px-3 py-2 text-[10px] font-bold uppercase tracking-[0.16em] text-slate-700 dark:border-white/10 dark:bg-white/[0.04] dark:text-slate-200">
                                {copiedToken === link.token ? 'Copied' : 'Copy'}
                              </button>
                              <a href={href} target="_blank" rel="noreferrer" className="rounded-full border border-cyan-200/70 bg-cyan-50 px-3 py-2 text-[10px] font-bold uppercase tracking-[0.16em] text-cyan-700">
                                Open
                              </a>
                              {!link.isRevoked && (
                                <button type="button" onClick={() => fleetLifecycleApi.revokeTrackingLink(shipment.id, link.id).then(load).catch((err) => notifyApiError(err, 'Unable to revoke link.'))} className="rounded-full border border-rose-200 bg-rose-50 px-3 py-2 text-[10px] font-bold uppercase tracking-[0.16em] text-rose-700">
                                  Revoke
                                </button>
                              )}
                            </div>
                          </div>
                        </div>
                      );
                    })}
                    {!links.length && <p className="rounded-[20px] border border-dashed border-slate-200/70 bg-slate-50/70 p-4 text-sm text-slate-500 dark:border-white/10 dark:bg-white/[0.03]">No public tracking link has been generated yet.</p>}
                  </div>
                </div>

                <div className="rounded-[28px] border border-white/80 bg-white/80 p-4 backdrop-blur-xl dark:border-white/10 dark:bg-white/[0.04]">
                  <div className="flex items-center justify-between">
                    <div>
                      <p className="text-[10px] font-bold uppercase tracking-[0.24em] text-slate-400">Shipment events</p>
                      <h3 className="mt-1 text-[18px] font-black tracking-tight text-slate-950 dark:text-white">Operational audit trail</h3>
                    </div>
                    <RefreshCcw className="h-5 w-5 text-emerald-500" />
                  </div>
                  <div className="mt-4 space-y-2">
                    {visibleEvents.map((event) => (
                      <div key={event.id} className="rounded-[18px] border border-slate-200/70 bg-slate-50/75 p-3 dark:border-white/10 dark:bg-white/[0.03]">
                        <div className="flex items-start justify-between gap-3">
                          <div>
                            <p className="text-[12px] font-bold text-slate-900 dark:text-white">{event.eventType}</p>
                            <p className="mt-1 text-[11px] leading-relaxed text-slate-500 dark:text-slate-400">{event.message}</p>
                          </div>
                          <span className="rounded-full border border-slate-200/70 px-2 py-1 text-[9px] font-bold uppercase tracking-[0.18em] text-slate-500 dark:border-white/10 dark:text-slate-300">
                            {event.visibility}
                          </span>
                        </div>
                        <p className="mt-2 text-[10px] uppercase tracking-[0.18em] text-slate-400">{toDateTimeString(event.occurredAtUtc)}</p>
                      </div>
                    ))}
                    {!visibleEvents.length && <p className="rounded-[18px] border border-dashed border-slate-200/70 bg-slate-50/70 p-4 text-sm text-slate-500 dark:border-white/10 dark:bg-white/[0.03]">Shipment events will appear here as the load moves.</p>}
                  </div>
                </div>
              </section>
            </div>

            <div className="flex min-h-0 flex-col gap-4 overflow-y-auto pl-1">
              <section className="rounded-[28px] border border-white/80 bg-white/80 p-4 backdrop-blur-xl dark:border-white/10 dark:bg-white/[0.04]">
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-[10px] font-bold uppercase tracking-[0.24em] text-slate-400">New stop</p>
                    <h3 className="mt-1 text-[18px] font-black tracking-tight text-slate-950 dark:text-white">Add execution details</h3>
                  </div>
                  <Package className="h-5 w-5 text-cyan-500" />
                </div>
                <div className="mt-4 grid gap-3">
                  <div className="grid gap-3 md:grid-cols-2">
                    <label className="space-y-1">
                      <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Stop type</span>
                      <select className="w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={stopForm.stopType} onChange={(e) => setStopForm((current) => ({ ...current, stopType: e.target.value }))}>
                        <option>Pickup</option>
                        <option>Delivery</option>
                        <option>Transfer</option>
                        <option>Return</option>
                      </select>
                    </label>
                    <label className="space-y-1">
                      <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Sequence</span>
                      <input type="number" min="1" className="w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={stopForm.sequenceNo} onChange={(e) => setStopForm((current) => ({ ...current, sequenceNo: Number(e.target.value) }))} />
                    </label>
                  </div>
                  <label className="space-y-1">
                    <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Location name</span>
                    <input className="w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={stopForm.locationName} onChange={(e) => setStopForm((current) => ({ ...current, locationName: e.target.value }))} placeholder="Warehouse, store, dock, customer site" />
                  </label>
                  <div className="grid gap-3 md:grid-cols-2">
                    <label className="space-y-1">
                      <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Contact name</span>
                      <input className="w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={stopForm.contactName} onChange={(e) => setStopForm((current) => ({ ...current, contactName: e.target.value }))} />
                    </label>
                    <label className="space-y-1">
                      <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Contact phone</span>
                      <input className="w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={stopForm.contactPhone} onChange={(e) => setStopForm((current) => ({ ...current, contactPhone: e.target.value }))} />
                    </label>
                  </div>
                  <SaudiAddressFields
                    value={{
                      addressLine1: stopForm.addressLine1,
                      addressLine2: stopForm.addressLine2,
                      city: stopForm.city,
                      region: stopForm.region,
                      postalCode: stopForm.postalCode,
                      country: stopForm.country,
                      buildingNo: stopForm.saudiNationalAddressBuildingNo,
                      additionalNo: stopForm.saudiNationalAddressAdditionalNo,
                      district: stopForm.saudiNationalAddressDistrict,
                    }}
                    onChange={(next) => setStopForm((current) => ({
                      ...current,
                      addressLine1: next.addressLine1 ?? '',
                      addressLine2: next.addressLine2 ?? '',
                      city: next.city ?? '',
                      region: next.region ?? '',
                      postalCode: next.postalCode ?? '',
                      country: next.country ?? 'Saudi Arabia',
                      saudiNationalAddressBuildingNo: next.buildingNo ?? '',
                      saudiNationalAddressAdditionalNo: next.additionalNo ?? '',
                      saudiNationalAddressDistrict: next.district ?? '',
                    }))}
                    compact
                  />
                  <div className="grid gap-3 md:grid-cols-2">
                    <label className="space-y-1">
                      <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Latitude</span>
                      <input className="w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={stopForm.latitude} onChange={(e) => setStopForm((current) => ({ ...current, latitude: e.target.value }))} />
                    </label>
                    <label className="space-y-1">
                      <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Longitude</span>
                      <input className="w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={stopForm.longitude} onChange={(e) => setStopForm((current) => ({ ...current, longitude: e.target.value }))} />
                    </label>
                  </div>
                  <label className="space-y-1">
                    <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Planned arrival</span>
                    <input type="datetime-local" className="w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={stopForm.plannedArrivalAt} onChange={(e) => setStopForm((current) => ({ ...current, plannedArrivalAt: e.target.value }))} />
                  </label>
                  <label className="space-y-1">
                    <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Notes</span>
                    <textarea className="min-h-[90px] w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={stopForm.notes} onChange={(e) => setStopForm((current) => ({ ...current, notes: e.target.value }))} />
                  </label>
                  <button type="button" onClick={handleCreateStop} disabled={busy === 'create-stop'} className="inline-flex items-center justify-center gap-2 rounded-[22px] bg-gradient-to-r from-emerald-600 via-cyan-500 to-sky-400 px-4 py-3.5 text-[12px] font-bold text-white shadow-[0_16px_30px_rgba(16,185,129,0.22)] disabled:opacity-60">
                    <ArrowRight className="h-4 w-4" />
                    {busy === 'create-stop' ? 'Creating stop...' : 'Create stop'}
                  </button>
                </div>
              </section>

              <section className="rounded-[28px] border border-white/80 bg-white/80 p-4 backdrop-blur-xl dark:border-white/10 dark:bg-white/[0.04]">
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-[10px] font-bold uppercase tracking-[0.24em] text-slate-400">Driver tasks</p>
                    <h3 className="mt-1 text-[18px] font-black tracking-tight text-slate-950 dark:text-white">Operational checklist</h3>
                  </div>
                  <UserRound className="h-5 w-5 text-emerald-500" />
                </div>
                <div className="mt-4 space-y-2">
                  {tasks.map((task) => (
                    <div key={task.id} className="rounded-[20px] border border-slate-200/70 bg-slate-50/80 p-3 dark:border-white/10 dark:bg-white/[0.03]">
                      <div className="flex items-start justify-between gap-3">
                        <div>
                          <p className="text-[12px] font-bold text-slate-900 dark:text-white">{task.title}</p>
                          <p className="mt-1 text-[11px] text-slate-500 dark:text-slate-400">{task.taskType} · {task.status} · {toDateTimeString(task.dueAtUtc)}</p>
                        </div>
                        <span className="rounded-full border border-slate-200/70 px-2 py-1 text-[9px] font-bold uppercase tracking-[0.18em] text-slate-500 dark:border-white/10 dark:text-slate-300">
                          {task.vehicleNumber || 'No vehicle'}
                        </span>
                      </div>
                    </div>
                  ))}
                  {!tasks.length && (
                    <p className="rounded-[20px] border border-dashed border-slate-200/70 bg-slate-50/70 p-4 text-sm text-slate-500 dark:border-white/10 dark:bg-white/[0.03]">
                      No driver tasks are tied to this shipment yet.
                    </p>
                  )}
                </div>
              </section>

              <section className="rounded-[28px] border border-white/80 bg-white/80 p-4 backdrop-blur-xl dark:border-white/10 dark:bg-white/[0.04]">
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-[10px] font-bold uppercase tracking-[0.24em] text-slate-400">Shipment summary</p>
                    <h3 className="mt-1 text-[18px] font-black tracking-tight text-slate-950 dark:text-white">Snapshot for leaders</h3>
                  </div>
                  <Truck className="h-5 w-5 text-cyan-500" />
                </div>
                <div className="mt-4 rounded-[20px] border border-slate-200/70 bg-slate-50/75 p-4 dark:border-white/10 dark:bg-white/[0.03]">
                  <div className="flex items-center justify-between">
                    <div>
                      <p className="text-[10px] font-bold uppercase tracking-[0.24em] text-slate-400">Carrier assignment</p>
                      <h4 className="mt-1 text-[14px] font-black tracking-tight text-slate-950 dark:text-white">Subcontracted capacity</h4>
                    </div>
                    <Link2 className="h-4 w-4 text-violet-500" />
                  </div>
                  <div className="mt-3 grid gap-3">
                    <select className="w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={carrierId} onChange={(e) => setCarrierId(e.target.value)}>
                      <option value="">Choose carrier</option>
                      {carriers.map((carrier) => (
                        <option key={carrier.id} value={carrier.id}>{carrier.name} · {carrier.region}</option>
                      ))}
                    </select>
                    <div className="grid gap-3 md:grid-cols-2">
                      <label className="space-y-1">
                        <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Quoted amount</span>
                        <input className="w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={quotedAmount} onChange={(e) => setQuotedAmount(e.target.value)} />
                      </label>
                      <label className="space-y-1">
                        <span className="text-[10px] font-bold uppercase tracking-[0.22em] text-slate-400">Agreed amount</span>
                        <input className="w-full rounded-2xl border border-slate-200/80 bg-white/85 px-3.5 py-3 text-sm outline-none dark:border-white/10 dark:bg-white/[0.04] dark:text-white" value={agreedAmount} onChange={(e) => setAgreedAmount(e.target.value)} />
                      </label>
                    </div>
                    <button type="button" onClick={handleAssignCarrier} disabled={busy === 'assign-carrier' || !carrierId} className="inline-flex items-center justify-center gap-2 rounded-[20px] bg-gradient-to-r from-violet-600 via-fuchsia-500 to-cyan-400 px-4 py-3 text-[12px] font-bold text-white shadow-[0_16px_30px_rgba(124,58,237,0.22)] disabled:opacity-60">
                      <ArrowRight className="h-4 w-4" />
                      {busy === 'assign-carrier' ? 'Assigning...' : 'Assign carrier'}
                    </button>
                  </div>
                </div>
                <div className="mt-4 grid gap-3 sm:grid-cols-2">
                  <SummaryTile label="Status" value={shipment.status} />
                  <SummaryTile label="Priority" value={shipment.priority} />
                  <SummaryTile label="Carrier" value={carrierName} />
                  <SummaryTile label="Vehicle" value={shipment.vehicleNumber || 'Unassigned'} />
                  <SummaryTile label="POD" value={shipment.podStatus || 'Pending'} />
                  <SummaryTile label="Invoice-ready" value={shipment.isInvoiceReady ? 'Yes' : 'No'} />
                </div>
                {trackingLink && (
                  <div className="mt-4 rounded-[20px] border border-cyan-200/60 bg-cyan-50/70 p-3 dark:border-cyan-400/10 dark:bg-cyan-400/8">
                    <p className="text-[10px] font-bold uppercase tracking-[0.22em] text-cyan-700/70">Public tracking URL</p>
                    <p className="mt-1 break-all text-sm font-semibold text-slate-900 dark:text-white">{typeof window !== 'undefined' ? `${window.location.origin}/track/${trackingLink.token}` : trackingLink.token}</p>
                  </div>
                )}
              </section>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}

function SummaryTile({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-[18px] border border-slate-200/70 bg-slate-50/75 p-3 dark:border-white/10 dark:bg-white/[0.03]">
      <p className="text-[10px] font-bold uppercase tracking-[0.24em] text-slate-400">{label}</p>
      <p className="mt-2 text-[13px] font-bold text-slate-900 dark:text-white">{value}</p>
    </div>
  );
}
