'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { Briefcase, ChevronLeft, ChevronRight, MapPin, UserX } from 'lucide-react';
import { essJobsApi } from '../api/essJobs';
import { EssLoadError, EssPageHeader } from '../components/ess/EssParts';
import { Modal } from '../components/Modal';
import { EmptyState } from '../components/ui/EmptyState';
import { Field, Input } from '../components/ui/Field';
import { SkeletonRows } from '../components/ui/Skeleton';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import { employmentTypeLabel } from '../lib/employmentType';
import {
  ESS_JOBS_PAGE_SIZE, jobsLoadProblem, lastPage, pageRange, splitLines,
  type EssJob, type EssJobsPage, type JobsLoadProblem,
} from '../lib/essJobs';
import { createLatestRequestGate, runLatest } from '../lib/latestRequest';

/** How long the search box waits after the last keystroke before asking the server. */
const SEARCH_DELAY_MS = 300;

type ListState =
  | { status: 'loading' }
  | { status: 'ready'; data: EssJobsPage }
  | { status: 'problem'; problem: JobsLoadProblem };

/**
 * Open positions (Self-Service): the jobs open in the signed-in employee's own company, read-only. The server resolves the
 * company from the session and pages the list; this screen shows one page at a time with the server's own total, and opens
 * a position in a side panel to read it. It carries no pay and no way to respond to a position.
 */
export function MyOpenPositionsPage() {
  const { t, locale } = useLocale();
  const [query, setQuery] = useState('');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [reloadKey, setReloadKey] = useState(0);
  const [list, setList] = useState<ListState>({ status: 'loading' });
  const [selected, setSelected] = useState<EssJob | null>(null);
  const gate = useMemo(() => createLatestRequestGate(), []);

  // Debounce: the server is asked once the typing pauses, and a new search starts from page 1.
  useEffect(() => {
    if (query.trim() === search.trim()) return;
    const timer = setTimeout(() => { setSearch(query); setPage(1); }, SEARCH_DELAY_MS);
    return () => clearTimeout(timer);
  }, [query, search]);

  useEffect(() => {
    setList((prev) => (prev.status === 'ready' ? prev : { status: 'loading' }));
    void runLatest(gate, () => essJobsApi.list({ q: search, page }), {
      onResult: (data) => {
        // The list shrank under the reader (a position was closed): go to the last page that still exists.
        if (data.items.length === 0 && data.total > 0 && page > lastPage(data.total, ESS_JOBS_PAGE_SIZE)) {
          setPage(lastPage(data.total, ESS_JOBS_PAGE_SIZE));
          return;
        }
        setList({ status: 'ready', data });
      },
      onError: (error) => setList({ status: 'problem', problem: jobsLoadProblem(error, locale) }),
    });
  }, [gate, search, page, reloadKey, locale]);

  const clearSearch = useCallback(() => { setQuery(''); setSearch(''); setPage(1); }, []);

  const data = list.status === 'ready' ? list.data : null;
  const searching = search.trim().length > 0;
  const busy = list.status === 'loading' || (data !== null && query.trim() !== search.trim());

  return (
    <div className="space-y-4" data-testid="my-open-positions">
      <EssPageHeader
        title={t('Open positions')}
        subtitle={t('See the jobs open in your company and what each one involves. Applying is not available here yet.')}
      />

      {list.status === 'problem' && list.problem.kind === 'unlinked' ? (
        <div data-testid="open-positions-unlinked" className="wg-card flex flex-col items-center gap-3 rounded-2xl p-10 text-center">
          <UserX className="h-8 w-8 text-fg-subtle" aria-hidden="true" />
          <p className="text-body font-semibold text-fg">{t('No employee profile for this login')}</p>
          <p className="max-w-md text-body-sm text-fg-muted">
            {t('Open positions shows the jobs open in the company of the employee your login belongs to. Ask HR to link your login to your employee record.')}
          </p>
          {list.problem.message && <p className="max-w-md text-meta text-fg-muted" data-testid="open-positions-unlinked-reason">{list.problem.message}</p>}
        </div>
      ) : (
        <>
          <div className="max-w-md">
            <Field label={t('Search open positions')} info={t('Type a word to narrow the list. Results update as you type.')}>
              <Input
                type="search"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                className="w-full"
                placeholder={t('Job title or keyword')}
                autoComplete="off"
                data-testid="open-positions-search"
              />
            </Field>
          </div>

          {/* One persistent polite region: the outcome of every search and page change is spoken here. */}
          <p role="status" className="sr-only" data-testid="open-positions-status">
            {data && !busy ? t('{count, plural, one {# open position} other {# open positions}}', { count: data.total }) : ''}
          </p>

          <section aria-label={t('Open positions in your company')} aria-busy={busy} className="wg-card overflow-hidden rounded-2xl">
            {list.status === 'loading' && (
              <div className="p-4" data-testid="open-positions-loading">
                <span className="sr-only">{t('Loading open positions')}</span>
                <SkeletonRows rows={6} />
              </div>
            )}

            {list.status === 'problem' && (
              <div className="p-4" data-testid="open-positions-error">
                <EssLoadError error={list.problem.kind === 'failed' ? list.problem.error : { response: { status: 403 } }} onRetry={() => setReloadKey((k) => k + 1)} />
              </div>
            )}

            {data && data.items.length === 0 && (
              searching ? (
                <EmptyState
                  kind="filtered"
                  icon={Briefcase}
                  title={t('No open positions match “{query}”', { query: search.trim() })}
                  description={t('Try a different word, or clear the search to see every open position.')}
                  action={{ label: t('Clear search'), onClick: clearSearch }}
                />
              ) : (
                <EmptyState
                  kind="first-use"
                  icon={Briefcase}
                  title={t('There are no open positions right now')}
                  description={t('When your company opens a job, it is listed here.')}
                  action={{ label: t('Back to my Self-Service overview'), href: '/ess' }}
                />
              )
            )}

            {data && data.items.length > 0 && (
              <>
                <JobRows jobs={data.items} onOpen={setSelected} />
                <Pager data={data} page={page} onPage={setPage} />
              </>
            )}
          </section>
        </>
      )}

      {selected && <JobPanel job={selected} onClose={() => setSelected(null)} />}
    </div>
  );
}

/** The grid shared by the column heads and the rows, so they line up; on a phone each row stacks. */
const ROW_GRID = 'md:grid md:grid-cols-[minmax(0,2.2fr)_minmax(0,1.2fr)_minmax(0,1.2fr)_minmax(0,1fr)_minmax(0,0.9fr)] md:items-center md:gap-4';

function JobRows({ jobs, onOpen }: { jobs: EssJob[]; onOpen: (job: EssJob) => void }) {
  const { t } = useLocale();
  const fx = useFormat();
  return (
    <div>
      <div aria-hidden="true" className={`hidden border-b border-line bg-surface-sunken px-4 py-2 text-meta font-semibold text-fg-muted ${ROW_GRID}`}>
        <span>{t('Position')}</span><span>{t('Department')}</span><span>{t('Location')}</span><span>{t('Type')}</span><span>{t('Published')}</span>
      </div>
      <ul className="divide-y divide-line" data-testid="open-positions-list">
        {jobs.map((job) => (
          <li key={job.id}>
            <button
              type="button"
              aria-haspopup="dialog"
              onClick={() => onOpen(job)}
              data-testid="open-position-row"
              className={`flex w-full flex-wrap items-center gap-x-3 gap-y-0.5 px-4 py-3 text-start transition-colors duration-fast hover:bg-surface-sunken ${ROW_GRID}`}
            >
              <span className="block w-full min-w-0 md:w-auto">
                <span className="block truncate text-body font-semibold text-fg">{job.title}</span>
                {job.jobCode && <span className="block truncate text-meta text-fg-muted"><bdi>{job.jobCode}</bdi></span>}
              </span>
              <span className="block min-w-0 truncate text-body-sm text-fg-muted md:text-fg">
                <span className="sr-only">{t('Department')}: </span>{job.departmentName || '—'}
              </span>
              <span className="flex min-w-0 items-center gap-1 text-body-sm text-fg-muted md:text-fg">
                <MapPin className="h-3.5 w-3.5 shrink-0 md:hidden" aria-hidden="true" />
                <span className="sr-only">{t('Location')}: </span>
                <span className="min-w-0 truncate">{job.location || '—'}</span>
              </span>
              <span className="block min-w-0 truncate text-body-sm text-fg-muted md:text-fg">
                <span className="sr-only">{t('Type')}: </span>{job.employmentType ? employmentTypeLabel(t, job.employmentType) : '—'}
              </span>
              <span className="block text-meta tabular-nums text-fg-muted md:text-body-sm md:text-fg">
                <span className="sr-only">{t('Published')}: </span>{job.publishedAtUtc ? fx.date(job.publishedAtUtc) : t('Not dated')}
              </span>
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}

function Pager({ data, page, onPage }: { data: EssJobsPage; page: number; onPage: (page: number) => void }) {
  const { t } = useLocale();
  const fx = useFormat();
  const range = pageRange(page, ESS_JOBS_PAGE_SIZE, data.total, data.items.length);
  const pages = range.pages;
  return (
    <nav aria-label={t('Open positions in your company')} className="flex flex-wrap items-center justify-between gap-3 border-t border-line px-4 py-3" data-testid="open-positions-pager">
      <p className="text-meta text-fg-muted tabular-nums" data-testid="open-positions-range">
        {t('Showing {from} to {to} of {total}', { from: fx.number(range.from), to: fx.number(range.to), total: fx.number(data.total) })}
      </p>
      {pages > 1 && (
        <div className="flex items-center gap-2">
          <button type="button" className="btn-secondary inline-flex items-center gap-1" disabled={page <= 1} onClick={() => onPage(page - 1)} aria-label={t('Previous page')}>
            <ChevronLeft className="h-4 w-4 rtl:rotate-180" aria-hidden="true" />{t('Previous')}
          </button>
          <span className="text-meta text-fg-muted tabular-nums">{t('Page {page} of {pages}', { page: fx.number(page), pages: fx.number(pages) })}</span>
          <button type="button" className="btn-secondary inline-flex items-center gap-1" disabled={page >= pages} onClick={() => onPage(page + 1)} aria-label={t('Next page')}>
            {t('Next')}<ChevronRight className="h-4 w-4 rtl:rotate-180" aria-hidden="true" />
          </button>
        </div>
      )}
    </nav>
  );
}

/**
 * One position in a side panel. It opens with what the list already carried and then reads the position again by id, so a
 * position closed in the meantime says so instead of showing stale text.
 */
function JobPanel({ job: listed, onClose }: { job: EssJob; onClose: () => void }) {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const [job, setJob] = useState<EssJob>(listed);
  const [problem, setProblem] = useState<JobsLoadProblem | null>(null);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let live = true;
    setProblem(null);
    essJobsApi.get(listed.id).then(
      (fresh) => { if (live) setJob(fresh); },
      (error) => { if (live) setProblem(jobsLoadProblem(error, locale)); },
    );
    return () => { live = false; };
  }, [listed.id, locale, attempt]);

  const requirements = splitLines(job.requirements);
  const responsibilities = splitLines(job.responsibilities);
  const description = job.description.trim();
  const facts: Array<[string, string]> = [
    [t('Job code'), job.jobCode],
    [t('Department'), job.departmentName],
    [t('Designation'), job.designationTitle],
    [t('Location'), job.location],
    [t('Employment type'), job.employmentType ? employmentTypeLabel(t, job.employmentType) : ''],
    [t('Published'), job.publishedAtUtc ? fx.date(job.publishedAtUtc) : ''],
  ];

  return (
    <Modal isOpen title={job.title} onClose={onClose} placement="end" size="lg" closeLabel={t('Close')}>
      <div className="space-y-5" data-testid="open-position-detail">
        {problem && (
          <p role="alert" className="rounded-xl bg-danger-soft p-3 text-body-sm font-medium text-danger-fg" data-testid="open-position-detail-problem">
            {problem.kind === 'unlinked'
              ? t('This position is no longer open. Close this panel and refresh the list to see what is open now.')
              : t('This position could not be loaded. Retry in a moment.')}
            {problem.kind !== 'unlinked' && (
              <button type="button" className="btn-secondary ms-3" onClick={() => setAttempt((n) => n + 1)}>{t('Retry')}</button>
            )}
          </p>
        )}

        <dl className="grid gap-x-4 gap-y-3 sm:grid-cols-2">
          {facts.filter(([, value]) => value).map(([label, value]) => (
            <div key={label} className="min-w-0">
              <dt className="text-meta font-semibold text-fg-muted">{label}</dt>
              <dd className="text-body text-fg"><bdi>{value}</bdi></dd>
            </div>
          ))}
        </dl>

        {description && (
          <section aria-labelledby="open-position-about">
            <h3 id="open-position-about" className="mb-1.5 text-body font-semibold text-fg">{t('About this position')}</h3>
            <p dir="auto" className="whitespace-pre-line text-body text-fg">{description}</p>
          </section>
        )}
        {requirements.length > 0 && (
          <section aria-labelledby="open-position-requirements">
            <h3 id="open-position-requirements" className="mb-1.5 text-body font-semibold text-fg">{t('Requirements')}</h3>
            <ul dir="auto" className="list-disc space-y-1 ps-5 text-body text-fg" data-testid="open-position-requirements">
              {requirements.map((line, i) => <li key={i}>{line}</li>)}
            </ul>
          </section>
        )}
        {responsibilities.length > 0 && (
          <section aria-labelledby="open-position-responsibilities">
            <h3 id="open-position-responsibilities" className="mb-1.5 text-body font-semibold text-fg">{t('Responsibilities')}</h3>
            <ul dir="auto" className="list-disc space-y-1 ps-5 text-body text-fg" data-testid="open-position-responsibilities">
              {responsibilities.map((line, i) => <li key={i}>{line}</li>)}
            </ul>
          </section>
        )}
        {!description && requirements.length === 0 && responsibilities.length === 0 && (
          <p className="text-body-sm text-fg-muted">{t('No further details were added for this position.')}</p>
        )}
      </div>
    </Modal>
  );
}
