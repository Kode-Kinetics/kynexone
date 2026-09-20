'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import dynamic from 'next/dynamic';
import {
  AlertTriangle, ArrowDownRight, ArrowUpRight, Building2, CheckCircle2, Info,
  Minus, RefreshCw, Settings2, ShieldAlert, TrendingDown, TrendingUp, UserPlus, Users,
} from 'lucide-react';
import {
  bandLabel, bandStyle, nitaqatApi, NITAQAT_CURVE_METHOD,
  type NitaqatActivity, type NitaqatGridCoverage, type NitaqatHireImpact, type NitaqatStanding,
  type NitaqatStandingResponse, type NitaqatTrendResponse,
} from '../api/nitaqat';
import { companiesApi, type CompanyDto } from '../api/organization';

const NitaqatTrendChart = dynamic(
  () => import('../components/charts/compliance/NitaqatTrendChart').then((m) => m.NitaqatTrendChart),
  { ssr: false },
);

// ─────────────────────────────────────────────────────────────────────────────
//  Saudization / Nitaqat
//
//  Design rule for this whole panel: never show a band without the two numbers
//  that produced it and its verification status. A band gates work-visa issuance
//  and Iqama transfer, so a reader who cannot audit it will still act on it.
// ─────────────────────────────────────────────────────────────────────────────

const isSaudiCompany = (c: CompanyDto) =>
  c.countryCode?.toUpperCase() === 'SA' || c.countryCode?.toUpperCase() === 'SAU';

function BandChip({ band, large = false }: { band: string; large?: boolean }) {
  const s = bandStyle(band);
  return (
    <span
      className={`inline-flex items-center rounded-full font-semibold ${s.chip} ${
        large ? 'px-3.5 py-1.5 text-base' : 'px-2.5 py-0.5 text-xs'
      }`}
    >
      {bandLabel(band)}
    </span>
  );
}

function Stat({
  label, value, sub, tone = 'default',
}: {
  label: string;
  value: string;
  sub?: string;
  tone?: 'default' | 'warn' | 'good';
}) {
  const valueTone =
    tone === 'warn'
      ? 'text-amber-600 dark:text-amber-400'
      : tone === 'good'
        ? 'text-emerald-600 dark:text-emerald-400'
        : 'text-slate-800 dark:text-slate-100';
  return (
    <div className="rounded-lg border border-slate-100 bg-slate-50 px-3 py-2.5 dark:border-white/[0.07] dark:bg-white/[0.03]">
      <p className="text-[11px] font-medium uppercase tracking-wide text-slate-500 dark:text-slate-400">{label}</p>
      <p className={`mt-0.5 text-lg font-semibold tabular-nums ${valueTone}`}>{value}</p>
      {sub && <p className="mt-0.5 text-[11px] text-slate-500 dark:text-slate-400">{sub}</p>}
    </div>
  );
}

/** Where the establishment sits between the floor it is on and the one above. */
function BandRail({ standing }: { standing: NitaqatStanding }) {
  const floor = standing.currentBandFloorPercent;
  const ceiling = standing.nextBandUp?.requiredPercent ?? 100;
  const span = Math.max(ceiling - floor, 0.01);
  const pos = Math.min(Math.max((standing.achievedPercent - floor) / span, 0), 1) * 100;
  const s = bandStyle(standing.band);

  return (
    <div className="mt-3">
      <div className="relative h-2.5 w-full rounded-full bg-slate-100 dark:bg-white/[0.07]">
        <div className={`h-2.5 rounded-full ${s.bar}`} style={{ width: `${pos}%` }} />
        <div
          className="absolute -top-1 w-0.5 bg-slate-700 dark:bg-slate-200"
          style={{ left: `${pos}%`, height: '1.125rem' }}
          aria-hidden
        />
      </div>
      <div className="mt-1 flex justify-between text-[11px] text-slate-500 dark:text-slate-400">
        <span>
          {bandLabel(standing.band)} floor {floor.toFixed(2)}%
        </span>
        <span>
          {standing.nextBandUp
            ? `${bandLabel(standing.nextBandUp.band)} at ${standing.nextBandUp.requiredPercent.toFixed(2)}%`
            : 'Top band'}
        </span>
      </div>
    </div>
  );
}

/** The MHRSD annex a customer must load before any real activity can be banded. */
const MHRSD_ANNEX_URL = 'https://www.hrsd.gov.sa/sites/default/files/2026-03/ntaqat-almtwr.pdf';

function Refused({ refusal, coverage, onConfigure }: {
  refusal: { reason: string; message: string; remedy: string };
  coverage: NitaqatGridCoverage | null;
  onConfigure: () => void;
}) {
  // The refusal a real customer hits: they picked their actual economic activity and the
  // product has no band floors for it. Before, this rendered as a bare message with no way
  // forward, which reads as a broken screen rather than a missing configuration.
  const thresholdsMissing = refusal.reason === 'nitaqat_thresholds_not_published';

  return (
    <div className="surface flex flex-col items-start gap-3 p-6">
      <div className="flex items-center gap-2">
        <ShieldAlert className="h-5 w-5 text-amber-500" />
        <h3 className="text-sm font-semibold text-slate-800 dark:text-slate-100">No Nitaqat band is shown</h3>
        <code className="rounded bg-slate-100 px-1.5 py-0.5 text-[11px] text-slate-600 dark:bg-white/[0.06] dark:text-slate-300">
          {refusal.reason}
        </code>
      </div>
      <p className="text-sm text-slate-600 dark:text-slate-300">{refusal.message}</p>
      <div className="rounded-lg border border-slate-100 bg-slate-50 px-3 py-2 text-sm text-slate-600 dark:border-white/[0.07] dark:bg-white/[0.03] dark:text-slate-300">
        <span className="font-semibold">What to do: </span>
        {refusal.remedy}
      </div>

      {thresholdsMissing && (
        <div className="w-full space-y-3 rounded-lg border border-amber-200 bg-amber-50/70 p-4 dark:border-amber-500/25 dark:bg-amber-500/[0.07]">
          <p className="text-sm font-semibold text-amber-900 dark:text-amber-200">
            Saudization banding needs configuring before it can answer
          </p>
          <p className="text-sm text-amber-900/90 dark:text-amber-100/90">
            Since 1&nbsp;December&nbsp;2021 MHRSD works out the band floor from a curve published
            per economic activity, not from a fixed table. Those constants are published by the
            Ministry and are deliberately <strong>not shipped with this product</strong>: they are
            revised periodically, and a stale constant would produce a confident wrong answer about
            whether you can issue a work visa.
          </p>
          <ol className="list-decimal space-y-1 pl-5 text-sm text-amber-900/90 dark:text-amber-100/90">
            <li>
              Download the current annex —{' '}
              <a
                href={MHRSD_ANNEX_URL}
                target="_blank"
                rel="noreferrer noopener"
                className="font-medium underline underline-offset-2"
              >
                MHRSD Nitaqat Mutawar procedural guide
              </a>{' '}
              (hrsd.gov.sa, free, no login).
            </li>
            <li>Find your establishment&apos;s economic activity in Annex&nbsp;1.</li>
            <li>
              Load its figures under <strong>Saudi Compliance → Saudization → Nitaqat grid</strong>,
              or ask your implementation consultant to. Every load records where the numbers came
              from and who checked them.
            </li>
          </ol>
          <p className="text-sm text-amber-900/90 dark:text-amber-100/90">
            You can also record the band Qiwa itself reports for your establishment — Qiwa is
            authoritative and the dashboard will show it alongside our estimate.
          </p>
          {coverage && (
            <p className="text-xs text-amber-900/80 dark:text-amber-100/80">
              {coverage.activitiesWithCompleteGrid} of {coverage.activitiesTotal} economic
              activities currently have band floors loaded for this tenant.
            </p>
          )}
        </div>
      )}

      {refusal.reason === 'nitaqat_activity_not_configured' && (
        <button
          type="button"
          onClick={onConfigure}
          className="inline-flex items-center gap-2 rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white hover:bg-sapphire/90"
        >
          <Settings2 className="h-4 w-4" />
          Set the economic activity
        </button>
      )}
    </div>
  );
}

/**
 * Shown when a band WAS produced but off the pre-2021 size-tier table rather than the curve
 * MHRSD uses today. The band is still the best answer available, so it is not suppressed —
 * but a reader must not take it for the current regime's answer.
 */
function BandingMethodNotice({ standing }: { standing: NitaqatStanding }) {
  if (standing.bandingMethod === NITAQAT_CURVE_METHOD) return null;

  return (
    <div className="surface flex items-start gap-2.5 border-l-4 border-amber-400 p-4 dark:border-amber-500/60">
      <Info className="mt-0.5 h-4 w-4 shrink-0 text-amber-500" />
      <div className="space-y-1">
        <p className="text-sm font-semibold text-slate-800 dark:text-slate-100">
          This band came from a manually loaded table, not the MHRSD curve
        </p>
        <p className="text-sm text-slate-600 dark:text-slate-300">{standing.bandingMethodNote}</p>
        <p className="text-xs text-slate-500 dark:text-slate-400">
          Load this activity&apos;s curve constants from the{' '}
          <a
            href={MHRSD_ANNEX_URL}
            target="_blank"
            rel="noreferrer noopener"
            className="underline underline-offset-2"
          >
            current MHRSD annex
          </a>{' '}
          for an answer on the regime in force.
        </p>
      </div>
    </div>
  );
}

// ── Setup ─────────────────────────────────────────────────────────────────────

function SetupForm({
  companies, companyId, onSaved,
}: {
  companies: CompanyDto[];
  companyId: string;
  onSaved: () => void;
}) {
  const [activities, setActivities] = useState<NitaqatActivity[] | null>(null);
  const [activityCode, setActivityCode] = useState('');
  const [mhrsd, setMhrsd] = useState('');
  const [qiwaBand, setQiwaBand] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    nitaqatApi
      .activities()
      .then((a) => live && setActivities(a))
      .catch(() => live && setActivities([]));
    return () => {
      live = false;
    };
  }, []);

  const grouped = useMemo(() => {
    const map = new Map<string, NitaqatActivity[]>();
    (activities ?? []).forEach((a) => {
      const key = a.activityGroup || 'Other';
      map.set(key, [...(map.get(key) ?? []), a]);
    });
    return [...map.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [activities]);

  const submit = async () => {
    if (!activityCode) {
      setError('Choose an economic activity. Nitaqat targets are activity-specific.');
      return;
    }
    setSaving(true);
    setError(null);
    try {
      await nitaqatApi.saveProfile({
        companyId,
        activityCode,
        mhrsdEstablishmentNumber: mhrsd || undefined,
        qiwaReportedBand: qiwaBand || undefined,
      });
      onSaved();
    } catch {
      setError('Could not save. You may not have permission to change compliance settings.');
    } finally {
      setSaving(false);
    }
  };

  const company = companies.find((c) => c.id === companyId);

  return (
    <div className="surface space-y-4 p-5">
      <div className="flex items-center gap-2">
        <Settings2 className="h-4 w-4 text-sapphire dark:text-cyanAccent" />
        <h3 className="text-sm font-semibold text-slate-800 dark:text-slate-100">
          Nitaqat setup — {company?.tradeName || company?.legalNameEn || 'establishment'}
        </h3>
      </div>

      <p className="text-sm text-slate-600 dark:text-slate-300">
        The required Saudization percentage depends on the establishment&apos;s MHRSD economic activity
        and its size tier. No band is computed until the activity is set, because a wrong band is
        worse than no band.
      </p>

      {activities === null ? (
        <div className="h-10 animate-pulse rounded-lg bg-slate-100 dark:bg-white/[0.06]" />
      ) : (
        <label className="block">
          <span className="text-xs font-medium text-slate-600 dark:text-slate-300">Economic activity</span>
          <select
            value={activityCode}
            onChange={(e) => setActivityCode(e.target.value)}
            className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 dark:border-white/[0.1] dark:bg-[#0d1225] dark:text-slate-100"
          >
            <option value="">Select…</option>
            {grouped.map(([group, items]) => (
              <optgroup key={group} label={group}>
                {items.map((a) => (
                  <option key={a.code} value={a.code}>
                    {a.nameEn}
                  </option>
                ))}
              </optgroup>
            ))}
          </select>
        </label>
      )}

      <div className="grid gap-3 sm:grid-cols-2">
        <label className="block">
          <span className="text-xs font-medium text-slate-600 dark:text-slate-300">
            MHRSD establishment number <span className="text-slate-400">(optional)</span>
          </span>
          <input
            value={mhrsd}
            onChange={(e) => setMhrsd(e.target.value)}
            placeholder="7000123456"
            className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 dark:border-white/[0.1] dark:bg-[#0d1225] dark:text-slate-100"
          />
        </label>
        <label className="block">
          <span className="text-xs font-medium text-slate-600 dark:text-slate-300">
            Band Qiwa reports <span className="text-slate-400">(optional)</span>
          </span>
          <select
            value={qiwaBand}
            onChange={(e) => setQiwaBand(e.target.value)}
            className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 dark:border-white/[0.1] dark:bg-[#0d1225] dark:text-slate-100"
          >
            <option value="">Not recorded</option>
            {['Platinum', 'HighGreen', 'MediumGreen', 'LowGreen', 'Red'].map((b) => (
              <option key={b} value={b}>
                {bandLabel(b)}
              </option>
            ))}
          </select>
        </label>
      </div>

      {error && (
        <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">
          {error}
        </p>
      )}

      <button
        type="button"
        onClick={submit}
        disabled={saving}
        className="rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white hover:bg-sapphire/90 disabled:opacity-60"
      >
        {saving ? 'Saving…' : 'Save'}
      </button>
    </div>
  );
}

// ── Scenario modelling ────────────────────────────────────────────────────────

function ScenarioCard({ standing, companyId }: { standing: NitaqatStanding; companyId: string }) {
  const [nationality, setNationality] = useState('Indian');
  const [count, setCount] = useState(1);
  const [impact, setImpact] = useState<NitaqatHireImpact | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const { scenario } = standing;

  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      setImpact(await nitaqatApi.hireImpact(nationality, count, companyId));
    } catch {
      setError('Could not model that hire.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="surface p-5">
      <div className="mb-3 flex items-center gap-2">
        <UserPlus className="h-4 w-4 text-sapphire dark:text-cyanAccent" />
        <h3 className="text-sm font-semibold text-slate-800 dark:text-slate-100">Scenario modelling</h3>
      </div>

      <div className="grid gap-3 sm:grid-cols-3">
        <Stat
          label="Saudi hires to next band"
          value={
            scenario.saudiHiresToNextBand === null
              ? standing.nextBandUp
                ? 'Not reachable'
                : 'Top band'
              : `${scenario.saudiHiresToNextBand}`
          }
          sub={scenario.nextBand ? `to reach ${bandLabel(scenario.nextBand)}` : 'already Platinum'}
          tone="good"
        />
        <Stat
          label="Expat hires before downgrade"
          value={scenario.expatHiresBeforeDowngrade === null ? '—' : `${scenario.expatHiresBeforeDowngrade}`}
          sub={scenario.bandBelow ? `then drops to ${bandLabel(scenario.bandBelow)}` : 'no band below'}
          tone={scenario.expatHiresBeforeDowngrade === 0 ? 'warn' : 'default'}
        />
        <Stat
          label="Saudi leavers before downgrade"
          value={scenario.saudiLeaversBeforeDowngrade === null ? '—' : `${scenario.saudiLeaversBeforeDowngrade}`}
          sub="attrition is how most establishments fall"
          tone={
            scenario.saudiLeaversBeforeDowngrade !== null && scenario.saudiLeaversBeforeDowngrade <= 1
              ? 'warn'
              : 'default'
          }
        />
      </div>

      {standing.nextBandUp?.infeasible && (
        <p className="mt-3 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:border-amber-800/60 dark:bg-amber-900/20 dark:text-amber-300">
          {standing.nextBandUp.infeasible}
        </p>
      )}

      <div className="mt-4 border-t border-slate-100 pt-4 dark:border-white/[0.07]">
        <p className="mb-2 text-xs font-medium uppercase tracking-wide text-slate-500 dark:text-slate-400">
          Model a pending hire
        </p>
        <div className="flex flex-wrap items-end gap-2">
          <label className="min-w-0 flex-1">
            <span className="sr-only">Nationality</span>
            <input
              value={nationality}
              onChange={(e) => setNationality(e.target.value)}
              placeholder="Nationality"
              className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 dark:border-white/[0.1] dark:bg-[#0d1225] dark:text-slate-100"
            />
          </label>
          <label className="w-24">
            <span className="sr-only">Number of hires</span>
            <input
              type="number"
              min={1}
              value={count}
              onChange={(e) => setCount(Math.max(1, Number(e.target.value) || 1))}
              className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 dark:border-white/[0.1] dark:bg-[#0d1225] dark:text-slate-100"
            />
          </label>
          <button
            type="button"
            onClick={run}
            disabled={busy}
            className="rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white hover:bg-sapphire/90 disabled:opacity-60"
          >
            {busy ? 'Modelling…' : 'Model'}
          </button>
        </div>

        {error && (
          <p role="alert" className="mt-2 text-sm text-rose-600 dark:text-rose-400">
            {error}
          </p>
        )}

        {impact?.ok && (
          <div
            className={`mt-3 flex items-start gap-2 rounded-lg border px-3 py-2 text-sm ${
              !impact.bandChanges
                ? 'border-slate-200 bg-slate-50 text-slate-700 dark:border-white/[0.07] dark:bg-white/[0.03] dark:text-slate-300'
                : impact.bandImproves
                  ? 'border-emerald-200 bg-emerald-50 text-emerald-800 dark:border-emerald-800/60 dark:bg-emerald-900/20 dark:text-emerald-300'
                  : 'border-rose-200 bg-rose-50 text-rose-800 dark:border-rose-800/60 dark:bg-rose-900/20 dark:text-rose-300'
            }`}
          >
            {!impact.bandChanges ? (
              <Minus className="mt-0.5 h-4 w-4 shrink-0" />
            ) : impact.bandImproves ? (
              <ArrowUpRight className="mt-0.5 h-4 w-4 shrink-0" />
            ) : (
              <ArrowDownRight className="mt-0.5 h-4 w-4 shrink-0" />
            )}
            <span>{impact.summary}</span>
          </div>
        )}
      </div>
    </div>
  );
}

// ── Working ───────────────────────────────────────────────────────────────────

function BreakdownCard({ standing }: { standing: NitaqatStanding }) {
  return (
    <div className="surface p-5">
      <div className="mb-3 flex items-center gap-2">
        <Users className="h-4 w-4 text-sapphire dark:text-cyanAccent" />
        <h3 className="text-sm font-semibold text-slate-800 dark:text-slate-100">
          Weighted headcount — how the percentage was reached
        </h3>
      </div>

      <p className="mb-3 text-xs text-slate-500 dark:text-slate-400">
        Nitaqat counts units, not heads. {standing.rawTotalHeadcount} employees resolve to{' '}
        {standing.totalWeighted} weighted units, of which {standing.saudiWeighted} are Saudi.
      </p>

      <div className="-mx-2 overflow-x-auto">
        <table className="w-full min-w-[34rem] text-left text-sm">
          <thead>
            <tr className="border-b border-slate-100 text-[11px] uppercase tracking-wide text-slate-500 dark:border-white/[0.07] dark:text-slate-400">
              <th scope="col" className="px-2 py-1.5 font-medium">Group</th>
              <th scope="col" className="px-2 py-1.5 text-right font-medium">Heads</th>
              <th scope="col" className="px-2 py-1.5 text-right font-medium">Saudi ea.</th>
              <th scope="col" className="px-2 py-1.5 text-right font-medium">Total ea.</th>
              <th scope="col" className="px-2 py-1.5 text-right font-medium">Saudi units</th>
              <th scope="col" className="px-2 py-1.5 text-right font-medium">Total units</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
            {standing.breakdown.map((b) => (
              <tr key={`${b.classification}-${b.countBasis}-${b.category}`}>
                <td className="px-2 py-1.5 text-slate-700 dark:text-slate-200">
                  {b.classification} · {b.countBasis}
                  {b.category !== 'Standard' && (
                    <span className="ml-1 rounded bg-slate-100 px-1.5 py-0.5 text-[10px] text-slate-600 dark:bg-white/[0.06] dark:text-slate-300">
                      {b.category}
                    </span>
                  )}
                  {!b.isVerified && (
                    <span
                      title={b.sourceNote}
                      className="ml-1 rounded bg-amber-100 px-1.5 py-0.5 text-[10px] text-amber-700 dark:bg-amber-500/20 dark:text-amber-400"
                    >
                      unverified
                    </span>
                  )}
                </td>
                <td className="px-2 py-1.5 text-right tabular-nums text-slate-600 dark:text-slate-300">{b.heads}</td>
                <td className="px-2 py-1.5 text-right tabular-nums text-slate-500 dark:text-slate-400">{b.numeratorWeightEach}</td>
                <td className="px-2 py-1.5 text-right tabular-nums text-slate-500 dark:text-slate-400">{b.denominatorWeightEach}</td>
                <td className="px-2 py-1.5 text-right tabular-nums font-medium text-slate-700 dark:text-slate-200">{b.numeratorTotal}</td>
                <td className="px-2 py-1.5 text-right tabular-nums font-medium text-slate-700 dark:text-slate-200">{b.denominatorTotal}</td>
              </tr>
            ))}
          </tbody>
          <tfoot>
            <tr className="border-t border-slate-200 text-sm font-semibold dark:border-white/[0.1]">
              <td className="px-2 py-2 text-slate-700 dark:text-slate-200">Total</td>
              <td className="px-2 py-2 text-right tabular-nums text-slate-700 dark:text-slate-200">{standing.rawTotalHeadcount}</td>
              <td colSpan={2} />
              <td className="px-2 py-2 text-right tabular-nums text-slate-800 dark:text-slate-100">{standing.saudiWeighted}</td>
              <td className="px-2 py-2 text-right tabular-nums text-slate-800 dark:text-slate-100">{standing.totalWeighted}</td>
            </tr>
          </tfoot>
        </table>
      </div>
    </div>
  );
}

// ── Panel ─────────────────────────────────────────────────────────────────────

export function NitaqatPanel() {
  const [companies, setCompanies] = useState<CompanyDto[] | null>(null);
  const [companyId, setCompanyId] = useState<string>('');
  const [data, setData] = useState<NitaqatStandingResponse | null>(null);
  const [trend, setTrend] = useState<NitaqatTrendResponse | null>(null);
  // Grid coverage is context for the refusal — how much of the MHRSD table this tenant has.
  const [coverage, setCoverage] = useState<NitaqatGridCoverage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showSetup, setShowSetup] = useState(false);

  useEffect(() => {
    let live = true;
    companiesApi
      .list(1, 100)
      .then((r) => {
        if (!live) return;
        const saudi = (r.items ?? []).filter(isSaudiCompany);
        setCompanies(saudi);
        setCompanyId((prev) => prev || saudi[0]?.id || '');
      })
      .catch(() => live && setCompanies([]));
    return () => {
      live = false;
    };
  }, []);

  const load = useCallback(async () => {
    if (companies === null) return;
    setLoading(true);
    setError(null);
    try {
      const [standing, series, grid] = await Promise.all([
        nitaqatApi.standing(companyId || undefined),
        nitaqatApi.trend(180, companyId || undefined).catch(() => null),
        // Coverage is context for the refusal, never a reason to fail the screen.
        nitaqatApi.gridCoverage().catch(() => null),
      ]);
      setData(standing);
      setTrend(series);
      setCoverage(grid);
      setShowSetup(standing.refusal?.reason === 'nitaqat_activity_not_configured' && companies.length > 0);
    } catch {
      setError('Unable to load Saudization data. You may not have access or the module is not enabled.');
    } finally {
      setLoading(false);
    }
  }, [companyId, companies]);

  useEffect(() => {
    void load();
  }, [load]);

  // ── Loading ───────────────────────────────────────────────────────────────
  if (loading || companies === null) {
    return (
      <div className="space-y-4" aria-busy="true">
        <div className="surface h-36 animate-pulse" />
        <div className="grid gap-4 lg:grid-cols-2">
          <div className="surface h-56 animate-pulse" />
          <div className="surface h-56 animate-pulse" />
        </div>
      </div>
    );
  }

  // ── Error ─────────────────────────────────────────────────────────────────
  if (error) {
    return (
      <div className="surface flex flex-col items-center gap-3 p-10 text-center">
        <AlertTriangle className="h-8 w-8 text-amber-500" />
        <p className="text-sm text-slate-600 dark:text-slate-300">{error}</p>
        <button
          type="button"
          onClick={() => void load()}
          className="rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white hover:bg-sapphire/90"
        >
          Retry
        </button>
      </div>
    );
  }

  // ── Empty: no Saudi establishment at all ──────────────────────────────────
  if (companies.length === 0) {
    return (
      <div className="surface flex flex-col items-center gap-3 p-10 text-center">
        <Building2 className="h-8 w-8 text-slate-400" />
        <p className="text-sm font-medium text-slate-700 dark:text-slate-200">No Saudi establishment</p>
        <p className="max-w-md text-sm text-slate-500 dark:text-slate-400">
          Nitaqat applies only to establishments registered with MHRSD in the Kingdom. Add a company
          with country SA, or set the country on an existing company.
        </p>
      </div>
    );
  }

  const picker = companies.length > 1 && (
    <label className="flex items-center gap-2 text-sm">
      <span className="text-slate-500 dark:text-slate-400">Establishment</span>
      <select
        value={companyId}
        onChange={(e) => setCompanyId(e.target.value)}
        className="rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-sm text-slate-800 dark:border-white/[0.1] dark:bg-[#0d1225] dark:text-slate-100"
      >
        {companies.map((c) => (
          <option key={c.id} value={c.id}>
            {c.tradeName || c.legalNameEn}
          </option>
        ))}
      </select>
    </label>
  );

  const standing = data?.standing ?? null;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        {picker || <div />}
        <div className="flex items-center gap-2">
          {standing && (
            <button
              type="button"
              onClick={() => setShowSetup((v) => !v)}
              className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-sm text-slate-600 hover:bg-slate-50 dark:border-white/[0.1] dark:text-slate-300 dark:hover:bg-white/[0.04]"
            >
              <Settings2 className="h-3.5 w-3.5" />
              Setup
            </button>
          )}
          <button
            type="button"
            onClick={() => void load()}
            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-sm text-slate-600 hover:bg-slate-50 dark:border-white/[0.1] dark:text-slate-300 dark:hover:bg-white/[0.04]"
          >
            <RefreshCw className="h-3.5 w-3.5" />
            Refresh
          </button>
        </div>
      </div>

      {showSetup && companyId && (
        <SetupForm
          companies={companies}
          companyId={companyId}
          onSaved={() => {
            setShowSetup(false);
            void load();
          }}
        />
      )}

      {!standing && data?.refusal && (
        <Refused
          refusal={data.refusal}
          coverage={coverage}
          onConfigure={() => setShowSetup(true)}
        />
      )}

      {standing && <BandingMethodNotice standing={standing} />}

      {standing && (
        <>
          {/* Headline */}
          <div className="surface p-5">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div>
                <div className="flex items-center gap-2.5">
                  <BandChip band={standing.band} large />
                  <span className="text-2xl font-semibold tabular-nums text-slate-800 dark:text-slate-100">
                    {standing.achievedPercent.toFixed(2)}%
                  </span>
                </div>
                <p className="mt-1.5 text-sm text-slate-600 dark:text-slate-300">
                  {standing.companyName} · {standing.activityNameEn} · {standing.sizeTierNameEn}
                </p>
              </div>
              <div className="text-right text-xs text-slate-500 dark:text-slate-400">
                <p>As of {standing.asOf}</p>
                <p className="mt-0.5 tabular-nums">
                  {standing.saudiWeighted} / {standing.totalWeighted} weighted units
                </p>
              </div>
            </div>

            <BandRail standing={standing} />

            <p
              className={`mt-3 flex items-start gap-2 rounded-lg border px-3 py-2 text-sm ${
                standing.restrictsServices
                  ? 'border-rose-200 bg-rose-50 text-rose-800 dark:border-rose-800/60 dark:bg-rose-900/20 dark:text-rose-300'
                  : 'border-emerald-200 bg-emerald-50 text-emerald-800 dark:border-emerald-800/60 dark:bg-emerald-900/20 dark:text-emerald-300'
              }`}
            >
              {standing.restrictsServices ? (
                <ShieldAlert className="mt-0.5 h-4 w-4 shrink-0" />
              ) : (
                <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" />
              )}
              <span>{standing.consequenceSummary}</span>
            </p>

            {standing.disagreesWithQiwa && (
              <p className="mt-2 flex items-start gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:border-amber-800/60 dark:bg-amber-900/20 dark:text-amber-300">
                <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
                <span>
                  Qiwa reports <strong>{bandLabel(standing.qiwaReportedBand)}</strong>
                  {standing.qiwaReportedOn ? ` as at ${standing.qiwaReportedOn}` : ''}, which differs
                  from this estimate. MHRSD computes the band from its own register over a rolling
                  window and is authoritative — treat this figure as an early indicator only.
                </span>
              </p>
            )}

            {!standing.allInputsVerified && (
              <details className="mt-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:border-amber-800/60 dark:bg-amber-900/20 dark:text-amber-300">
                <summary className="cursor-pointer font-medium">
                  <Info className="mr-1 inline h-4 w-4" />
                  {standing.unverifiedInputs.length} input
                  {standing.unverifiedInputs.length === 1 ? '' : 's'} not yet verified against MHRSD
                </summary>
                <ul className="mt-1.5 list-inside list-disc space-y-0.5 text-[13px]">
                  {standing.unverifiedInputs.map((u) => (
                    <li key={u}>{u}</li>
                  ))}
                </ul>
                <p className="mt-1.5 text-[13px]">
                  Confirm these against the published MHRSD Nitaqat table or the establishment&apos;s
                  Qiwa record before acting on this band.
                </p>
              </details>
            )}
          </div>

          {/* Distance, both ways */}
          <div className="grid gap-4 sm:grid-cols-3">
            <Stat
              label="Current band floor"
              value={`${standing.currentBandFloorPercent.toFixed(2)}%`}
              sub={`${bandLabel(standing.band)} starts here`}
            />
            <Stat
              label="Next band up"
              value={standing.nextBandUp ? `${standing.nextBandUp.requiredPercent.toFixed(2)}%` : '—'}
              sub={
                standing.nextBandUp
                  ? `${bandLabel(standing.nextBandUp.band)} · ${standing.nextBandUp.percentGap.toFixed(2)} pts away`
                  : 'Already Platinum'
              }
            />
            <Stat
              label="Headroom above the floor"
              value={`${(standing.achievedPercent - standing.currentBandFloorPercent).toFixed(2)} pts`}
              sub={standing.bandBelow ? `then ${bandLabel(standing.bandBelow.band)}` : 'lowest band'}
              tone={standing.achievedPercent - standing.currentBandFloorPercent < 2 ? 'warn' : 'default'}
            />
          </div>

          <ScenarioCard standing={standing} companyId={standing.companyId} />

          {/* Trend */}
          <div className="surface p-5">
            <div className="mb-3 flex items-center justify-between gap-2">
              <div className="flex items-center gap-2">
                {trend?.direction === 'Declining' ? (
                  <TrendingDown className="h-4 w-4 text-rose-500" />
                ) : (
                  <TrendingUp className="h-4 w-4 text-sapphire dark:text-cyanAccent" />
                )}
                <h3 className="text-sm font-semibold text-slate-800 dark:text-slate-100">
                  Saudization trend
                </h3>
              </div>
              {trend?.changePercentagePoints != null && (
                <span
                  className={`text-sm font-medium tabular-nums ${
                    trend.changePercentagePoints < 0
                      ? 'text-rose-600 dark:text-rose-400'
                      : 'text-emerald-600 dark:text-emerald-400'
                  }`}
                >
                  {trend.changePercentagePoints > 0 ? '+' : ''}
                  {trend.changePercentagePoints.toFixed(2)} pts
                </span>
              )}
            </div>

            {!trend || trend.points.length < 2 ? (
              <p className="py-8 text-center text-sm text-slate-400 dark:text-slate-500">
                Not enough history yet. A trend point is recorded each day this screen is opened, so
                the line builds from today.
              </p>
            ) : (
              <>
                <div className="h-56">
                  <NitaqatTrendChart
                    points={trend.points}
                    floor={standing.currentBandFloorPercent}
                    nextFloor={standing.nextBandUp?.requiredPercent ?? null}
                  />
                </div>
                {trend.projectedBandWarning && (
                  <p className="mt-3 flex items-start gap-2 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:border-amber-800/60 dark:bg-amber-900/20 dark:text-amber-300">
                    <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
                    <span>{trend.projectedBandWarning}</span>
                  </p>
                )}
              </>
            )}
          </div>

          <BreakdownCard standing={standing} />
        </>
      )}
    </div>
  );
}

export default NitaqatPanel;
