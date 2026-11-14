import type { MessageParams } from '../i18n/message';

type T = (key: string, params?: MessageParams) => string;

/**
 * A rules-engine finding (AiInsightEngine) in the viewer's language. The engine writes an English title and summary with the
 * numbers baked in, and gives each finding a stable `insightType`. The type decides the sentence and its numbers are read back
 * out of the engine's text, so no change to the API is needed. A type this does not know, or text that does not match what the
 * engine writes, is shown as sent (with "employee(s)" spelled out).
 */
export interface InsightLike { insightType: string; title: string; summary: string }

const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const asSent = (text: string) => text.replace(/employee\(s\)/g, 'employees');

/** "November 2026" as the engine writes a month, to "2026-11" for the date formatter; anything else is returned as written. */
function monthPeriod(label: string): string {
  const m = /^([A-Za-z]+) (\d{4})$/.exec(label.trim());
  const index = m ? MONTHS.indexOf(m[1]) : -1;
  return m && index >= 0 ? `${m[2]}-${String(index + 1).padStart(2, '0')}` : label;
}

export function insightTitle(t: T, i: InsightLike): string {
  const first = /^(\d+)/.exec(i.title);
  const count = first ? Number(first[1]) : null;
  switch (i.insightType) {
    case 'PayrollVariance': {
      const m = /^Net payroll (increased|decreased) by ([\d.]+)%$/.exec(i.title);
      if (m) return t(m[1] === 'increased' ? 'Net payroll increased by {pct}%' : 'Net payroll decreased by {pct}%', { pct: m[2] });
      break;
    }
    case 'MissingSalarySetup':
      if (count !== null && /without salary assignment$/.test(i.title)) return t('{count, plural, one {# employee without salary assignment} other {# employees without salary assignment}}', { count });
      break;
    case 'OvertimeAnomaly':
      if (count !== null && /overtime exceeding 30%/.test(i.title)) return t('{count, plural, one {# employee with overtime exceeding 30% of average basic} other {# employees with overtime exceeding 30% of average basic}}', { count });
      break;
    case 'LeaveAccumulationRisk':
      if (count !== null && /60\+ days leave balance/.test(i.title)) return t('{count, plural, one {# employee with 60+ days leave balance} other {# employees with 60+ days leave balance}}', { count });
      break;
    case 'HeadcountTurnover': {
      const m = /^(\d+) termination\(s\) in the last 30 days \(([\d.]+)%\)$/.exec(i.title);
      if (m) return t('{count, plural, one {# termination in the last 30 days ({pct}%)} other {# terminations in the last 30 days ({pct}%)}}', { count: Number(m[1]), pct: m[2] });
      break;
    }
    case 'VisaExpiryRisk':
      if (count !== null && /visa expiring within 60 days$/.test(i.title)) return t('{count, plural, one {# employee with visa expiring within 60 days} other {# employees with visa expiring within 60 days}}', { count });
      break;
    case 'InactiveSalaryStructure':
      if (count !== null && /assigned to inactive salary structures$/.test(i.title)) return t('{count, plural, one {# employee assigned to inactive salary structures} other {# employees assigned to inactive salary structures}}', { count });
      break;
  }
  return asSent(i.title);
}

/** `period` formats a "YYYY-MM" month for the viewer (useFormat().period). */
export function insightDetail(t: T, i: InsightLike, period: (yearMonth: string) => string): string {
  const s = i.summary;
  switch (i.insightType) {
    case 'PayrollVariance': {
      const m = /^Net payroll for (.+) is (increased|decreased) by ([\d.]+)% \(([\d,]+)\) vs\. 3-month average \(([\d,]+)\)\./.exec(s);
      if (m) return t(m[2] === 'increased'
        ? 'Net payroll for {period} is up {pct}% ({net}) against the 3-month average ({avg}). Review for new hires, terminations, or one-time adjustments.'
        : 'Net payroll for {period} is down {pct}% ({net}) against the 3-month average ({avg}). Review for new hires, terminations, or one-time adjustments.',
      { period: period(monthPeriod(m[1])), pct: m[3], net: m[4], avg: m[5] });
      break;
    }
    case 'MissingSalarySetup': {
      const m = /^(\d+) of (\d+) active employees \((\d+)%\)/.exec(s);
      if (m) return t('{count} of {total} active employees ({pct}%) have no active salary structure assigned. They will be excluded from payroll processing until salary is configured.', { count: m[1], total: m[2], pct: m[3] });
      break;
    }
    case 'OvertimeAnomaly': {
      const m = /^(\d+) employee\(s\) received overtime pay exceeding 30% of the company average basic salary in (.+?)\./.exec(s);
      if (m) return t('Employees who received overtime pay above 30% of the company average basic salary in {period}: {count}. Review for unauthorized overtime, shift planning issues, or data entry errors.', { count: m[1], period: period(monthPeriod(m[2])) });
      break;
    }
    case 'LeaveAccumulationRisk': {
      const m = /^(\d+) employee\(s\) have accumulated more than 60 days/.exec(s);
      if (m) return t('Employees with more than 60 days of accumulated leave balance: {count}. Large balances are a financial liability and may point to low team morale or vacation planning issues. Consider enforcing a leave utilization policy.', { count: m[1] });
      break;
    }
    case 'HeadcountTurnover': {
      const m = /^(\d+) employees left in the last 30 days, representing ([\d.]+)% monthly turnover\.[\s\S]*New hires: (\d+)\.$/.exec(s);
      if (m) return t('Employees who left in the last 30 days: {terminations}, or {pct}% monthly turnover. This is above normal thresholds. Review exit interviews and retention strategies. New hires: {joins}.', { terminations: m[1], pct: m[2], joins: m[3] });
      break;
    }
    case 'VisaExpiryRisk': {
      const m = /^(\d+) active employee\(s\) have visas expiring/.exec(s);
      if (m) return t('Active employees whose visas expire within the next 60 days: {count}. Expired visas can result in regulatory fines and operational disruption. Initiate renewal processes immediately.', { count: m[1] });
      break;
    }
    case 'InactiveSalaryStructure': {
      const m = /^(\d+) employee\(s\) are assigned to salary structures that have been deactivated\./.exec(s);
      if (m) return t('Employees assigned to salary structures that have been deactivated: {count}. They will fail payroll validation. Reassign them to active salary structures.', { count: m[1] });
      break;
    }
  }
  return asSent(s);
}
