'use client';

import { useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { Archive, Barcode, Boxes, CheckCheck, Layers3, SquareStack, Truck } from 'lucide-react';
import { Logo } from '../components/Logo';
import { notifyApiError } from '../api/client';
import { fleetApi, fleetAssetApi, fleetCommercialApi, type Asset, type AssetAssignment, type AssetEvent, type AssetType } from '../api/fleet';

type AssetDetail = {
  asset: Asset;
  assignments: AssetAssignment[];
  events: AssetEvent[];
};

export function FleetAssetManagementPage() {
  const [assetTypes, setAssetTypes] = useState<AssetType[]>([]);
  const [assets, setAssets] = useState<Asset[]>([]);
  const [carriers, setCarriers] = useState<Array<{ id: string; name: string }>>([]);
  const [shipments, setShipments] = useState<Array<{ id: string; shipmentNumber: string; customerName: string; status: string }>>([]);
  const [detail, setDetail] = useState<AssetDetail | null>(null);
  const [selectedAssetId, setSelectedAssetId] = useState('');
  const [selectedTypeId, setSelectedTypeId] = useState('');
  const [selectedShipmentId, setSelectedShipmentId] = useState('');
  const [selectedCarrierId, setSelectedCarrierId] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [forms, setForms] = useState({
    typeCode: '',
    typeName: '',
    typeDescription: '',
    assetTag: '',
    assetName: '',
    assetLocation: 'Main Warehouse',
    assetCondition: 'Good',
    assetQuantity: '1',
    scanValue: '',
    scanNotes: '',
    assignName: '',
    assignLocation: '',
    movementNotes: '',
  });

  const refresh = async () => {
    const [typesRes, assetsRes, shipmentsRes, carriersRes] = await Promise.all([
      fleetAssetApi.assetTypes(),
      fleetAssetApi.assets(),
      fleetApi.shipments({ pageSize: 8 }),
      fleetCommercialApi.carriers().catch(() => ({ items: [] as Array<{ id: string; name: string }> })),
    ]);
    setAssetTypes(typesRes.items);
    setAssets(assetsRes.items);
    setShipments(shipmentsRes.items as Array<{ id: string; shipmentNumber: string; customerName: string; status: string }>);
    setCarriers(carriersRes.items);
    setSelectedTypeId(typesRes.items[0]?.id ?? '');
    setSelectedShipmentId(shipmentsRes.items[0]?.id ?? '');
    setSelectedCarrierId(carriersRes.items[0]?.id ?? '');
    const currentSelectedAssetId = selectedAssetId || assetsRes.items[0]?.id || '';
    if (currentSelectedAssetId) {
      setSelectedAssetId(currentSelectedAssetId);
      const selected = await fleetAssetApi.asset(currentSelectedAssetId);
      setDetail(selected);
    }
  };

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    (async () => {
      try {
        const [typesRes, assetsRes, shipmentsRes, carriersRes] = await Promise.all([
          fleetAssetApi.assetTypes(),
          fleetAssetApi.assets(),
          fleetApi.shipments({ pageSize: 8 }),
          fleetCommercialApi.carriers().catch(() => ({ items: [] as Array<{ id: string; name: string }> })),
        ]);
        if (cancelled) return;
        setAssetTypes(typesRes.items);
        setAssets(assetsRes.items);
        setShipments(shipmentsRes.items as Array<{ id: string; shipmentNumber: string; customerName: string; status: string }>);
        setCarriers(carriersRes.items);
        setSelectedTypeId(typesRes.items[0]?.id ?? '');
        setSelectedShipmentId(shipmentsRes.items[0]?.id ?? '');
        setSelectedCarrierId(carriersRes.items[0]?.id ?? '');
        if (assetsRes.items[0]) {
          setSelectedAssetId(assetsRes.items[0].id);
          const selected = await fleetAssetApi.asset(assetsRes.items[0].id);
          if (!cancelled) setDetail(selected);
        }
      } catch (err) {
        if (!cancelled) notifyApiError(err, 'Unable to load asset management.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (!selectedAssetId) return;
    let cancelled = false;
    (async () => {
      try {
        const selected = await fleetAssetApi.asset(selectedAssetId);
        if (!cancelled) setDetail(selected);
      } catch (err) {
        if (!cancelled) notifyApiError(err, 'Unable to open asset details.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [selectedAssetId]);

  const metrics = useMemo(() => {
    const assigned = assets.filter((asset) => asset.status === 'Assigned' || asset.status === 'InUse').length;
    const available = assets.filter((asset) => asset.status === 'Available').length;
    const maintenance = assets.filter((asset) => asset.condition !== 'Good').length;
    return [
      { label: 'Asset types', value: assetTypes.length, tone: 'text-cyan-700' },
      { label: 'Assigned', value: assigned, tone: 'text-blue-700' },
      { label: 'Available', value: available, tone: 'text-emerald-700' },
      { label: 'Needs review', value: maintenance, tone: 'text-amber-700' },
    ];
  }, [assetTypes, assets]);

  const createAssetType = async () => {
    setSaving(true);
    try {
      await fleetAssetApi.createAssetType({
        code: forms.typeCode,
        name: forms.typeName,
        description: forms.typeDescription,
        isReturnable: true,
      });
      setForms((current) => ({ ...current, typeCode: '', typeName: '', typeDescription: '' }));
      await refresh();
    } catch (err) {
      notifyApiError(err, 'Unable to create asset type.');
    } finally {
      setSaving(false);
    }
  };

  const createAsset = async () => {
    if (!selectedTypeId) return;
    setSaving(true);
    try {
      await fleetAssetApi.createAsset({
        assetTypeId: selectedTypeId,
        assetTag: forms.assetTag,
        name: forms.assetName,
        status: 'Available',
        currentLocation: forms.assetLocation,
        condition: forms.assetCondition,
        quantity: Number(forms.assetQuantity),
        unitOfMeasure: 'Each',
        notes: 'Created from the asset management console.',
      });
      setForms((current) => ({ ...current, assetTag: '', assetName: '' }));
      await refresh();
    } catch (err) {
      notifyApiError(err, 'Unable to create asset.');
    } finally {
      setSaving(false);
    }
  };

  const scan = async () => {
    try {
      await fleetAssetApi.scan({
        kind: 'Barcode',
        assetId: selectedAssetId || undefined,
        shipmentId: selectedShipmentId || undefined,
        scannedValue: forms.scanValue,
        scannerId: 'WEB-SCAN-01',
        eventType: 'Scan',
        status: 'Captured',
        notes: forms.scanNotes,
      });
      setForms((current) => ({ ...current, scanValue: '', scanNotes: '' }));
      await refresh();
    } catch (err) {
      notifyApiError(err, 'Unable to capture scan.');
    }
  };

  const assign = async (mode: 'checkIn' | 'checkOut') => {
    if (!selectedAssetId) return;
    try {
      if (mode === 'checkIn') {
        await fleetAssetApi.checkInAsset(selectedAssetId, {
          location: forms.assignLocation || 'Main Warehouse',
          condition: forms.assetCondition,
          notes: forms.movementNotes,
          shipmentId: selectedShipmentId || undefined,
          carrierId: selectedCarrierId || undefined,
          assigneeName: forms.assignName || 'Warehouse',
        });
      } else {
        await fleetAssetApi.checkOutAsset(selectedAssetId, {
          location: forms.assignLocation || 'Dispatch Yard',
          condition: forms.assetCondition,
          notes: forms.movementNotes,
          shipmentId: selectedShipmentId || undefined,
          carrierId: selectedCarrierId || undefined,
          assigneeName: forms.assignName || (shipments.find((s) => s.id === selectedShipmentId)?.shipmentNumber ?? 'Dispatch'),
        });
      }
      await refresh();
    } catch (err) {
      notifyApiError(err, `Unable to ${mode === 'checkIn' ? 'check in' : 'check out'} asset.`);
    }
  };

  if (loading) return <div className="min-h-screen bg-slate-950" />;

  return (
    <main className="relative min-h-screen overflow-hidden bg-[radial-gradient(circle_at_top_right,_rgba(96,165,250,0.22),_transparent_34%),radial-gradient(circle_at_15%_10%,_rgba(16,185,129,0.16),_transparent_26%),linear-gradient(135deg,_#f9fbff_0%,_#eef6ff_48%,_#e8f0ff_100%)] text-slate-900">
      <div className="pointer-events-none absolute inset-0 overflow-hidden">
        <div className="absolute right-[-4rem] top-24 h-72 w-72 rounded-full bg-blue-300/30 blur-3xl animate-pulse" />
        <div className="absolute left-10 top-10 h-64 w-64 rounded-full bg-emerald-300/20 blur-3xl animate-pulse [animation-delay:1.5s]" />
        <div className="absolute bottom-0 right-1/3 h-80 w-80 rounded-full bg-cyan-300/25 blur-3xl animate-pulse [animation-delay:2.8s]" />
      </div>

      <section className="relative mx-auto flex w-full max-w-7xl flex-col gap-6 px-6 py-8 lg:px-10">
        <div className="flex flex-wrap items-center justify-between gap-4">
          <Logo size="lg" />
          <Link href="/fleet" className="rounded-full border border-slate-200/80 bg-white/70 px-4 py-2 text-sm font-semibold text-slate-700 shadow-sm backdrop-blur">
            Back to Fleet Command
          </Link>
        </div>

        <div className="grid gap-6 lg:grid-cols-[1.1fr_0.9fr]">
          <div className="space-y-6">
            <div className="rounded-[30px] border border-white/70 bg-white/60 p-7 shadow-[0_30px_80px_rgba(37,99,235,0.12)] backdrop-blur-xl">
              <div className="inline-flex items-center gap-2 rounded-full border border-sky-200 bg-sky-50 px-4 py-1.5 text-xs font-bold uppercase tracking-[0.26em] text-sky-700">
                <Boxes className="h-3.5 w-3.5" />
                Asset control centre
              </div>
              <h1 className="mt-5 max-w-3xl text-4xl font-black tracking-tight text-slate-950 md:text-6xl">
                Inventory that behaves like an operational system, not a spreadsheet.
              </h1>
              <p className="mt-4 max-w-2xl text-base leading-7 text-slate-600 md:text-lg">
                Manage asset types, custody, scan history, and assignment state from a single tenant-aware workspace. Every button here talks to the backend and updates the actual records.
              </p>
              <div className="mt-6 flex flex-wrap gap-3 text-xs font-semibold text-slate-600">
                {['Check-in/out', 'Barcode + RFID', 'Shipment custody', 'Audit friendly'].map((tag) => (
                  <span key={tag} className="rounded-full border border-slate-200 bg-white/80 px-3 py-1.5 shadow-sm">
                    {tag}
                  </span>
                ))}
              </div>
            </div>

            <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
              {metrics.map((metric) => (
                <article key={metric.label} className="rounded-3xl border border-white/80 bg-white/75 p-5 shadow-lg backdrop-blur">
                  <div className="flex items-center justify-between">
                    <p className="text-xs font-bold uppercase tracking-[0.24em] text-slate-500">{metric.label}</p>
                    <SquareStack className={`h-4 w-4 ${metric.tone}`} />
                  </div>
                  <p className="mt-4 text-3xl font-black tracking-tight text-slate-950">{metric.value}</p>
                </article>
              ))}
            </div>

            <div className="grid gap-6 xl:grid-cols-[0.92fr_1.08fr]">
              <section className="rounded-[28px] border border-white/75 bg-white/75 p-6 shadow-[0_24px_50px_rgba(15,23,42,0.08)] backdrop-blur">
                <p className="text-xs font-bold uppercase tracking-[0.24em] text-slate-500">Asset types</p>
                <h2 className="mt-2 text-2xl font-black text-slate-950">Categories in the tenant</h2>
                <div className="mt-5 space-y-3">
                  {assetTypes.map((type) => (
                    <div key={type.id} className="rounded-2xl border border-slate-200 bg-white p-4">
                      <div className="flex items-center justify-between">
                        <div>
                          <p className="font-bold text-slate-950">{type.name}</p>
                          <p className="text-sm text-slate-500">{type.code} · {type.description}</p>
                        </div>
                        <span className="rounded-full bg-cyan-50 px-3 py-1 text-xs font-bold text-cyan-700">{type.isReturnable ? 'Returnable' : 'Consumable'}</span>
                      </div>
                    </div>
                  ))}
                </div>
              </section>

              <section className="rounded-[28px] border border-white/75 bg-white/75 p-6 shadow-[0_24px_50px_rgba(15,23,42,0.08)] backdrop-blur">
                <p className="text-xs font-bold uppercase tracking-[0.24em] text-slate-500">Create asset</p>
                <h2 className="mt-2 text-2xl font-black text-slate-950">Inventory intake</h2>
                <div className="mt-5 grid gap-4 sm:grid-cols-2">
                  <select value={selectedTypeId} onChange={(e) => setSelectedTypeId(e.target.value)} className="rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none focus:border-cyan-400 sm:col-span-2">
                    {assetTypes.map((type) => <option key={type.id} value={type.id}>{type.name}</option>)}
                  </select>
                  <input value={forms.assetTag} onChange={(e) => setForms((current) => ({ ...current, assetTag: e.target.value }))} placeholder="Asset tag" className="rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none focus:border-cyan-400" />
                  <input value={forms.assetName} onChange={(e) => setForms((current) => ({ ...current, assetName: e.target.value }))} placeholder="Asset name" className="rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none focus:border-cyan-400" />
                  <input value={forms.assetLocation} onChange={(e) => setForms((current) => ({ ...current, assetLocation: e.target.value }))} placeholder="Location" className="rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none focus:border-cyan-400" />
                  <input value={forms.assetCondition} onChange={(e) => setForms((current) => ({ ...current, assetCondition: e.target.value }))} placeholder="Condition" className="rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none focus:border-cyan-400" />
                  <input value={forms.assetQuantity} onChange={(e) => setForms((current) => ({ ...current, assetQuantity: e.target.value }))} placeholder="Quantity" className="rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none focus:border-cyan-400" />
                  <input value={forms.typeCode} onChange={(e) => setForms((current) => ({ ...current, typeCode: e.target.value }))} placeholder="New type code" className="rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none focus:border-cyan-400" />
                  <input value={forms.typeName} onChange={(e) => setForms((current) => ({ ...current, typeName: e.target.value }))} placeholder="New type name" className="rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none focus:border-cyan-400" />
                  <textarea value={forms.typeDescription} onChange={(e) => setForms((current) => ({ ...current, typeDescription: e.target.value }))} rows={3} placeholder="Type description" className="sm:col-span-2 rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none focus:border-cyan-400" />
                </div>
                <div className="mt-4 grid gap-3 sm:grid-cols-2">
                  <button disabled={saving} onClick={createAsset} className="rounded-2xl bg-gradient-to-r from-blue-600 to-cyan-600 px-4 py-3 font-bold text-white shadow-lg transition hover:from-blue-500 hover:to-cyan-500 disabled:opacity-60">
                    {saving ? 'Saving...' : 'Create asset'}
                  </button>
                  <button disabled={saving} onClick={createAssetType} className="rounded-2xl border border-slate-200 bg-white px-4 py-3 font-bold text-slate-700 transition hover:border-cyan-300 hover:text-cyan-700 disabled:opacity-60">
                    Add asset type
                  </button>
                </div>
              </section>
            </div>
          </div>

          <aside className="space-y-6">
            <section className="rounded-[28px] border border-white/75 bg-slate-950/95 p-6 text-white shadow-[0_28px_60px_rgba(15,23,42,0.32)]">
              <div className="flex items-center justify-between">
                <div>
                  <p className="text-xs font-bold uppercase tracking-[0.24em] text-cyan-200/70">Assets</p>
                  <h2 className="mt-2 text-2xl font-black">Inventory list</h2>
                </div>
                <Archive className="h-5 w-5 text-cyan-300" />
              </div>
              <div className="mt-5 space-y-3">
                {assets.slice(0, 7).map((asset) => (
                  <button key={asset.id} onClick={() => setSelectedAssetId(asset.id)} className={`w-full rounded-2xl border p-4 text-left transition ${selectedAssetId === asset.id ? 'border-cyan-300 bg-white/10' : 'border-white/10 bg-white/5 hover:bg-white/8'}`}>
                    <div className="flex items-center justify-between">
                      <div>
                        <p className="font-bold">{asset.assetTag}</p>
                        <p className="text-sm text-slate-300">{asset.name} · {asset.currentLocation}</p>
                      </div>
                      <span className="rounded-full bg-white/10 px-3 py-1 text-xs font-bold">{asset.status}</span>
                    </div>
                  </button>
                ))}
              </div>
            </section>

            <section className="rounded-[28px] border border-white/75 bg-white/80 p-6 shadow-[0_24px_50px_rgba(15,23,42,0.08)] backdrop-blur">
              <p className="text-xs font-bold uppercase tracking-[0.24em] text-slate-500">Scan & custody</p>
              <h2 className="mt-2 text-2xl font-black text-slate-950">Barcode / RFID actions</h2>
              <div className="mt-5 space-y-3">
                <input value={forms.scanValue} onChange={(e) => setForms((current) => ({ ...current, scanValue: e.target.value }))} placeholder="Scan value / tag" className="w-full rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none focus:border-cyan-400" />
                <textarea value={forms.scanNotes} onChange={(e) => setForms((current) => ({ ...current, scanNotes: e.target.value }))} rows={3} placeholder="Scan notes" className="w-full rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none focus:border-cyan-400" />
                <button onClick={scan} className="inline-flex w-full items-center justify-center gap-2 rounded-2xl bg-slate-950 px-4 py-3 font-bold text-white transition hover:bg-slate-800">
                  <Barcode className="h-4 w-4" />
                  Capture barcode scan
                </button>
              </div>
            </section>

            <section className="rounded-[28px] border border-white/75 bg-white/80 p-6 shadow-[0_24px_50px_rgba(15,23,42,0.08)] backdrop-blur">
              <p className="text-xs font-bold uppercase tracking-[0.24em] text-slate-500">Assignment</p>
              <h2 className="mt-2 text-2xl font-black text-slate-950">Move selected asset</h2>
              <div className="mt-5 space-y-3">
                <input value={forms.assignName} onChange={(e) => setForms((current) => ({ ...current, assignName: e.target.value }))} placeholder="Assignee name" className="w-full rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none focus:border-cyan-400" />
                <input value={forms.assignLocation} onChange={(e) => setForms((current) => ({ ...current, assignLocation: e.target.value }))} placeholder="Current location" className="w-full rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none focus:border-cyan-400" />
                <div className="grid gap-3 sm:grid-cols-2">
                  <select value={selectedShipmentId} onChange={(e) => setSelectedShipmentId(e.target.value)} className="rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none focus:border-cyan-400">
                    {shipments.map((shipment) => <option key={shipment.id} value={shipment.id}>{shipment.shipmentNumber}</option>)}
                  </select>
                  <select value={selectedCarrierId} onChange={(e) => setSelectedCarrierId(e.target.value)} className="rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none focus:border-cyan-400">
                    <option value="">No carrier</option>
                    {carriers.map((carrier) => <option key={carrier.id} value={carrier.id}>{carrier.name}</option>)}
                  </select>
                </div>
                <div className="grid gap-3 sm:grid-cols-2">
                  <button onClick={() => assign('checkOut')} className="inline-flex items-center justify-center gap-2 rounded-2xl bg-gradient-to-r from-blue-600 to-cyan-600 px-4 py-3 font-bold text-white transition hover:from-blue-500 hover:to-cyan-500">
                    <CheckCheck className="h-4 w-4" />
                    Check out
                  </button>
                  <button onClick={() => assign('checkIn')} className="inline-flex items-center justify-center gap-2 rounded-2xl border border-slate-200 bg-white px-4 py-3 font-bold text-slate-700 transition hover:border-cyan-300 hover:text-cyan-700">
                    <Truck className="h-4 w-4" />
                    Check in
                  </button>
                </div>
                <textarea value={forms.movementNotes} onChange={(e) => setForms((current) => ({ ...current, movementNotes: e.target.value }))} rows={3} placeholder="Movement notes" className="w-full rounded-2xl border border-slate-200 bg-white px-4 py-3 outline-none focus:border-cyan-400" />
              </div>
            </section>

            <section className="rounded-[28px] border border-white/75 bg-white/80 p-6 shadow-[0_24px_50px_rgba(15,23,42,0.08)] backdrop-blur">
              <p className="text-xs font-bold uppercase tracking-[0.24em] text-slate-500">Selected asset</p>
              {detail ? (
                <div className="mt-4 space-y-4">
                  <div>
                    <p className="text-xl font-black text-slate-950">{detail.asset.assetTag}</p>
                    <p className="text-sm text-slate-500">{detail.asset.name} · {detail.asset.currentLocation}</p>
                  </div>
                  <div className="grid grid-cols-2 gap-3 text-sm">
                    <div className="rounded-2xl bg-slate-50 p-3">
                      <p className="text-[11px] uppercase tracking-[0.2em] text-slate-400">Status</p>
                      <p className="font-bold text-slate-900">{detail.asset.status}</p>
                    </div>
                    <div className="rounded-2xl bg-slate-50 p-3">
                      <p className="text-[11px] uppercase tracking-[0.2em] text-slate-400">Condition</p>
                      <p className="font-bold text-slate-900">{detail.asset.condition}</p>
                    </div>
                    <div className="rounded-2xl bg-slate-50 p-3">
                      <p className="text-[11px] uppercase tracking-[0.2em] text-slate-400">Quantity</p>
                      <p className="font-bold text-slate-900">{detail.asset.quantity}</p>
                    </div>
                    <div className="rounded-2xl bg-slate-50 p-3">
                      <p className="text-[11px] uppercase tracking-[0.2em] text-slate-400">Assignments</p>
                      <p className="font-bold text-slate-900">{detail.assignments.length}</p>
                    </div>
                  </div>

                  <div>
                    <p className="text-xs font-bold uppercase tracking-[0.24em] text-slate-500">Assignments</p>
                    <div className="mt-3 space-y-2">
                      {detail.assignments.slice(0, 3).map((assignment) => (
                        <div key={assignment.id} className="rounded-2xl border border-slate-200 bg-white p-3 text-sm">
                          <div className="flex items-center justify-between">
                            <p className="font-bold text-slate-950">{assignment.assigneeType}</p>
                            <span className="text-xs font-bold text-cyan-700">{assignment.status}</span>
                          </div>
                          <p className="text-slate-500">{assignment.assigneeName}</p>
                        </div>
                      ))}
                    </div>
                  </div>

                  <div>
                    <p className="text-xs font-bold uppercase tracking-[0.24em] text-slate-500">Events</p>
                    <div className="mt-3 space-y-2">
                      {detail.events.slice(0, 4).map((event) => (
                        <div key={event.id} className="rounded-2xl border border-slate-200 bg-slate-50 p-3 text-sm">
                          <div className="flex items-center justify-between">
                            <p className="font-bold text-slate-950">{event.type}</p>
                            <span className="text-xs text-slate-500">{event.occurredAtUtc}</span>
                          </div>
                          <p className="text-slate-500">{event.eventType} · {event.location}</p>
                        </div>
                      ))}
                    </div>
                  </div>
                </div>
              ) : (
                <p className="mt-4 text-sm text-slate-500">Select an asset to inspect custody, scan history, and assignment records.</p>
              )}
            </section>
          </aside>
        </div>
      </section>
    </main>
  );
}

export default FleetAssetManagementPage;
