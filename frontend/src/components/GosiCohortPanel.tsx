'use client';

import { useState } from 'react';
import { employeesApi } from '../api/employees';
import type { EmployeeDetail, GosiCohort } from '../api/employees';

/**
 * F02 — the employee's GOSI cohort: the person-level fact the Saudi contribution schedule has been keyed
 * on since 3 July 2024. Shows what payroll does with the current record, in plain words, and lets HR
 * record the first-registration date from the person's GOSI record. The write goes through the ordinary
 * sensitive-field path (PUT /api/employees/{id} → 202 → Approval Center); nothing changes until an
 * approver accepts it, and the server refuses a date that is not a real past date.
 *
 * Rendered only for Saudi nationals and only when the caller may see sensitive fields (the server sends
 * `gosiCohort: null` to anyone else, so a masked viewer never sees "not recorded" for a recorded date).
 */

// Mirrors GosiCalculationService.SaudiNationalityTerms — the cohort is a Saudi-national annuities fact.
const SAUDI_NATIONALITY = new Set(['sa', 'sau', 'saudi', 'saudi arabia', 'saudi arabian']);

export function isSaudiNational(nationality?: string | null): boolean {
  return !!nationality && SAUDI_NATIONALITY.has(nationality.trim().toLowerCase());
}

const STATUS: Record<GosiCohort, { label: string; tone: string }> = {
  Unknown: { label: 'Not recorded', tone: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-300' },
  PreJuly2024: { label: 'Existing subscriber', tone: 'bg-slate-100 text-slate-700 dark:bg-white/10 dark:text-slate-200' },
  NewEntrant: { label: 'New entrant', tone: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-300' },
};

function explain(cohort: GosiCohort, date?: string | null): string {
  switch (cohort) {
    case 'PreJuly2024':
      return `First registered with GOSI on ${date}, before 3 July 2024. Payroll uses the pre-3-July-2024 contribution schedule.`;
    case 'NewEntrant':
      return `First registered with GOSI on ${date}, on or after 3 July 2024, so this person is on the new-entrant schedule. `
        + 'That schedule is not modelled yet, so payroll blocks approval of any run that pays them until it is.';
    default:
      return 'No GOSI first-registration date is recorded. Payroll computes this person\'s GOSI on the pre-3-July-2024 '
        + 'schedule and marks it unverified on every payslip and in payroll validation until the date is recorded.';
  }
}

/**
 * F02 — the payslip's calculation explanation in the payroll register: a one-line label that says which
 * cohort and basis the GOSI lines were computed on, expanding to the full sentence the server froze on the
 * slip (rates applied, "unverified" / "not modelled"). Progressive disclosure: the label is the exception
 * signal, the sentence is the evidence.
 */
export function GosiBasisNote({ cohort, basis }: { cohort?: string | null; basis: string }) {
  const { label, tone } =
    cohort === 'NewEntrant' ? { label: 'GOSI: new entrant', tone: 'text-rose-600 dark:text-rose-400' }
      : cohort === 'Unknown' ? { label: 'GOSI basis: unverified', tone: 'text-amber-600 dark:text-amber-400' }
        : cohort === 'PreJuly2024' ? { label: 'GOSI basis: pre-3-July-2024', tone: 'text-slate-500' }
          : { label: 'GOSI basis', tone: 'text-slate-500' };
  return (
    <details className="mt-0.5 text-start" data-testid="gosi-basis-note">
      <summary className={`cursor-pointer text-[10px] leading-tight ${tone}`}>{label}</summary>
      <p className="mt-1 max-w-xs whitespace-normal text-[11px] leading-snug text-slate-600 dark:text-slate-400">{basis}</p>
    </details>
  );
}

export function GosiCohortPanel({ employee }: { employee: EmployeeDetail }) {
  const [value, setValue] = useState(employee.gosiFirstRegisteredOn ?? '');
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<{ ok: boolean; text: string } | null>(null);

  const cohort = employee.gosiCohort;
  if (!cohort || !isSaudiNational(employee.nationality)) return null;

  const today = new Date().toISOString().slice(0, 10);
  const unchanged = value === (employee.gosiFirstRegisteredOn ?? '');

  const submit = async () => {
    setSaving(true);
    setNotice(null);
    try {
      const res = await employeesApi.update(employee.id, today, { gosiFirstRegisteredOn: value === '' ? null : value });
      setNotice(res.status === 202
        ? { ok: true, text: 'Sent for approval. The date applies once an approver accepts it; payroll runs processed after that use it.' }
        : { ok: true, text: 'Saved.' });
    } catch (e: unknown) {
      const status = (e as { response?: { status?: number } })?.response?.status;
      const message = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setNotice({
        ok: false,
        text: status === 403
          ? 'You do not have permission to change sensitive employee fields.'
          : message ?? 'Could not submit the date. Please check it and try again.',
      });
    } finally {
      setSaving(false);
    }
  };

  const status = STATUS[cohort];
  return (
    <section aria-labelledby="gosi-cohort-heading" className="mt-4 rounded-lg border border-slate-200 p-3 dark:border-white/10" data-testid="gosi-cohort-panel">
      <div className="flex flex-wrap items-center gap-2">
        <h4 id="gosi-cohort-heading" className="text-sm font-semibold text-slate-900 dark:text-white">GOSI cohort</h4>
        <span className={`rounded-full px-2 py-0.5 text-xs font-medium ${status.tone}`} data-testid="gosi-cohort-status">{status.label}</span>
      </div>
      <p className="mt-1 text-xs text-slate-600 dark:text-slate-400">{explain(cohort, employee.gosiFirstRegisteredOn)}</p>

      <div className="mt-3 flex flex-wrap items-end gap-2">
        <label className="grid gap-1 text-xs font-medium text-slate-700 dark:text-slate-300">
          GOSI first-registration date
          <input
            type="date"
            max={today}
            value={value}
            onChange={(e) => setValue(e.target.value)}
            className="input w-44"
            data-testid="gosi-first-registered-on"
          />
        </label>
        <button type="button" onClick={submit} disabled={saving || unchanged} className="btn-primary disabled:opacity-50">
          {saving ? 'Sending…' : 'Send for approval'}
        </button>
      </div>
      <p className="mt-1 text-[11px] text-slate-500">Copy it from the person&apos;s GOSI record. The change is applied only after approval.</p>
      {notice && (
        <p role="status" className={`mt-2 text-xs ${notice.ok ? 'text-emerald-700 dark:text-emerald-400' : 'text-rose-700 dark:text-rose-400'}`}>
          {notice.text}
        </p>
      )}
    </section>
  );
}
