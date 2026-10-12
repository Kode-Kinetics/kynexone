'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { Download, EyeOff, Info, MessageSquareText, Plus, RefreshCw, ShieldCheck } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { useAuth } from '../../contexts/AuthContext';
import { useFormat } from '../../hooks/useFormat';
import { describeApiError } from '../../lib/apiError';
import {
  DIMENSION_LABELS, MODE_LABELS, SURVEY_STATUS_LABELS, TENURE_LABELS, metricCell, pick, suppressedText, suppressionReasonText,
} from '../../lib/engagementDisplay';
import {
  engagementApi, type Comments, type Dimension, type EngagementOptions, type Filters, type Results, type Survey, type TrendPoint,
} from '../../api/engagement';
import { DataPanel } from '../DataPanel';
import { Modal } from '../Modal';
import { StatusChip } from '../StatusChip';
import { LikertBar, LikertLegend } from './LikertBar';

const DIMENSIONS: Dimension[] = ['department', 'designation', 'grade', 'tenure', 'company'];

export interface ActionSeed { surveyId: string; sourceKind: 'Overall' | 'Driver' | 'Item' | 'Cohort'; sourceRef: string; title: string }

/**
 * Results of one survey: participation (with its denominator), favourable %, eNPS, drivers, every item's distribution,
 * a breakdown, comments and exports. Every number comes from the server already suppressed; a hidden value renders the
 * suppression sentence and its reason, never a number. Filters intersect; the server refuses an intersection that is too
 * small or that could be differenced against a wider view, and the panel says so.
 */
export function ResultsPanel({
  surveys, options, initialSurveyId, onCreateAction,
}: {
  surveys: Survey[];
  options: EngagementOptions | null;
  initialSurveyId?: string | null;
  onCreateAction?: (seed: ActionSeed) => void;
}) {
  const { t, locale } = useLocale();
  const { hasPermission } = useAuth();
  const f = useFormat();
  const canComments = hasPermission('engagement.comments.read');
  const canAct = hasPermission('engagement.actions.manage');
  const reportable = useMemo(() => surveys.filter((s) => s.status === 'Open' || s.status === 'Closed'), [surveys]);
  const [surveyId, setSurveyId] = useState<string>(initialSurveyId ?? reportable[0]?.id ?? '');
  const [filters, setFilters] = useState<Filters>({});
  const [results, setResults] = useState<Results | null>(null);
  const [trend, setTrend] = useState<TrendPoint[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [comments, setComments] = useState<{ question: string; data: Comments } | null>(null);
  const [exporting, setExporting] = useState<'csv' | 'xlsx' | null>(null);
  const [includeComments, setIncludeComments] = useState(false);

  useEffect(() => {
    if (initialSurveyId) setSurveyId(initialSurveyId);
  }, [initialSurveyId]);
  useEffect(() => {
    if (!surveyId && reportable[0]) setSurveyId(reportable[0].id);
  }, [reportable, surveyId]);

  const load = useCallback(async () => {
    if (!surveyId) return;
    setLoading(true);
    setError(null);
    try {
      const [r, tr] = await Promise.all([engagementApi.results(surveyId, filters), engagementApi.trend(surveyId)]);
      setResults(r);
      setTrend(tr);
    } catch (e) {
      setError(describeApiError(e, t));
      setResults(null);
    } finally {
      setLoading(false);
    }
  }, [surveyId, filters, t]);

  useEffect(() => { void load(); }, [load]);

  const k = results?.minCohortSize ?? 5;
  const pct = (n: number) => f.percent(n, 1);
  const score = (n: number) => f.number(n, { maximumFractionDigits: 1 });

  const optionLabel = (dim: Dimension, value: string) => {
    if (dim === 'tenure') return t(TENURE_LABELS[value] ?? value);
    const list = dim === 'department' ? options?.departments : dim === 'designation' ? options?.designations
      : dim === 'company' ? options?.companies : options?.gradeBands;
    const hit = list?.find((o) => o.value === value);
    return hit ? pick(locale, hit.labelEn, hit.labelAr) : value === 'unknown' ? t('Not recorded') : value;
  };

  const optionsFor = (dim: Dimension) => {
    if (dim === 'tenure') return Object.keys(TENURE_LABELS).map((v) => ({ value: v, label: t(TENURE_LABELS[v]) }));
    const list = dim === 'department' ? options?.departments : dim === 'designation' ? options?.designations
      : dim === 'company' ? options?.companies : options?.gradeBands;
    return (list ?? []).map((o) => ({ value: o.value, label: pick(locale, o.labelEn, o.labelAr) }));
  };

  const openComments = async (question: string) => {
    try {
      const commentFilters: Filters = {};
      if (commentDim !== 'none' && filters[commentDim as Dimension]) commentFilters[commentDim as Dimension] = filters[commentDim as Dimension];
      setComments({ question, data: await engagementApi.comments(surveyId, question, commentFilters) });
    } catch (e) {
      setError(describeApiError(e, t));
    }
  };

  const download = async (format: 'csv' | 'xlsx') => {
    setExporting(format);
    try {
      const blob = await engagementApi.exportResults(surveyId, format, filters, includeComments && canComments && commentsAllowed);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `engagement-results.${format}`;
      a.click();
      URL.revokeObjectURL(url);
    } catch (e) {
      setError(describeApiError(e, t));
    } finally {
      setExporting(null);
    }
  };

  if (reportable.length === 0) {
    return (
      <div className="surface rounded-xl p-8 text-center text-sm text-slate-600 dark:text-slate-300">
        <p className="font-semibold text-slate-900 dark:text-white">{t('No results yet')}</p>
        <p className="mt-1">{t('Results appear here once a survey has opened.')}</p>
      </div>
    );
  }

  const survey = reportable.find((s) => s.id === surveyId);
  const activeFilters = DIMENSIONS.filter((d) => filters[d]);
  // At most two of: the reader's scope (company / team), filters and a breakdown — the server refuses more, since
  // smaller intersections could identify people.
  const scopeDimension = results?.scopeDimension ?? null;
  const maxDims = scopeDimension ? 1 : 2;
  const usedDims = activeFilters.length + (filters.breakdown ? 1 : 0);
  const available = results?.resultsAvailable !== false;
  const commentDim = results?.commentDimension ?? 'department';
  // Comments: the whole survey, or one value of the survey's declared comment dimension — never intersected.
  const commentsAllowed = !filters.breakdown
    && activeFilters.every((d) => d === commentDim)
    && (activeFilters.length === 0 || commentDim !== 'none');

  return (
    <div className="space-y-4" data-testid="engagement-results">
      {/* Survey + filters: one row above the charts. */}
      <div className="surface flex flex-col gap-3 rounded-xl p-4 lg:flex-row lg:flex-wrap lg:items-end">
        <label className="flex min-w-[220px] flex-1 flex-col gap-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
          {t('Survey')}
          <select className="select" value={surveyId} onChange={(e) => { setSurveyId(e.target.value); setFilters({}); }} data-testid="results-survey">
            {reportable.map((s) => (
              <option key={s.id} value={s.id}>
                {pick(locale, s.titleEn, s.titleAr)} · {t('Cycle {n}', { n: s.cycleNumber })}
              </option>
            ))}
          </select>
        </label>
        {DIMENSIONS.map((dim) => (
          <label key={dim} className="flex min-w-[150px] flex-col gap-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
            {t(DIMENSION_LABELS[dim])}
            <select
              className="select"
              value={filters[dim] ?? ''}
              data-testid={`filter-${dim}`}
              disabled={!available || (usedDims >= maxDims && !filters[dim])}
              onChange={(e) => setFilters((prev) => ({ ...prev, [dim]: e.target.value || undefined, breakdown: prev.breakdown === dim ? undefined : prev.breakdown }))}
            >
              <option value="">{t('Everyone')}</option>
              {optionsFor(dim).map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
            </select>
          </label>
        ))}
        <label className="flex min-w-[150px] flex-col gap-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
          {t('Break down by')}
          <select
            className="select"
            value={filters.breakdown ?? ''}
            data-testid="filter-breakdown"
            disabled={!available || (usedDims >= maxDims && !filters.breakdown)}
            onChange={(e) => setFilters((prev) => ({ ...prev, breakdown: (e.target.value || undefined) as Dimension | undefined }))}
          >
            <option value="">{t('No breakdown')}</option>
            {DIMENSIONS.filter((d) => !filters[d]).map((d) => <option key={d} value={d}>{t(DIMENSION_LABELS[d])}</option>)}
          </select>
        </label>
        <button type="button" className="btn-secondary" onClick={() => void load()} disabled={loading}>
          <RefreshCw className={`h-4 w-4 ${loading ? 'animate-spin' : ''}`} aria-hidden="true" />
          {t('Refresh')}
        </button>
        <p className="w-full text-xs text-slate-500" data-testid="filter-limit-hint">
          {scopeDimension === 'team'
            ? t('Your view is limited to your team, which counts as one of the two: choose one filter or one breakdown.')
            : scopeDimension === 'company'
              ? t('Your view is limited to your company, which counts as one of the two: choose one filter or one breakdown.')
              : t('Combine at most two: filters and a breakdown. Smaller groups could identify people.')}
        </p>
      </div>

      {error && (
        <div role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-800/60 dark:bg-rose-900/20 dark:text-rose-300">
          {error}
        </div>
      )}

      {loading && !results && <div className="h-40 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />}

      {results && survey && (
        <>
          <div className="flex flex-wrap items-center gap-2 text-xs text-slate-600 dark:text-slate-300">
            <StatusChip label={t(MODE_LABELS[results.mode])} tone={results.mode === 'Identified' ? 'amber' : 'emerald'} dot />
            <StatusChip label={t(SURVEY_STATUS_LABELS[results.status])} tone={results.status === 'Open' ? 'blue' : 'slate'} />
            <span className="inline-flex items-center gap-1">
              <ShieldCheck className="h-3.5 w-3.5" aria-hidden="true" />
              {t('Groups smaller than {k} are never shown.', { k })}
            </span>
            {activeFilters.length > 0 && (
              <span>
                {t('Showing: {filters}', { filters: activeFilters.map((d) => `${t(DIMENSION_LABELS[d])} = ${optionLabel(d, filters[d]!)}`).join(' · ') })}
              </span>
            )}
          </div>

          {!available && (
            <div className="surface flex items-start gap-3 rounded-xl p-5" role="status" data-testid="results-after-close">
              <ShieldCheck className="mt-0.5 h-5 w-5 shrink-0 text-emerald-600" aria-hidden="true" />
              <div className="text-sm">
                <p className="font-semibold text-slate-900 dark:text-white">
                  {t('Results appear when the survey closes on {date}.', { date: f.dateTime(results.resultsAvailableFromUtc ?? survey.closesAtUtc) })}
                </p>
                <p className="mt-1 text-slate-600 dark:text-slate-300">
                  {t('Showing results while people are still answering would let anyone compare them before and after one person responds. Until then, only the overall response rate is shown.')}
                </p>
              </div>
            </div>
          )}
          {!available ? (
            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
              <Kpi
                label={t('Response rate')}
                cell={metricCell(t, k, results.participation.visible, results.participation.responseRatePct, pct)}
                note={results.participation.visible
                  ? t('{submitted} of {invited} invited responded. Drafts are not counted.', {
                    submitted: f.integer(results.participation.submitted), invited: f.integer(results.participation.invited),
                  })
                  : t('Shown only for {k} or more invitees.', { k })}
                testId="kpi-response-rate"
              />
            </div>
          ) : (<>
          {/* KPI row: every value carries its definition. */}
          <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            <Kpi
              label={t('Response rate')}
              cell={metricCell(t, k, results.participation.visible, results.participation.responseRatePct, pct)}
              note={results.participation.visible
                ? t('{submitted} of {invited} invited responded. Drafts are not counted.', {
                  submitted: f.integer(results.participation.submitted), invited: f.integer(results.participation.invited),
                })
                : t('Shown only for {k} or more invitees.', { k })}
              testId="kpi-response-rate"
            />
            <Kpi
              label={t('Favourable')}
              cell={metricCell(t, k, results.visible, results.favourablePct, pct)}
              note={t('Answers of Agree or Strongly agree, out of all answers to 5-point questions.')}
              testId="kpi-favourable"
            />
            <Kpi
              label={t('Net promoter score (eNPS)')}
              cell={metricCell(t, k, results.visible && !!results.enps, results.enps?.score, score)}
              note={results.enps
                ? t('{promoters} promoters (9–10) minus {detractors} detractors (0–6), of {n} who answered.', {
                  promoters: pct(results.enps.promotersPct), detractors: pct(results.enps.detractorsPct), n: f.integer(results.enps.answered),
                })
                : t('% promoters (9–10) minus % detractors (0–6).')}
              testId="kpi-enps"
            />
            <Kpi
              label={t('Respondents in this view')}
              cell={metricCell(t, k, results.visible, results.respondents, (n) => f.integer(n))}
              note={t('Submitted responses only.')}
              testId="kpi-respondents"
            />
          </div>

          {!results.visible ? (
            <div className="surface flex items-start gap-3 rounded-xl p-5" role="status" data-testid="results-suppressed">
              <EyeOff className="mt-0.5 h-5 w-5 shrink-0 text-slate-500" aria-hidden="true" />
              <div className="text-sm">
                <p className="font-semibold text-slate-900 dark:text-white">{suppressedText(t, k)}</p>
                <p className="mt-1 text-slate-600 dark:text-slate-300">{suppressionReasonText(t, results.suppressionReason, k)}</p>
                <p className="mt-1 text-slate-600 dark:text-slate-300">{t('Remove a filter to see a larger group.')}</p>
              </div>
            </div>
          ) : (
            <>
              <DataPanel title={t('Drivers')} description={t('Favourable % for each driver, across its 5-point questions.')}>
                {results.drivers.length === 0 ? (
                  <p className="text-sm text-slate-500">{t('This survey has no drivers.')}</p>
                ) : (
                  <ul className="space-y-3">
                    {results.drivers.map((d) => {
                      const cell = metricCell(t, k, d.visible, d.favourablePct, pct);
                      return (
                        <li key={d.key} className="grid gap-1 sm:grid-cols-[180px_1fr_auto] sm:items-center sm:gap-3">
                          <span className="text-sm font-semibold text-slate-800 dark:text-slate-100">{pick(locale, d.labelEn, d.labelAr)}</span>
                          {d.visible && d.favourablePct !== null ? (
                            <div className="h-2.5 rounded-full bg-slate-100 dark:bg-white/[0.06]" aria-hidden="true">
                              <div className="h-2.5 rounded-full bg-sapphire" style={{ width: `${d.favourablePct}%` }} />
                            </div>
                          ) : <span aria-hidden="true" />}
                          <span className={`text-sm ${cell.kind === 'suppressed' ? 'text-slate-500' : 'font-semibold text-slate-900 dark:text-white'}`}>
                            {cell.text}
                            {d.visible && d.mean !== null && <span className="ms-2 text-xs font-normal text-slate-500">{t('Mean {mean} of 5', { mean: f.number(d.mean) })}</span>}
                          </span>
                          {canAct && d.visible && onCreateAction && (
                            <button
                              type="button"
                              className="justify-self-start text-xs font-semibold text-sapphire hover:underline sm:col-start-3"
                              onClick={() => onCreateAction({ surveyId, sourceKind: 'Driver', sourceRef: d.key, title: t('Improve {driver}', { driver: pick(locale, d.labelEn, d.labelAr) }) })}
                            >
                              {t('Create action')}
                            </button>
                          )}
                        </li>
                      );
                    })}
                  </ul>
                )}
              </DataPanel>

              <DataPanel title={t('Questions')} description={t('Every answer, with the people who answered. A skipped question is left out of its own results.')} action={<LikertLegend />}>
                <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
                  {results.items.map((item) => (
                    <li key={item.key} className="py-3" data-testid={`item-${item.key}`}>
                      <p className="text-sm font-semibold text-slate-900 dark:text-white">{pick(locale, item.textEn, item.textAr)}</p>
                      {!item.visible ? (
                        <p className="mt-1 text-xs text-slate-500">
                          {suppressedText(t, k)} {suppressionReasonText(t, item.suppressionReason, k)}
                        </p>
                      ) : item.type === 'likert5' ? (
                        <div className="mt-2 space-y-1.5">
                          <LikertBar distribution={item.distribution} label={pick(locale, item.textEn, item.textAr)} />
                          <p className="text-xs text-slate-600 dark:text-slate-300">
                            {t('Favourable {fav} · mean {mean} of 5 · {n} answered', {
                              fav: pct(item.favourablePct ?? 0), mean: f.number(item.mean), n: f.integer(item.answered),
                            })}
                          </p>
                        </div>
                      ) : item.type === 'enps' && item.enps ? (
                        <p className="mt-1 text-xs text-slate-600 dark:text-slate-300">
                          {t('Net promoter score {score}: promoters {p}, passives {s}, detractors {d} · {n} answered', {
                            score: score(item.enps.score), p: pct(item.enps.promotersPct), s: pct(item.enps.passivesPct),
                            d: pct(item.enps.detractorsPct), n: f.integer(item.enps.answered),
                          })}
                        </p>
                      ) : item.type === 'text' ? (
                        <div className="mt-1 flex flex-wrap items-center gap-3 text-xs text-slate-600 dark:text-slate-300">
                          <span>{t('{n} comments', { n: f.integer(item.commentCount) })}</span>
                          {canComments && !commentsAllowed ? (
                            <span data-testid="comments-scope-note">
                              {commentDim === 'none'
                                ? t('Comments are shown for the whole survey only. Clear the filters to read them.')
                                : t('Comments are shown for the whole survey or by {dimension} only. Clear the other filters to read them.', { dimension: t(DIMENSION_LABELS[commentDim]) })}
                            </span>
                          ) : canComments ? (
                            <button type="button" className="inline-flex items-center gap-1 font-semibold text-sapphire hover:underline" onClick={() => void openComments(item.key)}>
                              <MessageSquareText className="h-3.5 w-3.5" aria-hidden="true" />
                              {t('Read comments')}
                            </button>
                          ) : (
                            <span>{t('Reading comments needs the engagement comments permission.')}</span>
                          )}
                        </div>
                      ) : (
                        <ul className="mt-1 flex flex-wrap gap-x-4 gap-y-1 text-xs text-slate-600 dark:text-slate-300">
                          {item.distribution.map((d) => (
                            <li key={d.key}>{pick(locale, d.labelEn, d.labelAr)}: {f.integer(d.count)} ({pct(d.pct)})</li>
                          ))}
                          <li>{t('{n} answered', { n: f.integer(item.answered) })}</li>
                        </ul>
                      )}
                      {canAct && item.visible && onCreateAction && item.type === 'likert5' && (
                        <button
                          type="button"
                          className="mt-1 text-xs font-semibold text-sapphire hover:underline"
                          onClick={() => onCreateAction({ surveyId, sourceKind: 'Item', sourceRef: item.key, title: t('Follow up: {question}', { question: pick(locale, item.textEn, item.textAr).slice(0, 120) }) })}
                        >
                          {t('Create action')}
                        </button>
                      )}
                    </li>
                  ))}
                </ul>
              </DataPanel>

              {results.breakdown && (
                <DataPanel title={t('By {dimension}', { dimension: t(DIMENSION_LABELS[results.breakdown.dimension]) })} description={t('A small group, and the next-smallest group beside it, are hidden so neither can be worked out from the total.')}>
                  <div className="overflow-x-auto">
                    <table className="w-full min-w-[560px] text-start text-sm" data-testid="breakdown-table">
                      <thead>
                        <tr className="text-xs text-slate-500">
                          <th scope="col" className="py-2 text-start font-semibold">{t('Group')}</th>
                          <th scope="col" className="py-2 text-start font-semibold">{t('Respondents')}</th>
                          <th scope="col" className="py-2 text-start font-semibold">{t('Favourable')}</th>
                          <th scope="col" className="py-2 text-start font-semibold">{t('Net promoter score (eNPS)')}</th>
                          {results.drivers.map((d) => <th key={d.key} scope="col" className="py-2 text-start font-semibold">{pick(locale, d.labelEn, d.labelAr)}</th>)}
                          {canAct && onCreateAction && <th scope="col" className="py-2"><span className="sr-only">{t('Actions')}</span></th>}
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
                        {results.breakdown.cells.map((c) => (
                          <tr key={c.value} data-testid={`cell-${c.value}`}>
                            <th scope="row" className="py-2 text-start font-semibold text-slate-900 dark:text-white">{optionLabel(results.breakdown!.dimension, c.value)}</th>
                            {c.visible ? (
                              <>
                                <td className="py-2">{f.integer(c.respondents)}</td>
                                <td className="py-2">{metricCell(t, k, true, c.favourablePct, pct).text}</td>
                                <td className="py-2">{metricCell(t, k, c.enps !== null, c.enps, score).text}</td>
                                {results.drivers.map((d) => (
                                  <td key={d.key} className="py-2">{metricCell(t, k, c.driverFavourablePct[d.key] != null, c.driverFavourablePct[d.key], pct).text}</td>
                                ))}
                              </>
                            ) : (
                              <td colSpan={3 + results.drivers.length} className="py-2 text-xs text-slate-500" title={suppressionReasonText(t, c.suppressionReason, k)}>
                                {suppressedText(t, k)}
                              </td>
                            )}
                            {canAct && onCreateAction && (
                              <td className="py-2">
                                {c.visible && (
                                  <button type="button" className="text-xs font-semibold text-sapphire hover:underline"
                                    onClick={() => onCreateAction({ surveyId, sourceKind: 'Cohort', sourceRef: optionLabel(results.breakdown!.dimension, c.value), title: t('Follow up with {group}', { group: optionLabel(results.breakdown!.dimension, c.value) }) })}>
                                    {t('Create action')}
                                  </button>
                                )}
                              </td>
                            )}
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </DataPanel>
              )}
            </>
          )}

          {trend.length > 1 && (
            <DataPanel title={t('Trend across cycles')} description={t('Favourable % in each cycle of this recurring survey. A hidden cycle has no point.')}>
              <div className="h-56" dir="ltr">
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart data={trend.map((p) => ({ cycle: t('Cycle {n}', { n: p.cycleNumber }), favourable: p.visible ? p.favourablePct : null }))} margin={{ top: 8, right: 16, bottom: 8, left: 0 }}>
                    <CartesianGrid stroke="#e2e8f0" strokeDasharray="3 3" vertical={false} />
                    <XAxis dataKey="cycle" tick={{ fontSize: 12 }} reversed={locale === 'ar'} />
                    <YAxis domain={[0, 100]} tick={{ fontSize: 12 }} width={36} orientation={locale === 'ar' ? 'right' : 'left'} />
                    <Tooltip formatter={(v) => (typeof v === 'number' ? pct(v) : suppressedText(t, k))} />
                    <Line type="monotone" dataKey="favourable" name={t('Favourable')} stroke="#2F6BFF" strokeWidth={2} dot={{ r: 4 }} connectNulls={false} />
                  </LineChart>
                </ResponsiveContainer>
              </div>
            </DataPanel>
          )}

          <DataPanel title={t('Export')} description={t('The file shows exactly what this page shows, with the same groups hidden.')}>
            <div className="flex flex-wrap items-center gap-3">
              <button type="button" className="btn-secondary" disabled={!!exporting} onClick={() => void download('csv')} data-testid="export-csv">
                <Download className="h-4 w-4" aria-hidden="true" />
                {exporting === 'csv' ? t('Preparing…') : t('Download CSV')}
              </button>
              <button type="button" className="btn-secondary" disabled={!!exporting} onClick={() => void download('xlsx')} data-testid="export-xlsx">
                <Download className="h-4 w-4" aria-hidden="true" />
                {exporting === 'xlsx' ? t('Preparing…') : t('Download Excel')}
              </button>
              {canComments && commentsAllowed && (
                <label className="flex items-center gap-2 text-sm text-slate-700 dark:text-slate-200">
                  <input type="checkbox" checked={includeComments} onChange={(e) => setIncludeComments(e.target.checked)} />
                  {t('Include comments (only groups large enough to show them)')}
                </label>
              )}
            </div>
            <p className="mt-2 flex items-start gap-1 text-xs text-slate-500">
              <Info className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
              {t('Downloads are recorded in the audit log. To email these results on a schedule, use Reports → Engagement Survey Results.')}
            </p>
          </DataPanel>

          {canAct && onCreateAction && results.visible && (
            <button type="button" className="btn-primary" onClick={() => onCreateAction({ surveyId, sourceKind: 'Overall', sourceRef: '', title: '' })}>
              <Plus className="h-4 w-4" aria-hidden="true" />
              {t('Create an action from these results')}
            </button>
          )}
          </>)}
        </>
      )}

      <Modal isOpen={!!comments} title={t('Comments')} onClose={() => setComments(null)} size="lg">
        {comments && (
          comments.data.visible ? (
            comments.data.comments.length === 0 ? (
              <p className="text-sm text-slate-600 dark:text-slate-300">{t('No comments in this view.')}</p>
            ) : (
              <div className="space-y-2">
                <p className="text-xs text-slate-500">{t('Shown in random order, without names. Opening comments is recorded in the audit log.')}</p>
                <ul className="space-y-2" data-testid="comments-list">
                  {comments.data.comments.map((c, i) => (
                    <li key={i} className="rounded-lg bg-slate-50 p-3 text-sm text-slate-800 dark:bg-white/[0.04] dark:text-slate-100">{c}</li>
                  ))}
                </ul>
              </div>
            )
          ) : (
            <p className="text-sm text-slate-600 dark:text-slate-300" data-testid="comments-suppressed">
              {suppressedText(t, comments.data.minCohortSize)} {suppressionReasonText(t, comments.data.suppressionReason, comments.data.minCohortSize)}
            </p>
          )
        )}
      </Modal>
    </div>
  );
}

function Kpi({ label, cell, note, testId }: { label: string; cell: ReturnType<typeof metricCell>; note: string; testId: string }) {
  return (
    <article className="surface rounded-xl border-s-[3px] border-s-sapphire p-4" data-testid={testId}>
      <p className="text-xs font-semibold uppercase tracking-[0.08em] text-slate-500 dark:text-slate-400">{label}</p>
      <p className={cell.kind === 'suppressed' ? 'mt-2 text-sm font-semibold text-slate-600 dark:text-slate-300' : 'mt-2 text-2xl font-bold tracking-tight text-slate-950 dark:text-white'}>
        {cell.text}
      </p>
      <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{note}</p>
    </article>
  );
}
