'use client';

import Link from 'next/link';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { Award, CalendarClock, ChevronRight, GraduationCap } from 'lucide-react';
import { learningApi, type AssignmentView, type MyLearning } from '../api/learning';
import { StatusChip } from '../components/StatusChip';
import { EmptyBlock, ErrorBlock, LearningGate, LoadingRows, ProgressBar } from '../components/learning/LearningParts';
import { useLocale } from '../contexts/LocaleContext';
import { useFormat } from '../hooks/useFormat';
import {
  assignmentChip, completionSourceLabel, contentKindLabel, credentialStatusLabel, credentialTone, dueIn,
  enrollmentStatusLabel, enrollmentTone, learningError, nextActionLabel, todayIso,
} from '../lib/learning';

/**
 * My learning (LRN-04): the employee's own assigned paths with progress and due dates, each course's next step, their
 * certificates and their transcript. Everything comes from GET /api/ess/learning, which resolves the employee from the
 * session — there is no way to ask for someone else's.
 */
export function MyLearningPage() {
  return (
    <LearningGate>
      <MyLearningBody />
    </LearningGate>
  );
}

function MyLearningBody() {
  const { t, locale } = useLocale();
  const fx = useFormat();
  const [data, setData] = useState<MyLearning | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      setData(await learningApi.mine());
    } catch (e) {
      setError(learningError(e, t));
    }
  }, [t]);
  useEffect(() => { void load(); }, [load]);

  const today = todayIso();
  const open = useMemo(() => (data?.assignments ?? []).filter((a) => a.status === 'Assigned' || a.status === 'InProgress'), [data]);
  const finished = useMemo(() => (data?.assignments ?? []).filter((a) => a.status === 'Completed'), [data]);
  const overdue = open.filter((a) => a.isOverdue).length;
  const name = (en: string, ar: string | null) => (locale === 'ar' && ar ? ar : en);

  return (
    <div className="space-y-5" data-testid="my-learning">
      <header>
        <h1 className="text-lg font-semibold text-slate-800 dark:text-slate-100">{t('My learning')}</h1>
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('The training assigned to you, what to do next, and the certificates you have earned.')}</p>
      </header>

      {!data && !error && <LoadingRows label={t('Loading your learning')} />}
      {error && <ErrorBlock message={error} onRetry={() => void load()} />}

      {data && (
        <>
          <p className="text-sm text-slate-600 dark:text-slate-300" data-testid="my-learning-summary">
            {t('{open, plural, one {# path to complete} other {# paths to complete}} · {overdue, plural, one {# overdue} other {# overdue}} · {certs, plural, one {# certificate} other {# certificates}}', {
              open: open.length, overdue, certs: data.credentials.filter((c) => c.status === 'Active').length,
            })}
          </p>

          <section aria-labelledby="learning-todo" className="space-y-3">
            <h2 id="learning-todo" className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('To do')}</h2>
            {open.length === 0
              ? <EmptyBlock title={t('Nothing to do right now')} body={t('When HR assigns you training it appears here, with its due date.')} />
              : open.map((a) => <AssignmentCard key={a.id} assignment={a} today={today} name={name} />)}
          </section>

          {finished.length > 0 && (
            <section aria-labelledby="learning-done" className="space-y-3">
              <h2 id="learning-done" className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Completed paths')}</h2>
              {finished.map((a) => <AssignmentCard key={a.id} assignment={a} today={today} name={name} />)}
            </section>
          )}

          <section aria-labelledby="learning-certs" className="wg-card space-y-3 rounded-2xl p-4">
            <h2 id="learning-certs" className="flex items-center gap-2 text-sm font-semibold text-slate-800 dark:text-slate-100">
              <Award className="h-4 w-4 text-amber-500" aria-hidden="true" />{t('My certificates')}
            </h2>
            {data.credentials.length === 0 ? (
              <p className="text-xs text-slate-500 dark:text-slate-400">{t('Certificates appear here when you complete a course that grants one.')}</p>
            ) : (
              <ul className="divide-y divide-slate-100 dark:divide-white/5">
                {data.credentials.map((c) => (
                  <li key={c.id} className="flex flex-wrap items-center justify-between gap-2 py-2.5">
                    <div className="min-w-0">
                      <p className="text-sm font-semibold text-slate-800 dark:text-slate-100">{name(c.courseTitle, c.courseTitleAr)}</p>
                      <p className="text-xs text-slate-500 dark:text-slate-400">
                        <span dir="ltr" className="font-mono">{c.certificateNumber}</span>
                        {' · '}
                        {c.expiresOn ? t('Valid until {date}', { date: fx.date(c.expiresOn) }) : t('Does not expire')}
                      </p>
                    </div>
                    <div className="flex items-center gap-2">
                      <StatusChip label={t(credentialStatusLabel[c.status])} tone={credentialTone(c.status)} dot />
                      <Link href={`/ess/learning/certificates/${c.id}`} className="btn-secondary text-xs">{t('View certificate')}</Link>
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section aria-labelledby="learning-transcript" className="wg-card space-y-3 rounded-2xl p-4">
            <h2 id="learning-transcript" className="text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Transcript')}</h2>
            {data.transcript.length === 0 ? (
              <p className="text-xs text-slate-500 dark:text-slate-400">{t('Every course you complete is recorded here, with how it was completed.')}</p>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full min-w-[560px] text-start text-xs" data-testid="learning-transcript">
                  <thead className="text-slate-500 dark:text-slate-400">
                    <tr>
                      <th scope="col" className="py-2 text-start font-semibold">{t('Course')}</th>
                      <th scope="col" className="py-2 text-start font-semibold">{t('Completed on')}</th>
                      <th scope="col" className="py-2 text-start font-semibold">{t('How it was completed')}</th>
                      <th scope="col" className="py-2 text-start font-semibold">{t('Score')}</th>
                      <th scope="col" className="py-2 text-start font-semibold">{t('Certificate')}</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                    {data.transcript.map((row) => (
                      <tr key={row.enrollmentId}>
                        <td className="py-2 pe-3">
                          <span className="font-semibold text-slate-800 dark:text-slate-100">{name(row.title, row.titleAr)}</span>
                          <span className="block text-slate-500 dark:text-slate-400">{t('{path} · version {version}', { path: name(row.pathName, row.pathNameAr), version: row.courseVersion })}</span>
                        </td>
                        <td className="py-2 pe-3 tabular-nums">{fx.date(row.completedAtUtc)}</td>
                        <td className="py-2 pe-3">{t(completionSourceLabel[row.completionSource])}</td>
                        <td className="py-2 pe-3 tabular-nums">{row.scorePercent == null ? t('Not scored') : fx.percent(row.scorePercent)}</td>
                        <td className="py-2">
                          {row.credentialId
                            ? <Link href={`/ess/learning/certificates/${row.credentialId}`} className="font-mono text-sapphire hover:underline dark:text-cyanAccent" dir="ltr">{row.certificateNumber}</Link>
                            : t('None')}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}
    </div>
  );
}

function AssignmentCard({ assignment: a, today, name }: { assignment: AssignmentView; today: string; name: (en: string, ar: string | null) => string }) {
  const { t } = useLocale();
  const fx = useFormat();
  const days = dueIn(a.dueDate, today);
  const live = a.status === 'Assigned' || a.status === 'InProgress';
  return (
    <article className="wg-card rounded-2xl p-4" data-testid="learning-assignment" aria-label={name(a.pathName, a.pathNameAr)}>
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <h3 className="flex items-center gap-2 text-sm font-semibold text-slate-800 dark:text-slate-100">
            <GraduationCap className="h-4 w-4 shrink-0 text-sapphire dark:text-cyanAccent" aria-hidden="true" />
            {name(a.pathName, a.pathNameAr)}
          </h3>
          <p className="mt-0.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-slate-500 dark:text-slate-400">
            {a.isMandatory && <span className="font-semibold text-slate-700 dark:text-slate-200">{t('Mandatory')}</span>}
            {a.cycle > 1 && <span>{t('Renewal, cycle {n}', { n: a.cycle })}</span>}
            {a.dueDate && live && (
              <span className={`inline-flex items-center gap-1 ${a.isOverdue ? 'font-semibold text-rose-600 dark:text-rose-400' : ''}`}>
                <CalendarClock className="h-3.5 w-3.5" aria-hidden="true" />
                {a.isOverdue
                  ? t('Overdue since {date}', { date: fx.date(a.dueDate) })
                  : days === 0 ? t('Due today') : t('Due {date} (in {count, plural, one {# day} other {# days}})', { date: fx.date(a.dueDate), count: days ?? 0 })}
              </span>
            )}
            {a.completedAtUtc && <span>{t('Completed on {date}', { date: fx.date(a.completedAtUtc) })}</span>}
          </p>
        </div>
        {(() => { const chip = assignmentChip(a.status, a.isOverdue, a.awaitingVerification); return <StatusChip label={t(chip.label)} tone={chip.tone} dot />; })()}
      </div>
      <div className="mt-3">
        <ProgressBar percent={a.progressPercent} label={t('{done} of {total} required courses completed', { done: a.requiredCompleted, total: a.requiredCount })} />
        <p className="mt-1 text-caption text-slate-500 dark:text-slate-400">{t('{done} of {total} required courses completed', { done: a.requiredCompleted, total: a.requiredCount })}</p>
      </div>
      <ol className="mt-3 divide-y divide-slate-100 dark:divide-white/5">
        {a.items.map((item) => (
          <li key={item.id} className="flex flex-wrap items-center justify-between gap-2 py-2.5" data-testid="learning-item">
            <div className="min-w-0">
              <p className="text-sm font-medium text-slate-800 dark:text-slate-100">
                {name(item.title, item.titleAr)}
                {!item.isRequired && <span className="ms-2 text-caption font-normal text-slate-500">{t('Optional')}</span>}
              </p>
              <p className="text-caption text-slate-500 dark:text-slate-400">
                {t(contentKindLabel[item.contentKind])}
                {item.estimatedMinutes ? ` · ${t('About {count, plural, one {# minute} other {# minutes}}', { count: item.estimatedMinutes })}` : ''}
                {item.isBlocked && item.blockedByTitle ? ` · ${t('After {course}', { course: name(item.blockedByTitle, item.blockedByTitleAr) })}` : ''}
              </p>
            </div>
            <div className="flex items-center gap-2">
              <StatusChip label={t(enrollmentStatusLabel[item.status])} tone={enrollmentTone(item.status)} />
              {item.nextAction === 'done' || item.nextAction === 'closed' || item.nextAction === 'blocked' || item.nextAction === 'locked'
                || item.nextAction === 'pending_verification' ? (
                <Link href={`/ess/learning/${item.id}`} className="btn-secondary text-xs" aria-label={t('Open {course}', { course: name(item.title, item.titleAr) })}>
                  {t('Details')}
                </Link>
              ) : (
                <Link href={`/ess/learning/${item.id}`} className="btn-primary inline-flex items-center gap-1 text-xs">
                  {t(nextActionLabel[item.nextAction])}<ChevronRight className="h-3.5 w-3.5 rtl:-scale-x-100" aria-hidden="true" />
                </Link>
              )}
            </div>
          </li>
        ))}
      </ol>
    </article>
  );
}
