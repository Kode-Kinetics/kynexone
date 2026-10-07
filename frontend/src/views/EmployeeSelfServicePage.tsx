'use client';

import { useEffect, useState } from 'react';
import {
  MessageSquareText, Loader2, CalendarOff, Send, FileText, Clock,
  ChevronRight, Megaphone, CheckCircle2, AlertCircle,
  Zap, ClipboardList, TrendingUp, CreditCard, Banknote,
  Star, Target, Calendar, BadgeCheck, User, Download, FileSignature,
} from 'lucide-react';
import { useRouter } from 'next/navigation';
import { ESS_PAYSLIPS_PATH } from '../lib/essPayslip';
import { ESS_LEAVE_PATH, ESS_OVERTIME_PATH, ESS_REQUESTS_PATH, hrRequestStatus, splitMinutes } from '../lib/essSelfService';
import { essActionsApi, essApi, type EssDashboard, type EssHrRequest, type EssRosterEntry } from '../api/ess';
import type { LeaveType } from '../api/leave';
import { essDocumentsApi, type EssDocumentRequest, type EssLetterType } from '../api/hrLetters';
import { useAuth } from '../contexts/AuthContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import { enumLabel } from '../i18n/enumLabel';
import { StatusChip } from '../components/StatusChip';
import { EssReadOnly, useCanWriteEss } from '../components/ess/EssParts';

// ── helpers ───────────────────────────────────────────────────────────────────

type T = ReturnType<typeof useLocale>['t'];

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

// ── Leave balance bar ────────────────────────────────────────────────────────

function LeaveBar({ name, available, entitled, statutoryDays }: { name: string; available: number; entitled: number; statutoryDays?: number | null }) {
  const { t } = useLocale();
  const fx = useFormat();
  if (statutoryDays != null) {
    // Saudi statutory event leave is granted by law per event, not drawn from a balance, so its
    // "available" can read negative while a request is pending. Show the entitlement instead.
    return (
      <div className="space-y-1.5">
        <div className="flex items-center justify-between text-xs">
          <span className="font-medium text-slate-700 dark:text-slate-200">{name}</span>
          <span className="tabular-nums text-emerald-700 dark:text-emerald-300">
            {t('Statutory entitlement: {days} days per event', { days: fx.number(statutoryDays) })}
          </span>
        </div>
      </div>
    );
  }
  const pct = entitled > 0 ? Math.round((available / entitled) * 100) : 0;
  const color = pct >= 60 ? 'bg-emerald-500' : pct >= 30 ? 'bg-amber-500' : 'bg-rose-500';
  const one = (n: number) => fx.number(n, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
  return (
    <div className="space-y-1.5">
      <div className="flex items-center justify-between text-xs">
        <span className="font-medium text-slate-700 dark:text-slate-200">{name}</span>
        <span className="tabular-nums text-slate-500 dark:text-slate-400">
          {entitled > 0
            ? t('{available} of {entitled} days', { available: one(available), entitled: one(entitled) })
            : t('{available} days', { available: one(available) })}
        </span>
      </div>
      <div className="h-1.5 w-full overflow-hidden rounded-full bg-slate-100 dark:bg-white/[0.07]">
        {/* eslint-disable-next-line react/forbid-dom-props */}
        <div className={`h-full rounded-full transition-all duration-700 ${color}`} style={{ width: `${Math.min(100, pct)}%` }} />
      </div>
    </div>
  );
}

// ── KPI Card ──────────────────────────────────────────────────────────────────

function KpiCard({
  icon: Icon, iconBg, label, value, sub, sub2, onClick, emptyText,
}: {
  icon: React.ElementType;
  iconBg: string;
  label: string;
  value: React.ReactNode;
  sub?: string;
  sub2?: string;
  onClick?: () => void;
  emptyText?: string;
}) {
  const inner = (
    <div className="flex flex-col gap-3 h-full">
      <div className={`flex h-9 w-9 items-center justify-center rounded-xl ${iconBg}`}>
        <Icon className="h-4 w-4" />
      </div>
      <div className="flex-1">
        {emptyText ? (
          <p className="text-sm text-slate-400 dark:text-slate-500">{emptyText}</p>
        ) : (
          <>
            <div className="text-xl font-extrabold leading-tight text-slate-900 dark:text-white tabular-nums"><bdi>{value}</bdi></div>
            {sub && <p className="mt-1 text-xs text-slate-500 dark:text-slate-400 leading-snug">{sub}</p>}
            {sub2 && <p className="text-xs text-slate-400 dark:text-slate-500 leading-snug">{sub2}</p>}
          </>
        )}
      </div>
      <p className="text-[11px] font-bold uppercase tracking-wider text-slate-400 dark:text-slate-600">{label}</p>
    </div>
  );

  const cls = 'rounded-xl border border-slate-100 bg-white p-4 dark:border-white/[0.07] dark:bg-white/[0.03]';

  if (onClick) {
    return (
      <button type="button" onClick={onClick} className={`${cls} text-start group hover:shadow-md hover:-translate-y-0.5 transition-all w-full`}>
        {inner}
      </button>
    );
  }
  return <div className={cls}>{inner}</div>;
}

// ── Star rating display ───────────────────────────────────────────────────────

function StarRating({ rating }: { rating: number }) {
  const fx = useFormat();
  const full = Math.floor(rating);
  const half = rating % 1 >= 0.4;
  return (
    <span className="inline-flex items-center gap-0.5">
      {[1, 2, 3, 4, 5].map((i) => (
        <Star
          key={i}
          className={`h-3.5 w-3.5 ${i <= full ? 'fill-amber-400 text-amber-400' : i === full + 1 && half ? 'fill-amber-200 text-amber-400' : 'text-slate-200 dark:text-slate-700'}`}
        />
      ))}
      <span className="ms-1 text-xs font-semibold text-slate-700 dark:text-slate-200">{fx.number(rating, { minimumFractionDigits: 1, maximumFractionDigits: 1 })}</span>
    </span>
  );
}

// ── Goals progress bar ────────────────────────────────────────────────────────

function GoalsBar({ done, total }: { done: number; total: number }) {
  const { t } = useLocale();
  const pct = total > 0 ? Math.round((done / total) * 100) : 0;
  return (
    <div className="space-y-1">
      <div className="flex items-center justify-between text-xs">
        <span className="text-slate-500 dark:text-slate-400">{t('Goals complete')}</span>
        <span className="font-semibold text-slate-800 dark:text-white">{t('{done} of {total}', { done, total })}</span>
      </div>
      <div className="h-2 w-full overflow-hidden rounded-full bg-slate-100 dark:bg-white/[0.07]">
        {/* eslint-disable-next-line react/forbid-dom-props */}
        <div className="h-full rounded-full bg-sapphire dark:bg-cyanAccent transition-all duration-700" style={{ width: `${Math.min(100, pct)}%` }} />
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
    <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
      <div className="flex items-center justify-between border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
        <p className="flex items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
          <Calendar className="h-4 w-4" /> {t('My Upcoming Shifts')}
        </p>
        <span className="text-[10px] font-medium uppercase tracking-wide text-slate-400">{t('Next 2 weeks')}</span>
      </div>
      <div className="p-5">
        {shifts === null ? (
          <p className="text-sm text-slate-400 dark:text-slate-500">{t('Loading…')}</p>
        ) : shifts.length === 0 ? (
          <p className="text-sm text-slate-400 dark:text-slate-500">{t('No shifts scheduled. Your roster will appear here once published.')}</p>
        ) : (
          <ul className="space-y-2">
            {shifts.map((s) => (
              <li key={s.id} className="flex items-center gap-3 rounded-lg border border-slate-100 px-3 py-2 dark:border-white/[0.06]">
                <span className="h-2.5 w-2.5 shrink-0 rounded-full" style={{ backgroundColor: s.shiftColor || '#2F6BFF' }} />
                <span className="text-sm font-medium text-slate-800 dark:text-slate-200">{s.shiftName}</span>
                <span className="ms-auto text-xs text-slate-500 dark:text-slate-400">{fx.date(s.date, 'weekdayDate')}</span>
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}


// ── My documents (B6) ─────────────────────────────────────────────────────────
//
// The employee asks; HR issues. There is no "produce it myself" button here, and there is no
// endpoint behind one: a salary certificate an employee could mint would not be worth the paper
// to the bank that asked for it.

function MyDocumentsCard() {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const canWrite = useCanWriteEss();
  const [types, setTypes] = useState<EssLetterType[] | null>(null);
  const [requests, setRequests] = useState<EssDocumentRequest[]>([]);
  const [letterType, setLetterType] = useState('');
  const [language, setLanguage] = useState('bilingual');
  const [purpose, setPurpose] = useState('');
  const [addressee, setAddressee] = useState('');
  const [busy, setBusy] = useState(false);
  const [downloading, setDownloading] = useState<string | null>(null);
  const [error, setError] = useState('');
  const [note, setNote] = useState('');

  const typeName = (code: string) => {
    const ty = types?.find((x) => x.letterType === code);
    if (!ty) return code;
    return locale === 'ar' && ty.nameAr ? ty.nameAr : ty.nameEn;
  };

  const refresh = async () => {
    try { setRequests(await essDocumentsApi.list()); } catch { /* non-blocking */ }
  };

  useEffect(() => {
    let cancelled = false;
    essDocumentsApi.types()
      .then((list) => { if (!cancelled) { setTypes(list); if (list.length > 0) setLetterType(list[0].letterType); } })
      .catch(() => { if (!cancelled) setTypes([]); });
    void refresh();
    return () => { cancelled = true; };
  }, []);

  const submit = async () => {
    if (!letterType) return;
    setBusy(true); setError(''); setNote('');
    try {
      await essDocumentsApi.create({ letterType, language, purpose, addresseeName: addressee });
      setNote(t('Requested. HR will issue it and it will appear below to download.'));
      setPurpose(''); setAddressee('');
      await refresh();
    } catch (e) {
      const detail = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(detail ?? t('The request could not be submitted. Please try again.'));
    } finally {
      setBusy(false);
    }
  };

  const download = async (id: string) => {
    setDownloading(id);
    try { await essDocumentsApi.download(id); }
    catch { setError(t('That document could not be downloaded. Please contact HR.')); }
    finally { setDownloading(null); }
  };

  const field = 'w-full rounded-xl border border-slate-200 bg-slate-50/80 px-4 py-2.5 text-sm text-slate-900 placeholder-slate-400 outline-none transition focus:border-sapphire/50 focus:ring-2 focus:ring-sapphire/10 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-white dark:placeholder-slate-600';

  return (
    <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
      <div className="border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
        <p className="flex items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
          <FileSignature className="h-4 w-4" /> {t('Request a Document')}
        </p>
      </div>
      <div className="space-y-3 p-5">
        {types === null ? (
          <p className="flex items-center gap-2 text-sm text-slate-400 dark:text-slate-500">
            <Loader2 className="h-3.5 w-3.5 animate-spin" /> {t('Loading…')}
          </p>
        ) : types.length === 0 ? (
          <p className="text-sm text-slate-400 dark:text-slate-500">
            {t('Your organisation has not set up HR letters yet. Raise an HR request instead and someone will help.')}
          </p>
        ) : !canWrite ? (
          <EssReadOnly />
        ) : (
          <>
            {error && (
              <p className="flex items-start gap-2 rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-600 dark:bg-rose-500/10 dark:text-rose-400">
                <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" /> {error}
              </p>
            )}
            {note && (
              <p className="flex items-center gap-2 rounded-lg bg-emerald-500/10 px-3 py-2 text-sm font-medium text-emerald-600 dark:text-emerald-400">
                <CheckCircle2 className="h-4 w-4 shrink-0" /> {note}
              </p>
            )}

            <select value={letterType} onChange={(e) => setLetterType(e.target.value)} aria-label={t('Document type')} className={field}>
              {types.map((ty) => <option key={ty.letterType} value={ty.letterType}>{locale === 'ar' && ty.nameAr ? ty.nameAr : ty.nameEn}</option>)}
            </select>
            <select value={language} onChange={(e) => setLanguage(e.target.value)} aria-label={t('Language')} className={field}>
              <option value="bilingual">{t('Bilingual (English and Arabic)')}</option>
              <option value="en">{t('English only')}</option>
              <option value="ar">{t('Arabic only')}</option>
            </select>
            <input value={purpose} onChange={(e) => setPurpose(e.target.value)} placeholder={t('What do you need it for? For example, a bank loan')} className={field} />
            <input value={addressee} onChange={(e) => setAddressee(e.target.value)} placeholder={t('Addressed to (optional), for example Riyad Bank')} className={field} />
            <button
              type="button"
              onClick={submit}
              disabled={busy || !letterType}
              className="inline-flex w-full items-center justify-center gap-1.5 rounded-xl bg-slate-900 py-2.5 text-sm font-semibold text-white transition hover:bg-slate-700 disabled:opacity-50 dark:bg-white dark:text-slate-900 dark:hover:bg-slate-100"
            >
              {busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Send className="h-3.5 w-3.5" />}
              {busy ? t('Requesting…') : t('Request document')}
            </button>
          </>
        )}

        {requests.length > 0 && (
          <div className="space-y-2 border-t border-slate-100 pt-3 dark:border-white/[0.07]">
            <p className="text-xs font-semibold uppercase tracking-wide text-slate-400">{t('My Documents')}</p>
            {requests.map((r) => (
              <div key={r.id} className="flex items-center justify-between gap-2 rounded-lg border border-slate-100 px-3 py-2 dark:border-white/[0.07]">
                <div className="min-w-0">
                  <p className="truncate text-sm font-medium text-slate-800 dark:text-slate-200">{typeName(r.letterType)}</p>
                  <p className="truncate text-xs text-slate-400 dark:text-slate-500">
                    <bdi>{r.referenceNumber ?? fx.date(r.createdAtUtc)}</bdi>
                    {r.status === 'Declined' && r.decisionNote ? <> · <bdi>{r.decisionNote}</bdi></> : null}
                  </p>
                </div>
                {r.isIssued ? (
                  <button
                    type="button"
                    onClick={() => download(r.id)}
                    disabled={downloading !== null}
                    className="inline-flex shrink-0 items-center gap-1 rounded-lg border border-slate-200 px-2 py-1 text-xs font-semibold text-slate-700 transition hover:bg-slate-50 disabled:opacity-50 dark:border-white/[0.08] dark:text-slate-200 dark:hover:bg-white/[0.04]"
                  >
                    {downloading === r.id ? <Loader2 className="h-3 w-3 animate-spin" /> : <Download className="h-3 w-3" />} {t('Download PDF')}
                  </button>
                ) : (
                  <span className={`shrink-0 rounded-full px-2 py-0.5 text-[10px] font-semibold ${
                    r.status === 'Declined'
                      ? 'bg-rose-50 text-rose-600 dark:bg-rose-500/10 dark:text-rose-400'
                      : 'bg-amber-50 text-amber-600 dark:bg-amber-500/10 dark:text-amber-400'}`}>
                    {enumLabel(t, 'Status', r.status)}
                  </span>
                )}
              </div>
            ))}
          </div>
        )}
      </div>
    </section>
  );
}

// ── Upcoming item row ─────────────────────────────────────────────────────────

function UpcomingRow({
  dot, label, sub, badge, badgeColor,
}: {
  dot: string; label: string; sub: string; badge?: string; badgeColor?: string;
}) {
  return (
    <div className="flex items-start gap-3">
      <div className={`mt-1.5 h-2 w-2 shrink-0 rounded-full ${dot}`} />
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium text-slate-900 dark:text-white truncate">{label}</p>
        <p className="text-xs text-slate-500 dark:text-slate-400">{sub}</p>
      </div>
      {badge && (
        <span className={`shrink-0 rounded-full px-2 py-0.5 text-[10px] font-bold ${badgeColor ?? 'bg-slate-100 text-slate-600 dark:bg-white/[0.07] dark:text-slate-400'}`}>
          {badge}
        </span>
      )}
    </div>
  );
}

// ── Announcement card ─────────────────────────────────────────────────────────

function AnnouncementCard({ title, body }: { title: string; body: string }) {
  return (
    <div className="flex gap-3 rounded-xl border border-slate-100 bg-slate-50/60 p-3.5 dark:border-white/[0.07] dark:bg-white/[0.02]">
      <div className="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-blue-500/10">
        <Megaphone className="h-3.5 w-3.5 text-blue-500 dark:text-blue-400" />
      </div>
      <div className="min-w-0">
        <p className="text-sm font-semibold text-slate-900 dark:text-white">{title}</p>
        <p className="mt-0.5 line-clamp-2 text-xs leading-relaxed text-slate-500 dark:text-slate-400">{body}</p>
      </div>
    </div>
  );
}

// ── Attendance status pill ────────────────────────────────────────────────────

function AttendancePill({ status, worked, missing }: { status?: string; worked?: number; missing?: boolean }) {
  const { t } = useLocale();
  if (!status) return <span className="rounded-full bg-slate-100 px-3 py-1 text-xs text-slate-500 dark:bg-white/[0.07] dark:text-slate-400">{t('No record today')}</span>;
  const label = enumLabel(t, 'AttendanceStatus', status);
  return (
    <span className={`inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-xs font-semibold ${
      missing ? 'bg-rose-500/10 text-rose-600 dark:text-rose-400' : 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400'
    }`}>
      {missing ? <AlertCircle className="h-3.5 w-3.5" /> : <CheckCircle2 className="h-3.5 w-3.5" />}
      {worked ? t('{status}, {time} worked', { status: label, time: workedTime(t, worked) }) : label}
    </span>
  );
}

// ── Profile completeness bar ──────────────────────────────────────────────────

function CompletenessBar({ score }: { score: number }) {
  const { t } = useLocale();
  const pct = Math.round(score);
  const color = pct >= 80 ? 'bg-emerald-500' : pct >= 50 ? 'bg-amber-500' : 'bg-rose-500';
  return (
    <div className="mt-2 flex items-center gap-2">
      <div className="h-1.5 flex-1 overflow-hidden rounded-full bg-slate-200/60 dark:bg-white/[0.08]">
        {/* eslint-disable-next-line react/forbid-dom-props */}
        <div className={`h-full rounded-full transition-all duration-700 ${color}`} style={{ width: `${Math.min(100, pct)}%` }} />
      </div>
      <span className="text-[10px] font-semibold text-slate-500 dark:text-slate-400">{t('Profile {pct}% complete', { pct })}</span>
    </div>
  );
}

// ── Loading skeleton ──────────────────────────────────────────────────────────

function Skeleton({ className }: { className?: string }) {
  return <div className={`animate-pulse rounded-lg bg-slate-200/70 dark:bg-white/[0.06] ${className}`} />;
}

function DashboardSkeleton() {
  return (
    <div className="mx-auto max-w-[1400px] space-y-6">
      <Skeleton className="h-44 w-full rounded-2xl" />
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        {[1, 2, 3, 4].map((i) => <Skeleton key={i} className="h-28" />)}
      </div>
      <div className="grid gap-5 lg:grid-cols-2">
        <Skeleton className="h-40" />
        <Skeleton className="h-40" />
      </div>
      <Skeleton className="h-48" />
      <div className="grid gap-5 lg:grid-cols-2">
        <Skeleton className="h-60" />
        <Skeleton className="h-60" />
      </div>
    </div>
  );
}

// ── Main page ─────────────────────────────────────────────────────────────────

export function EmployeeSelfServicePage() {
  const { user } = useAuth();
  const { t, locale } = useLocale();
  const fx = useFormat();
  const canWrite = useCanWriteEss();
  const { isFeatureEnabled } = useFeatureFlags();
  const router = useRouter();
  const [dashboard, setDashboard] = useState<EssDashboard | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);
  const [leaveTypes, setLeaveTypes] = useState<LeaveType[]>([]);

  // AI assistant
  const [question, setQuestion] = useState('');
  const [answer, setAnswer] = useState('');
  const [asking, setAsking] = useState(false);

  // HR requests: the latest few, read here; raised and answered on /ess/requests.
  const [myRequests, setMyRequests] = useState<EssHrRequest[]>([]);

  const load = async () => {
    setLoading(true); setError('');
    try {
      setDashboard(await essApi.dashboard());
      try { setMyRequests(await essApi.hrRequests()); } catch { /* non-blocking */ }
      try { setLeaveTypes(await essActionsApi.leaveTypes()); } catch { /* names stay as stored */ }
    } catch (err: unknown) {
      const e = err as { response?: { data?: { message?: string } } };
      setError(e.response?.data?.message ?? t('Unable to load your workspace.'));
    } finally { setLoading(false); }
  };

  useEffect(() => { load(); }, []);

  const askAi = async () => {
    if (!question.trim()) return;
    setAsking(true); setAnswer('');
    try { const res = await essApi.askAi(question); setAnswer(res.answer); }
    catch (err: unknown) {
      const e = err as { response?: { data?: { message?: string } } };
      setAnswer(e.response?.data?.message ?? t('The assistant could not answer right now.'));
    } finally { setAsking(false); }
  };

  /** A leave type's name in the viewer's language, where the leave types carry an Arabic name. */
  const leaveName = (id: string | null, stored: string) => {
    const ty = leaveTypes.find((x) => (id ? x.id === id : x.nameEn === stored));
    return locale === 'ar' && ty?.nameAr ? ty.nameAr : stored;
  };

  if (loading) return <DashboardSkeleton />;

  if (error || !dashboard) {
    return (
      <div className="rounded-xl border border-rose-200 bg-rose-50 p-6 text-sm text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-200">
        {error || t('Your self-service workspace is empty.')}
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
  const loans = dashboard.loansSummary;
  const loanGroups = dashboard.loanSummaries ?? (loans ? [loans] : []);
  const nextLeave = dashboard.nextApprovedLeave;
  const totalUpcoming = (nextLeave ? 1 : 0) + dashboard.documentAlerts.length + dashboard.actionItems.length;
  const one = (n: number) => fx.number(n, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
  const wholeMoney = (n: number, currency: string) => fx.money(n, currency, { decimals: 0 });
  const otHours = fx.number(dashboard.overtimeHoursThisMonth);
  const recentRequests = myRequests.slice(0, 3);

  return (
    <div className="mx-auto max-w-[1400px] space-y-6">

      {/* ═══ HERO: My Portrait ══════════════════════════════════════════════ */}
      <div className="relative overflow-hidden rounded-2xl border border-slate-100 bg-gradient-to-br from-white via-blue-50/40 to-indigo-50/30 p-6 dark:border-white/[0.07] dark:from-[#0f1729] dark:via-[#101e36] dark:to-[#0d1525]">
        <div className="pointer-events-none absolute right-0 top-0 h-64 w-64 translate-x-16 -translate-y-16 rounded-full bg-sapphire/[0.06] blur-3xl dark:bg-blue-500/[0.12]" />
        <div className="pointer-events-none absolute bottom-0 left-1/3 h-40 w-72 -translate-y-4 rounded-full bg-indigo-400/[0.04] blur-2xl dark:bg-indigo-500/[0.07]" />

        <div className="relative flex flex-col gap-5 lg:flex-row lg:items-start lg:justify-between">

          {/* Avatar + identity */}
          <div className="flex items-start gap-5">
            <div className="relative shrink-0">
              {dashboard.profile.profilePhotoUrl ? (
                <img
                  src={dashboard.profile.profilePhotoUrl}
                  alt={dashboard.profile.fullName}
                  width={64}
                  height={64}
                  /* Above the fold: eager on purpose. Intrinsic size reserves the box so the
                     header does not shift when the photo resolves. */
                  decoding="async"
                  className="h-16 w-16 rounded-2xl object-cover ring-2 ring-white/60 dark:ring-white/10"
                />
              ) : (
                <div className="flex h-16 w-16 items-center justify-center rounded-2xl bg-sapphire/10 dark:bg-cyanAccent/10 ring-2 ring-white/60 dark:ring-white/10">
                  <User className="h-7 w-7 text-sapphire dark:text-cyanAccent" />
                </div>
              )}
              {/* Online indicator */}
              <span className="absolute -bottom-0.5 -end-0.5 h-3.5 w-3.5 rounded-full border-2 border-white bg-emerald-400 dark:border-[#0f1729]" />
            </div>

            <div className="min-w-0">
              <p className="text-[11px] font-bold uppercase tracking-widest text-sapphire dark:text-cyanAccent">{fx.date(new Date(), 'full')}</p>
              <h1 className="mt-0.5 text-2xl font-extrabold tracking-tight text-slate-900 dark:text-white">
                {greeting(t, firstName)}
              </h1>
              <p className="mt-0.5 text-sm text-slate-500 dark:text-slate-400">
                <bdi>{dashboard.profile.jobTitle || t('Employee')}</bdi>
                {dashboard.profile.department ? <> · <bdi>{dashboard.profile.department}</bdi></> : null}
                {dashboard.profile.employeeCode ? <> · <bdi>{dashboard.profile.employeeCode}</bdi></> : null}
              </p>

              {/* Tenure badge */}
              {dashboard.tenureMonths > 0 && (
                <span className="mt-2 inline-flex items-center gap-1 rounded-full bg-blue-500/10 px-2.5 py-0.5 text-[11px] font-semibold text-blue-600 dark:text-blue-400">
                  <BadgeCheck className="h-3 w-3" />
                  {tenureLabel(t, dashboard.tenureMonths)}
                </span>
              )}

              {/* Profile completeness */}
              <CompletenessBar score={dashboard.profile.profileCompletenessScore} />
            </div>
          </div>

          {/* Hero action buttons + today's status */}
          <div className="flex flex-col gap-3 lg:items-end">
            <div className="flex flex-wrap gap-2">
              {canWrite && (
                <button
                  type="button"
                  onClick={() => router.push(ESS_LEAVE_PATH)}
                  className="inline-flex items-center gap-1.5 rounded-xl bg-sapphire px-4 py-2 text-sm font-semibold text-white hover:bg-sapphire/90 transition dark:bg-cyanAccent dark:text-slate-900"
                >
                  <CalendarOff className="h-4 w-4" /> {t('Apply Leave')}
                </button>
              )}
              <button
                type="button"
                onClick={() => router.push(ESS_PAYSLIPS_PATH)}
                className="inline-flex items-center gap-1.5 rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50 transition dark:border-white/[0.12] dark:bg-white/[0.04] dark:text-slate-200 dark:hover:bg-white/[0.08]"
              >
                <FileText className="h-4 w-4" /> {t('View Payslip')}
              </button>
              {canWrite && isFeatureEnabled('overtime') && (
                <button
                  type="button"
                  onClick={() => router.push(ESS_OVERTIME_PATH)}
                  className="inline-flex items-center gap-1.5 rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50 transition dark:border-white/[0.12] dark:bg-white/[0.04] dark:text-slate-200 dark:hover:bg-white/[0.08]"
                >
                  <Zap className="h-4 w-4" /> {t('OT Request')}
                </button>
              )}
              <button
                type="button"
                onClick={() => router.push(ESS_REQUESTS_PATH)}
                className="inline-flex items-center gap-1.5 rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm font-semibold text-slate-700 hover:bg-slate-50 transition dark:border-white/[0.12] dark:bg-white/[0.04] dark:text-slate-200 dark:hover:bg-white/[0.08]"
              >
                <ClipboardList className="h-4 w-4" /> {t('My Requests')}
              </button>
            </div>

            {/* Today attendance pill */}
            <AttendancePill
              status={attendance?.status}
              worked={attendance?.totalWorkedMinutes}
              missing={attendance?.missingPunch}
            />
          </div>
        </div>
      </div>

      {/* ═══ ROW 1: 4 KPI Cards ══════════════════════════════════════════════ */}
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">

        {/* Leave KPI */}
        <KpiCard
          icon={CalendarOff}
          iconBg="bg-emerald-500/10 text-emerald-600 dark:text-emerald-400"
          label={t('Leave Balance')}
          value={primaryLeave ? t('{days} days', { days: one(primaryLeave.available) }) : '—'}
          sub={primaryLeave ? leaveName(primaryLeave.leaveTypeId, primaryLeave.leaveTypeName) : t('No leave types')}
          sub2={primaryLeave ? t('Out of {days} days entitled', { days: one(primaryLeave.entitled) }) : undefined}
          onClick={() => router.push(ESS_LEAVE_PATH)}
          emptyText={dashboard.leaveBalances.length === 0 ? t('No leave balances set up yet') : undefined}
        />

        {/* Attendance KPI */}
        <KpiCard
          icon={Clock}
          iconBg="bg-blue-500/10 text-blue-600 dark:text-blue-400"
          label={t("Today's Attendance")}
          value={attendance ? enumLabel(t, 'AttendanceStatus', attendance.status) : t('Not yet')}
          sub={attendance ? workedTime(t, attendance.totalWorkedMinutes) : t('No punch today')}
          sub2={attendance?.missingPunch
            ? t('Missing punch. Please ask for a correction.')
            : attendance?.lateMinutes ? t('Late by {minutes} min', { minutes: attendance.lateMinutes })
              : attendance ? t('On time') : undefined}
          emptyText={!attendance ? t('No attendance record for today') : undefined}
        />

        {/* Payslip KPI */}
        <KpiCard
          icon={Banknote}
          iconBg="bg-violet-500/10 text-violet-600 dark:text-violet-400"
          label={t('Last Payslip')}
          value={ps ? wholeMoney(ps.netSalary, ps.currency) : '—'}
          sub={ps?.period ? fx.period(ps.period) : undefined}
          sub2={ps?.nextPayrollDate ? t('Next payroll: {date}', { date: fx.date(ps.nextPayrollDate) }) : undefined}
          onClick={() => router.push(ESS_PAYSLIPS_PATH)}
          emptyText={!ps ? t('No finalised payslips yet') : undefined}
        />

        {/* Loans / OT KPI */}
        <KpiCard
          icon={CreditCard}
          iconBg="bg-rose-500/10 text-rose-600 dark:text-rose-400"
          label={t('My Loans')}
          value={loanGroups.length ? loanGroups.map((group) => wholeMoney(group.totalOutstanding, group.currency)).join(' · ') : '—'}
          sub={loanGroups.length
            ? t('{count} active loans', { count: loanGroups.reduce((sum, group) => sum + group.activeLoanCount, 0) })
            : t('View applications and loan history')}
          sub2={dashboard.overtimeHoursThisMonth > 0 ? t('{hours} h overtime this month', { hours: otHours }) : undefined}
          emptyText={loanGroups.length === 0 && dashboard.overtimeHoursThisMonth === 0 ? t('No active loans or overtime this month') : undefined}
          onClick={() => router.push('/loans?mine=true')}
        />
      </div>

      {/* ═══ ROW 2: Performance + Upcoming & Alerts (2 wider cards) ══════════ */}
      <div className="grid gap-5 lg:grid-cols-2">

        {/* Performance & KPIs */}
        <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
          <div className="flex items-center gap-2.5 border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
            <div className="flex h-7 w-7 items-center justify-center rounded-lg bg-sapphire/10 dark:bg-cyanAccent/10">
              <TrendingUp className="h-3.5 w-3.5 text-sapphire dark:text-cyanAccent" />
            </div>
            <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Performance and goals')}</p>
          </div>
          <div className="p-5 space-y-4">
            {perf ? (
              <>
                <div className="flex items-center justify-between">
                  <div>
                    <p className="text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">{t('Current cycle')}</p>
                    <p className="mt-0.5 text-sm font-semibold text-slate-900 dark:text-white">{perf.cycleName}</p>
                  </div>
                  {perf.lastRating !== null && (
                    <div className="text-end">
                      <p className="text-xs text-slate-400 dark:text-slate-500">{t('Last rating')}</p>
                      <StarRating rating={Number(perf.lastRating)} />
                    </div>
                  )}
                </div>
                <GoalsBar done={perf.goalsCompleted} total={perf.goalsTotal} />
                <div className="flex items-center gap-4 pt-1">
                  <div className="flex items-center gap-1.5">
                    <Target className="h-3.5 w-3.5 text-slate-400" />
                    <span className="text-xs text-slate-500 dark:text-slate-400">{t('{done} of {total} goals done', { done: perf.goalsCompleted, total: perf.goalsTotal })}</span>
                  </div>
                  {dashboard.overtimeHoursThisMonth > 0 && (
                    <div className="flex items-center gap-1.5">
                      <Zap className="h-3.5 w-3.5 text-amber-500" />
                      <span className="text-xs text-slate-500 dark:text-slate-400">{t('{hours} h overtime this month', { hours: otHours })}</span>
                    </div>
                  )}
                </div>
              </>
            ) : (
              <div className="flex flex-col items-center justify-center py-6 text-center gap-2">
                <Target className="h-8 w-8 text-slate-200 dark:text-slate-700" />
                <p className="text-sm text-slate-400 dark:text-slate-500">{t('No active performance cycle')}</p>
                {dashboard.overtimeHoursThisMonth > 0 && (
                  <p className="text-xs text-slate-400 dark:text-slate-500 mt-1">{t('{hours} h overtime this month', { hours: otHours })}</p>
                )}
              </div>
            )}
          </div>
        </section>

        {/* Upcoming & Alerts */}
        <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
          <div className="flex items-center justify-between border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
            <div className="flex items-center gap-2.5">
              <div className="flex h-7 w-7 items-center justify-center rounded-lg bg-amber-500/10">
                <Calendar className="h-3.5 w-3.5 text-amber-600 dark:text-amber-400" />
              </div>
              <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Upcoming and alerts')}</p>
            </div>
            {totalUpcoming > 0 && (
              <span className="rounded-full bg-rose-500/10 px-2 py-0.5 text-[10px] font-bold text-rose-600 dark:text-rose-400">
                {t('{count} items', { count: totalUpcoming })}
              </span>
            )}
          </div>
          <div className="space-y-3.5 p-5">
            {totalUpcoming === 0 ? (
              <p className="text-sm text-slate-400 dark:text-slate-500">{t('No upcoming items or alerts.')}</p>
            ) : (
              <>
                {nextLeave && (
                  <UpcomingRow
                    dot="bg-emerald-500"
                    label={t('Leave from {start} to {end}', { start: fx.date(nextLeave.startDate), end: fx.date(nextLeave.endDate) })}
                    sub={t('{type}, {days} days', { type: leaveName(null, nextLeave.leaveTypeName), days: fx.number(nextLeave.days) })}
                    badge={t('Approved')}
                    badgeColor="bg-emerald-500/10 text-emerald-700 dark:text-emerald-400"
                  />
                )}
                {dashboard.documentAlerts.map((doc) => (
                  <UpcomingRow
                    key={doc.id}
                    dot="bg-rose-500"
                    label={t('{document} is expiring', { document: doc.documentType })}
                    sub={doc.expiryDate ? t('Expires on {date}', { date: fx.date(doc.expiryDate) }) : t('Expiry date not set')}
                    badge={t('Alert')}
                    badgeColor="bg-rose-500/10 text-rose-700 dark:text-rose-400"
                  />
                ))}
                {dashboard.actionItems.map((item) => (
                  <UpcomingRow
                    key={item.id}
                    dot="bg-amber-500"
                    label={item.title}
                    sub={item.dueAtUtc ? t('Due on {date}', { date: fx.date(item.dueAtUtc) }) : item.category}
                    badge={t('Open')}
                    badgeColor="bg-amber-500/10 text-amber-700 dark:text-amber-400"
                  />
                ))}
                {dashboard.pendingRequests > 0 && (
                  <UpcomingRow
                    dot="bg-blue-500"
                    label={t('{count} pending requests', { count: dashboard.pendingRequests })}
                    sub={t('HR requests waiting for action')}
                    badge={fx.number(dashboard.pendingRequests)}
                    badgeColor="bg-blue-500/10 text-blue-700 dark:text-blue-400"
                  />
                )}
              </>
            )}
          </div>
        </section>
      </div>

      {/* ═══ ROW 3: Leave Balances (all types) ══════════════════════════════ */}
      <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
        <div className="flex items-center justify-between border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Leave balances')}</p>
          <button type="button" onClick={() => router.push(ESS_LEAVE_PATH)} className="text-[11px] font-medium text-sapphire hover:underline dark:text-cyanAccent">
            {t('Request leave')}
          </button>
        </div>
        <div className="p-5">
          {dashboard.leaveBalances.length === 0 ? (
            <p className="text-sm text-slate-400 dark:text-slate-500">{t('No leave balances have been set up for you this year.')}</p>
          ) : (
            <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
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
        </div>
      </section>

      {/* ═══ My Roster (own shifts only) ═══════════════════════════════════ */}
      <MyShiftsCard />

      {/* ═══ ROW 4: Announcements + Action items / AI + HR Request ══════════ */}
      <div className="grid gap-5 xl:grid-cols-[1.5fr_1fr]">

        {/* Left: Action center + AI */}
        <div className="space-y-5">

          {/* Action center */}
          {(dashboard.actionItems.length > 0 || dashboard.documentAlerts.length > 0) && (
            <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
              <div className="flex items-center justify-between border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Action Center')}</p>
                <span className="rounded-full bg-rose-500/10 px-2 py-0.5 text-[10px] font-bold text-rose-600 dark:text-rose-400">
                  {t('{count} open', { count: dashboard.actionItems.length + dashboard.documentAlerts.length })}
                </span>
              </div>
              <div className="divide-y divide-slate-50 dark:divide-white/[0.04]">
                {dashboard.documentAlerts.map((doc) => (
                  <div key={doc.id} className="flex items-center justify-between gap-4 px-5 py-3.5">
                    <div className="flex items-start gap-3 min-w-0">
                      <div className="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-rose-500/10">
                        <AlertCircle className="h-3.5 w-3.5 text-rose-500" />
                      </div>
                      <div className="min-w-0">
                        <p className="text-sm font-medium text-slate-900 dark:text-white truncate">{doc.documentType}</p>
                        <p className="text-xs text-slate-500 dark:text-slate-400 truncate">
                          {doc.expiryDate
                            ? t('{file}, expires on {date}', { file: doc.fileName || t('Document'), date: fx.date(doc.expiryDate) })
                            : t('{file}, no expiry date set', { file: doc.fileName || t('Document') })}
                        </p>
                      </div>
                    </div>
                    <StatusChip label={enumLabel(t, 'ApprovalStatus', doc.approvalStatus)} />
                  </div>
                ))}
                {dashboard.actionItems.map((item) => (
                  <div key={item.id} className="flex items-center justify-between gap-4 px-5 py-3.5">
                    <div className="flex items-start gap-3 min-w-0">
                      <div className="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-amber-500/10">
                        <Clock className="h-3.5 w-3.5 text-amber-500" />
                      </div>
                      <div className="min-w-0">
                        <p className="text-sm font-medium text-slate-900 dark:text-white truncate">{item.title}</p>
                        <p className="text-xs text-slate-500 dark:text-slate-400">{item.category}</p>
                      </div>
                    </div>
                    <StatusChip label={item.dueAtUtc ? t('Due') : t('Open')} />
                  </div>
                ))}
              </div>
            </section>
          )}

          {/* AI assistant */}
          <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
            <div className="flex items-center gap-2.5 border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
              <div className="flex h-7 w-7 items-center justify-center rounded-lg bg-sapphire/10 dark:bg-cyanAccent/10">
                <MessageSquareText className="h-4 w-4 text-sapphire dark:text-cyanAccent" />
              </div>
              <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Kody the HR Assistant')}</p>
            </div>
            <div className="space-y-3 p-5">
              <textarea
                value={question}
                onChange={(e) => setQuestion(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) askAi(); }}
                placeholder={t('Ask anything: your leave balance, policies, payslip dates…')}
                rows={3}
                className="w-full resize-none rounded-xl border border-slate-200 bg-slate-50/80 px-4 py-3 text-sm text-slate-900 placeholder-slate-400 outline-none transition focus:border-sapphire/50 focus:ring-2 focus:ring-sapphire/10 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-white dark:placeholder-slate-600 dark:focus:border-cyanAccent/40"
              />
              <div className="flex items-center justify-between gap-3">
                <p className="text-[10px] text-slate-400 dark:text-slate-600">{t('Press Cmd+Enter to send')}</p>
                <button
                  type="button"
                  onClick={askAi}
                  disabled={asking || !question.trim()}
                  className="inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-4 py-2 text-sm font-semibold text-white transition hover:bg-sapphire/90 disabled:opacity-50 dark:bg-cyanAccent dark:text-slate-900"
                >
                  {asking ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Send className="h-3.5 w-3.5" />}
                  {asking ? t('Thinking…') : t('Ask Kody')}
                </button>
              </div>
              {answer && (
                <div className="rounded-xl border border-sapphire/15 bg-sapphire/[0.04] p-4 text-sm leading-relaxed text-slate-700 dark:border-cyanAccent/15 dark:bg-cyanAccent/[0.04] dark:text-slate-200">
                  {answer}
                </div>
              )}
            </div>
          </section>
        </div>

        {/* Right: Announcements + HR Request */}
        <div className="space-y-5">

          {/* Announcements */}
          <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]">
            <div className="border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
              <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Announcements')}</p>
            </div>
            <div className="space-y-2.5 p-5">
              {dashboard.announcements.length === 0 ? (
                <p className="text-sm text-slate-400 dark:text-slate-500">{t('No active announcements.')}</p>
              ) : dashboard.announcements.map((a) => (
                <AnnouncementCard key={a.id} title={a.title} body={a.body} />
              ))}
            </div>
          </section>

          {/* HR requests: raised, followed and answered on the employee's own requests page */}
          <section className="rounded-xl border border-slate-100 bg-white dark:border-white/[0.07] dark:bg-white/[0.03]" data-testid="ess-home-hr-requests">
            <div className="flex items-center justify-between border-b border-slate-100 px-5 py-3.5 dark:border-white/[0.07]">
              <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('My HR Requests')}</p>
              <button type="button" onClick={() => router.push(ESS_REQUESTS_PATH)} className="text-[11px] font-medium text-sapphire hover:underline dark:text-cyanAccent">
                {canWrite ? t('Raise a request') : t('Open my requests')}
              </button>
            </div>
            <div className="space-y-2 p-5">
              {recentRequests.length === 0 ? (
                <p className="text-sm text-slate-400 dark:text-slate-500">{t('You have not raised any requests yet.')}</p>
              ) : recentRequests.map((r) => {
                const s = hrRequestStatus(r.responseStatus);
                return (
                  <button key={r.id} type="button" onClick={() => router.push(ESS_REQUESTS_PATH)}
                    className="flex w-full items-center justify-between gap-2 rounded-lg border border-slate-100 px-3 py-2 text-start hover:bg-slate-50 dark:border-white/[0.07] dark:hover:bg-white/[0.03]">
                    <span className="min-w-0">
                      <span className="block truncate text-sm font-medium text-slate-800 dark:text-slate-200">{r.subject}</span>
                      <span className="block truncate text-xs text-slate-400">
                        {t('{category}, raised on {date}', { category: t(r.categoryName), date: fx.date(r.createdAtUtc) })}
                      </span>
                    </span>
                    <StatusChip label={t(s.label)} tone={s.tone} dot />
                  </button>
                );
              })}
            </div>
          </section>

          <MyDocumentsCard />

          {/* Quick navigation cards */}
          <div className="grid grid-cols-2 gap-2">
            {[
              { icon: CalendarOff, label: 'Request Leave', path: ESS_LEAVE_PATH, bg: 'bg-emerald-500/10 text-emerald-700 dark:text-emerald-400', border: 'border-emerald-100 dark:border-emerald-500/20' },
              { icon: FileText, label: 'My Payslips', path: ESS_PAYSLIPS_PATH, bg: 'bg-violet-500/10 text-violet-700 dark:text-violet-400', border: 'border-violet-100 dark:border-violet-500/20' },
              { icon: FileText, label: 'Jawazat Requests', path: '/ess/jawazat', bg: 'bg-sapphire/10 text-sapphire dark:text-cyanAccent', border: 'border-blue-100 dark:border-blue-500/20' },
            ].map(({ icon: Icon, label, path, bg, border }) => (
              <button
                key={label}
                type="button"
                onClick={() => router.push(path)}
                className={`flex items-center gap-2 rounded-xl border p-3 text-start hover:shadow-sm transition ${border} bg-white dark:bg-white/[0.02]`}
              >
                <div className={`flex h-7 w-7 shrink-0 items-center justify-center rounded-lg ${bg}`}>
                  <Icon className="h-3.5 w-3.5" />
                </div>
                <span className="text-xs font-semibold text-slate-700 dark:text-slate-200">{t(label)}</span>
                <ChevronRight className="ms-auto h-3.5 w-3.5 text-slate-300 rtl:-scale-x-100 dark:text-slate-600" />
              </button>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
