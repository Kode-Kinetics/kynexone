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
    title: 'Command the day from one live operations board.',
    subtitle: 'See queued work, live routes, exceptions, and delivery progress in a single control room built for teams who own the floor.',
    accent: 'from-blue-600 via-sky-500 to-cyan-400',
    path: '/dispatch',
    summary: 'Reduce manual handoffs, protect SLAs, and surface exceptions before customers feel the delay.',
  },
  orders: {
    label: 'Jobs & Orders',
    title: 'Turn inbound demand into an executable order pipeline.',
    subtitle: 'One workspace for order intake, prioritisation, assignment, and dispatch decisions with business context visible at a glance.',
    accent: 'from-indigo-600 via-blue-500 to-cyan-400',
    path: '/dispatch/jobs-orders',
    summary: 'Keep intake, priority, route assignment, and fulfilment status in one place so teams stop chasing spreadsheets.',
  },
  routes: {
    label: 'Route Planning',
    title: 'Build routes that make operational sense, not just map sense.',
    subtitle: 'Balance stops, vehicle load, distance, and SLA pressure with a planner that feels like a real logistics cockpit.',
    accent: 'from-sky-600 via-cyan-500 to-teal-400',
    path: '/dispatch/route-planning',
    summary: 'See route density, stop completion, and live movement so planners can adjust the day without losing control.',
  },
  delivery: {
    label: 'Last Mile Delivery',
    title: 'Make every drop-off visible from handoff to proof of delivery.',
    subtitle: 'Track rider progress, proof status, attempts, and customer exceptions in a clean delivery console built for scale.',
    accent: 'from-cyan-600 via-sky-500 to-blue-400',
    path: '/dispatch/last-mile-delivery',
    summary: 'Show proof, capture exceptions, and keep the last mile accountable all the way to the customer door.',
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
  const [loading, setLoading] = useState(true);
  const [savingId, setSavingId] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    (async () => {
      try {
        const [ov, orderRes, routeRes, stopRes] = await Promise.all([
          logisticsApi.overview(),
          mode === 'dispatch' || mode === 'orders' ? logisticsApi.orders({ pageSize: 12 }) : Promise.resolve(null),
          mode === 'dispatch' || mode === 'routes' ? logisticsApi.routes() : Promise.resolve(null),
          mode === 'dispatch' || mode === 'delivery' ? logisticsApi.lastMile({ pageSize: 12 }) : Promise.resolve(null),
        ]);
        if (cancelled) return;
        setOverview(ov);
        setOrders(orderRes?.items ?? ov.orderCards);
        setRoutes(routeRes?.items ?? ov.routeCards);
        setStops(stopRes?.items ?? ov.liveStops);
      } catch (err) {
        if (!cancelled) notifyApiError(err, 'Unable to load logistics workspace.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => { cancelled = true; };
  }, [mode]);

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

  const liveRecordRows = useMemo(() => {
    if (mode === 'routes') {
      return routes.slice(0, 5).map((route) => ({
        id: route.id,
        title: route.routeCode,
        subtitle: `${route.hub} · ${route.territory}`,
        status: route.status,
      }));
    }

    if (mode === 'delivery') {
      return stops.slice(0, 5).map((stop) => ({
        id: stop.id,
        title: stop.customerName,
        subtitle: stop.addressLine,
        status: stop.status,
      }));
    }

    return orders.slice(0, 5).map((order) => ({
      id: order.id,
      title: order.orderNumber,
      subtitle: `${order.customerName} · ${order.city}`,
      status: order.status,
    }));
  }, [mode, orders, routes, stops]);

  const alerts = overview?.alerts ?? [];

  const handleDispatch = async (order: LogisticsOrder) => {
    setSavingId(order.id);
    try {
      await logisticsApi.dispatchOrder(order.id, {
        routeCode: order.routeCode,
        driverName: order.driverName,
        vehicleNumber: order.vehicleNumber,
        notes: order.dispatchNotes,
      });
      const refreshed = await logisticsApi.overview();
      setOverview(refreshed);
      setOrders(refreshed.orderCards.length > 0 ? refreshed.orderCards : orders);
      setRoutes(refreshed.routeCards.length > 0 ? refreshed.routeCards : routes);
      setStops(refreshed.liveStops.length > 0 ? refreshed.liveStops : stops);
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
      const refreshed = await logisticsApi.overview();
      setOverview(refreshed);
      setRoutes(refreshed.routeCards.length > 0 ? refreshed.routeCards : routes);
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
        recipientName: stop.recipientName || stop.customerName.split(' ')[0],
        proofStatus: 'POD',
      });
      const refreshed = await logisticsApi.overview();
      setOverview(refreshed);
      setStops(refreshed.liveStops.length > 0 ? refreshed.liveStops : stops);
    } catch (err) {
      notifyApiError(err, 'Unable to confirm delivery.');
    } finally {
      setSavingId(null);
    }
  };

  return (
    <>
      <style>{`
        @keyframes kx-breathe {
          0%, 100% { transform: translateY(0px); opacity: 0.75; }
          50% { transform: translateY(-8px); opacity: 1; }
        }
        @keyframes kx-sweep {
          0% { transform: translateX(-30%); }
          100% { transform: translateX(30%); }
        }
        @keyframes kx-grid {
          0%, 100% { opacity: 0.12; }
          50% { opacity: 0.18; }
        }
        .kx-breathe { animation: kx-breathe 8s ease-in-out infinite; }
        .kx-sweep { animation: kx-sweep 16s linear infinite; }
        .kx-grid { animation: kx-grid 10s ease-in-out infinite; }
        @media (prefers-reduced-motion: reduce) {
          .kx-breathe,
          .kx-sweep,
          .kx-grid { animation: none !important; }
        }
      `}</style>

      <div className="relative min-h-[100svh] overflow-hidden bg-[#eef3ff] text-slate-900 dark:bg-[#040814] dark:text-white">
        <div className="pointer-events-none absolute inset-0 bg-[radial-gradient(circle_at_top_left,rgba(47,107,255,0.16),transparent_28%),radial-gradient(circle_at_80%_18%,rgba(94,235,255,0.10),transparent_22%),linear-gradient(180deg,rgba(255,255,255,0.5),transparent_34%)] dark:bg-[radial-gradient(circle_at_top_left,rgba(47,107,255,0.20),transparent_28%),radial-gradient(circle_at_80%_18%,rgba(94,235,255,0.08),transparent_22%),linear-gradient(180deg,rgba(6,11,24,0.92),rgba(4,8,20,0.98))]" />
        <div className="pointer-events-none absolute inset-0 opacity-[0.16] mix-blend-soft-light [background-image:linear-gradient(rgba(47,107,255,0.07)_1px,transparent_1px),linear-gradient(90deg,rgba(47,107,255,0.07)_1px,transparent_1px)] [background-size:72px_72px] kx-grid" />
        <div className="pointer-events-none absolute left-[8%] top-[14%] h-56 w-56 rounded-full bg-white/35 blur-3xl kx-breathe" />
        <div className="pointer-events-none absolute right-[8%] top-[18%] h-64 w-64 rounded-full bg-cyan-300/12 blur-3xl kx-breathe" />
        <div className="pointer-events-none absolute bottom-[-5rem] left-[20%] h-72 w-72 rounded-full bg-blue-400/12 blur-3xl kx-breathe" />
        <div className="pointer-events-none absolute inset-x-0 top-1/2 h-px bg-[linear-gradient(90deg,transparent,rgba(47,107,255,0.20),rgba(94,235,255,0.24),transparent)] opacity-40 kx-sweep" />

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
                        <ActionRouteCard key={route.id} route={route} saving={savingId === route.id} onAdvance={() => handleProgress(route)} />
                      ))
                    ) : mode === 'delivery' ? (
                      stops.slice(0, 6).map((stop) => (
                        <ActionStopCard key={stop.id} stop={stop} saving={savingId === stop.id} onConfirm={() => handleConfirm(stop)} />
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
                        <div key={item.id} className="flex items-center justify-between gap-3 rounded-xl bg-white/70 px-3 py-2 dark:bg-white/[0.04]">
                          <div className="min-w-0">
                            <p className="truncate text-[12px] font-semibold text-slate-800 dark:text-slate-200">{item.title}</p>
                            <p className="truncate text-[10px] text-slate-400 dark:text-slate-500">
                              {item.subtitle}
                            </p>
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
                  Built for the people who move the day forward.
                </h2>
                <p className="mt-3 max-w-xl text-[14px] leading-relaxed text-slate-300/80">
                  This screen works as a premium sub-landing page and a control room at the same time: the story is strong, the data is live, and every action writes back to the database.
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
                    <p className="text-[10px] font-semibold uppercase tracking-[0.22em] text-white/50">Last-mile visibility</p>
                    <Truck className="h-4 w-4 text-sky-300/80" />
                  </div>
                  <div className="mt-3 space-y-2">
                    {stops.slice(0, 4).map((stop) => (
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
                  Actions on this page update the database: dispatching orders, advancing routes, and confirming proof of delivery all round-trip through the API.
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

function ActionRouteCard({ route, onAdvance, saving }: { route: LogisticsRoute; onAdvance: () => void; saving: boolean }) {
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
      <button
        type="button"
        onClick={onAdvance}
        disabled={saving}
        className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-2xl bg-gradient-to-r from-sky-600 via-cyan-500 to-teal-400 px-4 py-3 text-[12px] font-bold text-white shadow-[0_14px_30px_rgba(47,107,255,0.26)] transition hover:brightness-105 disabled:cursor-not-allowed disabled:opacity-60"
      >
        {saving ? 'Advancing...' : 'Advance route'}
        <ArrowRight className="h-4 w-4" />
      </button>
    </div>
  );
}

function ActionStopCard({ stop, onConfirm, saving }: { stop: LogisticsStop; onConfirm: () => void; saving: boolean }) {
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
      <button
        type="button"
        onClick={onConfirm}
        disabled={saving}
        className="mt-4 inline-flex w-full items-center justify-center gap-2 rounded-2xl bg-gradient-to-r from-emerald-600 via-teal-500 to-cyan-400 px-4 py-3 text-[12px] font-bold text-white shadow-[0_14px_30px_rgba(47,107,255,0.22)] transition hover:brightness-105 disabled:cursor-not-allowed disabled:opacity-60"
      >
        {saving ? 'Confirming...' : 'Confirm delivery'}
        <CheckCircle2 className="h-4 w-4" />
      </button>
    </div>
  );
}
