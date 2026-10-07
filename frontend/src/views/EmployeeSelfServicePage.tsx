'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';
import {
  MessageSquareText, Loader2, CalendarOff, Send, FileText, Clock,
  ChevronRight, Megaphone, CheckCircle2, AlertCircle, Zap, ClipboardList,
  CreditCard, Banknote, Target, Calendar, CalendarClock, BadgeCheck, User,
  FileSignature, MessageCircleReply, type LucideIcon,
} from 'lucide-react';
import { ESS_PAYSLIPS_PATH } from '../lib/essPayslip';
import { ESS_LEAVE_PATH, ESS_OVERTIME_PATH, ESS_REQUESTS_PATH, hrRequestStatus, readSeenReplies, splitMinutes } from '../lib/essSelfService';
import { ESS_DOCUMENTS_PATH } from '../routes/essSections';
import { essActionsApi, essApi, type EssDashboard, type EssHrRequest, type EssRosterEntry } from '../api/ess';
import type { LeaveType } from '../api/leave';
import { useAuth } from '../contexts/AuthContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import { enumLabel } from '../i18n/enumLabel';
import { StatusChip } from '../components/StatusChip';
import { useCanWriteEss } from '../components/ess/EssParts';

/**
 * Self-Service overview: the employee's day, exception first. What needs them comes before any
 * number; every number links to the page that explains it; the rest of the workspace (pay, leave,
 * requests, benefits) sits in the tabs above (EssWorkspaceNav), not in a sidebar of its own.
 */

// ── helpers ───────────────────────────────────────────────────────────────────

type T = ReturnType<typeof useLocale>['t'];
type Tone = 'blue' | 'emerald' | 'amber' | 'rose' | 'violet' | 'slate';

const TONE_ICON: Record<Tone, string> = {
  blue: 'bg-blue-500/10 text-blue-600 dark:text-blue-400',
  emerald: 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400',
  amber: 'bg-amber-500/10 text-amber-600 dark:text-amber-400',
  rose: 'bg-rose-500/10 text-rose-600 dark:text-rose-400',
  violet: 'bg-violet-500/10 text-violet-600 dark:text-violet-400',
  slate: 'bg-slate-500/10 text-slate-600 dark:text-slate-300',
};

/** A whole greeting sentence, so Arabic can order the name and punctuation itself. */
function greeting(t: T, name: string): string {
  const h = new Date().getHours();
  if (h < 12) return t('Good morning, {name}', { name });
  if (h < 17) return t('Good afternoon, {name}', { name });
  return t('Good evening, {name}', { name });
}

function workedTime(t: T, value?: number): string {
  return t('{hours} h {minutes} min', splitMinutes(value ?? 0));
}

function tenureLabel(t: T, months: number): string {
  if (months < 12) return t('{months} months of service', { months });
  const years = Math.floor(months / 12);
  const rest = months % 12;
  return rest > 0
    ? t('{years} years {months} months of service', { years, months: rest })
    : t('{years} years of service', { years });
}

/** Leave days as people say them: "15 days", and a decimal only when there is one ("2.5 days"). */
function useDays(): (n: number) => string {
  const fx = useFormat();
  return (n: number) => (Number.isInteger(n) ? fx.number(n) : fx.number(n, { minimumFractionDigits: 1, maximumFractionDigits: 1 }));
}

const primaryButton =
  'inline-flex items-center gap-1.5 rounded-xl bg-sapphire px-4 py-2 text-sm font-semibold text-white transition hover:bg-sapphire/90 dark:bg-cyanAccent dark:text-slate-900';
const secondaryButton =
  'inline-flex items-center gap-1.5 rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm font-semibold text-slate-700 transition hover:bg-slate-50 dark:border-white/[0.12] dark:bg-white/[0.04] dark:text-slate-200 dark:hover:bg-white/[0.08]';
const textLink = 'text-xs font-semibold text-sapphire hover:underline dark:text-cyanAccent';

// ── Building blocks ───────────────────────────────────────────────────────────

function Panel({ title, icon: Icon, tone = 'slate', action, children, testId }: {
  title: string; icon: LucideIcon; tone?: Tone; action?: React.ReactNode; children: React.ReactNode; testId?: string;
}) {
  return (
    <section className="wg-card rounded-2xl" data-testid={testId}>
      <div className="flex items-center justify-between gap-3 border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
        <h2 className="flex items-center gap-2.5 text-sm font-semibold text-slate-900 dark:text-white">
          <span className={`flex h-7 w-7 items-center justify-center rounded-lg ${TONE_ICON[tone]}`}><Icon className="h-3.5 w-3.5" aria-hidden="true" /></span>
          {title}
        </h2>
        {action}
      </div>
      <div className="p-5">{children}</div>
    </section>
  );
}

function Quiet({ children }: { children: React.ReactNode }) {
  return <p className="text-sm text-slate-400 dark:text-slate-500">{children}</p>;
}

/**
 * One headline number. It says what it is before what it is worth, and when there is a page that
 * explains it the whole tile is the link there.
 */
function StatTile({ icon: Icon, tone, label, value, detail, note, note2, href, linkLabel, empty, testId }: {
  icon: LucideIcon; tone: Tone; label: string; value?: React.ReactNode; detail?: string; note?: string; note2?: string;
  href?: string; linkLabel?: string; empty?: string; testId?: string;
}) {
  const body = (
    <>
      <div className="flex items-center gap-2.5">
        <span className={`flex h-8 w-8 items-center justify-center rounded-lg ${TONE_ICON[tone]}`}><Icon className="h-4 w-4" aria-hidden="true" /></span>
        <p className="text-xs font-semibold text-slate-500 dark:text-slate-400">{label}</p>
      </div>
      <div className="mt-3 flex-1">
        {empty ? (
          <p className="text-sm text-slate-400 dark:text-slate-500">{empty}</p>
        ) : (
          <>
            <p className="text-2xl font-extrabold leading-tight tracking-tight text-slate-900 tabular-nums dark:text-white"><bdi>{value}</bdi></p>
            {detail && <p className="mt-1 text-xs text-slate-600 dark:text-slate-300">{detail}</p>}
            {note && <p className="text-xs text-slate-400 dark:text-slate-500">{note}</p>}
            {note2 && <p className="text-xs text-slate-400 dark:text-slate-500">{note2}</p>}
          </>
        )}
      </div>
      {href && linkLabel && (
        <p className="mt-3 flex items-center gap-1 text-xs font-semibold text-sapphire dark:text-cyanAccent">
          {linkLabel}
          <ChevronRight className="h-3.5 w-3.5 transition-transform group-hover:translate-x-0.5 rtl:-scale-x-100 rtl:group-hover:-translate-x-0.5" aria-hidden="true" />
        </p>
      )}
    </>
  );
  const cls = 'wg-card flex h-full flex-col rounded-2xl p-4';
  return href ? (
    <Link href={href} data-testid={testId} className={`${cls} wg-press group outline-none transition-shadow hover:shadow-md focus-visible:ring-2 focus-visible:ring-sapphire`}>{body}</Link>
  ) : (
    <div data-testid={testId} className={cls}>{body}</div>
  );
}

// ── Leave balance bar ────────────────────────────────────────────────────────

function LeaveBar({ name, available, entitled, statutoryDays }: { name: string; available: number; entitled: number; statutoryDays?: number | null }) {
  const { t } = useLocale();
  const fx = useFormat();
  if (statutoryDays != null) {
    // Saudi statutory event leave is granted by law per event, not drawn from a balance, so its
    // "available" can read negative while a request is pending. Show the entitlement instead.
    return (
      <div className="rounded-xl border border-slate-100 p-3 dark:border-white/[0.06]">
        <p className="text-sm font-medium text-slate-800 dark:text-slate-200">{name}</p>
        <p className="mt-1 text-xs text-emerald-700 dark:text-emerald-300">
          {t('Statutory entitlement: {days} days per event', { days: fx.number(statutoryDays) })}
        </p>
      </div>
    );
  }
  const days = useDays();
  const pct = entitled > 0 ? Math.round((available / entitled) * 100) : 0;
  const color = pct >= 60 ? 'bg-emerald-500' : pct >= 30 ? 'bg-amber-500' : 'bg-rose-500';
  return (
    <div className="rounded-xl border border-slate-100 p-3 dark:border-white/[0.06]">
      <div className="flex items-baseline justify-between gap-2">
        <p className="truncate text-sm font-medium text-slate-800 dark:text-slate-200">{name}</p>
        <p className="shrink-0 text-xs tabular-nums text-slate-500 dark:text-slate-400">
          {entitled > 0
            ? t('{available} of {entitled} days', { available: days(available), entitled: days(entitled) })
            : t('{available} days', { available: days(available) })}
        </p>
      </div>
      <div className="mt-2 h-1.5 w-full overflow-hidden rounded-full bg-slate-100 dark:bg-white/[0.07]">
        {/* eslint-disable-next-line react/forbid-dom-props */}
        <div className={`h-full rounded-full transition-all duration-700 ${color}`} style={{ width: `${Math.min(100, Math.max(0, pct))}%` }} />
      </div>
    </div>
  );
}

// ── My Roster card (own shifts only) ──────────────────────────────────────────

function MyShiftsCard() {
  const { t } = useLocale();
  const fx = useFormat();
  const [shifts, setShifts] = useState<EssRosterEntry[] | null>(null);

  useEffect(() => {
    const from = new Date().toISOString().slice(0, 10);
    const to = new Date(Date.now() + 13 * 86400000).toISOString().slice(0, 10);
    essApi.myRoster(from, to).then(setShifts).catch(() => setShifts([]));
  }, []);

  return (
    <Panel title={t('My Upcoming Shifts')} icon={Calendar} tone="blue"
      action={<span className="text-[11px] font-medium text-slate-400">{t('Next 2 weeks')}</span>}>
      {shifts === null ? (
        <Quiet>{t('Loading…')}</Quiet>
      ) : shifts.length === 0 ? (
        <Quiet>{t('No shifts scheduled. Your roster will appear here once published.')}</Quiet>
      ) : (
        <ul className="grid gap-2 sm:grid-cols-2">
          {shifts.map((s) => (
            <li key={s.id} className="flex items-center gap-3 rounded-xl border border-slate-100 px-3 py-2 dark:border-white/[0.06]">
              <span className="h-2.5 w-2.5 shrink-0 rounded-full" style={{ backgroundColor: s.shiftColor || '#2F6BFF' }} />
              <span className="truncate text-sm font-medium text-slate-800 dark:text-slate-200">{s.shiftName}</span>
              <span className="ms-auto shrink-0 text-xs text-slate-500 dark:text-slate-400">{fx.date(s.date, 'weekdayDate')}</span>
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}

// ── Needs your attention ──────────────────────────────────────────────────────

interface AttentionItem {
  id: string;
  tone: 'rose' | 'amber' | 'blue';
  icon: LucideIcon;
  title: string;
  detail: string;
  action?: { label: string; href: string };
}

function AttentionPanel({ items, incomplete, onRetry }: { items: AttentionItem[]; incomplete: boolean; onRetry: () => void }) {
  const { t } = useLocale();
  // A failed read is not "all clear": say what could not be checked instead.
  const unchecked = incomplete && (
    <p role="alert" className="flex flex-wrap items-center gap-2 px-5 py-3 text-xs text-amber-700 dark:text-amber-300">
      <AlertCircle className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
      {t('Your HR requests could not be loaded, so replies from HR may be missing here.')}
      <button type="button" onClick={onRetry} className="font-semibold underline">{t('Retry')}</button>
    </p>
  );
  if (items.length === 0 && incomplete) {
    return <section className="wg-card rounded-2xl" data-testid="ess-attention">{unchecked}</section>;
  }
  if (items.length === 0) {
    return (
      <p className="wg-card flex items-center gap-2.5 rounded-2xl px-5 py-3.5 text-sm text-slate-600 dark:text-slate-300" data-testid="ess-attention">
        <CheckCircle2 className="h-4 w-4 shrink-0 text-emerald-500" aria-hidden="true" />
        {t('Nothing needs your attention right now.')}
      </p>
    );
  }
  return (
    <section className="wg-card rounded-2xl" data-testid="ess-attention" aria-labelledby="ess-attention-title">
      <div className="flex items-center justify-between gap-3 px-5 pt-4">
        <h2 id="ess-attention-title" className="text-sm font-semibold text-slate-900 dark:text-white">{t('Needs your attention')}</h2>
        <span className={`rounded-full px-2 py-0.5 text-[11px] font-bold ${items.some((i) => i.tone === 'rose')
          ? 'bg-rose-500/10 text-rose-600 dark:text-rose-400'
          : 'bg-slate-500/10 text-slate-600 dark:text-slate-300'}`}>
          {t('{count, plural, one {# item} other {# items}}', { count: items.length })}
        </span>
      </div>
      <ul className="divide-y divide-slate-100 px-5 pb-2 pt-2 dark:divide-white/[0.06]">
        {items.map((item, index) => {
          const Icon = item.icon;
          // The first item with a next step carries the page's one filled button.
          const primary = index === items.findIndex((i) => i.action);
          return (
            <li key={item.id} className="flex flex-wrap items-center gap-3 py-3 sm:flex-nowrap">
              <span className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-lg ${TONE_ICON[item.tone]}`}><Icon className="h-4 w-4" aria-hidden="true" /></span>
              {/* Phones: the text takes the row and the button drops beneath it, under the text. */}
              <div className="min-w-0 flex-1 max-sm:basis-[calc(100%-2.75rem)]">
                <p className="text-sm font-medium text-slate-900 dark:text-white">{item.title}</p>
                <p className="text-xs text-slate-500 dark:text-slate-400">{item.detail}</p>
              </div>
              {item.action && (
                <Link href={item.action.href} className={`ms-11 shrink-0 rounded-lg px-3 py-1.5 text-xs font-semibold transition sm:ms-0 ${primary
                  ? 'bg-sapphire text-white hover:bg-sapphire/90 dark:bg-cyanAccent dark:text-slate-900'
                  : 'border border-slate-200 text-slate-700 hover:bg-slate-50 dark:border-white/[0.1] dark:text-slate-200 dark:hover:bg-white/[0.05]'}`}>
                  {item.action.label}
                </Link>
              )}
            </li>
          );
        })}
      </ul>
      {unchecked}
    </section>
  );
}

// ── Coming up ─────────────────────────────────────────────────────────────────

function UpcomingRow({ icon: Icon, tone, label, sub, chip }: { icon: LucideIcon; tone: Tone; label: string; sub: string; chip?: string }) {
  return (
    <li className="flex items-start gap-3">
      <span className={`mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-lg ${TONE_ICON[tone]}`}><Icon className="h-3.5 w-3.5" aria-hidden="true" /></span>
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium text-slate-900 dark:text-white">{label}</p>
        <p className="text-xs text-slate-500 dark:text-slate-400">{sub}</p>
      </div>
      {chip && <StatusChip label={chip} tone="emerald" />}
    </li>
  );
}

// ── Ask Kody (the employee's own HR questions) ────────────────────────────────

function AskKodyCard() {
  const { t } = useLocale();
  const [question, setQuestion] = useState('');
  const [answer, setAnswer] = useState('');
  const [asking, setAsking] = useState(false);

  const askAi = async () => {
    if (!question.trim()) return;
    setAsking(true); setAnswer('');
    try { const res = await essApi.askAi(question); setAnswer(res.answer); }
    catch (err: unknown) {
      const e = err as { response?: { data?: { message?: string } } };
      setAnswer(e.response?.data?.message ?? t('The assistant could not answer right now.'));
    } finally { setAsking(false); }
  };

  return (
    <Panel title={t('Kody the HR Assistant')} icon={MessageSquareText} tone="blue">
      <div className="space-y-3">
        <textarea
          value={question}
          onChange={(e) => setQuestion(e.target.value)}
          onKeyDown={(e) => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) void askAi(); }}
          placeholder={t('Ask anything: your leave balance, policies, payslip dates…')}
          aria-label={t('Ask Kody')}
          rows={2}
          className="w-full resize-none rounded-xl border border-slate-200 bg-slate-50/80 px-3.5 py-2.5 text-sm text-slate-900 placeholder-slate-400 outline-none transition focus:border-sapphire/50 focus:ring-2 focus:ring-sapphire/10 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-white dark:placeholder-slate-600 dark:focus:border-cyanAccent/40"
        />
        <div className="flex items-center justify-end gap-3 sm:justify-between">
          <p className="hidden text-[11px] text-slate-400 dark:text-slate-500 sm:block">{t('Press Ctrl+Enter or ⌘+Enter to send')}</p>
          <button
            type="button"
            onClick={() => void askAi()}
            disabled={asking || !question.trim()}
            className="inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-3.5 py-1.5 text-sm font-semibold text-white transition hover:bg-sapphire/90 disabled:opacity-50 dark:bg-cyanAccent dark:text-slate-900"
          >
            {asking ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Send className="h-3.5 w-3.5" />}
            {asking ? t('Thinking…') : t('Ask Kody')}
          </button>
        </div>
        {answer && (
          <div className="rounded-xl border border-sapphire/15 bg-sapphire/[0.04] p-3.5 text-sm leading-relaxed text-slate-700 dark:border-cyanAccent/15 dark:bg-cyanAccent/[0.04] dark:text-slate-200">
            {answer}
          </div>
        )}
      </div>
    </Panel>
  );
}

// ── Loading skeleton ──────────────────────────────────────────────────────────

function Skeleton({ className }: { className?: string }) {
  return <div className={`animate-pulse rounded-2xl bg-slate-200/70 dark:bg-white/[0.06] ${className}`} />;
}

function OverviewSkeleton() {
  const { t } = useLocale();
  return (
    <div className="space-y-5" aria-busy="true" aria-label={t('Loading your workspace')}>
      <Skeleton className="h-40 w-full" />
      <Skeleton className="h-14 w-full" />
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        {[1, 2, 3, 4].map((i) => <Skeleton key={i} className="h-36" />)}
      </div>
      <div className="grid gap-5 lg:grid-cols-3">
        <Skeleton className="h-64 lg:col-span-2" />
        <Skeleton className="h-64" />
      </div>
    </div>
  );
}

// ── Main page ─────────────────────────────────────────────────────────────────

export function EmployeeSelfServicePage() {
  const { user, hasPermission } = useAuth();
  const { t, locale } = useLocale();
  const fx = useFormat();
  const canWrite = useCanWriteEss();
  const days = useDays();
  const { isFeatureEnabled, verdictForPath } = useFeatureFlags();
  const [dashboard, setDashboard] = useState<EssDashboard | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);
  const [leaveTypes, setLeaveTypes] = useState<LeaveType[]>([]);
  // HR requests: the latest few, read here; raised and answered on /ess/requests.
  const [myRequests, setMyRequests] = useState<EssHrRequest[]>([]);
  // A failed read must not look like "no replies": the attention panel says so instead of "all clear".
  const [requestsFailed, setRequestsFailed] = useState(false);

  const load = async () => {
    setLoading(true); setError('');
    try {
      setDashboard(await essApi.dashboard());
      setRequestsFailed(false);
      try { setMyRequests(await essApi.hrRequests()); } catch { setRequestsFailed(true); }
      try { setLeaveTypes(await essActionsApi.leaveTypes()); } catch { /* names stay as stored */ }
    } catch (err: unknown) {
      const e = err as { response?: { data?: { message?: string } } };
      setError(e.response?.data?.message ?? t('Unable to load your workspace.'));
    } finally { setLoading(false); }
  };

  useEffect(() => { void load(); }, []);

  /** A leave type's name in the viewer's language, where the leave types carry an Arabic name. */
  const leaveName = (id: string | null, stored: string) => {
    const ty = leaveTypes.find((x) => (id ? x.id === id : x.nameEn === stored));
    return locale === 'ar' && ty?.nameAr ? ty.nameAr : stored;
  };

  if (loading) return <OverviewSkeleton />;

  if (error || !dashboard) {
    return (
      <div role="alert" className="flex flex-col items-center gap-3 rounded-2xl border border-rose-200 bg-rose-50 p-6 text-center text-sm text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-200">
        <AlertCircle className="h-6 w-6" aria-hidden="true" />
        {error || t('Your self-service workspace is empty.')}
        <button type="button" onClick={() => void load()} className="rounded-lg bg-rose-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-rose-700">{t('Retry')}</button>
      </div>
    );
  }

  const attendance = dashboard.attendanceToday;
  // The headline balance is an accruing one; a Saudi statutory event leave has no balance to headline.
  const accruing = dashboard.leaveBalances.filter((b) => b.statutoryEntitlementDays == null);
  const primaryLeave = accruing.find((b) =>
    b.leaveTypeName.toLowerCase().includes('annual') || b.leaveTypeName.toLowerCase().includes('casual')
  ) ?? accruing[0];
  const firstName = (dashboard.profile.fullName ?? user?.fullName ?? '').split(' ')[0];
  const ps = dashboard.payrollSnapshot;
  const perf = dashboard.performanceSnapshot;
  const loanGroups = dashboard.loanSummaries ?? (dashboard.loansSummary ? [dashboard.loansSummary] : []);
  const nextLeave = dashboard.nextApprovedLeave;
  const wholeMoney = (n: number, currency: string) => fx.money(n, currency, { decimals: 0 });
  const otHours = fx.number(dashboard.overtimeHoursThisMonth);
  const recentRequests = myRequests.slice(0, 4);
  const mayOpenLoans = ['loans.self', 'loans.read', 'loans.write'].some((p) => hasPermission(p));
  const overtimeOn = isFeatureEnabled('overtime');

  // Exceptions first: only what the employee can or should act on.
  const attention: AttentionItem[] = [];
  if (attendance?.missingPunch) {
    attention.push({
      id: 'missing-punch', tone: 'rose', icon: AlertCircle,
      title: t('A punch is missing from today'),
      detail: t('Ask HR to correct it so the day is counted.'),
      action: canWrite ? {
        label: t('Ask for a correction'),
        href: `${ESS_REQUESTS_PATH}?subject=${encodeURIComponent(t('Attendance correction for {date}', { date: fx.date(attendance.workDate) }))}`,
      } : undefined,
    });
  }
  // "Responded" lasts until HR closes the request, so several replies collapse into one line.
  // Replies already opened on /ess/requests are not exceptions any more (lib/essSelfService readSeenReplies).
  const seenReplies = readSeenReplies(user?.id);
  const replied = myRequests.filter((x) => x.responseStatus === 'Responded' && !seenReplies.has(x.id));
  if (replied.length === 1) {
    const r = replied[0];
    attention.push({
      id: `reply-${r.id}`, tone: 'blue', icon: MessageCircleReply,
      title: t('HR replied to “{subject}”', { subject: r.subject }),
      detail: t('{category}, raised on {date}', { category: t(r.categoryName), date: fx.date(r.createdAtUtc) }),
      action: { label: t('Read the reply'), href: `${ESS_REQUESTS_PATH}?open=${encodeURIComponent(r.id)}` },
    });
  } else if (replied.length > 1) {
    attention.push({
      id: 'replies', tone: 'blue', icon: MessageCircleReply,
      title: t('HR replied to {count} of your requests', { count: replied.length }),
      detail: t('Open each request to read the reply.'),
      action: { label: t('Read the replies'), href: ESS_REQUESTS_PATH },
    });
  }
  for (const doc of dashboard.documentAlerts) {
    attention.push({
      id: `doc-${doc.id}`, tone: 'amber', icon: FileText,
      title: t('{document} is expiring', { document: t(doc.documentType) }),
      detail: doc.expiryDate ? t('Expires on {date}', { date: fx.date(doc.expiryDate) }) : t('Expiry date not set'),
      action: canWrite ? { label: t('Tell HR'), href: ESS_REQUESTS_PATH } : undefined,
    });
  }
  for (const item of dashboard.actionItems) {
    attention.push({
      id: `task-${item.id}`, tone: 'amber', icon: Clock,
      title: item.title,
      detail: item.dueAtUtc ? t('Due on {date}', { date: fx.date(item.dueAtUtc) }) : item.category,
    });
  }

  const quickActions = [
    ...(canWrite && overtimeOn ? [{ icon: Zap, tone: 'amber' as Tone, label: t('Request overtime'), sub: t('Hours you worked beyond your shift'), href: ESS_OVERTIME_PATH }] : []),
    // Read-only (ess.read without ess.write) users are not promised a request they cannot make.
    { icon: FileSignature, tone: 'violet' as Tone, label: canWrite ? t('Request an HR letter') : t('My letters'), sub: t('Salary certificate, experience letter and more'), href: ESS_DOCUMENTS_PATH },
  ].filter((a) => verdictForPath(a.href).allowed);
  // With something waiting in "Needs your attention", that is the next action; Apply Leave steps back.
  const attentionLeads = attention.some((a) => a.action);

  return (
    <div className="space-y-5">

      {/* ═══ Who and today ═══════════════════════════════════════════════════ */}
      <section className="wg-card rounded-2xl p-4 sm:p-6" data-testid="ess-hero">
        <div className="flex flex-col gap-4 sm:gap-5 lg:flex-row lg:items-center lg:justify-between">
          <div className="flex min-w-0 items-start gap-4">
            <div className="relative hidden shrink-0 sm:block">
              {dashboard.profile.profilePhotoUrl ? (
                <img
                  src={dashboard.profile.profilePhotoUrl}
                  alt={dashboard.profile.fullName}
                  width={64}
                  height={64}
                  /* Above the fold: eager on purpose. Intrinsic size reserves the box so the
                     header does not shift when the photo resolves. */
                  decoding="async"
                  className="h-16 w-16 rounded-2xl object-cover ring-1 ring-slate-200 dark:ring-white/10"
                />
              ) : (
                <div className="flex h-16 w-16 items-center justify-center rounded-2xl bg-sapphire/10 ring-1 ring-sapphire/15 dark:bg-cyanAccent/10 dark:ring-cyanAccent/20">
                  <User className="h-7 w-7 text-sapphire dark:text-cyanAccent" aria-hidden="true" />
                </div>
              )}
            </div>
            <div className="min-w-0">
              <p className="text-[11px] font-bold uppercase tracking-widest text-sapphire dark:text-cyanAccent">{fx.date(new Date(), 'full')}</p>
              <h1 className="mt-0.5 text-xl font-extrabold sm:text-2xl tracking-tight text-slate-900 dark:text-white">{greeting(t, firstName)}</h1>
              <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
                <bdi>{dashboard.profile.jobTitle || t('Employee')}</bdi>
                {dashboard.profile.department ? <> · <bdi>{dashboard.profile.department}</bdi></> : null}
                {dashboard.profile.employeeCode ? <> · <bdi className="whitespace-nowrap">{dashboard.profile.employeeCode}</bdi></> : null}
              </p>
              {dashboard.tenureMonths > 0 && (
                <span className="mt-2.5 hidden items-center gap-1 rounded-full bg-blue-500/10 px-2.5 py-0.5 text-[11px] font-semibold text-blue-700 dark:text-blue-300 sm:inline-flex">
                  <BadgeCheck className="h-3 w-3" aria-hidden="true" /> {tenureLabel(t, dashboard.tenureMonths)}
                </span>
              )}
            </div>
          </div>

          {/* One filled button on the page: Apply Leave, unless "Needs your attention" holds the next step.
              On phones only that one shows here; payslips and requests are a tab away. */}
          <div className="flex flex-wrap gap-2 lg:justify-end">
            {canWrite && (
              <Link href={ESS_LEAVE_PATH} className={attentionLeads ? secondaryButton : primaryButton}>
                <CalendarOff className="h-4 w-4" aria-hidden="true" /> {t('Apply Leave')}
              </Link>
            )}
            <Link href={ESS_PAYSLIPS_PATH} className={`${secondaryButton} max-sm:hidden`}>
              <FileText className="h-4 w-4" aria-hidden="true" /> {t('View Payslip')}
            </Link>
            <Link href={ESS_REQUESTS_PATH} className={`${secondaryButton} max-sm:hidden`}>
              <ClipboardList className="h-4 w-4" aria-hidden="true" /> {canWrite ? t('Raise a request') : t('My Requests')}
            </Link>
          </div>
        </div>
      </section>

      {/* ═══ Exceptions first ════════════════════════════════════════════════ */}
      <AttentionPanel items={attention} incomplete={requestsFailed} onRetry={() => void load()} />

      {/* ═══ Four headline numbers, each linked to the records behind it ═══════ */}
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <StatTile
          testId="ess-tile-leave"
          icon={CalendarOff} tone="emerald" label={t('Leave Balance')}
          value={primaryLeave ? t('{days} days', { days: days(primaryLeave.available) }) : undefined}
          detail={primaryLeave ? leaveName(primaryLeave.leaveTypeId, primaryLeave.leaveTypeName) : undefined}
          note={primaryLeave ? t('Out of {days} days entitled', { days: days(primaryLeave.entitled) }) : undefined}
          note2={primaryLeave ? t('After approved and pending leave') : undefined}
          empty={primaryLeave ? undefined : t('No leave balances set up yet')}
          href={ESS_LEAVE_PATH} linkLabel={t('Open my leave')}
        />
        <StatTile
          testId="ess-tile-payslip"
          icon={Banknote} tone="violet" label={t('Last Payslip')}
          value={ps ? wholeMoney(ps.netSalary, ps.currency) : undefined}
          detail={ps?.period ? t('Net pay for {period}', { period: fx.period(ps.period) }) : undefined}

          empty={ps ? undefined : t('No finalised payslips yet')}
          href={ESS_PAYSLIPS_PATH} linkLabel={t('Open my payslips')}
        />
        <StatTile
          testId="ess-tile-attendance"
          icon={Clock} tone={attendance?.missingPunch ? 'rose' : 'blue'} label={t("Today's Attendance")}
          // A day with a punch missing is not "Present" yet: say what is wrong, not the status it would have.
          value={attendance ? (attendance.missingPunch ? t('Incomplete') : enumLabel(t, 'AttendanceStatus', attendance.status)) : undefined}
          detail={attendance ? t('{time} worked', { time: workedTime(t, attendance.totalWorkedMinutes) }) : undefined}
          note={attendance?.missingPunch
            ? t('A punch is missing')
            : attendance?.lateMinutes ? t('Late by {minutes} min', { minutes: attendance.lateMinutes })
              : attendance ? t('On time') : undefined}
          note2={dashboard.overtimeHoursThisMonth > 0 ? t('{hours} h of approved overtime this month', { hours: otHours }) : undefined}
          empty={attendance ? undefined : t('No attendance record for today')}
        />
        <StatTile
          testId="ess-tile-loans"
          icon={CreditCard} tone="rose" label={t('My Loans')}
          value={loanGroups.length ? loanGroups.map((g) => wholeMoney(g.totalOutstanding, g.currency)).join(' · ') : undefined}
          detail={loanGroups.length ? t('{count, plural, one {Outstanding on # active loan} other {Outstanding on # active loans}}', { count: loanGroups.reduce((sum, g) => sum + g.activeLoanCount, 0) }) : undefined}
          empty={loanGroups.length ? undefined : t('No active loans')}
          href={mayOpenLoans ? '/loans?mine=true' : undefined} linkLabel={t('Open my loans')}
        />
      </div>

      <div className="grid gap-5 lg:grid-cols-3">
        {/* ── Main column ─────────────────────────────────────────────────── */}
        <div className="min-w-0 space-y-5 lg:col-span-2">
          <Panel title={t('Leave balances')} icon={CalendarOff} tone="emerald" testId="ess-home-leave"
            action={<Link href={ESS_LEAVE_PATH} className={textLink}>{canWrite ? t('Request leave') : t('Open my leave')}</Link>}>
            {dashboard.leaveBalances.length === 0 ? (
              <Quiet>{t('No leave balances have been set up for you this year.')}</Quiet>
            ) : (
              <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
                {dashboard.leaveBalances.map((b) => (
                  <LeaveBar
                    key={b.leaveTypeId}
                    name={leaveName(b.leaveTypeId, b.leaveTypeName)}
                    available={b.available}
                    entitled={b.entitled ?? b.available}
                    statutoryDays={b.statutoryEntitlementDays}
                  />
                ))}
              </div>
            )}
          </Panel>

          {/* HR requests: raised, followed and answered on the employee's own requests page */}
          <Panel title={t('My HR Requests')} icon={ClipboardList} tone="violet" testId="ess-home-hr-requests"
            action={<Link href={ESS_REQUESTS_PATH} className={textLink}>{t('Open my requests')}</Link>}>
            {requestsFailed ? (
              <p role="alert" className="text-sm text-amber-700 dark:text-amber-300">{t('Your HR requests could not be loaded.')}</p>
            ) : recentRequests.length === 0 ? (
              <Quiet>{t('You have not raised any requests yet.')}</Quiet>
            ) : (
              <ul className="space-y-2">
                {recentRequests.map((r) => {
                  const s = hrRequestStatus(r.responseStatus);
                  return (
                    <li key={r.id}>
                      <Link href={`${ESS_REQUESTS_PATH}?open=${encodeURIComponent(r.id)}`}
                        className="flex w-full items-center justify-between gap-3 rounded-xl border border-slate-100 px-3.5 py-2.5 text-start transition hover:bg-slate-50 dark:border-white/[0.07] dark:hover:bg-white/[0.03]">
                        <span className="min-w-0">
                          <span className="block truncate text-sm font-medium text-slate-800 dark:text-slate-200">{r.subject}</span>
                          <span className="block truncate text-xs text-slate-400">
                            {t('{category}, raised on {date}', { category: t(r.categoryName), date: fx.date(r.createdAtUtc) })}
                          </span>
                        </span>
                        <StatusChip label={t(s.label)} tone={s.tone} dot />
                      </Link>
                    </li>
                  );
                })}
              </ul>
            )}
          </Panel>

          <MyShiftsCard />
        </div>

        {/* ── Side column ─────────────────────────────────────────────────── */}
        <div className="min-w-0 space-y-5">
          <Panel title={t('Coming up')} icon={CalendarClock} tone="amber" testId="ess-home-upcoming">
            {!nextLeave && !ps?.nextPayrollDate && !loanGroups.some((g) => g.nextInstallmentDate) ? (
              <Quiet>{t('Nothing scheduled yet.')}</Quiet>
            ) : (
              <ul className="space-y-3.5">
                {nextLeave && (
                  <UpcomingRow
                    icon={CalendarOff} tone="emerald"
                    label={t('Leave from {start} to {end}', { start: fx.date(nextLeave.startDate), end: fx.date(nextLeave.endDate) })}
                    sub={t('{type}, {days} days', { type: leaveName(null, nextLeave.leaveTypeName), days: fx.number(nextLeave.days) })}
                    chip={t('Approved')}
                  />
                )}
                {ps?.nextPayrollDate && (
                  // The server's month-end estimate, not a date HR has set: labelled as such.
                  <UpcomingRow icon={Banknote} tone="violet" label={t('Expected pay date (estimate)')}
                    sub={t('{date}: the end of the month, confirmed when HR approves payroll', { date: fx.date(ps.nextPayrollDate) })} />
                )}
                {loanGroups.filter((g) => g.nextInstallmentDate).map((g) => (
                  <UpcomingRow
                    key={g.currency}
                    icon={CreditCard} tone="rose"
                    label={g.nextInstallmentAmount != null
                      ? t('Loan instalment of {amount}', { amount: wholeMoney(g.nextInstallmentAmount, g.currency) })
                      : t('Loan instalment')}
                    sub={t('Due on {date}', { date: fx.date(g.nextInstallmentDate!) })}
                  />
                ))}
              </ul>
            )}
          </Panel>

          <Panel title={t('Quick actions')} icon={Zap} tone="blue" testId="ess-home-quick-actions">
            <ul className="-my-1.5 divide-y divide-slate-100 dark:divide-white/[0.06]">
              {quickActions.map((a) => {
                const Icon = a.icon;
                return (
                  <li key={a.href}>
                    <Link href={a.href} className="group flex items-center gap-3 py-2.5 outline-none focus-visible:ring-2 focus-visible:ring-sapphire">
                      <span className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-lg ${TONE_ICON[a.tone]}`}><Icon className="h-4 w-4" aria-hidden="true" /></span>
                      <span className="min-w-0 flex-1">
                        <span className="block text-sm font-medium text-slate-800 group-hover:text-sapphire dark:text-slate-200 dark:group-hover:text-cyanAccent">{a.label}</span>
                        <span className="block truncate text-xs text-slate-400 dark:text-slate-500">{a.sub}</span>
                      </span>
                      <ChevronRight className="h-4 w-4 shrink-0 text-slate-300 rtl:-scale-x-100 dark:text-slate-600" aria-hidden="true" />
                    </Link>
                  </li>
                );
              })}
            </ul>
          </Panel>

          <Panel title={t('Announcements')} icon={Megaphone} tone="blue">
            {dashboard.announcements.length === 0 ? (
              <Quiet>{t('No active announcements.')}</Quiet>
            ) : (
              <ul className="space-y-3">
                {dashboard.announcements.map((a) => (
                  <li key={a.id}>
                    <p className="text-sm font-semibold text-slate-900 dark:text-white">{a.title}</p>
                    <p className="mt-0.5 line-clamp-3 text-xs leading-relaxed text-slate-500 dark:text-slate-400">{a.body}</p>
                  </li>
                ))}
              </ul>
            )}
          </Panel>

          {perf && (
            <Panel title={t('Performance and goals')} icon={Target} tone="violet">
              <p className="text-xs text-slate-500 dark:text-slate-400">{t('Current cycle')}</p>
              <p className="text-sm font-semibold text-slate-900 dark:text-white">{perf.cycleName}</p>
              <div className="mt-3 flex items-center justify-between text-xs">
                <span className="text-slate-500 dark:text-slate-400">{t('Goals complete')}</span>
                <span className="font-semibold text-slate-800 dark:text-white">{t('{done} of {total}', { done: perf.goalsCompleted, total: perf.goalsTotal })}</span>
              </div>
              <div className="mt-1.5 h-2 w-full overflow-hidden rounded-full bg-slate-100 dark:bg-white/[0.07]">
                {/* eslint-disable-next-line react/forbid-dom-props */}
                <div className="h-full rounded-full bg-sapphire dark:bg-cyanAccent" style={{ width: `${perf.goalsTotal > 0 ? Math.min(100, Math.round((perf.goalsCompleted / perf.goalsTotal) * 100)) : 0}%` }} />
              </div>
              {perf.lastRating !== null && (
                <p className="mt-3 text-xs text-slate-500 dark:text-slate-400">
                  {t('Last rating: {rating} out of 5', { rating: fx.number(Number(perf.lastRating), { minimumFractionDigits: 1, maximumFractionDigits: 1 }) })}
                </p>
              )}
            </Panel>
          )}

          <AskKodyCard />
        </div>
      </div>
    </div>
  );
}
