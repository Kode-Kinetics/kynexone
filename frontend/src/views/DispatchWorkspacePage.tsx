'use client';

import { useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { usePathname } from 'next/navigation';
import {
  AlertTriangle,
  ArrowRight,
  CheckCircle2,
  ChevronRight,
  Clock3,
  MapPinned,
  Package,
  Route,
  Sparkles,
  Truck,
} from 'lucide-react';
import { Logo } from '../components/Logo';
import { logisticsApi, type LogisticsOrder, type LogisticsOverview, type LogisticsRoute, type LogisticsStop } from '../api/logistics';
import { notifyApiError } from '../api/client';

type DispatchMode = 'dispatch' | 'orders' | 'routes' | 'delivery';

const MODULES: Record<DispatchMode, {
  label: string;
  title: string;
  subtitle: string;
  accent: string;
  path: string;
  summary: string;
}> = {
  dispatch: {
    label: 'Dispatch Command Center',
    title: 'Protect service promises before delays become customer conversations.',
    subtitle: 'Bring intake pressure, route movement, recovery actions, and proof visibility into one command surface that helps operators and decision-makers act from the same truth.',
    accent: 'from-blue-600 via-sky-500 to-cyan-400',
    path: '/dispatch',
    summary: 'When dispatch, customer service, and operations leadership all read the same live board, SLA risk becomes manageable instead of reactive.',
  },
  orders: {
    label: 'Jobs & Orders',
    title: 'Turn incoming demand into an order pipeline your team can actually execute.',
    subtitle: 'See who ordered, what matters most, where the work should flow next, and which commitments are putting margin or service at risk.',
    accent: 'from-indigo-600 via-blue-500 to-cyan-400',
    path: '/dispatch/jobs-orders',
    summary: 'This is the intake layer clients expect to trust: visible priorities, visible ownership, and live status that moves with the operation.',
  },
  routes: {
    label: 'Route Planning',
    title: 'Plan routes that respect operational pressure, not just road geometry.',
    subtitle: 'Balance stop density, load, driver readiness, and completion pressure in a planner built for real tradeoffs instead of theoretical optimisation.',
    accent: 'from-sky-600 via-cyan-500 to-teal-400',
    path: '/dispatch/route-planning',
    summary: 'Good routing is a commercial capability. This screen helps planners shape the day with fewer blind spots and faster recovery.',
  },
  delivery: {
    label: 'Last Mile Delivery',
    title: 'Make the last mile accountable all the way to the customer doorstep.',
    subtitle: 'Track live delivery state, attempted drops, reschedules, recipient proof, and recovery notes in one surface built for operators who cannot afford ambiguity.',
    accent: 'from-cyan-600 via-sky-500 to-blue-400',
    path: '/dispatch/last-mile-delivery',
    summary: 'The most visible part of the brand is the delivery moment. This workspace keeps that moment governed, traceable, and fast to recover.',
  },
};

const MODE_ORDER: DispatchMode[] = ['dispatch', 'orders', 'routes', 'delivery'];

export function DispatchWorkspacePage({ mode }: { mode: DispatchMode }) {
  const pathname = usePathname();
  const config = MODULES[mode];
  const [overview, setOverview] = useState<LogisticsOverview | null>(null);
  const [orders, setOrders] = useState<LogisticsOrder[]>([]);
  const [routes, setRoutes] = useState<LogisticsRoute[]>([]);
  const [stops, setStops] = useState<LogisticsStop[]>([]);
  const [routeStops, setRouteStops] = useState<LogisticsStop[]>([]);
  const [selectedRouteId, setSelectedRouteId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [savingId, setSavingId] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [orderForm, setOrderForm] = useState({
    orderNumber: '',
    customerName: '',
    city: 'Riyadh',
    area: 'City Core',
    priority: 'Normal',
    routeCode: '',
    itemCount: '3',
    orderValue: '250',
  });
  const [routeForm, setRouteForm] = useState({
    routeCode: '',
    hub: 'Central Hub',
    territory: 'City Core',
    driverName: '',
    vehicleNumber: '',
    plannedStops: '8',
    distanceKm: '42',
  });
  const [lastMileForm, setLastMileForm] = useState({
    recipientName: '',
    exceptionReason: 'Customer requested another delivery slot.',
    nextStop: '',
    timeWindow: '16:00-19:00',
  });

  const syncSelectedRoute = (routeList: LogisticsRoute[], stopList: LogisticsStop[]) => {
    if (!routeList.length) {
      setSelectedRouteId(null);
      return;
    }

    setSelectedRouteId((current) => {
      if (current && routeList.some((route) => route.id === current)) return current;
      const routeCodeFromStops = stopList.find((stop) => stop.routeCode)?.routeCode;
      return routeList.find((route) => route.routeCode === routeCodeFromStops)?.id ?? routeList[0].id;
    });
  };

  const refreshWorkspace = async (activeMode: DispatchMode = mode) => {
    const [ov, orderRes, routeRes, stopRes] = await Promise.all([
      logisticsApi.overview(),
      activeMode === 'dispatch' || activeMode === 'orders' ? logisticsApi.orders({ pageSize: 12 }) : Promise.resolve(null),
      activeMode === 'dispatch' || activeMode === 'routes' || activeMode === 'delivery' ? logisticsApi.routes() : Promise.resolve(null),
      activeMode === 'dispatch' || activeMode === 'delivery' ? logisticsApi.lastMile({ pageSize: 12 }) : Promise.resolve(null),
    ]);
    setOverview(ov);
    const nextOrders = orderRes?.items ?? ov.orderCards;
    const nextRoutes = routeRes?.items ?? ov.routeCards;
    const nextStops = stopRes?.items ?? ov.liveStops;
    setOrders(nextOrders);
    setRoutes(nextRoutes);
    setStops(nextStops);
    syncSelectedRoute(nextRoutes, nextStops);
  };

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    (async () => {
      try {
        const [ov, orderRes, routeRes, stopRes] = await Promise.all([
          logisticsApi.overview(),
          mode === 'dispatch' || mode === 'orders' ? logisticsApi.orders({ pageSize: 12 }) : Promise.resolve(null),
          mode === 'dispatch' || mode === 'routes' || mode === 'delivery' ? logisticsApi.routes() : Promise.resolve(null),
          mode === 'dispatch' || mode === 'delivery' ? logisticsApi.lastMile({ pageSize: 12 }) : Promise.resolve(null),
        ]);
        if (cancelled) return;
        setOverview(ov);
        const nextOrders = orderRes?.items ?? ov.orderCards;
        const nextRoutes = routeRes?.items ?? ov.routeCards;
        const nextStops = stopRes?.items ?? ov.liveStops;
        setOrders(nextOrders);
        setRoutes(nextRoutes);
        setStops(nextStops);
        syncSelectedRoute(nextRoutes, nextStops);
      } catch (err) {
        if (!cancelled) notifyApiError(err, 'Unable to load logistics workspace.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => { cancelled = true; };
  }, [mode]);

  useEffect(() => {
    if (!selectedRouteId) {
      setRouteStops([]);
      return;
    }
    let cancelled = false;
    (async () => {
      try {
        const response = await logisticsApi.routeStops(selectedRouteId);
        if (!cancelled) setRouteStops(response.items);
      } catch (err) {
        if (!cancelled) notifyApiError(err, 'Unable to load route stops.');
      }
    })();
    return () => { cancelled = true; };
  }, [selectedRouteId]);

  const stats = useMemo(() => {
    if (!overview) return [];
    const s = overview.summary;
    return [
      { label: 'Active orders', value: s.activeOrders.toString(), hint: 'Queued + in motion', icon: Package },
      { label: 'Routes active', value: s.activeRoutes.toString(), hint: 'Today on road', icon: Route },
      { label: 'In transit', value: s.inTransit.toString(), hint: 'Needs close follow-up', icon: Truck },
      { label: 'On-time rate', value: `${s.onTimeRate.toFixed(1)}%`, hint: 'Route completion', icon: Clock3 },
    ];
  }, [overview]);

  const valuePillars = useMemo(() => {
    switch (mode) {
      case 'orders':
        return ['Demand intake', 'Priority control', 'Assignment readiness'];
      case 'routes':
        return ['Territory balance', 'Stop density', 'ETA discipline'];
      case 'delivery':
        return ['Recipient proof', 'Attempt recovery', 'Doorstep visibility'];
      default:
        return ['SLA command', 'Exception recovery', 'Unified dispatch truth'];
    }
  }, [mode]);

  const commandSignals = useMemo(() => {
    const topRoute = routes[0];
    const topStop = stops[0];
    const topOrder = orders[0];

    switch (mode) {
      case 'orders':
        return [
          {
            label: 'Intake under control',
            value: `${orders.filter((order) => order.status === 'Queued').length}`,
            note: 'Queued orders still waiting for operational ownership.',
          },
          {
            label: 'High-priority exposure',
            value: `${orders.filter((order) => order.priority === 'High' || order.priority === 'Critical').length}`,
            note: 'Orders likely to become escalation calls if left untouched.',
          },
          {
            label: 'Lead account in motion',
            value: topOrder?.customerName ?? 'Awaiting fresh orders',
            note: 'The account most visible to sales and customer service right now.',
          },
        ];
      case 'routes':
        return [
          {
            label: 'Most loaded route',
            value: topRoute?.routeCode ?? 'No route selected',
            note: topRoute ? `${topRoute.completedStops}/${topRoute.plannedStops} stops completed` : 'Route creation is available from this panel.',
          },
          {
            label: 'Distance commitment',
            value: `${routes.reduce((sum, route) => sum + route.distanceKm, 0).toFixed(0)} km`,
            note: 'Total planned distance currently sitting with the dispatch desk.',
          },
          {
            label: 'Planner pressure',
            value: `${routes.filter((route) => route.status !== 'Completed').length}`,
            note: 'Routes still active and likely to need monitoring or intervention.',
          },
        ];
      case 'delivery':
        return [
          {
            label: 'Proof-ready stops',
            value: `${stops.filter((stop) => stop.proofStatus === 'POD' || stop.proofStatus === 'Verified').length}`,
            note: 'Stops already carrying proof or ready for final verification.',
          },
          {
            label: 'Customer-facing risk',
            value: `${stops.filter((stop) => stop.status === 'Attempted' || stop.status === 'Rescheduled').length}`,
            note: 'Last-mile exceptions likely to generate customer follow-up.',
          },
          {
            label: 'Next doorstep moment',
            value: topStop?.customerName ?? 'Awaiting route execution',
            note: topStop ? `${topStop.routeCode} · ${topStop.timeWindow}` : 'No live stop selected yet.',
          },
        ];
      default:
        return [
          {
            label: 'Orders needing movement',
            value: `${orders.filter((order) => order.status === 'Queued').length}`,
            note: 'Open order load still waiting to enter execution.',
          },
          {
            label: 'Routes under watch',
            value: `${routes.filter((route) => route.status !== 'Completed').length}`,
            note: 'Routes that remain active and commercially visible.',
          },
          {
            label: 'Exception touchpoints',
            value: `${stops.filter((stop) => stop.status === 'Attempted' || stop.status === 'Delayed').length}`,
            note: 'Delivery moments most likely to turn into support conversations.',
          },
        ];
    }
  }, [mode, orders, routes, stops]);

  const actionNarrative = useMemo(() => {
    switch (mode) {
      case 'orders':
        return 'Order intake should read like a controlled funnel, not a pile-up. This page now frames demand by urgency, ownership readiness, and commercial visibility.';
      case 'routes':
        return 'Route planning is where service promises become operational commitments. The page now spotlights route pressure, completion risk, and the next routing move.';
      case 'delivery':
        return 'Last mile is the most emotionally visible part of the product experience. This surface is tuned to keep proof, attempts, and recovery actions impossible to miss.';
      default:
        return 'The command center should reassure leadership and help operators act. This workspace now emphasizes urgency, route motion, and exception containment in one read.';
    }
  }, [mode]);

  const liveRecordRows = useMemo(() => {
    if (mode === 'routes') {
      return routes.slice(0, 5).map((route) => ({
        id: route.id,
        title: route.routeCode,
        subtitle: `${route.hub} · ${route.territory}`,
        status: route.status,
        actionLabel: 'Focus route',
        onClick: () => setSelectedRouteId(route.id),
      }));
    }

    if (mode === 'delivery') {
      return stops.slice(0, 5).map((stop) => ({
        id: stop.id,
        title: stop.customerName,
        subtitle: stop.addressLine,
        status: stop.status,
        actionLabel: 'Focus stop',
        onClick: () => {
          const matchingRoute = routes.find((route) => route.routeCode === stop.routeCode);
          if (matchingRoute) setSelectedRouteId(matchingRoute.id);
        },
      }));
    }

    return orders.slice(0, 5).map((order) => ({
      id: order.id,
      title: order.orderNumber,
      subtitle: `${order.customerName} · ${order.city}`,
      status: order.status,
      actionLabel: '',
      onClick: undefined,
    }));
  }, [mode, orders, routes, stops]);

  const alerts = overview?.alerts ?? [];
  const selectedRoute = routes.find((route) => route.id === selectedRouteId) ?? null;
  const visibleStops = mode === 'routes'
    ? routeStops
    : mode === 'delivery' && routeStops.length
      ? routeStops
      : stops;

  const handleDispatch = async (order: LogisticsOrder) => {
    setSavingId(order.id);
    try {
      await logisticsApi.dispatchOrder(order.id, {
        routeCode: order.routeCode,
        driverName: order.driverName,
        vehicleNumber: order.vehicleNumber,
        notes: order.dispatchNotes,
      });
      await refreshWorkspace();
    } catch (err) {
      notifyApiError(err, 'Unable to dispatch this order.');
    } finally {
      setSavingId(null);
    }
  };

  const handleProgress = async (route: LogisticsRoute) => {
    setSavingId(route.id);
    try {
      await logisticsApi.progressRoute(route.id, {
        completedStopsDelta: 1,
        currentStop: route.currentStop,
        nextStop: route.nextStop,
        notes: 'Route advanced from the command center.',
      });
      await refreshWorkspace();
    } catch (err) {
      notifyApiError(err, 'Unable to advance the route.');
    } finally {
      setSavingId(null);
    }
  };

  const handleConfirm = async (stop: LogisticsStop) => {
    setSavingId(stop.id);
    try {
      await logisticsApi.confirmDelivery(stop.id, {
        recipientName: lastMileForm.recipientName || stop.recipientName || stop.customerName.split(' ')[0],
        proofStatus: 'POD',
      });
      await refreshWorkspace();
    } catch (err) {
      notifyApiError(err, 'Unable to confirm delivery.');
    } finally {
      setSavingId(null);
    }
  };

  const handleAttempt = async (stop: LogisticsStop) => {
    setSavingId(stop.id);
    try {
      await logisticsApi.recordAttempt(stop.id, {
        status: 'Attempted',
        proofStatus: 'None',
        exceptionReason: lastMileForm.exceptionReason,
        nextEtaUtc: new Date(Date.now() + 4 * 60 * 60 * 1000).toISOString(),
        nextStop: lastMileForm.nextStop || stop.customerName,
      });
      await refreshWorkspace();
    } catch (err) {
      notifyApiError(err, 'Unable to record delivery attempt.');
    } finally {
      setSavingId(null);
    }
  };

  const handleReschedule = async (stop: LogisticsStop) => {
    setSavingId(stop.id);
    try {
      await logisticsApi.rescheduleStop(stop.id, {
        timeWindow: lastMileForm.timeWindow,
        reason: lastMileForm.exceptionReason,
        nextEtaUtc: new Date(Date.now() + 8 * 60 * 60 * 1000).toISOString(),
      });
      await refreshWorkspace();
    } catch (err) {
      notifyApiError(err, 'Unable to reschedule stop.');
    } finally {
      setSavingId(null);
    }
  };

  const handleCreateOrder = async () => {
    setCreating(true);
    try {
      await logisticsApi.createOrder({
        orderNumber: orderForm.orderNumber,
        customerName: orderForm.customerName,
        city: orderForm.city,
        area: orderForm.area,
        priority: orderForm.priority,
        routeCode: orderForm.routeCode,
        itemCount: Number(orderForm.itemCount),
        orderValue: Number(orderForm.orderValue),
        status: 'Queued',
        customerSegment: 'Retail',
        salesChannel: 'Portal',
      });
      setOrderForm((current) => ({ ...current, orderNumber: '', customerName: '' }));
      await refreshWorkspace('orders');
    } catch (err) {
      notifyApiError(err, 'Unable to create order.');
    } finally {
      setCreating(false);
    }
  };

  const handleCreateRoute = async () => {
    setCreating(true);
    try {
      await logisticsApi.createRoute({
        routeCode: routeForm.routeCode,
        hub: routeForm.hub,
        territory: routeForm.territory,
        driverName: routeForm.driverName,
        vehicleNumber: routeForm.vehicleNumber,
        plannedStops: Number(routeForm.plannedStops),
        distanceKm: Number(routeForm.distanceKm),
        completedStops: 0,
        completionPercent: 0,
        status: 'Planned',
        currentStop: '',
        nextStop: '',
        departureTimeUtc: new Date().toISOString(),
        plannedForDate: new Date().toISOString(),
      });
      setRouteForm((current) => ({ ...current, routeCode: '' }));
      await refreshWorkspace('routes');
    } catch (err) {
      notifyApiError(err, 'Unable to create route.');
    } finally {
      setCreating(false);
    }
  };

  return (
    <>
      <div className="relative min-h-[100svh] overflow-hidden bg-[#eef3ff] text-slate-900 dark:bg-[#040814] dark:text-white">
        <div className="pointer-events-none absolute inset-0 bg-[radial-gradient(circle_at_top_left,rgba(47,107,255,0.16),transparent_28%),radial-gradient(circle_at_80%_18%,rgba(94,235,255,0.10),transparent_22%),linear-gradient(180deg,rgba(255,255,255,0.5),transparent_34%)] dark:bg-[radial-gradient(circle_at_top_left,rgba(47,107,255,0.20),transparent_28%),radial-gradient(circle_at_80%_18%,rgba(94,235,255,0.08),transparent_22%),linear-gradient(180deg,rgba(6,11,24,0.92),rgba(4,8,20,0.98))]" />
        <div className="pointer-events-none absolute inset-0 opacity-[0.16] mix-blend-soft-light [background-image:linear-gradient(rgba(47,107,255,0.07)_1px,transparent_1px),linear-gradient(90deg,rgba(47,107,255,0.07)_1px,transparent_1px)] [background-size:72px_72px] animate-grid-breathe" />
        <div className="pointer-events-none absolute left-[8%] top-[14%] h-56 w-56 rounded-full bg-white/35 blur-3xl animate-ambient-drift" />
        <div className="pointer-events-none absolute right-[8%] top-[18%] h-64 w-64 rounded-full bg-cyan-300/12 blur-3xl animate-ambient-drift" />
        <div className="pointer-events-none absolute bottom-[-5rem] left-[20%] h-72 w-72 rounded-full bg-blue-400/12 blur-3xl animate-ambient-drift" />
        <div className="pointer-events-none absolute inset-x-0 top-1/2 h-px bg-[linear-gradient(90deg,transparent,rgba(47,107,255,0.20),rgba(94,235,255,0.24),transparent)] opacity-40 animate-lane-drift" />
        <svg className="pointer-events-none absolute inset-0 h-full w-full opacity-50" viewBox="0 0 1440 1024" fill="none" aria-hidden="true">
          <path className="ops-route-line animate-route-flow" d="M118 766C242 692 272 590 386 547C495 506 640 556 750 499C854 444 919 317 1046 298C1153 282 1231 335 1318 258" stroke="rgba(47,107,255,0.26)" strokeWidth="2.5" />
          <path className="ops-route-line animate-route-flow" d="M160 872C302 812 395 858 531 792C668 724 707 611 821 565C953 513 1062 577 1216 494" stroke="rgba(94,235,255,0.24)" strokeWidth="2" style={{ animationDelay: '-3s' }} />
          <circle cx="386" cy="547" r="8" fill="rgba(255,255,255,0.9)" />
          <circle cx="386" cy="547" r="18" className="animate-signal-pulse" fill="rgba(47,107,255,0.22)" />
          <circle cx="1046" cy="298" r="7" fill="rgba(94,235,255,0.88)" />
          <circle cx="1046" cy="298" r="16" className="animate-signal-pulse" fill="rgba(94,235,255,0.18)" style={{ animationDelay: '-1.2s' }} />
        </svg>

        <div className="relative z-10 mx-auto flex min-h-[100svh] w-full max-w-[1600px] flex-col px-4 py-3 sm:px-6 lg:px-8 lg:py-4">
          <div className="mb-4 flex items-center justify-between gap-4">
            <div className="flex items-center gap-3 rounded-[22px] border border-white/80 bg-white/74 px-4 py-3 shadow-[0_18px_40px_rgba(37,99,235,0.08)] backdrop-blur-xl dark:border-white/[0.08] dark:bg-white/[0.04]">
              <div className="rounded-[18px] bg-[radial-gradient(circle_at_top_left,rgba(255,255,255,0.45),transparent_45%),linear-gradient(160deg,rgba(29,78,216,0.98),rgba(8,47,122,0.98))] p-1.5 shadow-[0_12px_24px_rgba(37,99,235,0.22)]">
                <Logo size="xl" />
              </div>
              <div className="h-9 w-px bg-slate-300/60" />
              <div>
                <p className="text-[10px] font-bold tracking-[0.28em] uppercase text-blue-500/70">Dispatch & Delivery</p>
                <p className="text-[11px] text-slate-500">{config.label}</p>
              </div>
            </div>

            <div className="hidden items-center gap-2 rounded-full border border-white/70 bg-white/72 p-1 shadow-[0_14px_30px_rgba(37,99,235,0.05)] backdrop-blur-xl md:flex dark:border-white/[0.08] dark:bg-white/[0.04]">
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
            <div className="rounded-[32px] border border-white/75 bg-[linear-gradient(160deg,rgba(251,253,255,0.92),rgba(239,245,255,0.74))] p-5 shadow-[0_24px_80px_rgba(37,99,235,0.12)] backdrop-blur-3xl dark:border-white/[0.08] dark:bg-[linear-gradient(160deg,rgba(11,18,34,0.96),rgba(7,12,24,0.92))]">
              <div className="flex flex-wrap items-center gap-2">
                <span className={`inline-flex items-center gap-2 rounded-full border border-blue-300/30 bg-white/78 px-3 py-1.5 text-[10px] font-semibold uppercase tracking-[0.24em] text-blue-600 shadow-sm backdrop-blur`}>
                  <span className="h-1.5 w-1.5 rounded-full bg-emerald-400" />
                  Live tenant data
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
                <div className="mt-4 flex flex-wrap gap-2">
                  {valuePillars.map((pillar) => (
                    <span key={pillar} className="rounded-full border border-white/80 bg-white/78 px-3 py-1.5 text-[10px] font-semibold uppercase tracking-[0.18em] text-slate-600 shadow-sm backdrop-blur dark:border-white/10 dark:bg-white/[0.04] dark:text-slate-300">
                      {pillar}
                    </span>
                  ))}
                </div>
              </div>

              <div className="mt-6 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                {loading ? (
                  Array.from({ length: 4 }).map((_, index) => (
                    <div
                      key={index}
                      className="rounded-[24px] border border-white/80 bg-white/78 p-4 shadow-[0_12px_26px_rgba(37,99,235,0.05)] backdrop-blur-xl dark:border-white/[0.06] dark:bg-white/[0.04]"
                    >
                      <div className="space-y-3">
                        <div className="h-3 w-20 animate-pulse rounded bg-slate-200/80 dark:bg-white/10" />
                        <div className="h-9 w-16 animate-pulse rounded bg-slate-200/80 dark:bg-white/10" />
                        <div className="h-3 w-28 animate-pulse rounded bg-slate-200/80 dark:bg-white/10" />
                      </div>
                    </div>
                  ))
                ) : (
                  stats.map((stat) => (
                    <div
                      key={stat.label}
                      className="rounded-[24px] border border-white/80 bg-white/78 p-4 shadow-[0_12px_26px_rgba(37,99,235,0.05)] backdrop-blur-xl dark:border-white/[0.06] dark:bg-white/[0.04]"
                    >
                      <div className="flex items-center justify-between">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.2em] text-slate-400">{stat.label}</p>
                        <stat.icon className="h-4 w-4 text-blue-500/70" />
                      </div>
                      <p className="mt-2 text-[28px] font-black tracking-tight text-slate-950 dark:text-white">{stat.value}</p>
                      <p className="mt-1 text-[12px] text-slate-500 dark:text-slate-500">{stat.hint}</p>
                    </div>
                  ))
                )}
              </div>

              <div className="mt-4 grid gap-4 xl:grid-cols-[1.25fr_0.75fr]">
                <div className="rounded-[28px] border border-white/80 bg-white/74 p-4 shadow-[0_18px_40px_rgba(37,99,235,0.06)] backdrop-blur-xl dark:border-white/[0.06] dark:bg-white/[0.04]">
                  <div className="mb-4 grid gap-3 lg:grid-cols-3">
                    {commandSignals.map((signal) => (
                      <div key={signal.label} className="rounded-[22px] border border-slate-200/70 bg-[linear-gradient(180deg,rgba(255,255,255,0.92),rgba(244,248,255,0.84))] p-3.5 dark:border-white/10 dark:bg-white/[0.03]">
                        <p className="text-[10px] font-semibold uppercase tracking-[0.2em] text-slate-400">{signal.label}</p>
                        <p className="mt-2 text-[24px] font-black tracking-tight text-slate-950 dark:text-white">{signal.value}</p>
                        <p className="mt-1 text-[11px] leading-relaxed text-slate-500 dark:text-slate-400">{signal.note}</p>
                      </div>
                    ))}
                  </div>

                  <div className="mb-3 flex items-center justify-between gap-3">
                    <div>
                      <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-slate-400">Operational board</p>
                      <p className="mt-1 text-[18px] font-black tracking-tight text-slate-950 dark:text-white">
                        {mode === 'routes' ? 'Route load and completion' : mode === 'delivery' ? 'Last mile progress' : 'Orders, routes, and exceptions'}
                      </p>
                    </div>
                    <span className="rounded-full border border-emerald-200/70 bg-emerald-50 px-3 py-1 text-[10px] font-semibold uppercase tracking-[0.18em] text-emerald-600">
                      Backend live
                    </span>
                  </div>

                  <div className="space-y-3">
                    {mode === 'routes' ? (
                      routes.slice(0, 4).map((route) => (
                        <ActionRouteCard key={route.id} route={route} saving={savingId === route.id} onAdvance={() => handleProgress(route)} onInspect={() => setSelectedRouteId(route.id)} />
                      ))
                    ) : mode === 'delivery' ? (
                      stops.slice(0, 6).map((stop) => (
                        <ActionStopCard key={stop.id} stop={stop} saving={savingId === stop.id} onConfirm={() => handleConfirm(stop)} onAttempt={() => handleAttempt(stop)} onReschedule={() => handleReschedule(stop)} />
                      ))
                    ) : (
                      orders.slice(0, 6).map((order) => (
                        <ActionOrderCard key={order.id} order={order} saving={savingId === order.id} onDispatch={() => handleDispatch(order)} />
                      ))
                    )}
                  </div>
                </div>

                <div className="rounded-[28px] border border-white/80 bg-white/74 p-4 shadow-[0_18px_40px_rgba(37,99,235,0.06)] backdrop-blur-xl dark:border-white/[0.06] dark:bg-white/[0.04]">
                  <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-slate-400">Operational cues</p>
                  <div className="mt-3 rounded-[22px] border border-slate-200/70 bg-[linear-gradient(135deg,rgba(19,30,56,0.96),rgba(31,87,184,0.86))] p-4 text-white shadow-[0_18px_40px_rgba(37,99,235,0.18)]">
                    <p className="text-[10px] font-semibold uppercase tracking-[0.2em] text-white/60">Executive readout</p>
                    <p className="mt-2 text-[16px] font-black leading-snug">{mode === 'delivery' ? 'Protect the customer moment.' : mode === 'routes' ? 'Shape the day before it shapes you.' : 'Keep service promises commercially safe.'}</p>
                    <p className="mt-2 text-[12px] leading-relaxed text-white/78">{actionNarrative}</p>
                  </div>
                  <div className="mt-3 space-y-3">
                    {alerts.length ? alerts.slice(0, 4).map((alert) => (
                      <div key={alert.orderNumber} className="rounded-2xl border border-amber-200/60 bg-amber-50/70 p-3 dark:border-amber-400/10 dark:bg-amber-400/8">
                        <div className="flex items-center justify-between gap-3">
                          <p className="text-[12px] font-bold text-amber-800 dark:text-amber-200">{alert.orderNumber}</p>
                          <span className="rounded-full bg-white/70 px-2 py-1 text-[9px] font-semibold uppercase tracking-[0.18em] text-amber-700 dark:bg-white/10 dark:text-amber-300">
                            {alert.status}
                          </span>
                        </div>
                        <p className="mt-1 text-[12px] font-medium text-slate-700 dark:text-slate-300">{alert.customerName}</p>
                        <p className="mt-1 text-[11px] leading-relaxed text-slate-500 dark:text-slate-400">
                          {alert.exceptionReason || 'Keep this delivery visible and move fast on recovery.'}
                        </p>
                      </div>
                    )) : (
                      <div className="rounded-2xl border border-slate-200/70 bg-slate-50/70 p-4 text-sm text-slate-500 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-400">
                        No urgent exceptions. The board is clean.
                      </div>
                    )}
                  </div>

                  <div className="mt-4 rounded-2xl border border-slate-200/70 bg-[linear-gradient(180deg,rgba(255,255,255,0.88),rgba(245,249,255,0.78))] p-4 dark:border-white/10 dark:bg-[linear-gradient(180deg,rgba(255,255,255,0.05),rgba(255,255,255,0.02))]">
                    <div className="flex items-center justify-between">
                      <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-slate-400">Live DB records</p>
                      <ArrowRight className="h-4 w-4 text-blue-500/70" />
                    </div>
                    <div className="mt-3 space-y-2">
                      {liveRecordRows.map((item) => (
                        <button
                          key={item.id}
                          type="button"
                          onClick={item.onClick}
                          disabled={!item.onClick}
                          className={`ops-live-row ${item.onClick ? 'ops-live-row-action' : 'ops-live-row-static'}`}
                        >
                          <div className="min-w-0">
                            <p className="truncate text-[12px] font-semibold text-slate-800 dark:text-slate-200">{item.title}</p>
                            <p className="truncate text-[10px] text-slate-400 dark:text-slate-500">
                              {item.subtitle}
                            </p>
                          </div>
                          <span className="rounded-full bg-slate-100 px-2 py-1 text-[9px] font-semibold uppercase tracking-[0.18em] text-slate-500 dark:bg-white/[0.05] dark:text-slate-300">
                            {item.status}
                          </span>
                          {item.onClick ? (
                            <span className="inline-flex items-center gap-1 text-[10px] font-semibold uppercase tracking-[0.18em] text-blue-500/80">
                              {item.actionLabel}
                              <ChevronRight className="h-4 w-4 shrink-0 text-slate-300" />
                            </span>
                          ) : (
                            <span className="text-[10px] font-semibold uppercase tracking-[0.18em] text-slate-300">Synced</span>
                          )}
                        </button>
                      ))}
                    </div>
                  </div>
                </div>
              </div>
            </div>

            <div className="rounded-[32px] border border-white/75 bg-[linear-gradient(180deg,rgba(12,18,34,0.96),rgba(7,12,24,0.92))] p-5 shadow-[0_24px_80px_rgba(0,0,0,0.32)] backdrop-blur-3xl">
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
                  Built to impress on first look and hold up under live operational pressure.
                </h2>
                <p className="mt-3 max-w-xl text-[14px] leading-relaxed text-slate-300/80">
                  Every panel on this workspace is telling the same business story: faster response, tighter SLA control, fewer customer surprises, and actions that update the real tenant dataset instead of a demo layer.
                </p>
              </div>

              <div className="mt-6 grid gap-3">
                <div className="rounded-[24px] border border-white/10 bg-white/[0.04] p-4">
                  <div className="flex items-center justify-between">
                    <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-white/50">Route intelligence</p>
                    <MapPinned className="h-4 w-4 text-cyan-300/80" />
                  </div>
                  <div className="mt-3 space-y-3">
                    {routes.slice(0, 3).map((route) => (
                      <div key={route.id} className="rounded-2xl border border-white/10 bg-white/[0.03] p-3">
                        <div className="flex items-center justify-between gap-3">
                          <div>
                            <p className="text-[12px] font-bold text-white">{route.routeCode}</p>
                            <p className="text-[11px] text-slate-400">{route.driverName} · {route.vehicleNumber}</p>
                          </div>
                          <span className="text-[10px] font-bold text-cyan-300">{route.completionPercent.toFixed(1)}%</span>
                        </div>
                        <div className="mt-2 h-1.5 rounded-full bg-white/10">
                          <div className="h-1.5 rounded-full bg-gradient-to-r from-cyan-400 via-sky-400 to-blue-500" style={{ width: `${Math.min(100, route.completionPercent)}%` }} />
                        </div>
                      </div>
                    ))}
                  </div>
                </div>

                <div className="rounded-[24px] border border-white/10 bg-white/[0.04] p-4">
                  <div className="flex items-center justify-between">
                    <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-white/50">
                      {mode === 'orders' ? 'Create job or order' : mode === 'routes' ? 'Create route' : mode === 'delivery' ? 'Delivery exception notes' : 'Create dispatch order'}
                    </p>
                    <Package className="h-4 w-4 text-sky-300/80" />
                  </div>
                  <div className="mt-3 space-y-3">
                    {(mode === 'dispatch' || mode === 'orders') && (
                      <>
                        <input value={orderForm.orderNumber} onChange={(e) => setOrderForm((current) => ({ ...current, orderNumber: e.target.value }))} placeholder="Order number" className="w-full rounded-2xl border border-white/10 bg-white/[0.05] px-3 py-2.5 text-sm text-white outline-none placeholder:text-slate-500" />
                        <input value={orderForm.customerName} onChange={(e) => setOrderForm((current) => ({ ...current, customerName: e.target.value }))} placeholder="Customer name" className="w-full rounded-2xl border border-white/10 bg-white/[0.05] px-3 py-2.5 text-sm text-white outline-none placeholder:text-slate-500" />
                        <div className="grid grid-cols-2 gap-2">
                          <input value={orderForm.city} onChange={(e) => setOrderForm((current) => ({ ...current, city: e.target.value }))} placeholder="City" className="rounded-2xl border border-white/10 bg-white/[0.05] px-3 py-2.5 text-sm text-white outline-none placeholder:text-slate-500" />
                          <input value={orderForm.routeCode} onChange={(e) => setOrderForm((current) => ({ ...current, routeCode: e.target.value }))} placeholder="Route code" className="rounded-2xl border border-white/10 bg-white/[0.05] px-3 py-2.5 text-sm text-white outline-none placeholder:text-slate-500" />
                        </div>
                        <button onClick={handleCreateOrder} disabled={creating} className="inline-flex w-full items-center justify-center rounded-2xl bg-gradient-to-r from-blue-600 via-sky-500 to-cyan-400 px-4 py-3 text-[12px] font-bold text-white shadow-[0_14px_30px_rgba(47,107,255,0.26)] transition hover:brightness-105 disabled:opacity-60">
                          {creating ? 'Creating...' : 'Create order'}
                        </button>
                      </>
                    )}
                    {mode === 'routes' && (
                      <>
                        <input value={routeForm.routeCode} onChange={(e) => setRouteForm((current) => ({ ...current, routeCode: e.target.value }))} placeholder="Route code" className="w-full rounded-2xl border border-white/10 bg-white/[0.05] px-3 py-2.5 text-sm text-white outline-none placeholder:text-slate-500" />
                        <div className="grid grid-cols-2 gap-2">
                          <input value={routeForm.driverName} onChange={(e) => setRouteForm((current) => ({ ...current, driverName: e.target.value }))} placeholder="Driver name" className="rounded-2xl border border-white/10 bg-white/[0.05] px-3 py-2.5 text-sm text-white outline-none placeholder:text-slate-500" />
                          <input value={routeForm.vehicleNumber} onChange={(e) => setRouteForm((current) => ({ ...current, vehicleNumber: e.target.value }))} placeholder="Vehicle number" className="rounded-2xl border border-white/10 bg-white/[0.05] px-3 py-2.5 text-sm text-white outline-none placeholder:text-slate-500" />
                        </div>
                        <button onClick={handleCreateRoute} disabled={creating} className="inline-flex w-full items-center justify-center rounded-2xl bg-gradient-to-r from-sky-600 via-cyan-500 to-teal-400 px-4 py-3 text-[12px] font-bold text-white shadow-[0_14px_30px_rgba(47,107,255,0.26)] transition hover:brightness-105 disabled:opacity-60">
                          {creating ? 'Creating...' : 'Create route'}
                        </button>
                      </>
                    )}
                    {mode === 'delivery' && (
                      <>
                        <input value={lastMileForm.recipientName} onChange={(e) => setLastMileForm((current) => ({ ...current, recipientName: e.target.value }))} placeholder="Recipient name override" className="w-full rounded-2xl border border-white/10 bg-white/[0.05] px-3 py-2.5 text-sm text-white outline-none placeholder:text-slate-500" />
                        <textarea value={lastMileForm.exceptionReason} onChange={(e) => setLastMileForm((current) => ({ ...current, exceptionReason: e.target.value }))} rows={3} placeholder="Attempt / reschedule reason" className="w-full rounded-2xl border border-white/10 bg-white/[0.05] px-3 py-2.5 text-sm text-white outline-none placeholder:text-slate-500" />
                      </>
                    )}
                  </div>
                </div>

                <div className="rounded-[24px] border border-white/10 bg-white/[0.04] p-4">
                  <div className="flex items-center justify-between">
                    <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-white/50">
                      {mode === 'routes' ? 'Stops on selected route' : 'Last-mile visibility'}
                    </p>
                    <Truck className="h-4 w-4 text-sky-300/80" />
                  </div>
                  <div className="mt-3 space-y-2">
                    {mode === 'delivery' && routes.length > 0 && (
                      <div className="mb-3 flex flex-wrap gap-2">
                        {routes.slice(0, 4).map((route) => {
                          const active = route.id === selectedRouteId;
                          return (
                            <button
                              key={route.id}
                              type="button"
                              onClick={() => setSelectedRouteId(route.id)}
                              className={`rounded-full px-3 py-1.5 text-[10px] font-semibold uppercase tracking-[0.18em] transition ${
                                active
                                  ? 'bg-cyan-400 text-slate-950 shadow-[0_10px_24px_rgba(34,211,238,0.18)]'
                                  : 'border border-white/10 bg-white/[0.04] text-white/65 hover:bg-white/[0.08]'
                              }`}
                            >
                              {route.routeCode}
                            </button>
                          );
                        })}
                      </div>
                    )}
                    {mode === 'delivery' && selectedRoute && (
                      <p className="mb-3 text-[10px] uppercase tracking-[0.18em] text-white/45">
                        Showing stops for {selectedRoute.routeCode} · {selectedRoute.driverName || 'Assigned driver pending'}
                      </p>
                    )}
                    {visibleStops.slice(0, 4).map((stop) => (
                      <div key={stop.id} className="rounded-2xl border border-white/10 bg-white/[0.03] px-3 py-2.5">
                        <div className="flex items-center justify-between gap-3">
                          <div className="min-w-0">
                            <p className="truncate text-[12px] font-bold text-white">{stop.customerName}</p>
                            <p className="truncate text-[10px] text-slate-400">{stop.addressLine}</p>
                          </div>
                          <span className="rounded-full border border-white/10 px-2 py-1 text-[9px] font-semibold uppercase tracking-[0.18em] text-white/65">
                            {stop.status}
                          </span>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              </div>

              <div className="mt-6 rounded-[26px] border border-white/10 bg-gradient-to-r from-blue-500/12 via-cyan-400/10 to-transparent p-4">
                <div className="flex items-center gap-2">
                  <CheckCircle2 className="h-5 w-5 text-emerald-300" />
                  <p className="text-[11px] font-semibold uppercase tracking-[0.22em] text-white/70">Connected backend</p>
                </div>
                <p className="mt-2 text-[13px] leading-relaxed text-slate-300/80">
                  Actions on this page update real entities in the workflow: orders become dispatched, routes progress their completion state, and last-mile proof changes are persisted through the logistics API.
                </p>
              </div>
            </div>
          </section>
        </div>
      </div>
    </>
  );
}

function ActionOrderCard({ order, onDispatch, saving }: { order: LogisticsOrder; onDispatch: () => void; saving: boolean }) {
  return (
    <div className="rounded-[24px] border border-slate-200/70 bg-[linear-gradient(180deg,rgba(255,255,255,0.92),rgba(245,248,255,0.78))] p-4 shadow-[0_10px_24px_rgba(37,99,235,0.05)] dark:border-white/10 dark:bg-white/[0.04]">
      <div className="flex items-start justify-between gap-3">
        <div>
          <p className="text-[12px] font-black tracking-tight text-slate-950 dark:text-white">{order.orderNumber}</p>
          <p className="text-[11px] text-slate-500 dark:text-slate-400">{order.customerName} · {order.city}</p>
        </div>
        <span className="rounded-full border border-slate-200/70 bg-white px-2.5 py-1 text-[9px] font-semibold uppercase tracking-[0.18em] text-slate-500 dark:border-white/10 dark:bg-white/[0.05] dark:text-slate-300">
          {order.status}
        </span>
      </div>
      <div className="mt-3 flex flex-wrap items-center gap-2 text-[10px] font-semibold uppercase tracking-[0.18em] text-slate-400">
        <span>{order.routeCode}</span>
        <span>•</span>
        <span>{order.priority}</span>
        <span>•</span>
        <span>{order.driverName}</span>
      </div>
      <p className="mt-3 text-[11px] leading-relaxed text-slate-500 dark:text-slate-400">
        {order.priority === 'High' || order.priority === 'Critical'
          ? 'Priority order with visible service impact if dispatch slips.'
          : 'Ready for operational assignment and route ownership.'}
      </p>
      <button
        type="button"
        onClick={onDispatch}
        disabled={saving}
        className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-2xl bg-gradient-to-r from-blue-600 via-sky-500 to-cyan-400 px-4 py-3 text-[12px] font-bold text-white shadow-[0_14px_30px_rgba(47,107,255,0.26)] transition hover:brightness-105 disabled:cursor-not-allowed disabled:opacity-60"
      >
        {saving ? 'Dispatching...' : 'Dispatch order'}
        <ArrowRight className="h-4 w-4" />
      </button>
    </div>
  );
}

function ActionRouteCard({ route, onAdvance, onInspect, saving }: { route: LogisticsRoute; onAdvance: () => void; onInspect: () => void; saving: boolean }) {
  return (
    <div className="rounded-[24px] border border-slate-200/70 bg-[linear-gradient(180deg,rgba(255,255,255,0.92),rgba(245,248,255,0.78))] p-4 shadow-[0_10px_24px_rgba(37,99,235,0.05)] dark:border-white/10 dark:bg-white/[0.04]">
      <div className="flex items-start justify-between gap-3">
        <div>
          <p className="text-[12px] font-black tracking-tight text-slate-950 dark:text-white">{route.routeCode}</p>
          <p className="text-[11px] text-slate-500 dark:text-slate-400">{route.hub} · {route.territory}</p>
        </div>
        <span className="rounded-full border border-slate-200/70 bg-white px-2.5 py-1 text-[9px] font-semibold uppercase tracking-[0.18em] text-slate-500 dark:border-white/10 dark:bg-white/[0.05] dark:text-slate-300">
          {route.status}
        </span>
      </div>
      <div className="mt-3 grid grid-cols-3 gap-2 text-[10px] text-slate-400">
        <span>{route.completedStops}/{route.plannedStops} stops</span>
        <span>{route.distanceKm.toFixed(1)} km</span>
        <span>{route.driverName}</span>
      </div>
      <p className="mt-3 text-[11px] leading-relaxed text-slate-500 dark:text-slate-400">
        {route.nextStop
          ? `Next operational handoff is ${route.nextStop}.`
          : 'Planner view is ready for next-stop progression and route recovery.'}
      </p>
      <div className="mt-4 grid grid-cols-2 gap-2">
        <button
          type="button"
          onClick={onInspect}
          className="inline-flex w-full items-center justify-center gap-2 rounded-2xl border border-slate-200/70 bg-white px-4 py-3 text-[12px] font-bold text-slate-700 transition hover:border-sky-300 hover:text-sky-700 dark:border-white/10 dark:bg-white/[0.05] dark:text-white"
        >
          Inspect stops
        </button>
        <button
          type="button"
          onClick={onAdvance}
          disabled={saving}
          className="inline-flex w-full items-center justify-center gap-2 rounded-2xl bg-gradient-to-r from-sky-600 via-cyan-500 to-teal-400 px-4 py-3 text-[12px] font-bold text-white shadow-[0_14px_30px_rgba(47,107,255,0.26)] transition hover:brightness-105 disabled:cursor-not-allowed disabled:opacity-60"
        >
          {saving ? 'Advancing...' : 'Advance route'}
          <ArrowRight className="h-4 w-4" />
        </button>
      </div>
    </div>
  );
}

function ActionStopCard({ stop, onConfirm, onAttempt, onReschedule, saving }: { stop: LogisticsStop; onConfirm: () => void; onAttempt: () => void; onReschedule: () => void; saving: boolean }) {
  return (
    <div className="rounded-[24px] border border-slate-200/70 bg-[linear-gradient(180deg,rgba(255,255,255,0.92),rgba(245,248,255,0.78))] p-4 shadow-[0_10px_24px_rgba(37,99,235,0.05)] dark:border-white/10 dark:bg-white/[0.04]">
      <div className="flex items-start justify-between gap-3">
        <div>
          <p className="text-[12px] font-black tracking-tight text-slate-950 dark:text-white">{stop.customerName}</p>
          <p className="text-[11px] text-slate-500 dark:text-slate-400">{stop.addressLine}</p>
        </div>
        <span className="rounded-full border border-slate-200/70 bg-white px-2.5 py-1 text-[9px] font-semibold uppercase tracking-[0.18em] text-slate-500 dark:border-white/10 dark:bg-white/[0.05] dark:text-slate-300">
          {stop.status}
        </span>
      </div>
      <div className="mt-3 flex flex-wrap gap-2 text-[10px] font-semibold uppercase tracking-[0.18em] text-slate-400">
        <span>{stop.routeCode}</span>
        <span>•</span>
        <span>{stop.timeWindow}</span>
        <span>•</span>
        <span>{stop.proofStatus}</span>
      </div>
      <p className="mt-3 text-[11px] leading-relaxed text-slate-500 dark:text-slate-400">
        {stop.status === 'Attempted' || stop.status === 'Rescheduled'
          ? 'This doorstep interaction needs a recovery decision, not just a status update.'
          : 'Use proof, attempt, or reschedule actions to keep customer visibility current.'}
      </p>
      <div className="mt-4 grid grid-cols-3 gap-2">
        <button
          type="button"
          onClick={onAttempt}
          disabled={saving}
          className="inline-flex w-full items-center justify-center rounded-2xl border border-amber-200/70 bg-amber-50 px-3 py-3 text-[11px] font-bold text-amber-700 transition hover:border-amber-300 disabled:opacity-60"
        >
          Attempt
        </button>
        <button
          type="button"
          onClick={onReschedule}
          disabled={saving}
          className="inline-flex w-full items-center justify-center rounded-2xl border border-slate-200/70 bg-white px-3 py-3 text-[11px] font-bold text-slate-700 transition hover:border-sky-300 disabled:opacity-60 dark:border-white/10 dark:bg-white/[0.05] dark:text-white"
        >
          Reschedule
        </button>
        <button
          type="button"
          onClick={onConfirm}
          disabled={saving}
          className="inline-flex w-full items-center justify-center gap-2 rounded-2xl bg-gradient-to-r from-emerald-600 via-teal-500 to-cyan-400 px-3 py-3 text-[11px] font-bold text-white shadow-[0_14px_30px_rgba(47,107,255,0.22)] transition hover:brightness-105 disabled:cursor-not-allowed disabled:opacity-60"
        >
          {saving ? 'Saving...' : 'Deliver'}
        </button>
      </div>
    </div>
  );
}
