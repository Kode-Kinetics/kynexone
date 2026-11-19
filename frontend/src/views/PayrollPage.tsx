'use client';

import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import Link from 'next/link';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import {
  AlertTriangle, BarChart2, ShieldCheck, BookOpen, Building2, Calculator,
  CheckCircle2, ChevronDown, ChevronRight, Download, FileText, Landmark, Layers3,
  Lock, Pencil, Play, Plus, RefreshCw, RotateCcw,
  Settings, TrendingUp, Users, WalletCards, X, Trash2,
  Zap, Shield, Lightbulb, ArrowUpRight, Circle, Info,
} from 'lucide-react';
import {
  payrollApi,
  type PayrollRun, type PayrollSlip, type PayrollValidationResult,
  type SalaryChange, type SalaryStructure, type EmployeeSalaryStructure, type Payslip, type GradePackage, type GradePackagesView,
  type PayrollPaymentBatch, type PayrollPaymentRecord, type WpsEvidenceKind,
  type PayrollApproval, type PayrollSummary,
  type PayrollGLJournal, type PayrollReconciliation, type FinalSettlementResult, type FinalSettlementListRow,
  type PayrollCompany, type PayrollOverview, type PayrollReadiness,
  type PayrollCompanySummary, type AIInsight,
  type PayrollRunPopulation, type PayrollPopulationEntry, type PayrollValidationOverrideReport, type AuditIntegrityReport,
} from '../api/payroll';
import { ExitDebtsPanel } from '../components/offboarding/ExitDebtsPanel';
import { identityAuditApi } from '../api/identity';
import { requestFailureReason } from '../lib/requestFailure';
import { commonPayrollCurrency, resolvePayrollRunCurrency, totalsByCurrency, type CompaniesLoadState } from '../lib/payrollCurrency';
import { usePayrollCompanies } from '../hooks/usePayrollCompanies';
import { formatCalendarDate } from '../lib/calendarDate';
import { dateLocale } from '../lib/format';
import { employeeLabel, employeeLabelsFromRecords } from '../lib/employeeLabel';
import { EmployeeLabelText } from '../components/payroll/EmployeeLabelText';
import { useRunEmployeeLabels } from '../hooks/useRunEmployeeLabels';
import { RunSelectOptions } from '../components/payroll/RunSelectOptions';
import { filterPayrollInsightsForReadiness, paymentReadinessHeadline, prerequisiteLabel, type PaymentPrerequisites, type PayrollReadinessWithPrerequisites } from '../lib/payrollPrerequisites';
import client, { apiErrorReason, notifyApiError } from '../api/client';
import { SecondApproverNotice } from '../components/SecondApproverNotice';
import { Field } from '../components/ui/Field';
import { GosiBasisNote } from '../components/GosiCohortPanel';
import { useT } from '../hooks/useT';
import { useConfirm } from '../components/ui/useConfirm';
import { BenefitsByGradeDoor } from '../components/benefits/BenefitsByGradeDoor';
import { formatAmount } from '../lib/hire/money';
import { isolateLtr } from '../lib/bidi';
import { SalaryAllowancesEditor } from '../components/payroll/SalaryAllowancesEditor';
import { SalaryAllowanceComparison } from '../components/payroll/SalaryAllowanceViews';
import { ContractWageNote, SalaryReductionNotice, SalaryReductionSummary } from '../components/payroll/SalaryReductionNotice';
import { CashAllowanceIssuesPanel } from '../components/payroll/CashAllowanceIssues';
import { useCashAllowanceCatalog } from '../hooks/useCashAllowanceCatalog';
import {
  allowanceProblems, allowanceRefusal, allowancesForRequest, currentRecordLines, lineName, shownLines, totalMonthly,
  type AllowanceDraftLine, type AllowanceValue,
} from '../lib/salaryAllowances';
import {
  consentReferenceForRequest, proposalReduction, recordInForce, reductionBlock, reductionRefusal, consentReferenceProblem,
  type ReductionRefusal,
} from '../lib/salaryReduction';
import { parseReconError } from '../lib/payrollRecon';
import { useFormat } from '../hooks/useFormat';
import { useLocale } from '../contexts/LocaleContext';
import { useAuth } from '../contexts/AuthContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { useTenantSettings } from '../contexts/TenantSettingsContext';
import { SectionNav } from '../components/ui/SectionNav';
import { PayrollSectionHeader, PAYROLL_SECTION_HEADING_ID } from '../components/payroll/PayrollSectionHeader';
import { SaudiBankExportGate } from '../components/payroll/SaudiBankExportGate';
import { BankFileGuide, useBankHandOver, type BankPanelData, type GuideSlots } from '../components/payroll/BankFileGuide';
import { BankFileFacts, tieOut } from '../components/payroll/BankFileFacts';
import { RecordSubmittedDialog } from '../components/payroll/RecordSubmittedDialog';
import { bankResponseBuckets, confirmedResponsePurpose, derivePayOut, pickPayOutBatch, rejectedFilePurpose, type PayOutStepKey } from '../lib/payOut';
import { EmptyState } from '../components/ui/EmptyState';
import { Modal as SideDrawer } from '../components/Modal';
import { suggestedBatchReference, type BankFlowProgress } from '../lib/bankFileSteps';
import type { PipelineBatch } from '../lib/payrollPipeline';
import { AttendanceReconciliationPanel } from '../components/payroll/AttendanceReconciliationPanel';
import { payrollInsightEmptyCopy, payrollInsightState, payrollPeriodState } from '../lib/payrollInsightState';
import { LoadFailedNotice, LoadFailedRow } from '../components/ui/LoadFailedRow';
import { SkeletonRows } from '../components/ui/Skeleton';
import { employeeWorkspaceHref } from '../lib/peopleRoutes';
import { RunPipeline, type PayOutContext } from '../components/payroll/RunPipeline';
import { useLockWithSummary } from '../hooks/useLockWithSummary';
import { startRunProcessing } from '../api/payrollProcessing';
import { parseDeepLink, withDeepLink } from '../lib/payrollPipeline';

import { EnumLabel, enumLabel, type EnumName } from '../components/EnumLabel';
import { RunDeductionsReview } from '../components/deductions/RunDeductionsReview';
import { JournalExportsPanel } from '../components/gl/JournalExportsPanel';
import { LoadError, serverMessage } from '../components/gl/glUi';
import { financeGlApi } from '../api/financeGl';
import { isCustomFormatId, saudiBankExportsApi } from '../api/saudiBankExports';
import { CostCentreLockPreview, CostingOffNote, periodLabel } from '../components/payroll/CostCentreLockPreview';
import { VoidRunForm } from '../components/payroll/VoidRunForm';
import { validationPanelState } from '../lib/payrollValidationState';
import { exitLeaveMoney } from '../lib/settlementExitArrears';
import { HeldPayPanel } from '../components/payroll/HeldPayPanel';
import { PostedJournalsView, PostedWithoutCostCentresNote } from '../components/payroll/PostedJournals';
import { costingNote } from '../lib/glJournalView';
import { paymentActionRefusal, paymentActionsAllowed, PAYMENT_ACTION_DENIED } from '../lib/payrollPaymentAccess';
import { UncostedApprovalAck, useUncostedAck } from '../components/payroll/UncostedApprovalAck';
import { CostCentreJournalReport } from '../components/payroll/CostCentreJournalReport';
import { SettlementRunDutyPanel } from '../components/payroll/SettlementRunDutyPanel';
import { SettlementRunDutyCard } from '../components/payroll/SettlementRunDutyCard';
import { msg } from '../i18n/translations';
import { insightDetail, insightTitle } from '../lib/insightText';
import { languageLabel } from '../lib/displayLabels';
import { wpsStatusNote } from '../lib/payrollBatchDisplay';
import { leftOutGroups, payPopulationReasonLabel } from '../lib/payPopulation';
// ── Shared helpers ──────────────────────────────────────────────────────────────

function fmtDate(s: string | null | undefined, locale: string) {
  return formatCalendarDate(s, locale);
}

/** A date or timestamp in the viewer's language (month names follow it; digits stay Latin). */
function useFmtDate() {
  const { locale } = useLocale();
  return useCallback((s: string | null | undefined) => fmtDate(s, dateLocale(locale)), [locale]);
}

/** An amount with its currency in the viewer's language (Arabic shows the Arabic currency symbol and a bidi-safe order). */
function useFmtAmt() {
  const fx = useFormat();
  return useCallback((n: number, currency = 'USD') => fx.money(n, currency), [fx]);
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** Month (and optional year) names in the viewer's language; MONTHS stays the English key list the API uses. */
function useMonthLabel() {
  const fmt = useFormat();
  return useCallback((month: number, year?: number) => fmt.period(year == null ? MONTHS[month - 1] : `${MONTHS[month - 1]} ${year}`, 'long'), [fmt]);
}

const STATUS_COLOR: Record<string, string> = {
  Draft: 'bg-slate-100 text-slate-600 dark:bg-slate-700 dark:text-slate-300',
  Processed: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
  PendingFinanceReview: 'bg-orange-50 text-orange-700 dark:bg-orange-500/10 dark:text-orange-400',
  Approved: 'bg-blue-50 text-blue-700 dark:bg-blue-500/10 dark:text-blue-400',
  Locked: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
  Pending: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
  Paid: 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400',
  Failed: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
  Generated: 'bg-teal-50 text-teal-700 dark:bg-teal-500/10 dark:text-teal-400',
  FileGenerated: 'bg-teal-50 text-teal-700 dark:bg-teal-500/10 dark:text-teal-400',
  SentBack: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
  Warning: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400',
  Error: 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-400',
  Info: 'bg-sky-50 text-sky-700 dark:bg-sky-500/10 dark:text-sky-400',
};

function StatusBadge({ status, enumName = 'PayrollRunStatus' }: { status: string; enumName?: EnumName }) {
  const cls = STATUS_COLOR[status] ?? 'bg-slate-100 text-slate-500 dark:bg-slate-700 dark:text-slate-400';
  return <span className={`inline-flex rounded-md px-2 py-0.5 text-xs font-medium ${cls}`}><EnumLabel enum={enumName} value={status} /></span>;
}

function KpiCard({ label, value, sub, icon: Icon, color }: {
  label: string; value: string | number; sub?: string;
  icon: React.ComponentType<{ className?: string }>; color: string;
}) {
  return (
    <div className="surface flex items-center gap-3 p-3.5">
      <div className={`grid h-10 w-10 shrink-0 place-items-center rounded-xl ${color}`}>
        <Icon className="h-5 w-5" />
      </div>
      <div className="min-w-0">
        <p className="whitespace-nowrap text-lg font-bold tabular-nums text-slate-900 2xl:text-xl dark:text-white">{value}</p>
        <p className="mt-0.5 truncate text-xs text-slate-500 dark:text-slate-400">{label}</p>
        {sub && <p className="text-[10px] text-slate-400 dark:text-slate-500">{sub}</p>}
      </div>
    </div>
  );
}

/** One count in a toolbar row (a KpiCard without the second line): the number, then what it counts. */
function CountChip({ label, value, color }: { label: string; value: string | number; color: string }) {
  return (
    <div className="surface flex items-center gap-2 px-3 py-1.5">
      <span className={`grid h-7 w-7 shrink-0 place-items-center rounded-lg ${color}`}><AlertTriangle className="h-4 w-4" aria-hidden="true" /></span>
      <span className="whitespace-nowrap text-base font-bold tabular-nums text-slate-900 dark:text-white">{value}</span>
      <span className="whitespace-nowrap text-xs text-slate-500 dark:text-slate-400">{label}</span>
    </div>
  );
}

function Modal({ title, onClose, children, wide }: { title: string; onClose: () => void; children: React.ReactNode; wide?: boolean }) {
  const t = useT();
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 p-4">
      <div className={`relative w-full rounded-2xl bg-white shadow-2xl dark:bg-slate-800 ${wide ? 'max-w-3xl' : 'max-w-lg'} max-h-[90vh] overflow-y-auto`}>
        <div className="sticky top-0 flex items-center justify-between border-b border-slate-100 bg-white px-6 py-4 dark:border-white/10 dark:bg-slate-800">
          <h2 className="text-sm font-semibold text-slate-900 dark:text-white">{title}</h2>
          <button type="button" onClick={onClose} aria-label={t('Close')} className="rounded-lg p-1 hover:bg-slate-100 dark:hover:bg-white/10">
            <X className="h-4 w-4 text-slate-500" />
          </button>
        </div>
        <div className="p-6">{children}</div>
      </div>
    </div>
  );
}

const inp = 'w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white';
const sel = `${inp} appearance-none`;
const btn = {
  primary: 'inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white hover:bg-sapphire/90 disabled:opacity-50',
  ghost: 'inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5',
  danger: 'inline-flex items-center gap-1.5 rounded-lg bg-rose-600 px-4 py-2 text-sm font-medium text-white hover:bg-rose-700 disabled:opacity-50',
  sm: 'inline-flex items-center gap-1 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5',
  link: 'text-xs font-medium text-sapphire underline-offset-2 hover:underline',
};

// ── Tabs ────────────────────────────────────────────────────────────────────────

type Tab = 'dashboard' | 'salary-structures' | 'employee-salary' | 'runs' | 'validation' | 'approvals' | 'payslips' | 'bank-wps' | 'payment-tracking' | 'reports' | 'eosb' | 'ai-validation' | 'audit-integrity' | 'gl-journal' | 'reconciliation' | 'final-settlement';

// The order is the order of the groups below: arrow keys move through the tabs in this order.
const TABS: { key: Tab; label: string; icon: React.ComponentType<{ className?: string }> }[] = [
  { key: 'dashboard',        label: msg('Dashboard'),          icon: BarChart2 },
  { key: 'salary-structures', label: msg('Grade Packages'),     icon: Layers3 },
  { key: 'employee-salary',  label: msg('Employee Salary'),     icon: Users },
  { key: 'runs',             label: msg('Payroll Runs'),        icon: Play },
  { key: 'validation',       label: msg('Validation'),          icon: AlertTriangle },
  { key: 'approvals',        label: msg('Approvals'),           icon: CheckCircle2 },
  { key: 'payslips',         label: msg('Payslips'),            icon: FileText },
  { key: 'bank-wps',         label: msg('Bank / WPS Files'),    icon: Landmark },
  { key: 'payment-tracking', label: msg('Payment Tracking'),    icon: TrendingUp },
  { key: 'reports',          label: msg('Reports'),             icon: BarChart2 },
  { key: 'gl-journal',       label: msg('GL Journal'),          icon: BookOpen },
  { key: 'reconciliation',   label: msg('Reconciliation'),      icon: RefreshCw },
  { key: 'eosb',             label: msg('EOSB / Gratuity'),    icon: FileText },
  { key: 'final-settlement', label: msg('Final Settlement'),    icon: Calculator },
  { key: 'ai-validation',    label: msg('Anomaly Checks'),      icon: ShieldCheck },
  { key: 'audit-integrity',  label: msg('Audit Integrity'),     icon: Shield },
];

/**
 * The sixteen sections as five labelled groups, in the order a payroll month goes (the Payroll section nav). Each group has one purpose
 * sentence its sections use under their heading until they have their own (Bank / WPS Files has its own).
 */
const TAB_GROUPS: { id: string; label: string; purpose: string; ids: Tab[] }[] = [
  { id: 'set-up',  label: msg('Set up'),            purpose: msg('Set up the grade packages and salaries every payroll run uses.'), ids: ['dashboard', 'salary-structures', 'employee-salary'] },
  { id: 'run',     label: msg('Run payroll'),       purpose: msg('Process, check and approve the month’s payroll run, then lock it.'), ids: ['runs', 'validation', 'approvals', 'payslips'] },
  { id: 'pay-out', label: msg('Pay out'),           purpose: msg('Record what the bank confirms or returns for each payment, then settle the payments.'), ids: ['bank-wps', 'payment-tracking'] },
  { id: 'ledger',  label: msg('Report and ledger'), purpose: msg('See the run’s reports and post its journal to the general ledger.'), ids: ['reports', 'gl-journal', 'reconciliation'] },
  { id: 'settle',  label: msg('Settle and check'),  purpose: msg('Settle end-of-service and final pay, and check the payroll for anomalies.'), ids: ['eosb', 'final-settlement', 'ai-validation', 'audit-integrity'] },
];
const groupOf = (tab: Tab) => TAB_GROUPS.find(g => g.ids.includes(tab)) ?? TAB_GROUPS[0];
/** A section's own purpose line; the others still use their group's sentence (follow-up). */
const TAB_PURPOSE: Partial<Record<Tab, string>> = {
  runs: msg('Create, process and lock the month’s run.'),
  validation: msg('Check the run for problems before it goes to approval.'),
  approvals: msg('See who has approved this run and what is waiting.'),
};

// ── Payroll Setup Wizard ───────────────────────────────────────────────────────

function PayrollSetupWizard({ readiness, onNavigate }: { readiness: PayrollReadiness; onNavigate: (t: Tab) => void }) {
  const t = useT();
  const stepToTab: Record<number, Tab> = {
    1: 'salary-structures',
    2: 'salary-structures',
    3: 'employee-salary',
    4: 'runs',
    5: 'validation',
    6: 'approvals',
  };

  return (
    <div className="surface overflow-hidden">
      <div className="border-b border-slate-100 bg-gradient-to-r from-sapphire/5 to-transparent px-6 py-4 dark:border-white/10 dark:from-cyanAccent/5">
        <div className="flex items-center gap-3">
          <div className="grid h-9 w-9 place-items-center rounded-xl bg-sapphire/10 dark:bg-cyanAccent/10">
            <Settings className="h-5 w-5 text-sapphire dark:text-cyanAccent" />
          </div>
          <div>
            <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{t('Payroll Setup Wizard')}</h3>
            <p className="text-xs text-slate-500 dark:text-slate-400">{t('Complete these steps before running payroll · {completionPercent}% done', { completionPercent: readiness.completionPercent })}</p>
          </div>
          <div className="ms-auto">
            <div className="h-2 w-32 overflow-hidden rounded-full bg-slate-200 dark:bg-white/10">
              <div
                className="h-full rounded-full bg-sapphire transition-all dark:bg-cyanAccent [width:var(--progress-w)]"
                style={{ '--progress-w': `${readiness.completionPercent}%` } as React.CSSProperties}
              />
            </div>
          </div>
        </div>
      </div>
      <div className="divide-y divide-slate-100 dark:divide-white/5">
        {readiness.steps.map((s) => (
          <div key={s.step} className="flex items-center gap-4 px-6 py-3.5">
            <div className={`grid h-7 w-7 shrink-0 place-items-center rounded-full text-xs font-bold ${s.complete ? 'bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400' : 'bg-slate-100 text-slate-500 dark:bg-white/10 dark:text-slate-400'}`}>
              {s.complete ? <CheckCircle2 className="h-4 w-4" /> : s.step}
            </div>
            <div className="min-w-0 flex-1">
              <p className={`text-sm font-medium ${s.complete ? 'text-slate-500 line-through dark:text-slate-500' : 'text-slate-800 dark:text-white'}`}>{s.label}</p>
              <p className="text-xs text-slate-400 dark:text-slate-500">{s.detail}</p>
            </div>
            {!s.complete && (
              <button type="button" onClick={() => onNavigate(stepToTab[s.step] ?? 'salary-structures')}
                className="shrink-0 inline-flex items-center gap-1 rounded-lg bg-sapphire/10 px-3 py-1.5 text-xs font-medium text-sapphire hover:bg-sapphire/20 dark:bg-cyanAccent/10 dark:text-cyanAccent dark:hover:bg-cyanAccent/20">
                {t('Configure')} <ChevronRight className="h-3 w-3" />
              </button>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}

// ── Payment prerequisites (who can actually be paid) ──────────────────────────
// Salary coverage says who can be CALCULATED. This panel says who can be PAID, per employee, before a
// run exists — the same gaps the run validation will later raise. Blocking gaps and recommendations
// are separate lists; nothing here is inferred on the client.

function PrerequisiteRow({ label, count, nextAction, danger, action }: {
  label: string; count?: number; nextAction: string; danger: boolean; action?: { label: string; onClick: () => void };
}) {
  const t = useT();
  return (
    <li className="flex flex-wrap items-center gap-x-3 gap-y-1 px-5 py-2.5">
      <span className={`w-12 shrink-0 text-sm font-semibold tabular-nums ${danger ? 'text-rose-700 dark:text-rose-300' : 'text-amber-700 dark:text-amber-300'}`}>
        {count !== undefined
          ? count.toLocaleString('en-US')
          : <Building2 className="h-4 w-4" aria-label={t('Company setting')} />}
      </span>
      <span className="min-w-0 flex-1 text-sm text-slate-800 dark:text-slate-100">
        {label}
        <span className="block text-xs text-slate-500 dark:text-slate-400">{t('Next: {nextAction}', { nextAction })}</span>
      </span>
      {action && (
        <button type="button" onClick={action.onClick} className={btn.sm}>{action.label} <ChevronRight className="h-3 w-3" /></button>
      )}
    </li>
  );
}

function PaymentPrerequisitesPanel({ prerequisites, runStatus, onNavigate }: { prerequisites: PaymentPrerequisites; runStatus?: string | null; onNavigate: (t: Tab) => void }) {
  const t = useT();
  const headline = paymentReadinessHeadline(prerequisites, runStatus);
  if (headline.tone === 'unknown' || headline.tone === 'empty') return null;
  const tone = {
    blocked: { box: 'border-rose-200 dark:border-rose-500/30', icon: 'text-rose-600 dark:text-rose-400' },
    attention: { box: 'border-amber-200 dark:border-amber-500/30', icon: 'text-amber-600 dark:text-amber-400' },
    clear: { box: 'border-emerald-200 dark:border-emerald-500/30', icon: 'text-emerald-600 dark:text-emerald-400' },
    // A locked or paid run: the gaps are records to fix for next month, so nothing here reads as a block.
    settled: { box: 'border-slate-200 dark:border-white/10', icon: 'text-sky-600 dark:text-sky-400' },
  }[headline.tone];
  const settled = headline.tone === 'settled';
  // Salary gaps are fixed on a payroll tab; every other gap lives on the employee record.
  const salaryAction = { label: t('Assign salaries'), onClick: () => onNavigate('employee-salary') };
  const hasBlocking = prerequisites.companyBlocking.length > 0 || prerequisites.blocking.length > 0;
  const hasRecommended = prerequisites.companyRecommended.length > 0 || prerequisites.recommended.length > 0;
  const listed = prerequisites.employees.length;
  // The long "how this is checked" sentence sits inside the employee disclosure when there is one, so the panel's header stays two lines.
  const checkedNote = (
    <p className="mt-1 text-[11px] text-slate-400 dark:text-slate-500" data-testid="payment-readiness-checked">
      {prerequisites.attendanceChecked
        ? t('Checked for {count} active employees against salary assignments, payroll profiles and IBANs, statutory readiness and nationality, and attendance for this period — the rules the run validation applies. One employee can have more than one gap.', { count: prerequisites.evaluatedEmployees })
        : t('Checked for {count} active employees against salary assignments, payroll profiles and IBANs, statutory readiness and nationality (attendance is checked once the period starts) — the rules the run validation applies. One employee can have more than one gap.', { count: prerequisites.evaluatedEmployees })}
    </p>
  );

  return (
    <section aria-label={t('Payment readiness')} className={`surface overflow-hidden border ${tone.box}`}>
      <div className="flex items-start gap-3 border-b border-slate-100 px-5 py-3 dark:border-white/10">
        {headline.tone === 'clear'
          ? <CheckCircle2 className={`mt-0.5 h-5 w-5 shrink-0 ${tone.icon}`} />
          : settled
            ? <Info className={`mt-0.5 h-5 w-5 shrink-0 ${tone.icon}`} aria-hidden="true" />
            : <AlertTriangle className={`mt-0.5 h-5 w-5 shrink-0 ${tone.icon}`} />}
        <div className="min-w-0">
          <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{t(headline.titleKey, headline.params)}</h3>
          <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{t(headline.detailKey, headline.params)}</p>
          {listed === 0 && checkedNote}
        </div>
      </div>

      {hasBlocking && (
        <div>
          <p className={`px-5 py-2 text-xs font-semibold uppercase tracking-wide ${settled ? 'bg-slate-50 text-slate-600 dark:bg-white/5 dark:text-slate-300' : 'bg-rose-50 text-rose-700 dark:bg-rose-500/10 dark:text-rose-300'}`}>{settled ? t('To fix before next month') : t('Blocks payment')}</p>
          <ul className="divide-y divide-slate-100 dark:divide-white/5">
            {prerequisites.companyBlocking.map(c => (
              <PrerequisiteRow key={`c-${c.companyId ?? 'none'}-${c.code}`} danger={!settled} label={c.companyName ? `${c.companyName}: ${t(c.label)}` : t(c.label)} nextAction={t(c.nextAction)} />
            ))}
            {prerequisites.blocking.map(b => (
              <PrerequisiteRow key={b.code} danger={!settled} label={t(b.label)} count={b.count} nextAction={t(b.nextAction)}
                action={b.code === 'MISSING_SALARY_STRUCTURE' ? salaryAction : undefined} />
            ))}
          </ul>
        </div>
      )}

      {hasRecommended && (
        <div>
          <p className="bg-amber-50 px-5 py-2 text-xs font-semibold uppercase tracking-wide text-amber-700 dark:bg-amber-500/10 dark:text-amber-300">{t('Recommended — does not block approval')}</p>
          <ul className="divide-y divide-slate-100 dark:divide-white/5">
            {prerequisites.companyRecommended.map(c => (
              <PrerequisiteRow key={`c-${c.companyId ?? 'none'}-${c.code}`} danger={false} label={c.companyName ? `${c.companyName}: ${t(c.label)}` : t(c.label)} nextAction={t(c.nextAction)} />
            ))}
            {prerequisites.recommended.map(r => (
              <PrerequisiteRow key={r.code} danger={false} label={t(r.label)} count={r.count} nextAction={t(r.nextAction)} />
            ))}
          </ul>
        </div>
      )}

      {listed > 0 && (
        <details className="border-t border-slate-100 dark:border-white/10">
          <summary className="cursor-pointer px-5 py-3 text-xs font-medium text-sapphire dark:text-cyanAccent">
            {prerequisites.employeesTruncated
              ? t('Show each employee ({count}, the first 500 — the counts above include everyone)', { count: listed })
              : t('Show each employee ({count})', { count: listed })}
          </summary>
          <div className="px-5 pb-2">{checkedNote}</div>
          <div className="max-h-96 overflow-auto">
            <table className="w-full text-xs">
              <thead className="sticky top-0 bg-surface-sunken text-fg-muted">
                <tr>
                  <th scope="col" className="px-5 py-2 text-start font-medium">{t('Employee')}</th>
                  <th scope="col" className="px-3 py-2 text-start font-medium">{t('Blocks payment')}</th>
                  <th scope="col" className="px-3 py-2 text-start font-medium">{t('Recommended')}</th>
                  <th scope="col" className="px-5 py-2 text-end font-medium">{t('Next action')}</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100 dark:divide-white/5">
                {prerequisites.employees.map(e => (
                  <tr key={e.employeeId}>
                    <td className="px-5 py-2">
                      <span className="font-mono text-slate-500 dark:text-slate-400">{e.employeeCode}</span>{' '}
                      <span className="text-slate-800 dark:text-slate-100">{e.employeeName}</span>
                    </td>
                    <td className={`px-3 py-2 ${settled ? 'text-slate-700 dark:text-slate-200' : 'text-rose-700 dark:text-rose-300'}`}>{e.blocking.map(code => t(prerequisiteLabel(prerequisites, code))).join('; ') || '—'}</td>
                    <td className="px-3 py-2 text-amber-700 dark:text-amber-300">{e.recommended.map(code => t(prerequisiteLabel(prerequisites, code))).join('; ') || '—'}</td>
                    <td className="px-5 py-2 text-end">
                      {e.blocking.length === 1 && e.blocking[0] === 'MISSING_SALARY_STRUCTURE' ? (
                        <button type="button" onClick={salaryAction.onClick} className="font-medium text-sapphire hover:underline dark:text-cyanAccent">{t('Assign salary')}</button>
                      ) : (
                        <Link href={employeeWorkspaceHref(e.employeeId, { section: 'pay-benefits', panel: 'payroll' })} className="font-medium text-sapphire hover:underline dark:text-cyanAccent">
                          {t('Open {employeeCode}', { employeeCode: e.employeeCode })}
                        </Link>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </details>
      )}
    </section>
  );
}

// ── Company Bird's-Eye Table ───────────────────────────────────────────────────

function CompanyBirdsEyeTable({ overview, onDrillDown }: { overview: PayrollOverview; onDrillDown: (company: PayrollCompanySummary) => void }) {
  const t = useT();
  const fmtAmt = useFmtAmt();
  const monthLabel = useMonthLabel();
  // Group totals add every company's run together, so they only have one currency when every
  // company with a run pays in the same one. A mixed group is read per company row instead.
  const groupCurrency = commonPayrollCurrency(overview.companies.filter((company) => company.hasPayrollRun));
  return (
    <div className="surface overflow-hidden">
      <div className="flex items-center justify-between border-b border-slate-100 px-5 py-3.5 dark:border-white/10">
        <h3 className="text-sm font-semibold text-slate-900 dark:text-white">
          {t('All Group Companies — {period}', { period: monthLabel(overview.month, overview.year) })}
        </h3>
        <span className="text-xs text-slate-400">{t('{totalCompanies} companies · {toLocaleString} employees', { totalCompanies: overview.totalCompanies, toLocaleString: overview.totalActiveEmployees.toLocaleString() })}</span>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full text-xs">
          <thead>
            <tr className="border-b border-line bg-surface-sunken">
              {[t('Company'), t('Employees'), t('Salary Coverage'), t('Gross Payroll'), t('Net Payroll'), t('Errors'), t('Pending Approvals'), t('Status'), ''].map(h => (
                <th key={h} className="px-4 py-2.5 text-start font-medium text-fg-muted">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/5">
            {overview.companies.map(c => (
              <tr key={c.companyId} className="group hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                <td className="px-4 py-3">
                  <p className="font-medium text-slate-800 dark:text-white">{c.companyName}</p>
                  {c.tradeName && c.tradeName !== c.companyName && <p className="text-slate-400">{c.tradeName}</p>}
                </td>
                <td className="px-4 py-3 text-slate-600 dark:text-slate-300">{c.activeEmployees.toLocaleString()}</td>
                {/* Coverage is "how many of this company's employees have a salary on file". With nobody
                    on file the server returns 0.0 (PayrollController.cs:9459 — the divide-by-zero guard),
                    which is not a 0% failure, it is a company nobody has joined yet. Rendering the red bar
                    for it made a brand-new tenant read as a broken one. */}
                <td className="px-4 py-3">
                  {c.activeEmployees === 0 ? (
                    <span className="text-slate-400 dark:text-slate-500">{t('No employees yet')}</span>
                  ) : (
                    <>
                      <div className="flex items-center gap-2">
                        <div className="h-1.5 w-16 overflow-hidden rounded-full bg-slate-200 dark:bg-white/10">
                          <div
                            className={`h-full rounded-full transition-all [width:var(--cov-w)] ${c.salaryCoveragePercent >= 90 ? 'bg-emerald-500' : c.salaryCoveragePercent >= 70 ? 'bg-amber-500' : 'bg-rose-500'}`}
                            style={{ '--cov-w': `${c.salaryCoveragePercent}%` } as React.CSSProperties}
                          />
                        </div>
                        <span className={c.salaryCoveragePercent < 70 ? 'font-semibold text-rose-600 dark:text-rose-400' : 'text-slate-600 dark:text-slate-300'}>
                          {c.salaryCoveragePercent.toFixed(0)}%
                        </span>
                      </div>
                      {c.employeesMissingSalary > 0 && <p className="mt-0.5 text-rose-500">{t('{employeesMissingSalary} missing', { employeesMissingSalary: c.employeesMissingSalary })}</p>}
                    </>
                  )}
                </td>
                <td className="px-4 py-3 font-mono text-slate-700 dark:text-slate-300">{c.hasPayrollRun ? fmtAmt(c.grossPayroll, c.currency) : '—'}</td>
                <td className="px-4 py-3 font-mono font-semibold text-slate-800 dark:text-white">{c.hasPayrollRun ? fmtAmt(c.netPayroll, c.currency) : '—'}</td>
                <td className="px-4 py-3">
                  {c.validationErrors > 0
                    ? <span className="inline-flex items-center gap-1 rounded-md bg-rose-50 px-1.5 py-0.5 text-rose-600 dark:bg-rose-500/10 dark:text-rose-400"><AlertTriangle className="h-3 w-3" />{c.validationErrors}</span>
                    : <span className="text-slate-300 dark:text-slate-600">—</span>}
                </td>
                <td className="px-4 py-3">
                  {c.pendingApprovals > 0
                    ? <span className="inline-flex items-center gap-1 rounded-md bg-amber-50 px-1.5 py-0.5 text-amber-600 dark:bg-amber-500/10 dark:text-amber-400"><Circle className="h-2 w-2 fill-current" />{c.pendingApprovals}</span>
                    : <span className="text-slate-300 dark:text-slate-600">—</span>}
                </td>
                <td className="px-4 py-3">{c.payrollRunStatus ? <StatusBadge status={c.payrollRunStatus} /> : <span className="rounded-md bg-slate-100 px-2 py-0.5 text-slate-500 dark:bg-white/10 dark:text-slate-400">{t('Not started')}</span>}</td>
                <td className="px-4 py-3">
                  <button type="button" onClick={() => onDrillDown(c)}
                    className="inline-flex items-center gap-1 rounded-lg border border-slate-200 px-2.5 py-1 text-xs font-medium text-slate-600 opacity-0 transition-opacity hover:bg-slate-50 group-hover:opacity-100 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5">
                    {t('Drill down')} <ArrowUpRight className="h-3 w-3" />
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {/* Same shape as the dashboard's "No payroll run yet" hero: say what is true, say what appears here
          once it is not, and give the one next action. Never a row of zeroes dressed as a result. */}
      {overview.totalActiveEmployees === 0 ? (
        <div role="status" className="border-t border-line bg-surface-sunken px-5 py-5">
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('No employees yet')}</p>
          <p className="mt-1 max-w-prose text-xs text-slate-600 dark:text-slate-400">
            {t('Salary coverage and payroll totals fill in per company once the first employees are on record. Nothing has failed here — there is simply nobody to pay yet.')}
          </p>
          <Link
            href="/people"
            className="mt-3 inline-flex w-fit items-center gap-1.5 rounded-lg bg-sapphire px-3.5 py-2 text-xs font-semibold text-white hover:bg-sapphire/90 dark:bg-cyanAccent dark:text-slate-900 dark:hover:bg-cyanAccent/90"
          >
            {t('Add employees')} <ChevronRight className="h-3.5 w-3.5" />
          </Link>
        </div>
      ) : (
        <div className="border-t border-line bg-surface-sunken px-5 py-3">
          <div className="flex items-center gap-8 text-xs">
            <span className="text-slate-500 dark:text-slate-400">{t('Group totals')}:</span>
            {groupCurrency ? (
              <>
                <span className="font-semibold text-slate-800 dark:text-white">{t('Gross: {totalGrossPayroll}', { totalGrossPayroll: fmtAmt(overview.totalGrossPayroll, groupCurrency) })}</span>
                <span className="font-semibold text-emerald-600 dark:text-emerald-400">{t('Net: {totalNetPayroll}', { totalNetPayroll: fmtAmt(overview.totalNetPayroll, groupCurrency) })}</span>
              </>
            ) : overview.companies.some((company) => company.hasPayrollRun) ? (
              <span className="font-semibold text-amber-700 dark:text-amber-300">{t('Companies pay in different currencies — read the totals per company above')}</span>
            ) : (
              <span className="text-slate-500 dark:text-slate-400">{t('No payroll run in this period')}</span>
            )}
            {overview.totalValidationErrors > 0 && <span className="font-semibold text-rose-600 dark:text-rose-400">{t('{totalValidationErrors} errors', { totalValidationErrors: overview.totalValidationErrors })}</span>}
            {overview.totalPendingApprovals > 0 && <span className="font-semibold text-amber-600 dark:text-amber-400">{t('{totalPendingApprovals} pending approvals', { totalPendingApprovals: overview.totalPendingApprovals })}</span>}
          </div>
        </div>
      )}
    </div>
  );
}

// ── AI Insights Panel ─────────────────────────────────────────────────────────

function AiInsightsPanel({ readiness }: { readiness: PayrollReadiness | null }) {
  const t = useT();
  const fx = useFormat();
  // This panel is ADVISORY and nobody asked for it — it fetches on mount, beside whatever payroll work
  // the operator is actually doing. /api/ai/insights requires `ai.insights_view`, which the HR Manager
  // and Finance Approver bundles do not hold, so for them it 403'd and the shared client's global
  // interceptor raised a red "Access Denied" toast over a payroll screen where the action had SUCCEEDED
  // — a completed payroll run reading as a permission failure. An evidence run caught it on 5 of 15
  // steps, once directly over a screen correctly showing 14 published payslips.
  //
  // Gated the way useWorkforceFindings already gates the same endpoint, rather than by suppressing the
  // toast: a user-initiated action that is refused must still say so. Nobody's permissions are widened;
  // the panel simply does not ask a question it is not allowed to ask, and renders nothing — NOT the
  // amber "insight status is unavailable" alert, which would trade a false alarm for a persistent one.
  const { hasPermission } = useAuth();
  const { isFeatureEnabled } = useFeatureFlags();
  const enabled = isFeatureEnabled('ai_assistant') && hasPermission('ai.insights_view');

  const [insights, setInsights] = useState<AIInsight[]>([]);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (!enabled) { setLoading(false); return; }
    client.get<{ items: AIInsight[] }>('/api/ai/insights', { params: { acknowledged: false, pageSize: 5 } })
      .then(r => setInsights(r.data.items))
      .catch(() => { setInsights([]); setFailed(true); })
      .finally(() => setLoading(false));
  }, [enabled]);

  const severityStyle: Record<string, string> = {
    Critical: 'border-rose-200 bg-rose-50 dark:border-rose-500/20 dark:bg-rose-500/5',
    Warning: 'border-amber-200 bg-amber-50 dark:border-amber-500/20 dark:bg-amber-500/5',
    Info: 'border-blue-200 bg-blue-50 dark:border-blue-500/20 dark:bg-blue-500/5',
  };
  const severityIcon: Record<string, string> = {
    Critical: 'text-rose-600 dark:text-rose-400',
    Warning: 'text-amber-600 dark:text-amber-400',
    Info: 'text-blue-600 dark:text-blue-400',
  };

  // A salary-gap insight the live readiness has already disproved is history, not a current finding.
  const visibleInsights = filterPayrollInsightsForReadiness(insights, readiness);
  // Nothing was asked and nothing is claimed: an advisory source this caller cannot read shows no panel
  // at all, so it never reads as either an alarm or an all-clear.
  if (!enabled) return null;
  const state = payrollInsightState(loading, failed, visibleInsights.length);
  if (state === 'loading') return <div className="surface h-16 animate-pulse" aria-label={t('Loading payroll insights')} />;
  if (state === 'empty' || state === 'unavailable') return (
    <div role={state === 'unavailable' ? 'alert' : 'status'} className={`surface flex items-center gap-3 p-5 ${state === 'unavailable' ? 'border-amber-300 dark:border-amber-500/30' : ''}`}>
      {state === 'unavailable' ? <AlertTriangle className="h-4 w-4 shrink-0 text-amber-500" /> : <Lightbulb className="h-4 w-4 shrink-0 text-slate-400" />}
      <p className="text-sm text-slate-500 dark:text-slate-400">{payrollInsightEmptyCopy(state)}</p>
    </div>
  );

  return (
    <div className="surface overflow-hidden">
      <div className="flex items-center gap-2 border-b border-slate-100 px-5 py-3.5 dark:border-white/10">
        <Lightbulb className="h-4 w-4 text-sapphire dark:text-cyanAccent" />
        <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{t('Insights')}</h3>
        <span className="ms-auto rounded-full bg-rose-100 px-2 py-0.5 text-xs font-semibold text-rose-600 dark:bg-rose-500/20 dark:text-rose-400">{t('{length} active', { length: visibleInsights.length })}</span>
      </div>
      <div className="divide-y divide-slate-100 dark:divide-white/5">
        {visibleInsights.map(ins => (
          <div key={ins.id} className={`mx-4 my-2 rounded-xl border p-3.5 ${severityStyle[ins.severity] ?? severityStyle.Info}`}>
            <div className="flex items-start gap-3">
              <AlertTriangle className={`mt-0.5 h-4 w-4 shrink-0 ${severityIcon[ins.severity] ?? severityIcon.Info}`} />
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2">
                  <p className="text-xs font-semibold text-slate-800 dark:text-white">{insightTitle(t, ins)}</p>
                  <span className="rounded-full bg-white/60 px-1.5 py-0.5 text-[10px] text-slate-500 dark:bg-white/10 dark:text-slate-400">{t(ins.module)}</span>
                </div>
                <p className="mt-1 text-xs leading-relaxed text-slate-600 dark:text-slate-300">{insightDetail(t, ins, (ym) => fx.period(ym, 'long'))}</p>
                <p className="mt-1.5 text-[10px] text-slate-400">{fx.dateTime(ins.createdAtUtc)}</p>
              </div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

// ── Dashboard Tab (Command Center) ─────────────────────────────────────────────

function DashboardTab({ onNavigate }: { onNavigate: (t: Tab) => void }) {
  const t = useT();
  const fmtAmt = useFmtAmt();
  const monthLabel = useMonthLabel();
  const now = new Date();
  const [selectedCompanyId, setSelectedCompanyId] = useState<string>('all');
  const [selectedYear, setSelectedYear] = useState(now.getFullYear());
  const [selectedMonth, setSelectedMonth] = useState(now.getMonth() + 1);
  const [companies, setCompanies] = useState<PayrollCompany[]>([]);
  const [overview, setOverview] = useState<PayrollOverview | null>(null);
  const [readiness, setReadiness] = useState<PayrollReadinessWithPrerequisites | null>(null);
  const [drillDown, setDrillDown] = useState<PayrollCompanySummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [summary, setSummary] = useState<PayrollSummary | null>(null);
  const [overviewFailed, setOverviewFailed] = useState(false);

  const load = () => {
    setLoading(true);
    setOverview(null);
    setReadiness(null);
    setSummary(null);
    setOverviewFailed(false);
    const params = {
      companyId: selectedCompanyId !== 'all' ? selectedCompanyId : undefined,
      year: selectedYear,
      month: selectedMonth,
    };
    Promise.all([
      payrollApi.listCompanies().then(setCompanies).catch(() => {}),
      payrollApi.getOverview(params).then(setOverview).catch(() => setOverviewFailed(true)),
      payrollApi.getReadiness(params).then(setReadiness).catch(() => {}),
      payrollApi.reportSummary().then(setSummary).catch(() => {}),
    ]).finally(() => setLoading(false));
  };

  useEffect(load, [selectedCompanyId, selectedYear, selectedMonth]);

  const isNotConfigured = !loading && readiness && readiness.completionPercent < 30;
  const hasPayrollRun = overview?.companies.some((company) => company.hasPayrollRun) ?? false;
  const periodState = payrollPeriodState(hasPayrollRun, overview !== null);
  // The KPI cards add up every company in scope; label them only with a currency they all share.
  const overviewCurrency = overview
    ? selectedCompanyId === 'all'
      ? commonPayrollCurrency(overview.companies.filter((company) => company.hasPayrollRun))
      : commonPayrollCurrency(overview.companies.filter((company) => company.companyId === selectedCompanyId))
    : null;
  const overviewAmount = (value: number) => overviewCurrency ? fmtAmt(value, overviewCurrency) : t('Mixed currencies');

  const years = Array.from({ length: 3 }, (_, i) => now.getFullYear() - i);



  return (
    <div className="space-y-5">
      <SettlementRunDutyCard onOpen={() => onNavigate('final-settlement')} />
      {/* ── Scope Selector ── */}
      <div className="surface flex flex-wrap items-center gap-3 p-3.5">
        <Building2 className="h-4 w-4 shrink-0 text-slate-400" />
        <select
          aria-label={t('Company scope')}
          className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm text-slate-700 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white"
          value={selectedCompanyId}
          onChange={e => { setSelectedCompanyId(e.target.value); setDrillDown(null); }}
        >
          <option value="all">{t('All Group Companies')}</option>
          {companies.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
        <select
          aria-label={t('Month')}
          className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm text-slate-700 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white"
          value={selectedMonth}
          onChange={e => setSelectedMonth(Number(e.target.value))}
        >
          {MONTHS.map((m, i) => <option key={m} value={i + 1}>{monthLabel(i + 1)}</option>)}
        </select>
        <select
          aria-label={t('Year')}
          className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-sm text-slate-700 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white"
          value={selectedYear}
          onChange={e => setSelectedYear(Number(e.target.value))}
        >
          {years.map(y => <option key={y}>{y}</option>)}
        </select>
        <button type="button" onClick={load} className="ms-auto inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-medium text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5">
          <RefreshCw className="h-3.5 w-3.5" /> {t('Refresh')}
        </button>
      </div>

      {/* ── Setup Wizard (when payroll not yet configured) ── */}
      {!loading && overviewFailed && (
        <div role="alert" className="rounded-xl border border-rose-200 bg-rose-50 px-5 py-4 text-sm text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">
          {t('Payroll overview is unavailable. Totals and period status are not shown because the request failed.')}
        </div>
      )}
      {!loading && periodState === 'no-run' && (
        <div role="status" className="rounded-xl border border-slate-200 bg-slate-50 px-5 py-4 text-sm text-slate-700 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-300">
          {t('No payroll run exists for {period}. Empty totals do not indicate a completed or healthy payroll.', { period: monthLabel(selectedMonth, selectedYear) })}
        </div>
      )}
      {isNotConfigured && readiness && (
        <div className="rounded-xl border border-amber-200 bg-amber-50 px-5 py-4 dark:border-amber-500/20 dark:bg-amber-500/5">
          <div className="mb-3 flex items-center gap-2">
            <AlertTriangle className="h-4 w-4 text-amber-600 dark:text-amber-400" />
            <p className="text-sm font-semibold text-amber-800 dark:text-amber-300">{t('Payroll is not yet configured')}</p>
          </div>
          <p className="mb-4 text-xs text-amber-700 dark:text-amber-400">
            {t('Complete the setup steps below to enable payroll processing. Once configured, this dashboard will show live payroll data.')}
          </p>
          <PayrollSetupWizard readiness={readiness} onNavigate={onNavigate} />
        </div>
      )}

      {/* ── Monthly allowance lines the run could not use: they stop payroll being ready ── */}
      {!loading && readiness?.cashAllowanceIssues && readiness.cashAllowanceIssues.length > 0 && (
        <CashAllowanceIssuesPanel issues={readiness.cashAllowanceIssues} />
      )}

      {/* ── Who can actually be paid (exceptions first) ── */}
      {!loading && readiness?.paymentPrerequisites && (
        <PaymentPrerequisitesPanel prerequisites={readiness.paymentPrerequisites} runStatus={readiness.payrollRunStatus} onNavigate={onNavigate} />
      )}

    <>
      {!loading && overview && (
        <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
          <KpiCard label={t('Active Employees')} value={overview.totalActiveEmployees.toLocaleString()} icon={Users} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
          <KpiCard label={t('Gross Payroll')} value={periodState === 'no-run' ? t('No run') : overviewAmount(overview.totalGrossPayroll)} sub={periodState !== 'no-run' && !overviewCurrency ? t('See each company below') : undefined} icon={WalletCards} color="bg-cyan-100 text-cyan-600 dark:bg-cyan-500/20 dark:text-cyan-400" />
          <KpiCard label={t('Net Payroll')} value={periodState === 'no-run' ? t('No run') : overviewAmount(overview.totalNetPayroll)} sub={periodState !== 'no-run' && !overviewCurrency ? t('See each company below') : undefined} icon={TrendingUp} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
          <KpiCard label={t('Locked Runs YTD')} value={summary?.lockedRuns ?? '—'} icon={Lock} color="bg-violet-100 text-violet-600 dark:bg-violet-500/20 dark:text-violet-400" />
        </div>
      )}

      {loading && (
        <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
          {[0, 1, 2, 3].map(i => <div key={i} className="surface h-20 animate-pulse rounded-xl" />)}
        </div>
      )}

    </>

      {/* ── Insights Panel ── */}
      <AiInsightsPanel readiness={readiness} />

      {/* ── Drill-down panel (when a company row is clicked) ── */}
      {drillDown && (
        <div className="surface overflow-hidden">
          <div className="flex items-center justify-between border-b border-slate-100 px-5 py-3.5 dark:border-white/10">
            <div className="flex items-center gap-2">
              <Building2 className="h-4 w-4 text-sapphire dark:text-cyanAccent" />
              <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{t('{companyName} — Payroll Drill-Down', { companyName: drillDown.companyName })}</h3>
            </div>
            <button type="button" aria-label={t('Close drill-down')} onClick={() => setDrillDown(null)} className="rounded-lg p-1 hover:bg-slate-100 dark:hover:bg-white/10">
              <X className="h-4 w-4 text-slate-400" />
            </button>
          </div>
          <div className="grid grid-cols-2 gap-4 p-5 lg:grid-cols-4">
            <div>
              <p className="text-xs text-slate-400">{t('Active Employees')}</p>
              <p className="text-xl font-bold text-slate-800 dark:text-white">{drillDown.activeEmployees}</p>
              {drillDown.employeesMissingSalary > 0 && <p className="text-xs text-rose-500">{t('{employeesMissingSalary} missing salary', { employeesMissingSalary: drillDown.employeesMissingSalary })}</p>}
            </div>
            <div>
              <p className="text-xs text-slate-400">{t('Gross Payroll')}</p>
              <p className="text-xl font-bold text-slate-800 dark:text-white">{drillDown.hasPayrollRun ? fmtAmt(drillDown.grossPayroll, drillDown.currency) : '—'}</p>
            </div>
            <div>
              <p className="text-xs text-slate-400">{t('Net Payroll')}</p>
              <p className="text-xl font-bold text-emerald-600 dark:text-emerald-400">{drillDown.hasPayrollRun ? fmtAmt(drillDown.netPayroll, drillDown.currency) : '—'}</p>
            </div>
            <div>
              <p className="text-xs text-slate-400">{t('Run Status')}</p>
              {drillDown.payrollRunStatus
                ? <StatusBadge status={drillDown.payrollRunStatus} />
                : <span className="text-sm text-slate-400">{t('Not started')}</span>}
            </div>
          </div>
          <div className="flex flex-wrap gap-2 border-t border-slate-100 px-5 py-3.5 dark:border-white/10">
            {drillDown.validationErrors > 0 && <span className="inline-flex items-center gap-1 rounded-full bg-rose-50 px-3 py-1 text-xs font-medium text-rose-600 dark:bg-rose-500/10 dark:text-rose-400"><AlertTriangle className="h-3 w-3" />{t('{validationErrors} validation error(s)', { validationErrors: drillDown.validationErrors })}</span>}
            {drillDown.validationWarnings > 0 && <span className="inline-flex items-center gap-1 rounded-full bg-amber-50 px-3 py-1 text-xs font-medium text-amber-600 dark:bg-amber-500/10 dark:text-amber-400"><AlertTriangle className="h-3 w-3" />{t('{validationWarnings} warning(s)', { validationWarnings: drillDown.validationWarnings })}</span>}
            {drillDown.pendingApprovals > 0 && <span className="inline-flex items-center gap-1 rounded-full bg-blue-50 px-3 py-1 text-xs font-medium text-blue-600 dark:bg-blue-500/10 dark:text-blue-400"><CheckCircle2 className="h-3 w-3" />{t('{pendingApprovals} pending approval(s)', { pendingApprovals: drillDown.pendingApprovals })}</span>}
            <button type="button" onClick={() => onNavigate('runs')} className="inline-flex items-center gap-1 rounded-full bg-sapphire/10 px-3 py-1 text-xs font-medium text-sapphire hover:bg-sapphire/20 dark:bg-cyanAccent/10 dark:text-cyanAccent">
              {t('Go to Payroll Runs')} <ChevronRight className="h-3 w-3" />
            </button>
          </div>
        </div>
      )}

      {/* ── Company bird's-eye view (All Group Companies) ── */}
      {!loading && overview && selectedCompanyId === 'all' && overview.companies.length > 0 && (
        <CompanyBirdsEyeTable overview={overview} onDrillDown={setDrillDown} />
      )}

      {/* ── Readiness progress (when single company selected) ── */}
      {!loading && readiness && selectedCompanyId !== 'all' && !isNotConfigured && (
        <PayrollSetupWizard readiness={readiness} onNavigate={onNavigate} />
      )}

      {/* ── Quick actions ── */}
      <div className="surface p-5">
        <h3 className="mb-3 text-sm font-semibold text-slate-800 dark:text-white">{t('Quick Actions')}</h3>
        <div className="grid grid-cols-2 gap-2 lg:grid-cols-3">
          {([
            [t('New Payroll Run'), 'runs', Plus],
            [t('Grade Packages'), 'salary-structures', Layers3],
            [t('Employee Salary'), 'employee-salary', Users],
            [t('Validation'), 'validation', AlertTriangle],
            [t('Approvals'), 'approvals', CheckCircle2],
            [t('Anomaly Checks'), 'ai-validation', ShieldCheck],
          ] as [string, Tab, React.ComponentType<{ className?: string }>][]).map(([label, tabKey, Icon]) => (
            <button key={tabKey} type="button" onClick={() => onNavigate(tabKey)}
              className="flex items-center gap-3 rounded-xl border border-slate-200 p-3 text-start hover:border-sapphire/40 hover:bg-sapphire/3 dark:border-white/10 dark:hover:border-cyanAccent/30 dark:hover:bg-cyanAccent/5">
              <div className="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-sapphire/10 dark:bg-cyanAccent/10">
                <Icon className="h-4 w-4 text-sapphire dark:text-cyanAccent" />
              </div>
              <span className="text-sm font-medium text-slate-700 dark:text-slate-300">{label}</span>
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}

// ── Grade packages (read-only) ─────────────────────────────────────────────────
// The grade is the one place a pay package is defined (Setup → Grades); its salary structure is derived from it. This tab
// shows what payroll will use and links to the grade. Older hand-made structures that still have people on them are
// listed apart, read-only — nothing is deleted or rewritten.

function GradePackagesTab() {
  const t = useT();
  const { hasPermission } = useAuth();
  const fmt = useFormat();
  const { locale } = useLocale();
  const [view, setView] = useState<GradePackagesView | null>(null);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setLoading(true); setFailed(false);
    payrollApi.gradePackages()
      .then((v) => { if (!cancelled) setView(v); })
      .catch(() => { if (!cancelled) setFailed(true); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, []);

  const benefitsByGrade = view?.source === 'BenefitsByGrade';
  const editHref = (gradeId: string) => (benefitsByGrade ? '/benefits/by-grade' : `/setup?tab=grades&grade=${encodeURIComponent(gradeId)}`);
  const range = (p: GradePackage) => (p.maxSalary > 0 || p.minSalary > 0
    ? `${fmt.money(p.minSalary, p.currency)} – ${p.maxSalary > 0 ? fmt.money(p.maxSalary, p.currency) : '∞'}`
    : '—');

  return (
    <div className="space-y-4" data-testid="grade-packages">
      <div className="surface flex flex-wrap items-center gap-3 p-4">
        <Layers3 className="h-5 w-5 shrink-0 text-sapphire dark:text-cyanAccent" aria-hidden="true" />
        <p className="min-w-0 flex-1 text-sm text-slate-700 dark:text-slate-300">
          {benefitsByGrade
            ? t('Grade benefits are set in Benefits by grade. This shows the cash lines payroll uses for each grade.')
            : t('Pay packages are set on each grade in Setup. Payroll uses them as they are shown here.')}
        </p>
        {benefitsByGrade && !hasPermission('entitlements.read')
          ? <BenefitsByGradeDoor intro="" />
          : (
            <Link href={benefitsByGrade ? '/benefits/by-grade' : '/setup?tab=grades'} className={btn.ghost}>
              {benefitsByGrade ? t('Open Benefits by grade') : t('Open Setup → Grades')}
            </Link>
          )}
      </div>

      {loading ? <p className="text-sm text-slate-500">{t('Loading…')}</p>
        : failed ? <p role="alert" className="text-sm text-rose-700 dark:text-rose-300">{t('Grade packages could not be loaded.')}</p>
        : !view || view.grades.length === 0 ? (
          <div className="surface flex flex-col items-center py-16 text-center">
            <Layers3 className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" aria-hidden="true" />
            <p className="text-sm font-medium text-slate-700 dark:text-slate-300">{t('No grades yet')}</p>
            <p className="text-xs text-slate-500">{t('Add grades and their pay packages in Setup → Grades.')}</p>
          </div>
        ) : (
          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
            {view.grades.map((p) => (
              <article key={p.gradeId} className="surface flex flex-col p-4" data-testid="grade-package-card">
                <div className="mb-2 flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <h3 className="font-semibold text-slate-900 dark:text-white">{locale === 'ar' && p.gradeNameAr ? p.gradeNameAr : p.gradeName}</h3>
                    <p className="text-xs text-slate-500">{p.gradeCode} · {p.companyName ?? t('Shared by all companies')}</p>
                  </div>
                  {!p.isActive && <span className="rounded bg-slate-100 px-1.5 py-0.5 text-[10px] font-medium text-slate-600 dark:bg-white/10 dark:text-slate-300">{t('Inactive')}</span>}
                </div>
                {p.lines.length === 0 ? <p className="text-xs text-slate-500">{t('No package lines')}</p> : (
                  <ul className="space-y-0.5 text-xs">
                    {p.lines.map((l) => (
                      <li key={l.code} className="flex justify-between gap-2">
                        <span className="min-w-0 break-words text-slate-600 dark:text-slate-300">{l.name}{l.calculationType !== 'Fixed' && l.percentage > 0 ? ` (${fmt.percent(l.percentage, l.percentage % 1 === 0 ? 0 : 2)})` : ''}</span>
                        <span className={`tabular-nums ${l.componentType === 'Deduction' ? 'text-rose-600' : 'text-slate-800 dark:text-slate-200'}`}>{fmt.money(l.periodAmount, p.currency)}</span>
                      </li>
                    ))}
                  </ul>
                )}
                <div className="mt-3 border-t border-slate-100 pt-2 text-xs dark:border-white/10">
                  <p className="flex justify-between font-semibold text-slate-900 dark:text-white"><span>{t('Gross monthly')}</span><span className="tabular-nums" data-testid="grade-package-gross">{fmt.money(p.monthlyGross, p.currency)}</span></p>
                  <p className="text-slate-500">{t('Range {range}', { range: range(p) })}</p>
                  {p.outsideRange && <p className="mt-0.5 flex items-center gap-1 text-amber-800 dark:text-amber-200"><AlertTriangle className="h-3.5 w-3.5" aria-hidden="true" /> {t("Outside the grade's range")}</p>}
                  <p className="text-slate-500">{t('{count, plural, one {# employee} other {# employees}}', { count: p.employeeCount })}</p>
                  <p className="text-slate-500">{p.structureCode ? t('Salary structure {code} · version {version}', { code: p.structureCode, version: p.structureVersion ?? 1 }) : t('No salary structure yet')}</p>
                </div>
                <div className="mt-3 flex justify-end">
                  {benefitsByGrade && !hasPermission('entitlements.read')
                    ? null
                    : <Link href={editHref(p.gradeId)} className={btn.sm}><Pencil className="h-3.5 w-3.5" aria-hidden="true" /> {t('Edit in Setup')}</Link>}
                </div>
              </article>
            ))}
          </div>
        )}

      {view && view.olderStructures.length > 0 && (
        <section className="space-y-2" data-testid="older-structures">
          <h3 className="text-sm font-semibold text-slate-800 dark:text-white">{t('Older structures (read-only)')}</h3>
          <p className="text-xs text-slate-500">{t("These structures are not a grade's package but still have employees on them. They keep working and are not changed. Move people onto a grade to retire them.")}</p>
          <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
            {view.olderStructures.map((s) => (
              <div key={s.id} className="surface p-3 text-xs">
                <p className="font-semibold text-slate-900 dark:text-white">{s.name}</p>
                <p className="text-slate-500">{s.code} · {s.companyName ?? t('Shared by all companies')}</p>
                <p className="mt-1 text-slate-600 dark:text-slate-300">
                  {t('{count, plural, one {# component} other {# components}}', { count: s.components?.length ?? 0 })} · {t('{count, plural, one {# employee} other {# employees}}', { count: s.assignedEmployeeCount ?? 0 })}
                </p>
              </div>
            ))}
          </div>
        </section>
      )}
    </div>
  );
}

// ── Employee Salary Setup Tab ───────────────────────────────────────────────────

/** Salary changes wait here for a Finance approver who is neither the person who proposed them nor the employee. */
function SalaryChangesPanel({ onApplied }: { onApplied: () => void }) {
  const t = useT();
  const [rows, setRows] = useState<SalaryChange[]>([]);
  const [notice, setNotice] = useState('');
  // A change that cannot be approved as it stands (409 salary_change_stale) is explained on its own row, in the server's words.
  // `reduced`: the refusal was a pay reduction without the employee's agreement reference, with what goes down.
  const [stale, setStale] = useState<{ id: string; message: string; reduced?: ReductionRefusal } | null>(null);
  const load = () => { payrollApi.listSalaryChanges('Pending').then(setRows).catch(() => setRows([])); };
  useEffect(load, []);
  const decide = async (id: string, fn: () => Promise<unknown>) => {
    setNotice(''); setStale(null);
    try { await fn(); load(); onApplied(); }
    catch (e: unknown) {
      const reduced = reductionRefusal(e);
      if (reduced?.kind === 'required') { setStale({ id, message: reduced.message, reduced }); return; }
      const refusal = allowanceRefusal(e);
      if (refusal?.code === 'salary_change_stale') { setStale({ id, message: refusal.message }); return; }
      setNotice((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? t('Could not record the decision.'));
    }
  };
  if (rows.length === 0 && !notice) return null;
  return (
    <section className="surface space-y-2 p-4" aria-label={t('Pending Finance approval')}>
      <h3 className="text-sm font-semibold text-slate-900 dark:text-white">{t('Pending Finance approval')}</h3>
      {notice && <p role="alert" className="text-sm text-rose-600">{notice}</p>}
      <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
        {rows.map(r => (
          <li key={r.id} className="flex flex-wrap items-center gap-3 py-2 text-sm" data-testid="salary-change-row">
            <span className="font-medium">{r.employeeName || `#${r.employeeId}`}</span>
            <span className="text-slate-500">{r.previousBasicSalary != null ? `${r.previousBasicSalary.toLocaleString()} → ` : ''}{r.basicSalary.toLocaleString()} {r.currency}</span>
            <span className="text-xs text-slate-400">{t('Proposed by')} {r.requestedByName}</span>
            <span className="ms-auto flex gap-2">
              <button type="button" className={btn.primary} onClick={() => decide(r.id, () => payrollApi.approveSalaryChange(r.id))}>{t('Approve')}</button>
              <button type="button" className={btn.ghost} onClick={() => decide(r.id, () => payrollApi.rejectSalaryChange(r.id))}>{t('Reject')}</button>
            </span>
            {stale?.id === r.id && (
              <div role="alert" className="basis-full rounded-lg border border-rose-300 bg-rose-50 p-3 text-sm text-rose-800 dark:border-rose-500/40 dark:bg-rose-500/10 dark:text-rose-200" data-testid="salary-change-stale">
                <p className="font-semibold">{stale.reduced
                  ? t('This change lowers the employee’s pay but has no written agreement reference, so it was not approved. Nothing was paid or changed.')
                  : t('This change is out of date, so it was not approved. Nothing was paid or changed.')}</p>
                {stale.reduced && <p className="mt-1">{t('Reject this change, or ask for it to be proposed again with the reference.')}</p>}
                {stale.message && <p className="mt-1">{stale.message}</p>}
              </div>
            )}
            <div className="basis-full space-y-2 empty:hidden">
              <SalaryChangeMoney change={r} />
              <SalaryAllowanceComparison current={r.currentAllowances} proposed={r.proposedAllowances} carriedForward={r.allowancesCarriedForward ?? false} currency={r.currency} basic={r.basicSalary} />
            </div>
          </li>
        ))}
      </ul>
    </section>
  );
}

/** Finance's reading of a salary change: a reduction first (what goes down, the agreement reference), current against proposed monthly pay, and the contract wage note. */
function SalaryChangeMoney({ change: r }: { change: SalaryChange }) {
  const { t, locale } = useLocale();
  const lineLabel = (code: string) => {
    const hit = [...(r.proposedAllowances ?? []), ...(r.currentAllowances ?? [])].find(l => l.code.toUpperCase() === code.toUpperCase());
    return lineName({ code, nameEn: hit?.nameEn, nameAr: hit?.nameAr }, [], locale);
  };
  const hasPay = r.currentMonthlyPay != null && r.proposedMonthlyPay != null && (r.currentMonthlyPay > 0 || r.proposedMonthlyPay > 0);
  if (!r.isReduction && !hasPay && !r.changesContractWage) return null;
  return (
    <div className="space-y-2" data-testid="salary-change-money">
      {r.isReduction && <SalaryReductionSummary items={r.reducedItems ?? []} currentTotal={r.currentMonthlyPay} proposedTotal={r.proposedMonthlyPay}
        currency={r.currency} reference={r.reductionConsentReference} lineLabel={lineLabel} />}
      {hasPay && <p className="text-sm text-slate-700 dark:text-slate-200" data-testid="salary-change-monthly-pay">
        {t('Monthly pay: {current} now, {proposed} proposed.', {
          current: isolateLtr(formatAmount(r.currentMonthlyPay!, r.currency, locale)), proposed: isolateLtr(formatAmount(r.proposedMonthlyPay!, r.currency, locale)),
        })}
      </p>}
      {r.changesContractWage && <ContractWageNote />}
    </div>
  );
}

function AssignSalaryModal({ structures, onClose, onSaved }: { structures: SalaryStructure[]; onClose: () => void; onSaved: () => void }) {
  const t = useT();
  const { locale } = useLocale();
  const today = new Date().toISOString().slice(0, 10);
  const { currencyCode } = useTenantSettings();
  const [form, setForm] = useState({
    employeeId: '', salaryStructureId: '', basicSalary: '', housingAllowance: '',
    transportAllowance: '', foodAllowance: '', mobileAllowance: '', otherAllowance: '',
    fixedDeduction: '', effectiveDate: today, currency: '',
  });
  const effectiveCurrency = form.currency || currencyCode;
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const set = (k: keyof typeof form, v: string) => setForm(f => ({ ...f, [k]: v }));

  // Monthly cash allowances. Untouched (null) sends no `allowances`, so the employee's current ones carry forward; only an edit sends a list.
  const { catalog, retry: retryCatalog } = useCashAllowanceCatalog();
  const [allowances, setAllowances] = useState<AllowanceValue>(null);
  const [showAllowanceProblems, setShowAllowanceProblems] = useState(false);
  const [allowanceServerProblem, setAllowanceServerProblem] = useState<string | null>(null);
  // The lines on the employee's salary now (what untouched means), read once an employee id is entered from the salary list, which carries
  // each record's lines already named (so no employee read and no catalogue access is needed). null = not known: the list masks them
  // (viewer may not see amounts), the read failed, or the server predates the field; untouched then means "left as they are".
  const [currentLines, setCurrentLines] = useState<AllowanceDraftLine[] | null>(null);
  // The employee's salary records, to tell whether the proposal lowers the pay (it then needs the employee's written agreement).
  const [records, setRecords] = useState<EmployeeSalaryStructure[]>([]);
  const [reference, setReference] = useState('');
  const [serverReduction, setServerReduction] = useState<ReductionRefusal | null>(null);
  const [showReferenceProblem, setShowReferenceProblem] = useState(false);
  const [referenceFocus, setReferenceFocus] = useState(0);
  useEffect(() => {
    setAllowances(null); setCurrentLines(null); setRecords([]); setAllowanceServerProblem(null); setServerReduction(null); setShowReferenceProblem(false);
    const id = Number(form.employeeId);
    if (!form.employeeId || !Number.isInteger(id) || id <= 0) return;
    let live = true;
    const timer = setTimeout(() => {
      payrollApi.listEmployeeSalaryStructures(id).then(list => { if (live) { setCurrentLines(currentRecordLines(list)); setRecords(list); } }, () => undefined);
    }, 400);
    return () => { live = false; clearTimeout(timer); };
  }, [form.employeeId]);
  const basicNumber = form.basicSalary.trim() === '' ? undefined : Number(form.basicSalary);
  const shownAllowances = shownLines(currentLines ?? [], allowances);
  const allowanceTotal = totalMonthly(shownAllowances, basicNumber);
  const reduction = useMemo(() => proposalReduction(recordInForce(records, form.effectiveDate), {
    basicSalary: Number(form.basicSalary) || 0, housingAllowance: Number(form.housingAllowance) || 0, transportAllowance: Number(form.transportAllowance) || 0,
    foodAllowance: Number(form.foodAllowance) || 0, mobileAllowance: Number(form.mobileAllowance) || 0, otherAllowance: Number(form.otherAllowance) || 0,
  }, allowances), [records, form.effectiveDate, form.basicSalary, form.housingAllowance, form.transportAllowance, form.foodAllowance, form.mobileAllowance, form.otherAllowance, allowances]);
  // The form's own check, or the server's refusal when the form could not tell (masked amounts, an older record).
  const lowersPay = reduction.isReduction || serverReduction?.kind === 'required';
  const reducedItems = reduction.isReduction ? reduction.items : serverReduction?.items ?? [];
  const referenceProblem = !showReferenceProblem ? null
    : lowersPay && !reference.trim() ? t('Enter the employee’s written agreement reference before saving.')
    : consentReferenceProblem(reference) ? t(consentReferenceProblem(reference)!) : null;
  const lineLabel = (code: string) => lineName(currentLines?.find(l => l.code.toUpperCase() === code.toUpperCase()) ?? { code },
    catalog.status === 'ready' ? catalog.choices : [], locale);

  const save = async () => {
    if (!form.employeeId || !form.basicSalary) { setError(t('Employee ID and basic salary are required.')); return; }
    if (Object.keys(allowanceProblems(shownAllowances, basicNumber)).length > 0) {
      setShowAllowanceProblems(true); setError(t('Fix the allowance lines that show a message before saving.')); return;
    }
    // Lowering the pay needs the employee's written agreement: say so here rather than after the server refuses.
    const blocked = reductionBlock({ isReduction: lowersPay }, reference);
    if (blocked) { setShowReferenceProblem(true); setReferenceFocus(n => n + 1); setError(t(blocked)); return; }
    setSaving(true); setError(''); setAllowanceServerProblem(null);
    try {
      await payrollApi.assignEmployeeSalary({
        // Blank: the employee's grade package structure (or DIRECT without a grade), resolved by the server.
        employeeId: Number(form.employeeId), salaryStructureId: form.salaryStructureId || null,
        basicSalary: Number(form.basicSalary), housingAllowance: Number(form.housingAllowance) || 0,
        transportAllowance: Number(form.transportAllowance) || 0, foodAllowance: Number(form.foodAllowance) || 0,
        mobileAllowance: Number(form.mobileAllowance) || 0, otherAllowance: Number(form.otherAllowance) || 0,
        fixedDeduction: Number(form.fixedDeduction) || 0, effectiveDate: form.effectiveDate, currency: form.currency,
        // Left out entirely when untouched: [] would remove every allowance.
        allowances: allowancesForRequest(allowances),
        reductionConsentReference: consentReferenceForRequest(reference, lowersPay),
      });
      onSaved();
    } catch (e: unknown) {
      const reduced = reductionRefusal(e);
      if (reduced) {
        setServerReduction(reduced.kind === 'required' ? reduced : serverReduction); setShowReferenceProblem(true); setReferenceFocus(n => n + 1);
        setError(reduced.message || t('Lowering an employee’s pay needs their written agreement; enter its reference.')); setSaving(false); return;
      }
      const refusal = allowanceRefusal(e);
      if (refusal && refusal.code !== 'salary_change_stale') { setAllowanceServerProblem(refusal.message); setError(t('The salary was not saved.')); setShowAllowanceProblems(true); }
      else setError(apiErrorReason(e, t('Save failed.')));
      setSaving(false);
    }
  };

  const gross = ['basicSalary', 'housingAllowance', 'transportAllowance', 'foodAllowance', 'mobileAllowance', 'otherAllowance']
    .reduce((sum, k) => sum + (Number(form[k as keyof typeof form]) || 0), 0) + allowanceTotal;

  return (
    <Modal title={t('Assign Salary to Employee')} onClose={onClose} wide>
      <div className="space-y-4">
        <div className="grid grid-cols-2 gap-3">
          <Field label={t('Employee ID *')}><input type="number" aria-label={t('Employee ID')} className={inp} value={form.employeeId} onChange={e => set('employeeId', e.target.value)} /></Field>
          <Field label={t('Salary structure')}>
            <select aria-label={t('Salary structure')} className={sel} value={form.salaryStructureId} onChange={e => set('salaryStructureId', e.target.value)}>
              <option value="">{t("The employee's grade package (automatic)")}</option>
              {structures.map(s => <option key={s.id} value={s.id}>{s.name} ({s.currency})</option>)}
            </select>
          </Field>
        </div>

        <div className="rounded-lg border border-slate-200 p-4 dark:border-white/10">
          <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-500">{t('Earnings')}</p>
          <div className="grid grid-cols-3 gap-3">
            {[
              ['basicSalary', t('Basic Salary *')], ['housingAllowance', t('Housing Allowance')],
              ['transportAllowance', t('Transport Allowance')], ['foodAllowance', t('Food Allowance')],
              ['mobileAllowance', t('Mobile Allowance')], ['otherAllowance', t('Other Allowances')],
            ].map(([k, label]) => (
              <Field key={k} label={label}><input type="number" step="0.01" aria-label={label} className={inp} value={form[k as keyof typeof form]} onChange={e => set(k as keyof typeof form, e.target.value)} /></Field>
            ))}
          </div>
          <div className="mt-3 rounded-lg bg-slate-50 px-4 py-2.5 dark:bg-white/5">
            <p className="text-sm font-semibold text-sapphire dark:text-cyanAccent">{t('Gross Total: {currency} {toLocaleString}', { currency: form.currency, toLocaleString: gross.toLocaleString('en-US', { minimumFractionDigits: 2 }) })}</p>
            {allowanceTotal > 0 && <p className="text-xs text-slate-600 dark:text-slate-300" data-testid="gross-includes-allowances">{t('Includes {amount} a month in other monthly allowances.', { amount: formatAmount(allowanceTotal, effectiveCurrency, locale) })}</p>}
          </div>
        </div>

        <SalaryAllowancesEditor idPrefix="assign-salary-allowances" baseline={currentLines ?? []} baselineKind="record" baselineKnown={currentLines !== null}
          value={allowances} onChange={v => { setAllowances(v); setAllowanceServerProblem(null); }} basic={basicNumber} currency={effectiveCurrency}
          catalog={catalog} onRetry={retryCatalog} showProblems={showAllowanceProblems} serverProblem={allowanceServerProblem} />

        {lowersPay && <SalaryReductionNotice idPrefix="assign-salary-reduction" items={reducedItems} currentTotal={reduction.isReduction ? reduction.currentTotal : undefined}
          proposedTotal={reduction.isReduction ? reduction.proposedTotal : undefined} currency={effectiveCurrency} reference={reference}
          onReference={setReference} problem={referenceProblem} focusToken={referenceFocus} lineLabel={lineLabel} />}

        <div className="grid grid-cols-3 gap-3">
          <Field label={t('Fixed Deduction')} info={t('A fixed amount removed every month (e.g. accommodation recovery), in the salary currency. Numbers only.')} infoKey="payroll.fixed_deduction"><input type="number" step="0.01" aria-label={t('Fixed deduction')} className={inp} value={form.fixedDeduction} onChange={e => set('fixedDeduction', e.target.value)} /></Field>
          <Field label={t('Effective Date')}><input type="date" aria-label={t('Effective date')} className={inp} value={form.effectiveDate} onChange={e => set('effectiveDate', e.target.value)} /></Field>
          <Field label={t('Currency')}>
            <select aria-label={t('Currency')} className={sel} value={effectiveCurrency} onChange={e => set('currency', e.target.value)}>
              {['USD', 'GBP', 'EUR', 'CAD', 'AUD', 'SGD', 'AED', 'SAR', 'QAR', 'KWD', 'BHD', 'OMR', 'EGP', 'INR', 'PKR', 'PHP', 'JOD', 'ZAR', 'NGN', 'KES'].map(c => <option key={c}>{c}</option>)}
            </select>
          </Field>
        </div>

        {error && <p className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2 pt-2">
          <button type="button" className={btn.ghost} onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className={btn.primary} onClick={save} disabled={saving}>{saving ? t('Saving…') : t('Assign Salary')}</button>
        </div>
      </div>
    </Modal>
  );
}

function EmployeeSalaryTab() {
  const [assignments, setAssignments] = useState<EmployeeSalaryStructure[]>([]);
  const [structures, setStructures] = useState<SalaryStructure[]>([]);
  const [loading, setLoading] = useState(true);
  const [empIdFilter, setEmpIdFilter] = useState('');
  const [showAssign, setShowAssign] = useState(false);
  const [submitted, setSubmitted] = useState(false);
  const t = useT();
  const fmtDate = useFmtDate();

  const load = () => {
    setLoading(true);
    Promise.all([
      payrollApi.listEmployeeSalaryStructures(empIdFilter ? Number(empIdFilter) : undefined).then(setAssignments),
      payrollApi.listSalaryStructures().then(setStructures),
    ]).finally(() => setLoading(false));
  };
  useEffect(load, []);

  const structName = (id: string) => structures.find(s => s.id === id)?.name ?? id.slice(0, 8);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <input type="number" aria-label={t('Employee ID filter')} placeholder={t('Filter by Employee ID')} className={`${inp} max-w-52`} value={empIdFilter} onChange={e => setEmpIdFilter(e.target.value)} />
        <button type="button" className={`${btn.ghost} whitespace-nowrap`} onClick={load}>{t('Search')}</button>
        <button type="button" className={`ms-auto whitespace-nowrap ${btn.primary}`} onClick={() => setShowAssign(true)}><Plus className="h-4 w-4 shrink-0" /> {t('Assign Salary')}</button>
      </div>
      {submitted && (
        <p role="status" className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-2.5 text-sm text-amber-800 dark:border-amber-500/30 dark:bg-amber-500/10 dark:text-amber-300">
          {t('Submitted for Finance approval. The new salary applies once a Finance approver, other than you, confirms it.')}
        </p>
      )}
      <SecondApproverNotice />
      <SalaryChangesPanel onApplied={load} />
      {loading ? <p className="text-sm text-slate-400">{t('Loading…')}</p> : assignments.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <Users className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">{t('No salary assignments found')}</p>
        </div>
      ) : (
        <div className="surface overflow-hidden">
          <table className="w-full min-w-[640px] text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {[t('Employee'), t('Structure'), t('Basic'), t('Housing'), t('Transport'), t('Gross'), t('Deduction'), t('Effective'), t('Status')].map(h => (
                  <th key={h} className="px-3 py-2 text-start text-xs font-bold uppercase text-fg-muted">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {assignments.map(a => {
                const gross = a.basicSalary + a.housingAllowance + a.transportAllowance + a.foodAllowance + a.mobileAllowance + a.otherAllowance;
                return (
                  <tr key={a.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                    <td className="px-3 py-1.5 font-medium text-slate-900 dark:text-white">{t('Emp #{employeeId}', { employeeId: a.employeeId })}</td>
                    <td className="px-3 py-1.5 text-slate-500">{structName(a.salaryStructureId)}</td>
                    <td className="px-3 py-1.5 text-end text-slate-700 dark:text-slate-300">{a.basicSalary.toLocaleString()}</td>
                    <td className="px-3 py-1.5 text-end text-slate-700 dark:text-slate-300">{a.housingAllowance.toLocaleString()}</td>
                    <td className="px-3 py-1.5 text-end text-slate-700 dark:text-slate-300">{a.transportAllowance.toLocaleString()}</td>
                    <td className="px-3 py-1.5 text-end font-semibold text-slate-900 dark:text-white">{gross.toLocaleString()}</td>
                    <td className="px-3 py-1.5 text-end text-rose-500">{a.fixedDeduction > 0 ? `(${a.fixedDeduction.toLocaleString()})` : '—'}</td>
                    <td className="px-3 py-1.5 text-xs text-slate-400">{fmtDate(a.effectiveDate)}</td>
                    <td className="px-3 py-1.5"><span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${a.isActive ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400' : 'bg-slate-100 text-slate-500'}`}>{a.isActive ? t('Active') : t('Inactive')}</span></td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      {showAssign && <AssignSalaryModal structures={structures} onClose={() => setShowAssign(false)} onSaved={() => { setShowAssign(false); setSubmitted(true); load(); }} />}
    </div>
  );
}

// ── Payroll Runs Tab ────────────────────────────────────────────────────────────

function RunsTab({ onSelectRun, focusRunId, onFocusRun, version, onRunsChanged }: {
  onSelectRun: (run: PayrollRun, tab: Tab) => void;
  /** The run the pipeline above has in focus; opening it here keeps both in step. */
  focusRunId?: string;
  onFocusRun: (runId: string) => void;
  /** Bumped by the page when a run changed elsewhere (the pipeline processed or locked one). */
  version: number;
  onRunsChanged: () => void;
}) {
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [selectedRunId, setSelectedRunId] = useState<string | null>(null);
  const [slips, setSlips] = useState<PayrollSlip[]>([]);
  const [slipsLoading, setSlipsLoading] = useState(false);
  const [slipsError, setSlipsError] = useState('');
  const [showCreate, setShowCreate] = useState(false);
  // Monotonic counter: only the response matching the latest fetch ID is applied.
  // Prevents stale responses from a slow previous fetch overwriting current slips.
  const slipsFetchSeq = useRef(0);
  const [createYear, setCreateYear] = useState(new Date().getFullYear());
  const [createMonth, setCreateMonth] = useState(new Date().getMonth() + 1);
  const [createCompanyId, setCreateCompanyId] = useState('');
  const [companies, setCompanies] = useState<{ id: string; name: string }[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  // Reopen and Void both existed on the API with no way to reach them: a run that needed correcting
  // could only be corrected by a consultant with a REST client.
  const [reopening, setReopening] = useState<PayrollRun | null>(null);
  const [voiding, setVoiding] = useState<PayrollRun | null>(null);
  // Process refused because attendance deductions no longer match their days (ERR_ATTENDANCE_IMPACT_STALE): the one
  // next action is the recompute on the Validation tab.
  const [staleAttendanceRun, setStaleAttendanceRun] = useState<PayrollRun | null>(null);
  const t = useT();
  const monthLabel = useMonthLabel();
  const { confirm, dialog: confirmDialog } = useConfirm();
  // Cost accounting v1: for an Approved run of a company with costing on, Lock becomes "Review & lock" (the preview owns the lock).
  // A company whose switch cannot be read keeps today's Lock: the server still decides.
  const [costingCompanies, setCostingCompanies] = useState<Set<string>>(new Set());
  const [costingChecked, setCostingChecked] = useState(false);

  // A failed load is shown as a failure, never as "No runs yet" (which invites creating a duplicate run).
  const [runsError, setRunsError] = useState<unknown>(null);
  const load = () => {
    setLoading(true);
    payrollApi.listAllRuns()
      .then(all => { setRuns(all); setTotal(all.length); setRunsError(null); })
      .catch(e => { setRuns([]); setTotal(0); setRunsError(e); })
      .finally(() => setLoading(false));
  };
  const approvedCompanyKey = [...new Set(runs.filter(r => r.status === 'Approved' && r.companyId).map(r => r.companyId as string))].sort().join(',');
  useEffect(() => {
    if (!approvedCompanyKey) { setCostingChecked(true); return; }
    let active = true;
    Promise.all(approvedCompanyKey.split(',').map(id =>
      financeGlApi.costAccountingReadiness(id).then(r => (r.settings.enabled ? id : null)).catch(() => null)))
      .then(ids => { if (active) { setCostingCompanies(new Set(ids.filter((id): id is string => !!id))); setCostingChecked(true); } });
    return () => { active = false; };
  }, [approvedCompanyKey]);
  useEffect(() => {
    load();
    payrollApi.listCompanies().then(cs => {
      setCompanies(cs.map(c => ({ id: c.id, name: c.name || c.tradeName })));
      if (cs.length === 1) setCreateCompanyId(cs[0].id);
    }).catch(() => {});
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Derive selectedRun from the runs list so the header always reflects the
  // latest server state (e.g. after Process updates totals in setRuns).
  const selectedRun = runs.find(r => r.id === selectedRunId) ?? null;

  // A run changed elsewhere (the pipeline processed or locked it): read the list and the open register again.
  const seenVersion = useRef(version);
  useEffect(() => {
    if (seenVersion.current === version) return;
    seenVersion.current = version;
    load();
    const open = runs.find(r => r.id === selectedRunId);
    if (open) openSlips(open);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [version]);

  // The pipeline's run opens here too.
  useEffect(() => {
    if (!focusRunId || focusRunId === selectedRunId) return;
    const run = runs.find(r => r.id === focusRunId);
    if (run) openSlips(run);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [focusRunId, runs]);

  const openSlips = (run: PayrollRun) => {
    onFocusRun(run.id);
    setSelectedRunId(run.id);
    setSlips([]);
    setSlipsError('');
    setSlipsLoading(true);
    const seq = ++slipsFetchSeq.current;
    // Every slip, page by page: one page of 200 left a larger run's register silently short.
    payrollApi.allSlips(run.id)
      .then(all => { if (slipsFetchSeq.current === seq) setSlips(all); })
      .catch(e => { if (slipsFetchSeq.current === seq) setSlipsError(t('Slips could not be loaded. {requestFailureReason}', { requestFailureReason: requestFailureReason(e) })); })
      .finally(() => { if (slipsFetchSeq.current === seq) setSlipsLoading(false); });
  };

  const createRun = async () => {
    setSaving(true); setError('');
    try {
      await payrollApi.createRun(createYear, createMonth, createCompanyId || undefined);
      setShowCreate(false); load();
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? t('Failed to create payroll run.'));
    } finally { setSaving(false); }
  };

  const processRun = async (id: string) => {
    setError('');
    setStaleAttendanceRun(null);
    try {
      // Processing runs as a background job: queueing it returns at once, and the pipeline above shows its
      // progress and reloads this list when it finishes. See api/payrollProcessing.ts.
      await startRunProcessing(id);
      onFocusRun(id);
      onRunsChanged();
    } catch (e) {
      const data = (e as { response?: { data?: { message?: string; code?: string } } })?.response?.data;
      if (data?.code === 'ERR_ATTENDANCE_IMPACT_STALE') setStaleAttendanceRun(runs.find(r => r.id === id) ?? null);
      setError(data?.message ?? t('Failed to process the payroll run. Check that the company has a country code and a statutory pack configured.'));
    }
  };

  // Locking posts the run's GL journal. A double-click sent two requests: the server posts once and
  // refuses the second, but that refusal then showed as an error under a run that had just locked.
  // The summary is read and confirmed in the app's confirm dialog first (hooks/useLockWithSummary).
  const { requestLock, lockDialog, lockingRunId: lockingId } = useLockWithSummary();
  const lockRun = async (id: string) => {
    setError('');
    const outcome = await requestLock(id);
    if (outcome.status === 'locked') {
      setRuns(rs => rs.map(r => r.id === id ? outcome.run : r));
      onRunsChanged();
    } else if (outcome.status === 'failed') setError(outcome.message);
  };

  const deleteRun = async (id: string) => {
    if (!(await confirm({
      title: t('Delete this draft payroll run?'),
      message: t('It is permanently removed. Processed or locked runs cannot be deleted; void them instead.'),
      confirmLabel: t('Delete run'),
    }))) return;
    setError('');
    try {
      await payrollApi.deleteRun(id);
      setRuns(rs => rs.filter(r => r.id !== id));
      if (selectedRunId === id) { setSelectedRunId(null); setSlips([]); }
    } catch (e) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? t('Failed to delete the payroll run.'));
    }
  };

  const fmt = (n: number) => n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

  return (
    <div className="space-y-4">
      {lockDialog}
      {confirmDialog}
      <div className="flex items-center justify-between">
        <p className="text-sm text-slate-500">{t('{count, plural, one {# payroll run} other {# payroll runs}}', { count: total })}</p>
        <button type="button" className={btn.primary} onClick={() => { setShowCreate(true); setError(''); }}><Plus className="h-4 w-4" /> {t('New Payroll Run')}</button>
      </div>

      {!showCreate && error && (
        <div className="flex items-start gap-2 rounded-lg bg-rose-50 px-4 py-2.5 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-400">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{error}</span>
          {staleAttendanceRun && (
            <button type="button" className={`${btn.sm} ms-auto shrink-0`} data-testid="open-attendance-recompute"
              onClick={() => onSelectRun(staleAttendanceRun, 'validation')}>
              {t('Review attendance and recompute')}
            </button>
          )}
        </div>
      )}

      <div className="grid gap-4 lg:grid-cols-3">
        <div className="surface overflow-hidden">
          <div className="border-b border-slate-100 px-4 py-3 dark:border-white/[0.07]">
            <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Payroll Runs')}</p>
          </div>
          {loading && <div className="flex justify-center py-10"><div className="h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div>}
          {!loading && runsError != null && <LoadFailedNotice error={runsError} onRetry={load} />}
          {!loading && runsError == null && runs.length === 0 && <p className="py-10 text-center text-sm text-slate-400">{t('No runs yet. Create one above.')}</p>}
          <div className="divide-y divide-slate-100 dark:divide-white/[0.05]">
            {runs.map(run => (
              <div key={run.id} role="button" tabIndex={0} onClick={() => openSlips(run)}
                onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openSlips(run); } }}
                className={`w-full cursor-pointer px-4 py-3 text-start transition hover:bg-slate-50 dark:hover:bg-white/[0.03] ${selectedRun?.id === run.id ? 'bg-sapphire/5 dark:bg-sapphire/10' : ''}`}>
                <div className="flex items-center justify-between">
                  <p className="font-semibold text-slate-900 dark:text-white">{monthLabel(run.month, run.year)}</p>
                  <StatusBadge status={run.status} />
                </div>
                {companies.length > 1 && run.companyId && (
                  <p className="mt-0.5 text-xs font-medium text-slate-600 dark:text-slate-300">{companies.find(c => c.id === run.companyId)?.name ?? ''}</p>
                )}
                <p className="mt-1 text-xs text-slate-500">{t('{employeeCount} employees · Net {totalNetSalary}', { employeeCount: run.employeeCount, totalNetSalary: fmt(run.totalNetSalary) })}</p>
                {/* A Voided run is finished: there is nothing left to process or validate, and a re-process would be refused.
                    A Locked or Paid run is past Process: it has its own action row below, so it never shows both. */}
                {run.status !== 'Locked' && run.status !== 'Paid' && run.status !== 'Voided' && (
                  <div className="mt-2 flex gap-1">
                    <button type="button" onClick={e => { e.stopPropagation(); processRun(run.id); }} className={`${btn.sm} h-6 px-2 text-xs`}>
                      <Play className="h-3 w-3" /> {t('Process')}
                    </button>
                    {run.status === 'Processed' && (
                      <button type="button" onClick={e => { e.stopPropagation(); onSelectRun(run, 'approvals'); }} className={`${btn.sm} h-6 px-2 text-xs`}>
                        <CheckCircle2 className="h-3 w-3" /> {t('Approve')}
                      </button>
                    )}
                    {run.status === 'Approved' && run.companyId && costingCompanies.has(run.companyId) && (
                      <button type="button" data-testid={`cc-lock-review-${run.id}`} onClick={e => { e.stopPropagation(); onSelectRun(run, 'gl-journal'); }} className={`${btn.sm} h-6 px-2 text-xs`}>
                        <Lock className="h-3 w-3" /> {t('Review & lock')}
                      </button>
                    )}
                    {run.status === 'Approved' && !(run.companyId && costingCompanies.has(run.companyId)) && (
                      <button type="button" onClick={e => { e.stopPropagation(); lockRun(run.id); }} disabled={lockingId !== null || !costingChecked} className={`${btn.sm} h-6 px-2 text-xs`}>
                        <Lock className="h-3 w-3" /> {lockingId === run.id ? t('Locking…') : t('Lock')}
                      </button>
                    )}
                    <button type="button" onClick={e => { e.stopPropagation(); onSelectRun(run, 'validation'); }} className={`${btn.sm} h-6 px-2 text-xs`}>
                      <AlertTriangle className="h-3 w-3" /> {t('Validate')}
                    </button>
                    {/* Reopen releases the loan installments, attendance/leave/overtime impacts and
                        bonuses a run consumed. The backend refuses once any Payroll GL row exists,
                        so a Locked/Paid run is deliberately not offered it. */}
                    {(run.status === 'Processed' || run.status === 'PendingFinanceReview' || run.status === 'Approved') && (
                      <button type="button" onClick={e => { e.stopPropagation(); setReopening(run); }} className={`${btn.sm} h-6 px-2 text-xs`}>
                        <RotateCcw className="h-3 w-3" /> {t('Reopen')}
                      </button>
                    )}
                    {run.status === 'Draft' && (
                      <button type="button" onClick={e => { e.stopPropagation(); deleteRun(run.id); }}
                        aria-label={t('Delete draft run')}
                        className="inline-flex h-6 items-center gap-1 rounded-md px-2 text-xs font-medium text-rose-500 hover:bg-rose-50 dark:hover:bg-rose-500/10">
                        <Trash2 className="h-3 w-3" /> {t('Delete')}
                      </button>
                    )}
                    {run.status !== 'Draft' && run.status !== 'Voided' && (
                      <button type="button" onClick={e => { e.stopPropagation(); setVoiding(run); }}
                        aria-label={t('Void run')}
                        className="inline-flex h-6 items-center gap-1 rounded-md px-2 text-xs font-medium text-rose-500 hover:bg-rose-50 dark:hover:bg-rose-500/10">
                        <X className="h-3 w-3" /> {t('Void')}
                      </button>
                    )}
                  </div>
                )}
                {/* A Locked or Paid run is financially live; voiding it writes GL contra-entries and
                    is the only correction route left, so the action must stay reachable. */}
                {(run.status === 'Locked' || run.status === 'Paid') && (
                  <div className="mt-2 flex gap-1">
                    <button type="button" onClick={e => { e.stopPropagation(); onSelectRun(run, 'validation'); }} className={`${btn.sm} h-6 px-2 text-xs`}>
                      <AlertTriangle className="h-3 w-3" /> {t('Validate')}
                    </button>
                    <button type="button" onClick={e => { e.stopPropagation(); setVoiding(run); }}
                      aria-label={t('Void run')}
                      className="inline-flex h-6 items-center gap-1 rounded-md px-2 text-xs font-medium text-rose-500 hover:bg-rose-50 dark:hover:bg-rose-500/10">
                      <X className="h-3 w-3" /> {t('Void')}
                    </button>
                  </div>
                )}
              </div>
            ))}
          </div>
        </div>

        <div className="surface overflow-hidden lg:col-span-2">
          {!selectedRun ? (
            <div className="flex flex-col items-center justify-center py-24 text-center">
              <FileText className="mb-3 h-10 w-10 text-slate-200 dark:text-slate-700" />
              <p className="text-sm text-slate-400">{t('Select a payroll run to view salary slips')}</p>
            </div>
          ) : (
            <>
              <div className="flex items-center justify-between border-b border-slate-100 px-4 py-3 dark:border-white/[0.07]">
                <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Slips — {period}', { period: monthLabel(selectedRun.month, selectedRun.year) })}</p>
                <div className="flex items-center gap-3 text-xs text-slate-500">
                  <span>{t('Gross')}: <span className="font-semibold text-slate-900 dark:text-white">{fmt(selectedRun.totalGrossSalary)}</span></span>
                  <span>{t('Net')}: <span className="font-bold text-emerald-600 dark:text-emerald-400">{fmt(selectedRun.totalNetSalary)}</span></span>
                </div>
              </div>
              {slipsLoading ? <div className="flex justify-center py-12"><div className="h-6 w-6 animate-spin rounded-full border-2 border-sapphire border-t-transparent" /></div> : (
                <div className="overflow-x-auto">
                  <table data-testid="run-slip-register" className="w-full min-w-[560px] text-sm">
                    <thead>
                      <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                        {/* Loans sits AFTER Deductions and says "of which", because the EMI is already
                            inside the Deductions total (PayrollController.cs:2635). Side by side, both
                            bracketed, the two columns read as two separate subtractions. */}
                        {[t('Employee'), t('Dept'), t('Basic'), t('Gross'), t('Deductions'), t('Of which: Loans'), t('Net'), t('YTD Gross'), t('YTD Net')].map(h => (
                          <th key={h} className="px-3 py-3 text-start text-xs font-bold uppercase text-fg-muted">{h}</th>
                        ))}
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                      {slipsError && <tr><td colSpan={9} role="alert" className="py-10 text-center text-sm text-rose-600 dark:text-rose-400">{slipsError}</td></tr>}
                      {!slipsError && slips.length === 0 && <tr><td colSpan={9} className="py-10 text-center text-sm text-slate-400">{selectedRun.status === 'Draft' ? t('No slips. Click Process to generate.') : t('No payslips came back for this run. Reload the page, or open Payslips.')}</td></tr>}
                      {slips.map(s => (
                        <tr key={s.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                          <td className="px-3 py-2.5">
                            <p className="font-medium text-slate-900 dark:text-white">{s.employeeName}</p>
                            <p className="text-xs text-slate-400">{s.employeeCode}</p>
                          </td>
                          <td className="px-3 py-2.5 text-xs text-slate-500">{s.department || '—'}</td>
                          <td className="px-3 py-2.5 text-end text-slate-700 dark:text-slate-300">
                            {fmt(s.basicSalary)}
                            {/* POD-C3 — a prorated wage must never look like an unexplained halving. */}
                            {s.prorationFactor != null && s.prorationFactor !== 1 && (
                              <p className="text-[10px] leading-tight text-sky-600 dark:text-sky-400">
                                {s.paidDays}/{s.prorationDenominatorDays} d
                                {s.isFinalWageMonth ? ` · ${t('Final month')}` : s.paidFromDate ? ` · ${t('From {date}', { date: s.paidFromDate })}` : ''}
                              </p>
                            )}
                          </td>
                          <td className="px-3 py-2.5 text-end font-semibold text-slate-900 dark:text-white">
                            {fmt(s.grossSalary)}
                            {s.arrearsAmount != null && s.arrearsAmount > 0 && (
                              <p className="text-[10px] font-normal leading-tight text-violet-600 dark:text-violet-400">
                                {t('Incl. arrears {arrearsAmount}', { arrearsAmount: fmt(s.arrearsAmount) })}
                              </p>
                            )}
                          </td>
                          <td className="px-3 py-2.5 text-end text-rose-500">
                            ({fmt(s.deductions)})
                            {s.employeeStatutoryTotal > 0 && (
                              <p className="text-[10px] leading-tight text-rose-400">{t('Of which GOSI {employeeStatutoryTotal}', { employeeStatutoryTotal: fmt(s.employeeStatutoryTotal) })}</p>
                            )}
                            {/* F02 — which cohort and rate basis produced the statutory lines, read off the slip. */}
                            {s.statutoryBasis && <GosiBasisNote cohort={s.gosiCohort} basis={s.statutoryBasis} />}
                          </td>
                          {/* Unbracketed: a slice of the cell to its left, not a further subtraction. */}
                          <td className="px-3 py-2.5 text-end text-amber-600 dark:text-amber-400">{s.loanDeductions > 0 ? fmt(s.loanDeductions) : '—'}</td>
                          <td className="px-3 py-2.5 text-end font-bold text-emerald-600 dark:text-emerald-400">{fmt(s.netSalary)}</td>
                          <td className="px-3 py-2.5 text-end text-xs text-slate-500">{fmt(s.ytdGross)}</td>
                          <td className="px-3 py-2.5 text-end text-xs font-semibold text-slate-700 dark:text-slate-300">{fmt(s.ytdNet)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
              <RunDeductionsReview runId={selectedRun.id} />
            </>
          )}
        </div>
      </div>

      {showCreate && (
        <Modal title={t('New Payroll Run')} onClose={() => setShowCreate(false)}>
          <div className="space-y-4">
            {error && <p className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-600 dark:bg-rose-500/10 dark:text-rose-400">{error}</p>}
            <div className="grid grid-cols-2 gap-3">
              <Field label={t('Year')} info={t('The calendar year this payroll run covers, e.g. 2026.')} infoKey="payroll.run_year">
                <input type="number" aria-label={t('Year')} className={inp} value={createYear} onChange={e => setCreateYear(Number(e.target.value))} min={2020} max={new Date().getFullYear() + 1} />
              </Field>
              <Field label={t('Month')} info={t('The salary month to process. One run per month — duplicates are rejected.')} infoKey="payroll.run_month">
                <select aria-label={t('Month')} className={sel} value={createMonth} onChange={e => setCreateMonth(Number(e.target.value))}>
                  {MONTHS.map((m, i) => <option key={m} value={i + 1}>{monthLabel(i + 1)}</option>)}
                </select>
              </Field>
            </div>
            {companies.length > 0 && (
              <Field label={t('Company')} info={t('The company whose country pack (GOSI/GPSSA/GRSIA) will be applied. Required for statutory deductions.')} infoKey="payroll.run_company">
                <select aria-label={t('Company')} className={sel} value={createCompanyId} onChange={e => setCreateCompanyId(e.target.value)}>
                  {companies.length > 1 && <option value="">{t('— select company —')}</option>}
                  {companies.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
                </select>
              </Field>
            )}
            <div className="flex justify-end gap-2">
              <button type="button" className={btn.ghost} onClick={() => setShowCreate(false)}>{t('Cancel')}</button>
              <button type="button" className={btn.primary} onClick={createRun} disabled={saving}>{saving ? t('Creating…') : t('Create Run')}</button>
            </div>
          </div>
        </Modal>
      )}

      {reopening && (
        <ReasonActionModal
          title={t('Reopen {period}', { period: monthLabel(reopening.month, reopening.year) })}
          confirmLabel={t('Reopen run')}
          minLength={1}
          blurb={t('Reopening returns the run to Draft and releases everything it consumed: loan and advance installments, attendance, leave and overtime impacts, and bonuses. Any validation overrides recorded on it are discarded, so a judgement made earlier must be re-entered. It is refused outright once the run has posted to the general ledger.')}
          onClose={() => setReopening(null)}
          onSubmit={async reason => { await payrollApi.reopenRun(reopening.id, reason); setReopening(null); load(); }}
        />
      )}

      {voiding && (
        <VoidRunDialog
          run={voiding}
          onClose={() => setVoiding(null)}
          onVoided={() => { setVoiding(null); load(); }}
        />
      )}
    </div>
  );
}

/** Void: the reason, plus the cash elections the server asks for once money has left the bank (see VoidRunForm). */
function VoidRunDialog({ run, onClose, onVoided }: { run: PayrollRun; onClose: () => void; onVoided: () => void }) {
  const t = useT();
  const { locale } = useLocale();
  const { resolution } = useRunCurrency(run);
  return (
    <Modal title={t('Void {period}', { period: periodLabel(`${run.year}-${String(run.month).padStart(2, '0')}`, locale) })} onClose={onClose}>
      <VoidRunForm
        currency={resolution.status === 'resolved' ? resolution.currency : null}
        onClose={onClose}
        onSubmit={async (reason, options) => { await payrollApi.voidRun(run.id, reason, options); onVoided(); }}
      />
    </Modal>
  );
}

/**
 * One dialog for the irreversible run actions whose whole control is "say why". The reason is
 * mandatory server-side; this collects it and surfaces the server's refusal verbatim rather than
 * guessing at which of the endpoint's many preconditions failed.
 */
function ReasonActionModal({ title, blurb, confirmLabel, minLength, danger, onClose, onSubmit }: {
  title: string;
  blurb: string;
  confirmLabel: string;
  minLength: number;
  danger?: boolean;
  onClose: () => void;
  onSubmit: (reason: string) => Promise<void>;
}) {
  const t = useT();
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const submit = async () => {
    setBusy(true); setError('');
    try {
      await onSubmit(reason.trim());
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? t('The action was refused.'));
      setBusy(false);
    }
  };

  return (
    <Modal title={title} onClose={onClose}>
      <div className="space-y-4">
        <p className="text-xs leading-5 text-slate-600 dark:text-slate-400">{blurb}</p>
        <Field label={t('Reason (required)')}>
          <textarea aria-label={t('Reason')} className={inp} rows={3} value={reason} onChange={e => setReason(e.target.value)} placeholder={t('Recorded against your identity on the audit trail.')} />
        </Field>
        {error && <p role="alert" className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2">
          <button type="button" className={btn.ghost} onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className={danger ? btn.danger : btn.primary} onClick={submit} disabled={busy || reason.trim().length < minLength}>
            {busy ? t('Working…') : confirmLabel}
          </button>
        </div>
      </div>
    </Modal>
  );
}

// ── Audit Integrity Tab ─────────────────────────────────────────────────────────
//
// Two tamper-evident hash chains exist — the payroll chain (POD-A3) and the tenant-wide audit
// chain — each with a verifier endpoint, and until now neither had a caller. The product could
// prove its own audit trail had not been altered and no human could ask it to. A 403 here is
// information, not an error: the Payroll Manager/Officer roles that CREATE payroll-audit events
// are deliberately barred from attesting their own integrity.

function AuditIntegrityTab() {
  const t = useT();
  return (
    <div className="space-y-4">
      <div className="surface p-4">
        <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Tamper-evident audit chains')}</p>
        <p className="mt-1 text-xs leading-5 text-slate-500 dark:text-slate-400">
          {t('Every payroll decision and every security event is written into a hash chain: each entry seals the hash of the one before it. Altering or removing a historical row breaks the chain at that point and every point after it. These two checks walk each chain end to end and report the first break. Verification is restricted to Admin and the read-only Auditor role — the roles that create the entries cannot attest to them.')}
        </p>
      </div>
      <ChainVerifier
        title={t('Payroll audit chain')}
        description={t('Run created, processed, validated, overridden, approved, locked, voided, paid — POD-A3.')}
        allowedRoles={t('Admin · Auditor')}
        verify={() => payrollApi.auditIntegrity()}
      />
      <ChainVerifier
        title={t('Tenant audit chain')}
        description={t('The tenant-wide security and configuration trail.')}
        allowedRoles={t('Admin')}
        verify={() => identityAuditApi.integrity()}
      />
    </div>
  );
}

function ChainVerifier({ title, description, allowedRoles, verify }: {
  title: string;
  description: string;
  allowedRoles: string;
  verify: () => Promise<AuditIntegrityReport>;
}) {
  const t = useT();
  const fmtDate = useFmtDate();
  const [report, setReport] = useState<AuditIntegrityReport | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  const run = async () => {
    setBusy(true); setError(''); setReport(null);
    try {
      setReport(await verify());
    } catch (e: unknown) {
      const status = (e as { response?: { status?: number } })?.response?.status;
      setError(status === 403 || status === 401
        ? t('Your role cannot verify this chain. Verification is restricted to {allowedRoles} — by design, the roles that write these entries cannot attest to them.', { allowedRoles })
        : (e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? t('The verification could not be run.'));
    } finally { setBusy(false); }
  };

  return (
    <div className="surface p-5 space-y-3">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{title}</p>
          <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">{description}</p>
        </div>
        <button type="button" className={btn.primary} onClick={run} disabled={busy}>
          <Shield className="h-4 w-4" />
          {busy ? t('Verifying…') : t('Verify chain')}
        </button>
      </div>

      {error && (
        <p role="alert" className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-xs text-amber-800 dark:border-amber-400/20 dark:bg-amber-500/10 dark:text-amber-300">
          {error}
        </p>
      )}

      {report && (
        <div
          className={`rounded-lg border p-3 ${report.isValid
            ? 'border-emerald-200 bg-emerald-50 dark:border-emerald-400/20 dark:bg-emerald-500/10'
            : 'border-rose-200 bg-rose-50 dark:border-rose-400/20 dark:bg-rose-500/10'}`}
        >
          <p className={`flex items-center gap-2 text-sm font-bold ${report.isValid ? 'text-emerald-800 dark:text-emerald-300' : 'text-rose-800 dark:text-rose-300'}`}>
            {report.isValid ? <CheckCircle2 className="h-4 w-4" /> : <AlertTriangle className="h-4 w-4" />}
            {report.isValid
              ? t('Intact — {checkedEntries} entries verified.', { checkedEntries: report.checkedEntries })
              : t('Broken — {length} break(s) across {checkedEntries} entries.', { length: report.failures.length, checkedEntries: report.checkedEntries })}
          </p>
          <p className={`mt-1 text-xs ${report.isValid ? 'text-emerald-700 dark:text-emerald-400' : 'text-rose-700 dark:text-rose-400'}`}>
            {report.checkedEntries === 0
              ? t('There is nothing on this chain yet.')
              : `${fmtDate(report.firstEntryUtc)} → ${fmtDate(report.lastEntryUtc)}`}
          </p>
          {report.failures.length > 0 && (
            <ul className="mt-2 max-h-56 space-y-1 overflow-y-auto">
              {report.failures.map(f => (
                <li key={`${f.auditLogId}-${f.reason}`} className="rounded border border-black/5 bg-white/60 px-2 py-1.5 text-xs text-rose-700 dark:border-white/5 dark:bg-black/20 dark:text-rose-400">
                  <span className="block font-mono font-semibold">{f.reason}</span>
                  <span className="block opacity-80">{f.action} · {f.entityName} · {fmtDate(f.createdAtUtc)}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}

// ── Validation Tab ──────────────────────────────────────────────────────────────

/** Mirrors PayrollValidationOverridePolicy.MinimumReasonLength. "ok" is not accountability. */
const MIN_OVERRIDE_REASON = 10;

/** F02 — PayrollValidationEngine.GosiCohortNotRecorded / GosiNewEntrantScheduleNotModelled /
 *  GosiNewEntrantScheduleApplied / GosiBelowMinimumContributoryWage. */
const GOSI_COHORT_NOT_RECORDED = 'GOSI_COHORT_NOT_RECORDED';
const GOSI_NEW_ENTRANT_BLOCKED = 'GOSI_NEW_ENTRANT_SCHEDULE_NOT_MODELLED';
const GOSI_NEW_ENTRANT_APPLIED = 'GOSI_NEW_ENTRANT_SCHEDULE_APPLIED';
const GOSI_BELOW_MINIMUM_WAGE = 'GOSI_BELOW_MINIMUM_CONTRIBUTORY_WAGE';
/** PayrollValidationEngine.ZeroNetUnpaidLeave — a Warning, acknowledged by count at approval. */
const ZERO_NET_UNPAID_LEAVE = 'ZERO_NET_UNPAID_LEAVE';

/**
 * The run a payroll tab works on. The page holds ONE selected run (the pipeline's Run selector and the tabs' own selectors are the same choice):
 * a tab opens on it, follows it when it changes elsewhere, and reports its own choice back, so the pipeline above never reads
 * "Choose a run…" while a tab below shows a run (and the other way round).
 */
function useSharedRun(selectedRunId: string | undefined, onRunChange?: (id: string | null) => void): [string, (id: string) => void] {
  const [runId, setLocal] = useState(selectedRunId ?? '');
  // The run bar is the source of truth: clearing it there clears it here too.
  useEffect(() => { setLocal(selectedRunId ?? ''); }, [selectedRunId]);
  const setRunId = React.useCallback((id: string) => { setLocal(id); onRunChange?.(id || null); }, [onRunChange]);
  return [runId, setRunId];
}

function ValidationTab({ selectedRunId, onRunChange }: { selectedRunId?: string; onRunChange?: (id: string | null) => void }) {
  const t = useT();
  // The run bar's run (no picker in this section).
  const [runId] = useSharedRun(selectedRunId, onRunChange);
  const [results, setResults] = useState<PayrollValidationResult[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [overridableCodes, setOverridableCodes] = useState<string[]>([]);
  const [overriding, setOverriding] = useState<PayrollValidationResult | null>(null);
  const names = useRunEmployeeLabels(runId);
  // The chosen run's status tells a never-validated Draft from a run with no saved issues.
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  useEffect(() => { payrollApi.listAllRuns().then(setRuns).catch(() => {}); }, [runId]);


  useEffect(() => {
    if (!runId) { setResults([]); setError(''); return; }
    setLoading(true); setError('');
    payrollApi.listValidationResults(runId)
      .then(setResults)
      .catch((e: unknown) => setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? t('Unable to load saved validation results.')))
      .finally(() => setLoading(false));
  }, [runId, t]);

  // The published overridable list comes from the server — a default-deny policy the client must
  // not restate. An unclassified code is NOT overridable, so offering the button would be a lie.
  useEffect(() => {
    setOverriding(null);
    if (!runId) { setOverridableCodes([]); return; }
    payrollApi.runValidationOverrides(runId)
      .then(r => setOverridableCodes(r.overridableCodes))
      .catch(() => setOverridableCodes([]));
  }, [runId]);

  const validate = async () => {
    if (!runId) return;
    setLoading(true); setError('');
    payrollApi.validateRun(runId).then(r => {
      setResults(Array.isArray(r) ? r : (r as { warnings?: PayrollValidationResult[] }).warnings ?? []);
    }).catch((e: unknown) => {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? t('Payroll validation failed.'));
    }).finally(() => setLoading(false));
  };


  const panelState = validationPanelState({ runId, status: runs.find(r => r.id === runId)?.status, loading, failed: error !== '' });
  const warnings = results.filter(r => r.severity === 'Warning');
  const errors = results.filter(r => r.severity === 'Error');
  // F02 — the GOSI entrant cohort is judged per employee; group it so it reads as one decision, not N rows.
  const cohortNotRecorded = results.filter(r => r.code === GOSI_COHORT_NOT_RECORDED);
  const cohortBlocked = results.filter(r => r.code === GOSI_NEW_ENTRANT_BLOCKED);
  const cohortApplied = results.filter(r => r.code === GOSI_NEW_ENTRANT_APPLIED);
  const belowMinimumWage = results.filter(r => r.code === GOSI_BELOW_MINIMUM_WAGE);
  const zeroNetLeave = results.filter(r => r.code === ZERO_NET_UNPAID_LEAVE);
  const [cohortOnly, setCohortOnly] = useState(false);
  const shown = cohortOnly && cohortBlocked.length + cohortNotRecorded.length + cohortApplied.length > 0
    ? [...cohortBlocked, ...cohortNotRecorded, ...cohortApplied]
    : results;

  return (
    <div className="space-y-4">
      {/* The run is the run bar's (shared by every Payroll section): no second picker here. */}
      {runId ? (
        <div className="flex flex-wrap items-center gap-3">
          <button type="button" className={`${btn.primary} whitespace-nowrap max-sm:min-h-11 max-sm:w-full max-sm:justify-center`} onClick={validate} disabled={loading || panelState === 'not_run'}
            title={panelState === 'not_run' ? t('Process the run before validating it.') : undefined}>
            {loading ? t('Validating…') : t('Run Validation')}
          </button>
          {/* The counts share the button's row: one slim toolbar, so the issue list starts in the first screenful. */}
          {results.length > 0 && (
            <>
              <CountChip label={t('Total Issues')} value={results.length} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
              <CountChip label={t('Warnings')} value={warnings.length} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
              <CountChip label={t('Errors')} value={errors.length} color="bg-rose-100 text-rose-600 dark:bg-rose-500/20 dark:text-rose-400" />
            </>
          )}
        </div>
      ) : <NoRunChosen />}

      {error && <div className="rounded-lg bg-rose-50 px-4 py-2.5 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-400">{error}</div>}

      {runId && <AttendanceReconciliationPanel runId={runId} />}

      {(cohortNotRecorded.length > 0 || cohortBlocked.length > 0 || cohortApplied.length > 0 || belowMinimumWage.length > 0) && (
        <div className="surface space-y-2 px-5 py-4" data-testid="gosi-cohort-findings">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('GOSI cohort — checked per employee')}</p>
            <button type="button" className={`${btn.sm} h-7 px-2 text-xs`} onClick={() => setCohortOnly(v => !v)} aria-pressed={cohortOnly}>
              {cohortOnly ? t('Show all findings') : t('Show only these employees')}
            </button>
          </div>
          {cohortBlocked.length > 0 && (
            <p className="text-sm text-rose-700 dark:text-rose-400">
              {t('{count, plural, one {# employee first registered} other {# employees first registered}} with GOSI on or after 3 July 2024, but their GOSI on this run was not computed on the new-entrant schedule, so approval is blocked. Reopen the run and process it again; correct the date first if it is wrong.', { count: cohortBlocked.length })}
            </p>
          )}
          {cohortApplied.length > 0 && (
            <p className="text-sm text-sky-700 dark:text-sky-400" data-testid="gosi-new-entrant-applied">
              {t('{count} new entrant(s) (first registered with GOSI on or after 3 July 2024) are on the new-entrant annuities schedule of the New Social Insurance Law (Royal Decree M/273). Each finding states the rate used. No action needed.', { count: cohortApplied.length })}
            </p>
          )}
          {belowMinimumWage.length > 0 && (
            <p className="text-sm text-amber-700 dark:text-amber-400" data-testid="gosi-below-minimum-wage">
              {t('{count} employee(s) are below the GOSI minimum contributory wage. GOSI may assess on the minimum; payroll still calculated on the actual wage. This does not block the run.', { count: belowMinimumWage.length })}
            </p>
          )}
          {cohortNotRecorded.length > 0 && (
            <p className="text-sm text-amber-700 dark:text-amber-400">
              {t('{count, plural, one {# employee has} other {# employees have}} no GOSI first-registration date, so their contribution basis is unverified (computed on the pre-3-July-2024 schedule). Record each date on the employee’s Payroll tab (approval required); runs processed afterwards use it.', { count: cohortNotRecorded.length })}
            </p>
          )}
        </div>
      )}

      {zeroNetLeave.length > 0 && (
        <div className="surface space-y-1 px-5 py-4" data-testid="zero-net-unpaid-leave-findings">
          <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Zero net pay from a full month of unpaid leave')}</p>
          <p className="text-sm text-amber-700 dark:text-amber-400">
            {t('{count} employee(s) net zero this month: unpaid leave took the whole wage. Any loan or advance instalment they owe is carried forward to the next run, not written off. Nothing is paid to them, so they are left out of the bank file. This is a warning, not a block: check the leave, then acknowledge it when approving.', { count: zeroNetLeave.length })}
          </p>
        </div>
      )}

      {results.length === 0 && !loading ? (
        panelState === 'unavailable' ? null : (
        <div className="surface flex flex-col items-center py-16 text-center" data-testid="validation-empty" data-state={panelState}>
          <CheckCircle2 className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">
            {panelState === 'not_run' ? t('Validation has not been run yet. Process the run first, then run validation.')
              : runId ? t('No saved validation issues. Run validation to refresh the current checks.') : t('Select a run and click validate')}
          </p>
        </div>
        )
      ) : (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5" data-testid="validation-results">
          {shown.map(r => (
            <div key={r.id} className="flex items-start gap-3 px-5 py-3">
              <div className={`mt-0.5 h-2 w-2 shrink-0 rounded-full ${r.severity === 'Error' ? 'bg-rose-500' : r.severity === 'Warning' ? 'bg-amber-500' : 'bg-sky-400'}`} />
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2">
                  <StatusBadge status={r.severity} />
                  <span className="font-mono text-xs text-slate-500">{r.code}</span>
                  {r.employeeId && <span className="text-xs text-slate-400"><EmployeeLabelText label={employeeLabel(r.employeeId, names, id => t('Emp #{employeeId}', { employeeId: id }))} /></span>}
                </div>
                <p className="mt-1 text-sm text-slate-700 dark:text-slate-300">
                  {r.code === ZERO_NET_UNPAID_LEAVE
                    ? t('Net pay is zero: unpaid leave took the whole wage this month. Any loan or advance instalment is carried forward to the next run, not written off.')
                    : r.message}
                </p>
              </div>
              {r.isResolved
                ? <span className="ms-auto shrink-0 rounded bg-emerald-50 px-1.5 py-0.5 text-[10px] font-medium text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400">{t('Overridden')}</span>
                : r.severity === 'Error' && overridableCodes.includes(r.code)
                  ? (
                    <button
                      type="button"
                      className={`${btn.sm} ms-auto h-7 shrink-0 px-2 text-xs`}
                      onClick={() => setOverriding(r)}
                    >
                      {t('Override…')}
                    </button>
                  )
                  : r.severity === 'Error'
                    ? (
                      <span
                        className="ms-auto shrink-0 rounded bg-slate-100 px-1.5 py-0.5 text-[10px] font-medium text-slate-500 dark:bg-white/10 dark:text-slate-400"
                        title={t('This code is not on the published overridable list. Fix the underlying data and re-validate.')}
                      >
                        {t('Must be fixed')}
                      </span>
                    )
                    : null}
            </div>
          ))}
        </div>
      )}

      {overriding && (
        <OverrideValidationModal
          runId={runId}
          result={overriding}
          onClose={() => setOverriding(null)}
          onDone={() => {
            setOverriding(null);
            payrollApi.listValidationResults(runId).then(setResults).catch(() => {});
          }}
        />
      )}
    </div>
  );
}

/**
 * Clearing ONE blocking compliance error. The backend is the control, not this dialog: it enforces
 * the published overridable list, a substantive reason, and maker-checker on identity. This just
 * makes the control reachable and reports its refusals in the operator's own words — it deliberately
 * does not pre-validate away a refusal the server is entitled to make.
 */
function OverrideValidationModal({ runId, result, onClose, onDone }: {
  runId: string;
  result: PayrollValidationResult;
  onClose: () => void;
  onDone: () => void;
}) {
  const t = useT();
  const [reason, setReason] = useState('');
  const [documentReference, setDocumentReference] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  // The deduction-limit override must rest on a named written basis (no legal conclusion is drawn here).
  const needsDocument = result.code === 'DEDUCTIONS_EXCEED_HALF_WAGE';
  const tooShort = reason.trim().length < MIN_OVERRIDE_REASON || (needsDocument && !documentReference.trim());

  const submit = async () => {
    setSaving(true); setError('');
    try {
      await payrollApi.resolveValidationResult(runId, result.id, reason.trim(), needsDocument ? documentReference.trim() : undefined);
      onDone();
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? t('The override was refused.'));
    } finally { setSaving(false); }
  };

  return (
    <Modal title={t('Override a blocking compliance error')} onClose={onClose}>
      <div className="space-y-4">
        <div className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-xs dark:border-rose-400/20 dark:bg-rose-500/10">
          <p className="font-mono font-semibold text-rose-800 dark:text-rose-300">{result.code}</p>
          <p className="mt-1 text-rose-700 dark:text-rose-400">{result.message}</p>
        </div>
        <p className="text-xs leading-5 text-slate-600 dark:text-slate-400">
          {t('This error blocks Approve and Lock. Clearing it is a judgement you are making on the record: your identity and this reason are written to the tamper-evident payroll audit chain, and the approver of this run will be required to acknowledge the override before they can approve it.')}
        </p>
        <Field label={t('Reason (at least {MIN_OVERRIDE_REASON} characters)', { MIN_OVERRIDE_REASON })}>
          <textarea
            aria-label={t('Override reason')}
            className={inp}
            rows={3}
            value={reason}
            onChange={e => setReason(e.target.value)}
            placeholder={t('Why is this acceptable for this run?')}
          />
        </Field>
        {needsDocument && (
          <Field label={t('Decision or document reference (required)')}>
            <input
              aria-label={t('Decision or document reference')}
              className={inp}
              value={documentReference}
              onChange={e => setDocumentReference(e.target.value)}
              placeholder={t('Labour court / commission decision or other written basis — its reference number')}
            />
          </Field>
        )}
        {error && <p role="alert" className="text-xs text-rose-500">{error}</p>}
        <div className="flex justify-end gap-2">
          <button type="button" className={btn.ghost} onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className={btn.danger} onClick={submit} disabled={saving || tooShort}>
            {saving ? t('Recording…') : t('Override on the record')}
          </button>
        </div>
      </div>
    </Modal>
  );
}

// ── Approvals Tab ───────────────────────────────────────────────────────────────

/**
 * A cash count, rendered. The backend refuses the approval unless the approver states a number it
 * already knows — the point being that stating it requires having LOOKED. So the list comes first
 * and the checkbox carries the number in its own words; there is no "acknowledge all" shortcut and
 * the count is never pre-ticked.
 */
function AcknowledgementPanel({
  tone, title, blurb, rows, checked, onChange, confirmLabel, ackId,
}: {
  tone: 'amber' | 'rose';
  /** Unique checkbox id when more than one panel of a tone is on screen. */
  ackId?: string;
  title: string;
  blurb: string;
  rows: { key: string; primary: string; secondary: string }[];
  checked: boolean;
  onChange: (v: boolean) => void;
  confirmLabel: string;
}) {
  const shell = tone === 'amber'
    ? 'border-amber-200 bg-amber-50 dark:border-amber-400/20 dark:bg-amber-500/10'
    : 'border-rose-200 bg-rose-50 dark:border-rose-400/20 dark:bg-rose-500/10';
  const heading = tone === 'amber'
    ? 'text-amber-800 dark:text-amber-300'
    : 'text-rose-800 dark:text-rose-300';
  const body = tone === 'amber'
    ? 'text-amber-700 dark:text-amber-400'
    : 'text-rose-700 dark:text-rose-400';
  const inputId = ackId ?? `ack-${tone}`;

  return (
    <div className={`rounded-lg border p-3 ${shell}`}>
      <div className="flex items-start gap-2">
        <AlertTriangle className={`mt-0.5 h-4 w-4 shrink-0 ${heading}`} aria-hidden="true" />
        <div className="min-w-0 flex-1">
          <p className={`text-xs font-bold ${heading}`}>{title}</p>
          <p className={`mt-0.5 text-xs leading-5 ${body}`}>{blurb}</p>

          <ul className="mt-2 max-h-48 space-y-1 overflow-y-auto">
            {rows.map(r => (
              <li key={r.key} className={`rounded border border-black/5 bg-white/60 px-2 py-1.5 text-xs dark:border-white/5 dark:bg-black/20 ${body}`}>
                <span className="block font-semibold">{r.primary}</span>
                {r.secondary ? <span className="block opacity-80">{r.secondary}</span> : null}
              </li>
            ))}
          </ul>

          <label htmlFor={inputId} className={`mt-3 flex cursor-pointer items-start gap-2 text-xs font-semibold ${heading}`}>
            <input
              id={inputId}
              type="checkbox"
              checked={checked}
              onChange={e => onChange(e.target.checked)}
              className="mt-0.5 h-4 w-4 shrink-0 rounded border-slate-300 text-sapphire focus:ring-sapphire dark:border-slate-600 dark:bg-slate-800"
            />
            <span>{confirmLabel}</span>
          </label>
        </div>
      </div>
    </div>
  );
}

/**
 * Everyone in the company the run does NOT pay, by reason. Read-only: a left-out person is a decision already
 * recorded elsewhere (a status, an offboarding, a selection), so it is shown, counted and never hidden — the old
 * report stopped at 25 names and skipped most statuses.
 */
function LeftOutPanel({ entries }: { entries: PayrollPopulationEntry[] }) {
  const t = useT();
  const fmtDate = useFmtDate();
  const groups = leftOutGroups(entries);
  return (
    <div className="rounded-lg border border-slate-200 bg-slate-50 p-3 dark:border-white/10 dark:bg-white/5" data-testid="payroll-left-out">
      <p className="text-xs font-semibold text-slate-800 dark:text-slate-200">
        {t('{leftOutCount} employee(s) in this company are not paid by this run', { leftOutCount: entries.length })}
      </p>
      <p className="mt-0.5 text-xs leading-5 text-slate-600 dark:text-slate-400">
        {t('Everyone the run leaves out is listed with the reason. Nobody is dropped without one.')}
      </p>
      <ul className="mt-2 space-y-1">
        {groups.map(g => (
          <li key={g.reasonCode} className="text-xs text-slate-700 dark:text-slate-300">
            <details>
              <summary className="cursor-pointer">
                <span className="font-semibold">{payPopulationReasonLabel(t, g.reasonCode)}</span>
                <span className="ms-1 tabular-nums">({g.entries.length})</span>
              </summary>
              <ul className="mt-1 max-h-40 space-y-0.5 overflow-y-auto ps-4">
                {g.entries.map(e => (
                  <li key={`${e.employeeId}-${e.reasonCode}`}>
                    {`${e.code} — ${e.name}`}
                    <span className="ms-1 opacity-70">
                      {e.date ? t('Last working day {date}', { date: fmtDate(e.date) }) : t('Status: {status}', { status: t(e.status) })}
                    </span>
                  </li>
                ))}
              </ul>
            </details>
          </li>
        ))}
      </ul>
    </div>
  );
}

// ── Run currency ──────────────────────────────────────────────────────────────
// A run is denominated in its employing company's currency, never the tenant display default. Every
// view that prints one run's amounts resolves it here; until it is confirmed the amounts carry no
// currency label (and the approval view withholds approval).

function useRunCurrency(run: PayrollRun | undefined) {
  const t = useT();
  const fmtAmt = useFmtAmt();
  const { companies, state: companiesState, retry } = usePayrollCompanies();
  const resolution = resolvePayrollRunCurrency(run, companies, companiesState);
  const amount = (n: number) => resolution.status === 'resolved'
    ? fmtAmt(n, resolution.currency)
    : unconfirmedAmount(n, t);
  return { resolution, companiesState, companies, amount, retry };
}

const unconfirmedAmount = (n: number, t: (key: string) => string) =>
  `${n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} (${t('Currency not confirmed')})`;

/**
 * R03 — amounts in several currencies, one line per currency. Used where a total spans runs or batches
 * of different companies; the amounts are never added across currencies.
 */
function perCurrency(totals: Array<{ currency: string | null; total: number }>, fmtAmt: (n: number, currency?: string) => string, t: (key: string) => string) {
  return totals.map(row => row.currency ? fmtAmt(row.total, row.currency) : unconfirmedAmount(row.total, t)).join(' · ');
}

function RunCurrencyNotice({ resolution, companiesState, onRetry, blocksApproval }: {
  resolution: ReturnType<typeof useRunCurrency>['resolution'];
  companiesState: CompaniesLoadState;
  onRetry: () => void;
  blocksApproval?: boolean;
}) {
  const t = useT();
  if (resolution.status === 'loading') return (
    <p className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400">
      <RefreshCw className="h-3.5 w-3.5 animate-spin" />
      {t('Confirming the employing company\'s currency…')}
    </p>
  );
  if (resolution.status !== 'unavailable') return null;
  return (
    <div role="alert" className="flex flex-wrap items-start gap-3 rounded-lg border border-rose-200 bg-rose-50 p-3 text-xs text-rose-700 dark:border-rose-400/20 dark:bg-rose-500/10 dark:text-rose-400">
      <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
      <span className="min-w-0 flex-1">{resolution.reason}{blocksApproval ? ` ${t('Approval is unavailable until the currency is confirmed.')}` : ''}</span>
      {companiesState === 'failed' && (
        <button type="button" className={btn.sm} onClick={onRetry}>
          <RefreshCw className="h-3.5 w-3.5" /> {t('Retry')}
        </button>
      )}
    </div>
  );
}

function ApprovalsTab({ selectedRunId, onRunChange, isAdmin, isFinance, isHROrPayroll, onReconNotReviewed }: {
  selectedRunId?: string;
  onRunChange?: (id: string | null) => void;
  isAdmin: boolean;
  isFinance: boolean;
  isHROrPayroll: boolean;
  /** Approve was refused because the reconciliation has no current sign-off: the page reopens the review. */
  onReconNotReviewed?: () => void;
}) {
  const { hasPermission } = useAuth();
  const t = useT();
  const fmtDate = useFmtDate();
  const monthLabel = useMonthLabel();
  const [reconNotice, setReconNotice] = useState('');
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  // The run bar's run (no picker in this section).
  const [runId] = useSharedRun(selectedRunId, onRunChange);
  const [approvals, setApprovals] = useState<PayrollApproval[]>([]);
  const [notes, setNotes] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  // ── The acknowledgement gate ───────────────────────────────────────────────
  // The backend refuses approval with 409 whenever the run deliberately excludes an employee, or
  // carries a compliance error somebody consciously overrode, unless the approver STATES the count.
  // The client used to send neither, so the first run with one exclusion could not be approved from
  // the UI at all. Both are cash counts: the approver must see WHO and WHY, then affirm the number.
  const [population, setPopulation] = useState<PayrollRunPopulation | null>(null);
  const [overrideReport, setOverrideReport] = useState<PayrollValidationOverrideReport | null>(null);
  const [gateLoading, setGateLoading] = useState(false);
  const [gateError, setGateError] = useState('');
  const [ackExcluded, setAckExcluded] = useState(false);
  const [ackOverridden, setAckOverridden] = useState(false);
  const [ackOutside, setAckOutside] = useState(false);
  const [ackPayWarnings, setAckPayWarnings] = useState(false);
  // Employees who net zero because unpaid leave took the whole wage (ZERO_NET_UNPAID_LEAVE): acknowledged by count.
  const [zeroNetIds, setZeroNetIds] = useState<number[]>([]);
  const [ackZeroNet, setAckZeroNet] = useState(false);
  // The cash/cheque list names the person (the run's population is already loaded for the gate) and falls back to the number.
  const employeeName = (id: number, ibanOnFile: boolean) => {
    const member = population ? [...population.included, ...population.excluded, ...population.notEligible].find(m => m.employeeId === id) : undefined;
    const known = member?.name ? (member.code ? `${member.code} · ${member.name}` : member.name) : '';
    if (known) return ibanOnFile ? t('{name} — a valid IBAN is on file', { name: known }) : known;
    return ibanOnFile ? t('Employee #{id} — a valid IBAN is on file', { id }) : t('Employee #{id}', { id });
  };

  const refreshRuns = () =>
    payrollApi.listAllRuns().then(setRuns).catch(() => {});

  const refreshGate = React.useCallback((id: string) => {
    if (!id) { setPopulation(null); setOverrideReport(null); setZeroNetIds([]); return; }
    setGateLoading(true); setGateError('');
    Promise.allSettled([payrollApi.runPopulation(id), payrollApi.runValidationOverrides(id), payrollApi.listValidationResults(id)])
      .then(([pop, ovr, findings]) => {
        setPopulation(pop.status === 'fulfilled' ? pop.value : null);
        setOverrideReport(ovr.status === 'fulfilled' ? ovr.value : null);
        setZeroNetIds(findings.status === 'fulfilled' && Array.isArray(findings.value)
          ? [...new Set(findings.value.filter(f => f.code === ZERO_NET_UNPAID_LEAVE && f.employeeId != null).map(f => Number(f.employeeId)))]
          : []);
        // A failure here is not cosmetic: without the counts the approver cannot acknowledge, and
        // the backend will refuse. Say so rather than presenting a button that 409s.
        if (pop.status === 'rejected' || ovr.status === 'rejected')
          setGateError(t('The exclusion and override counts this run must be approved against could not be read. Approval will be refused until they load — reload the page or check your role.'));
      })
      .finally(() => setGateLoading(false));
  }, [t]);

  useEffect(() => { refreshRuns(); }, []);
  useEffect(() => {
    if (!runId) return;
    payrollApi.runApprovals(runId).then(setApprovals).catch(() => {});
  }, [runId]);
  useEffect(() => {
    setAckExcluded(false);
    setAckOverridden(false);
    setAckOutside(false);
    setAckPayWarnings(false);
    setAckZeroNet(false);
    refreshGate(runId);
  }, [runId, refreshGate]);

  const selectedRun = runs.find(r => r.id === runId);
  // Until the run's currency is confirmed its amounts carry no currency label and approval is withheld.
  const { resolution: runCurrency, companiesState, amount: runAmount, retry: retryRunCurrency } = useRunCurrency(selectedRun);
  const currencyConfirmed = runCurrency.status === 'resolved';

  const excludedCount = population?.excludedCount ?? 0;
  const overriddenCount = overrideReport?.overrides.length ?? 0;
  const needsExcludedAck = excludedCount > 0;
  const needsOverriddenAck = overriddenCount > 0;
  const outsideList = overrideReport?.paidOutsideBankFile ?? [];
  const outsideCount = outsideList.length;
  const needsOutsideAck = outsideCount > 0;
  // People the run pays although their record says something that may mean they are not owed a full month, and people
  // it does not pay although their status says employed: one count the approver states.
  const payWarnings = population?.payWarnings ?? [];
  const payWarningCount = population?.payWarningCount ?? 0;
  const needsPayWarningAck = payWarningCount > 0;
  const zeroNetCount = zeroNetIds.length;
  const needsZeroNetAck = zeroNetCount > 0;
  const leftOut = population?.leftOut ?? [];
  // S-048: the final approval seals the journal, so with costing on the approver states how many employees post uncosted.
  const sealsJournal = hasPermission('payroll.approve') && (isFinance || isAdmin) && (selectedRun?.status === 'Processed' || selectedRun?.status === 'PendingFinanceReview');
  const uncosted = useUncostedAck(runId, sealsJournal);
  const gateSatisfied =
    (!needsExcludedAck || ackExcluded) && (!needsOverriddenAck || ackOverridden) && (!needsOutsideAck || ackOutside) && (!needsPayWarningAck || ackPayWarnings) && (!needsZeroNetAck || ackZeroNet) && (!uncosted.needed || uncosted.affirmed) && !gateLoading && !gateError && currencyConfirmed;

  const handleApprove = async () => {
    if (!runId) return;
    setSaving(true); setError(''); setReconNotice('');
    try {
      // Only send a count the approver has actually affirmed. Echoing the server's own number back
      // unconditionally would satisfy the check while acknowledging nothing.
      await payrollApi.approveRun(runId, {
        notes,
        expectedExcludedCount: needsExcludedAck && ackExcluded ? excludedCount : null,
        expectedOverriddenCount: needsOverriddenAck && ackOverridden ? overriddenCount : null,
        expectedOutsideBankCount: needsOutsideAck && ackOutside ? outsideCount : null,
        expectedPayWarningCount: needsPayWarningAck && ackPayWarnings ? payWarningCount : null,
        expectedZeroNetCount: needsZeroNetAck && ackZeroNet ? zeroNetCount : null,
        expectedUncostedCount: uncosted.expected(),
      });
      await refreshRuns();
      setRuns(r => r.map(x => x.id === runId ? { ...x, status: selectedRun?.status === 'Processed' && (isHROrPayroll && !isFinance && !isAdmin) ? 'PendingFinanceReview' : 'Approved' } : x));
      payrollApi.runApprovals(runId).then(setApprovals).catch(() => {});
      setNotes('');
      setAckExcluded(false);
      setAckOverridden(false);
      setAckOutside(false);
      setAckPayWarnings(false);
      setAckZeroNet(false);
      refreshGate(runId);
    } catch (e: unknown) {
      const data = (e as { response?: { data?: { message?: string; error?: string } } })?.response?.data;
      // No current sign-off on the reconciliation: say why, and send the user back to the review stage.
      const recon = parseReconError(e);
      if (recon.code === 'reconciliation_not_reviewed') {
        setReconNotice(recon.reason === 'Stale'
          ? t('The reconciliation was signed off on an earlier processing of this run, so that sign-off no longer counts. Review the run again and sign it off.')
          : recon.reason === 'PriorChanged'
            ? t('The month this run was compared with has changed since the reconciliation was signed off. Review it again and sign it off.')
            : recon.reason === 'Changed'
              ? t('Figures the reconciliation was based on have changed since it was signed off. Review it again and sign it off.')
              : t('The reconciliation has not been signed off. Review the run and sign it off before approving.'));
        onReconNotReviewed?.();
        return;
      }
      // A 409 here means the run moved under the approver — re-read the counts so the next attempt
      // acknowledges the truth rather than a stale number.
      if (data?.error === 'uncosted_not_acknowledged') void uncosted.refresh();
      if (data?.error === 'excluded_employees_not_acknowledged' || data?.error === 'overridden_errors_not_acknowledged'
          || data?.error === 'outside_bank_payments_not_acknowledged' || data?.error === 'pay_warnings_not_acknowledged'
          || data?.error === 'zero_net_not_acknowledged') {
        setAckExcluded(false);
        setAckZeroNet(false);
        setAckOutside(false);
        setAckPayWarnings(false);
        setAckOverridden(false);
        refreshGate(runId);
      }
      setError(data?.message ?? t('Action failed.'));
    } finally { setSaving(false); }
  };

  const handleSendBack = async () => {
    if (!runId) return;
    setSaving(true); setError('');
    try {
      await payrollApi.sendBackRun(runId, notes);
      await refreshRuns();
      payrollApi.runApprovals(runId).then(setApprovals).catch(() => {});
      setNotes('');
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? t('Action failed.'));
    } finally { setSaving(false); }
  };

  // The role decides WHICH step a user acts on; the API decides WHETHER they may: approve is
  // `payroll.approve` and send-back is `payroll.lock` (PayrollController). A Payroll Officer is in
  // the HR/payroll bucket but holds neither, so the buttons only ever returned 403 for them.
  const mayApprove = hasPermission('payroll.approve');
  const maySendBack = hasPermission('payroll.lock');
  const canApproveStep1 = mayApprove && (isHROrPayroll || isAdmin) && selectedRun?.status === 'Processed';
  const canApproveStep2 = mayApprove && (isFinance || isAdmin) && selectedRun?.status === 'PendingFinanceReview';
  const canFinanceApproveDirectly = mayApprove && (isFinance || isAdmin) && selectedRun?.status === 'Processed';
  const canSendBack = maySendBack && (isFinance || isAdmin) && selectedRun?.status === 'PendingFinanceReview';
  const canAct = canApproveStep1 || canApproveStep2 || canFinanceApproveDirectly;

  return (
    <div className="space-y-4">
      {/* Approval chain diagram */}
      <div className="surface p-4">
        <p className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-400">{t('Approval Workflow')}</p>
        <div className="flex flex-wrap items-center gap-2 text-sm">
          {[
            { label: t('Process'), badge: t('Payroll Officer'), color: 'bg-slate-100 text-slate-600 dark:bg-slate-700 dark:text-slate-300' },
            { label: '→', badge: null, color: '' },
            { label: t('HR / Payroll Review'), badge: t('Payroll Manager · HR Manager'), color: 'bg-amber-50 text-amber-700 dark:bg-amber-500/10 dark:text-amber-400' },
            { label: '→', badge: null, color: '' },
            { label: t('Finance Approval'), badge: t('Finance Controller · Finance Approver'), color: 'bg-orange-50 text-orange-700 dark:bg-orange-500/10 dark:text-orange-400' },
            { label: '→', badge: null, color: '' },
            { label: t('Approved'), badge: null, color: 'bg-blue-50 text-blue-700 dark:bg-blue-500/10 dark:text-blue-400' },
          ].map((step, i) => step.badge === null && step.label === '→' ? (
            <span key={i} className="text-slate-300 dark:text-slate-600">→</span>
          ) : (
            <div key={i} className={`rounded-lg px-2 py-1 text-xs font-medium ${step.color}`}>
              {step.label}
              {/* The role in the pill's own colour (at 60% opacity it measured about 2.5:1; the label itself passes 4.8:1). */}
              {step.badge && <span className="ms-1 font-normal">({step.badge})</span>}
            </div>
          ))}
        </div>
      </div>

      {/* The run is the run bar's (shared by every Payroll section): no second picker here. */}
      {!runId && <NoRunChosen />}

      {selectedRun && (
        <div className="surface p-5 space-y-4">
          <div className="flex items-start justify-between gap-4">
            <div>
              <p className="text-sm font-semibold text-slate-900 dark:text-white">
                {t('Payroll Run — {period}', { period: monthLabel(selectedRun.month, selectedRun.year) })}
                {runCurrency.status === 'resolved' && runCurrency.companyName ? ` · ${runCurrency.companyName}` : ''}
              </p>
              <p className="text-xs text-slate-400">{t('{employeeCount} employees · Gross {runAmount} · Net {runAmount2}', { employeeCount: selectedRun.employeeCount, runAmount: runAmount(selectedRun.totalGrossSalary), runAmount2: runAmount(selectedRun.totalNetSalary) })}</p>
            </div>
            <StatusBadge status={selectedRun.status} />
          </div>

          <RunCurrencyNotice resolution={runCurrency} companiesState={companiesState} onRetry={retryRunCurrency} blocksApproval />

          {canApproveStep1 && !canFinanceApproveDirectly && (
            <div className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-xs text-amber-700 dark:border-amber-400/20 dark:bg-amber-500/10 dark:text-amber-400">
              {t('Your approval will advance this run to Pending Finance Review for final Finance Controller sign-off.')}
            </div>
          )}

          {canAct && (
            <div className="space-y-3">
              {gateLoading && (
                <p className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400">
                  <RefreshCw className="h-3.5 w-3.5 animate-spin" />
                  {t('Reading what this run deliberately excludes and what was overridden on it…')}
                </p>
              )}
              {gateError && (
                <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-xs text-rose-700 dark:border-rose-400/20 dark:bg-rose-500/10 dark:text-rose-400">
                  {gateError}
                </p>
              )}

              {needsExcludedAck && population && (
                <AcknowledgementPanel
                  tone="amber"
                  title={`${excludedCount} employee(s) are deliberately excluded from payment`}
                  blurb={t('An exclusion is intentional, so it is only a Warning and does not block. But you are not the person who set it. Read the list, then state the count.')}
                  checked={ackExcluded}
                  onChange={setAckExcluded}
                  confirmLabel={t('I have reviewed the exclusions and confirm this run excludes exactly {excludedCount} employee(s).', { excludedCount })}
                  rows={population.excluded.map(e => ({
                    key: String(e.employeeId),
                    primary: `${e.code} — ${e.name}`,
                    secondary: e.reason ?? '',
                  }))}
                />
              )}

              {needsOutsideAck && overrideReport && (
                <AcknowledgementPanel
                  tone="amber"
                  title={t('{outsideCount} employee(s) are paid by cash or cheque, outside the bank/WPS file', { outsideCount })}
                  blurb={`${t('They will be left out of the bank file and their wages must be paid and recorded separately.')} ${overrideReport.mudadNote ?? ''}`}
                  checked={ackOutside}
                  onChange={setAckOutside}
                  confirmLabel={t('I have reviewed the list and confirm exactly {outsideCount} employee(s) are paid outside the bank file.', { outsideCount })}
                  rows={outsideList.map(o => ({
                    key: String(o.employeeId),
                    primary: employeeName(o.employeeId, o.code === 'PAID_OUTSIDE_BANK_FILE_WITH_IBAN'),
                    secondary: o.message,
                  }))}
                />
              )}

              {needsPayWarningAck && population && (
                <AcknowledgementPanel
                  tone="amber"
                  ackId="ack-pay-warnings"
                  title={t('{payWarningCount} employee(s) need a check before approval', { payWarningCount })}
                  blurb={t('Some are paid although their record suggests they may not be owed a full month; others are not paid although their status says they are employed. Check each one before approving.')}
                  checked={ackPayWarnings}
                  onChange={setAckPayWarnings}
                  confirmLabel={t('I have reviewed the list and confirm exactly {payWarningCount} employee(s) need a check.', { payWarningCount })}
                  rows={payWarnings.map(w => ({
                    key: `${w.employeeId}-${w.reasonCode}`,
                    primary: `${w.code} — ${w.name}`,
                    secondary: payPopulationReasonLabel(t, w.reasonCode),
                  }))}
                />
              )}

              {needsZeroNetAck && (
                <AcknowledgementPanel
                  tone="amber"
                  ackId="ack-zero-net"
                  title={t('{zeroNetCount} employee(s) net zero because unpaid leave took the whole wage', { zeroNetCount })}
                  blurb={t('They are paid nothing this month and are left out of the bank file. Any loan or advance instalment is carried forward, not written off. Check the leave before approving.')}
                  checked={ackZeroNet}
                  onChange={setAckZeroNet}
                  confirmLabel={t('I have reviewed the list and confirm exactly {zeroNetCount} employee(s) net zero this month.', { zeroNetCount })}
                  rows={zeroNetIds.map(id => ({ key: String(id), primary: employeeName(id, false), secondary: '' }))}
                />
              )}

              {leftOut.length > 0 && <LeftOutPanel entries={leftOut} />}

              {runId && <HeldPayPanel runId={runId} amount={runAmount} />}

              {needsOverriddenAck && overrideReport && (
                <AcknowledgementPanel
                  tone="rose"
                  title={t('{count, plural, one {# blocking compliance error was consciously overridden} other {# blocking compliance errors were consciously overridden}}', { count: overriddenCount })}
                  blurb={t('Each of these was an Error that would have blocked this run, cleared by a named person with a stated reason. Approving accepts them.')}
                  checked={ackOverridden}
                  onChange={setAckOverridden}
                  confirmLabel={t('I have read every override and confirm this run carries exactly {overriddenCount} of them.', { overriddenCount })}
                  rows={overrideReport.overrides.map(o => ({
                    key: o.id,
                    primary: o.employeeId === null ? o.code : t('{code} — employee #{id}', { code: o.code, id: o.employeeId }),
                    secondary: t('{reason} · overridden by {name} on {date}', { reason: o.reason, name: o.overriddenByName || t('Unknown'), date: fmtDate(o.createdAtUtc) }),
                  }))}
                />
              )}

              {uncosted.needed && <UncostedApprovalAck count={uncosted.count} affirmed={uncosted.affirmed} onChange={uncosted.setAffirmed} />}

              <Field label={t('Notes / Comments')}>
                <textarea aria-label={t('Approval notes')} className={inp} rows={2} value={notes} onChange={e => setNotes(e.target.value)} placeholder={t('Optional comments for the record…')} />
              </Field>
              {reconNotice && <p role="alert" className="rounded-lg border border-amber-200 bg-amber-50 p-3 text-xs text-amber-700 dark:border-amber-400/20 dark:bg-amber-500/10 dark:text-amber-400" data-testid="approve-reconciliation-notice">{reconNotice}</p>}
              {error && <p role="alert" className="text-xs text-rose-500">{error}</p>}
              <div className="flex flex-wrap gap-2">
                <button
                  type="button"
                  className={btn.primary}
                  onClick={handleApprove}
                  disabled={saving || !gateSatisfied}
                  title={gateSatisfied ? undefined : !currencyConfirmed
                    ? t('The run\'s currency must be confirmed before it can be approved.')
                    : t('Acknowledge the exclusions and overrides above before approving.')}
                >
                  <CheckCircle2 className="h-4 w-4" />
                  {saving ? t('Saving…') : canFinanceApproveDirectly || canApproveStep2 ? t('Approve — Final') : t('Approve → Send to Finance')}
                </button>
                {canSendBack && (
                  <button type="button" className={btn.danger} onClick={handleSendBack} disabled={saving}>
                    <RotateCcw className="h-4 w-4" /> {t('Send Back to Payroll')}
                  </button>
                )}
              </div>
            </div>
          )}

          {!canAct && (
            <p className="text-sm text-slate-400">
              {selectedRun.status === 'Approved' ? t('Payroll run has been approved and is ready to lock.') :
               selectedRun.status === 'Locked' ? t('Run is locked and finalised.') :
               selectedRun.status === 'PendingFinanceReview' ? t('Awaiting Finance Controller approval.') :
               t('No action available for this run at its current stage.')}
            </p>
          )}
        </div>
      )}

      {approvals.length > 0 && (
        <div className="surface divide-y divide-slate-100 dark:divide-white/5">
          <div className="px-5 py-3"><p className="text-sm font-semibold text-slate-800 dark:text-white">{t('Approval Chain History')}</p></div>
          {approvals.map(a => (
            <div key={a.id} className="flex items-start gap-3 px-5 py-4">
              {a.decision === 'SentBack' ? <RotateCcw className="mt-0.5 h-4 w-4 shrink-0 text-rose-500" /> : <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-emerald-500" />}
              <div className="min-w-0">
                <div className="flex items-center gap-2">
                  <StatusBadge status={a.decision} />
                  <span className="rounded bg-slate-100 px-1.5 py-0.5 text-xs text-slate-500 dark:bg-white/10 dark:text-slate-400">{a.approvalLevel}</span>
                </div>
                {a.notes && <p className="mt-1 text-xs text-slate-500">{a.notes}</p>}
                <p className="mt-1 text-xs text-slate-400">{fmtDate(a.decidedAtUtc)}</p>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ── Payslips Tab ────────────────────────────────────────────────────────────────

function PayslipsTab({ selectedRunId, onRunChange }: { selectedRunId?: string; onRunChange?: (id: string | null) => void }) {
  const t = useT();
  const fx = useFormat();
  // The payslip PDF and the run's ZIP bundle are `payroll.export` on the API (PayrollController
  // DownloadSlipPdf / DownloadRunPdfBundle); offering them without it only produced a 403.
  const { hasPermission } = useAuth();
  const canExport = hasPermission('payroll.export');
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [runId, setRunId] = useSharedRun(selectedRunId, onRunChange);
  const [payslips, setPayslips] = useState<Payslip[]>([]);
  const [listError, setListError] = useState('');
  const [loading, setLoading] = useState(false);
  const [generating, setGenerating] = useState(false);
  const [downloadingId, setDownloadingId] = useState<string | null>(null);
  const [downloadingBundle, setDownloadingBundle] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => { payrollApi.listAllRuns().then(setRuns).catch(() => {}); }, []);

  const loadPayslips = () => {
    if (!runId) return;
    setLoading(true);
    setListError('');
    payrollApi.allPayslips(runId)
      .then(setPayslips)
      .catch(e => { setPayslips([]); setListError(t('Payslips could not be loaded. {requestFailureReason}', { requestFailureReason: requestFailureReason(e) })); })
      .finally(() => setLoading(false));
  };
  useEffect(() => { if (runId) loadPayslips(); }, [runId]);

  const generate = async () => {
    if (!runId) return;
    setGenerating(true);
    setError('');
    try {
      await payrollApi.generatePayslips(runId);
    } catch (e) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? t('Failed to generate payslips. The run must be Processed first.'));
    }
    loadPayslips();
    setGenerating(false);
  };

  const downloadBundle = async () => {
    if (!runId) return;
    const selectedRun = runs.find(r => r.id === runId);
    const period = selectedRun ? `${selectedRun.year}${String(selectedRun.month).padStart(2, '0')}` : runId;
    setDownloadingBundle(true);
    await payrollApi.downloadRunPdfBundle(runId, period).catch(() => {});
    setDownloadingBundle(false);
  };

  const published = payslips.filter(p => p.isPublishedToEss).length;

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <select aria-label={t('Select payroll run')} className={`${sel} flex-1 max-w-xs`} value={runId} onChange={e => setRunId(e.target.value)}>
          <option value="">{t('Select a payroll run…')}</option>
          <RunSelectOptions runs={runs} />
        </select>
        {runId && (
          <button type="button" className={btn.primary} onClick={generate} disabled={generating}>
            {generating ? t('Generating…') : t('Generate Payslips')}
          </button>
        )}
        {payslips.length > 0 && canExport && (
          <button type="button" className={btn.ghost} onClick={downloadBundle} disabled={downloadingBundle}>
            <Download className="h-4 w-4" />
            {downloadingBundle ? t('Preparing ZIP…') : t('Download All (ZIP)')}
          </button>
        )}
      </div>

      {error && (
        <div className="flex items-start gap-2 rounded-lg bg-rose-50 px-4 py-2.5 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-400">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{error}</span>
        </div>
      )}

      {payslips.length > 0 && (
        <div className="grid grid-cols-3 gap-4">
          <KpiCard label={t('Total Payslips')} value={payslips.length} icon={FileText} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
          <KpiCard label={t('Published to ESS')} value={published} icon={CheckCircle2} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
          <KpiCard label={t('Pending Publish')} value={payslips.length - published} icon={AlertTriangle} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
        </div>
      )}

      {loading ? <p className="text-sm text-slate-400">{t('Loading…')}</p> : listError ? (
        <div role="alert" className="surface flex items-start gap-2 p-4 text-sm text-rose-700 dark:text-rose-400">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{listError}</span>
        </div>
      ) : payslips.length === 0 && runId ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <FileText className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">{t('No payslips yet — click Generate to create payslips for this run')}</p>
        </div>
      ) : (
        <div className="surface overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {[t('Employee'), t('Payslip #'), t('Language'), t('ESS Published'), t('Published At'), t('Generated'), ''].map(h => (
                  <th key={h} className="px-4 py-2 text-start text-xs font-bold uppercase text-fg-muted">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {payslips.map(p => (
                <tr key={p.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-2 font-medium text-slate-900 dark:text-white">
                    {/* The name and code come from the payslip record itself (denormalised at run time),
                        so this row reads exactly like the PDF the employee receives. */}
                    {p.employeeName}
                    {p.employeeCode && <span className="ms-2 font-mono text-[11px] font-normal text-slate-400">{p.employeeCode}</span>}
                  </td>
                  <td className="px-4 py-2 font-mono text-xs text-slate-500">{p.payslipNumber}</td>
                  <td className="px-4 py-2 text-slate-500">{languageLabel(t, p.language)}</td>
                  <td className="px-4 py-2">
                    <span className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${p.isPublishedToEss ? 'bg-emerald-50 text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400' : 'bg-slate-100 text-slate-500 dark:bg-white/10'}`}>
                      {p.isPublishedToEss ? t('Published') : t('Not published')}
                    </span>
                  </td>
                  <td className="px-4 py-2 text-xs text-slate-400">{p.publishedAtUtc ? fx.date(p.publishedAtUtc) : '—'}</td>
                  <td className="px-4 py-2 text-xs text-slate-400">{fx.date(p.createdAtUtc)}</td>
                  <td className="px-4 py-2">
                    {canExport ? <button
                      type="button"
                      title={t('Download payslip PDF')}
                      disabled={downloadingId === p.id}
                      onClick={() => {
                        setDownloadingId(p.id);
                        payrollApi.downloadSlipPdf(p.id, `${p.payslipNumber}.pdf`).finally(() => setDownloadingId(null));
                      }}
                      className="flex items-center gap-1 text-xs text-sapphire hover:text-sapphire/80 disabled:opacity-40"
                    >
                      <Download className="h-3.5 w-3.5" />
                      {downloadingId === p.id ? t('Downloading…') : 'PDF'}
                    </button> : <span className="text-xs text-slate-400">—</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ── Bank / WPS Files Tab ────────────────────────────────────────────────────────

/** A read a tab makes: still loading, loaded, or failed (with the error, so a refusal is told from an outage). */
type LoadStatus = { status: 'idle' } | { status: 'loading' } | { status: 'ready' } | { status: 'failed'; error: unknown };
const isForbidden = (error: unknown) => (error as { response?: { status?: number } } | null)?.response?.status === 403;

function BankWpsTab({ selectedRunId, onNavigate, onRunsChanged, onPayOutContext }: { selectedRunId?: string; onRunChange?: (id: string | null) => void; onNavigate?: (tab: string) => void; onRunsChanged?: () => void; onPayOutContext?: (ctx: PayOutContext | null) => void }) {
  const t = useT();
  // Creating a batch and generating the file need payroll.export (Admin) or payroll.payment_batch.manage. Without either the actions are shown off, with the reason.
  const { hasPermission } = useAuth();
  const canExport = paymentActionsAllowed(hasPermission, ['create']);
  const fmtAmt = useFmtAmt();
  const monthLabel = useMonthLabel();
  const { confirm, dialog: confirmDialog } = useConfirm();
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  // The run is the page's: the run bar's picker is the only run control (clearing it empties this tab too).
  const runId = selectedRunId ?? '';
  const [batches, setBatches] = useState<PayrollPaymentBatch[]>([]);
  const [selectedBatch, setSelectedBatch] = useState<PayrollPaymentBatch | null>(null);
  const [records, setRecords] = useState<PayrollPaymentRecord[]>([]);
  const [creating, setCreating] = useState(false);
  const [paymentMethod, setPaymentMethod] = useState('WPS');
  const [error, setError] = useState('');
  const openBatchIdRef = useRef<string | null>(null);
  // Whether the open batch's bank file existed the last time the flow reported (null: not reported yet for this batch).
  const fileMadeRef = useRef<boolean | null>(null);
  // The run's only batch opens by itself (once per run): there is nothing to choose between.
  const autoOpenedRef = useRef<string | null>(null);
  const currentRunRef = useRef(runId);
  currentRunRef.current = runId;
  // A list that is still loading or failed to load is never shown as an empty one.
  const [runsState, setRunsState] = useState<LoadStatus>({ status: 'loading' });
  const [batchesState, setBatchesState] = useState<LoadStatus>({ status: 'idle' });
  const [recordsState, setRecordsState] = useState<LoadStatus>({ status: 'idle' });
  const [reloadKey, setReloadKey] = useState(0);
  // Where the open batch is in check -> create -> download, reported by the bank-file gate and panel for the step strip above.
  const [flow, setFlow] = useState<BankFlowProgress | null>(null);
  const [recordsOpen, setRecordsOpen] = useState(false);
  const runNames = useRunEmployeeLabels(runId);
  const fx = useFormat();
  // Several live batches and none open: the one the user picked in step 1, before "Open batch".
  const [chosenBatchId, setChosenBatchId] = useState<string | null>(null);
  const [submitOpen, setSubmitOpen] = useState(false);
  const panelMenu = useRef<GuideSlots['menu'] | null>(null);
  // How many people each batch pays (its payment records), read only when there are batches to choose between.
  const [batchCounts, setBatchCounts] = useState<Record<string, number>>({});
  // The employer's bank (from the company's bank setup), named on each batch row when there are batches to choose between.
  const [chooseBank, setChooseBank] = useState<string | null>(null);

  useEffect(() => {
    setRunsState({ status: 'loading' });
    payrollApi.listAllRuns().then(rows => { setRuns(rows); setRunsState({ status: 'ready' }); }).catch(error => setRunsState({ status: 'failed', error }));
  }, [reloadKey]);

  useEffect(() => {
    let cancelled = false;
    openBatchIdRef.current = null;
    autoOpenedRef.current = null;
    setBatches([]); setSelectedBatch(null); setRecords([]); setRecordsState({ status: 'idle' }); setRecordsOpen(false); setChosenBatchId(null); setSubmitOpen(false);
    if (!runId) { setBatchesState({ status: 'idle' }); return; }
    setError('');
    setBatchesState({ status: 'loading' });
    payrollApi.listPaymentBatches(runId)
      .then(rows => { if (!cancelled) { setBatches(rows); setBatchesState({ status: 'ready' }); } })
      .catch(error => { if (!cancelled) setBatchesState({ status: 'failed', error }); });
    return () => { cancelled = true; };
  }, [runId, reloadKey]);

  const selectedRun = runs.find(r => r.id === runId);
  const hasExistingBatch = batches.some(b => b.wpsStatus !== 'Voided');

  const openBatch = (batch: PayrollPaymentBatch) => {
    openBatchIdRef.current = batch.id;
    setSelectedBatch(batch); setRecords([]); setRecordsState({ status: 'loading' }); setFlow(null); setRecordsOpen(false); fileMadeRef.current = null;
    payrollApi.paymentRecords(batch.id)
      .then(rows => { if (openBatchIdRef.current === batch.id) { setRecords(rows); setRecordsState({ status: 'ready' }); } })
      .catch(error => { if (openBatchIdRef.current === batch.id) setRecordsState({ status: 'failed', error }); });
  };

  // The batch opens by itself when there is nothing to choose: the run's only live batch, or the one that has gone furthest (lib/payOut.ts).
  useEffect(() => {
    if (selectedBatch) return;
    const pick = pickPayOutBatch(batches as PipelineBatch[], null);
    if (!pick || autoOpenedRef.current === pick.id) return;
    autoOpenedRef.current = pick.id;
    openBatch(pick as PayrollPaymentBatch);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [batches, selectedBatch]);

  // ── One stage for the whole page (lib/payOut.ts): this tab's strip, panel and button, and the run bar's chip and run option.
  const can = {
    create: canExport, wps: paymentActionsAllowed(hasPermission, ['wps']),
    settle: paymentActionsAllowed(hasPermission, ['settle']), setup: hasPermission('payroll.structure_manage'),
  };
  // Someone who can neither prepare, submit nor settle the file has nothing to do here (Finance's settle still sees the flow, read-only).
  const restricted = !paymentActionsAllowed(hasPermission, ['create', 'wps', 'settle']);
  // The open batch against the run (run net − paid outside the file): a difference blocks step 3, in the one stage source.
  const openRow = selectedBatch ? (batches.find(b => b.id === selectedBatch.id) ?? selectedBatch) : null;
  const tieDifference = openRow ? tieOut(openRow, selectedRun)?.difference ?? null : null;
  const exportFacts = useMemo(() => (flow ? {
    gate: flow.gate, setup: flow.setup, validation: flow.validation, fileExists: flow.generated, createRefused: !!flow.createRefused, tieDifference,
  } : null), [flow?.gate, flow?.setup, flow?.validation, flow?.generated, flow?.createRefused, tieDifference]); // eslint-disable-line react-hooks/exhaustive-deps
  const recordsRead = recordsState.status === 'ready' ? records : null;
  const payOut = derivePayOut({
    run: selectedRun ?? null, batches: batchesState.status === 'ready' ? batches as PipelineBatch[] : null, openBatchId: selectedBatch?.id ?? null,
    can, exportFacts, downloaded: !!flow?.downloaded, records: recordsRead,
  });
  const loadingGuide = runsState.status === 'loading' || batchesState.status === 'loading' || batchesState.status === 'idle';

  // The run bar reads the same facts (its chip and the run option cannot disagree with this tab).
  const payOutContext = useMemo<PayOutContext | null>(() => (runId && batchesState.status === 'ready' ? {
    runId, batches: batches as PipelineBatch[], openBatchId: selectedBatch?.id ?? null, exportFacts, downloaded: !!flow?.downloaded, records: recordsRead,
  } : null), [runId, batches, batchesState.status, selectedBatch?.id, exportFacts, flow?.downloaded, recordsRead]);
  useEffect(() => { onPayOutContext?.(payOutContext); }, [payOutContext, onPayOutContext]);
  useEffect(() => () => onPayOutContext?.(null), [onPayOutContext]);

  // What the panel last handed the flow (the bank's name, the file), for the facts strip, step 5 and the confirmations.
  const panelData = useRef<BankPanelData | null>(null);
  const submittedRef = useRef<string>('');
  // The batch was created here (not picked from the list): the hand-over says so.
  const batchCreatedRef = useRef(false);
  const handOver = useBankHandOver(payOut.stage, loadingGuide, (from) => {
    const data = panelData.current;
    switch (from) {
      case 'batch': return !selectedBatch ? '' : batchCreatedRef.current ? t('Payment batch {number} created.', { number: selectedBatch.batchNumber }) : t('Payment batch chosen.');
      case 'setup': return t('Bank setup saved.');
      case 'check': return t('Checked {n} employees — no blockers.', { n: flow?.checked ?? records.length });
      case 'create': return data?.file ? t('Bank file created: {count} employees, {amount}.', { count: data.file.employeeCount, amount: fmtAmt(data.file.totalAmount, selectedBatch?.currency) }) : t('Bank file created.');
      case 'send': return data?.bankName ? t('Recorded as submitted to {bank}, reference {ref}.', { bank: data.bankName, ref: submittedRef.current }) : t('Recorded as submitted, reference {ref}.', { ref: submittedRef.current });
      default: return '';
    }
  });

  // A done step opened read-only (?step= in the address, so it can be shared); a new stage starts clean.
  const searchParams = useSearchParams();
  const [viewing, setViewingState] = useState<PayOutStepKey | null>(() => {
    const s = searchParams?.get('step');
    return s === 'batch' || s === 'check' || s === 'create' || s === 'send' ? s : null;
  });
  const setViewing = (k: PayOutStepKey | null) => {
    setViewingState(k);
    try {
      const u = new URL(window.location.href);
      if (k) u.searchParams.set('step', k); else u.searchParams.delete('step');
      window.history.replaceState(window.history.state, '', u.toString());
    } catch { /* the address just does not follow */ }
  };
  const lastStage = useRef(payOut.stage);
  useEffect(() => {
    if (lastStage.current !== payOut.stage && !loadingGuide) { lastStage.current = payOut.stage; if (viewing) setViewing(null); }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [payOut.stage, loadingGuide]);

  // The bank file was made on this screen: the page's pipeline and run list read the batch list, so they are told to read it again.
  useEffect(() => {
    if (!selectedBatch || !flow || flow.gate !== 'ready') return;
    if (fileMadeRef.current === false && flow.generated) {
      onRunsChanged?.();
      payrollApi.listPaymentBatches(runId).then(rows => { if (currentRunRef.current === runId) setBatches(rows); }).catch(() => {});
    }
    fileMadeRef.current = flow.generated;
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [flow?.generated, flow?.gate, selectedBatch?.id]);

  useEffect(() => {
    const live = batches.filter(b => b.wpsStatus !== 'Voided');
    if (live.length < 2) return;
    let cancelled = false;
    for (const b of live) {
      if (batchCounts[b.id] !== undefined) continue;
      payrollApi.paymentRecords(b.id)
        .then(rows => { if (!cancelled && Array.isArray(rows)) setBatchCounts(prev => ({ ...prev, [b.id]: rows.filter(r => r.status !== 'Cancelled').length })); })
        .catch(() => { /* the count is left out, never guessed */ });
    }
    const companyId = runs.find(r => r.id === runId)?.companyId;
    if (companyId && live.some(b => b.isSaudi === true)) {
      Promise.all([saudiBankExportsApi.getSettings(companyId), saudiBankExportsApi.listFormats()])
        .then(([settings, formats]) => {
          if (cancelled) return;
          const f = Array.isArray(formats) ? formats.find(x => x.id === settings?.formatId) : undefined;
          setChooseBank(f ? (isCustomFormatId(f.id) ? t(f.name) : t(f.bank)) : null);
        })
        .catch(() => { /* the bank is left out, never guessed */ });
    }
    return () => { cancelled = true; };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [batches, runs]);

  const currentBatch = batches.find(b => b.wpsStatus !== 'Voided') ?? null;
  const batchFileGenerated = !!currentBatch && (currentBatch.status === 'FileGenerated' || (!!currentBatch.effectiveWpsStatus && currentBatch.effectiveWpsStatus !== 'Draft'));

  const createBatch = async () => {
    if (!runId) return;
    setCreating(true);
    setError('');
    try {
      let batch: PayrollPaymentBatch;
      try {
        batch = await payrollApi.createPaymentBatch(runId, paymentMethod);
      } catch (e) {
        // A run locked before payment methods were frozen: the cash/cheque list must be acknowledged by count.
        const data = (e as { response?: { data?: { error?: string; message?: string; outsideBankCount?: number; employees?: { employeeCode: string; method: string }[] } } })?.response?.data;
        if (data?.error !== 'outside_bank_payments_not_acknowledged' || !data.outsideBankCount) throw e;
        const list = (data.employees ?? []).map(x => <li key={x.employeeCode}><bdi>{x.employeeCode}</bdi> ({x.method})</li>);
        const confirmed = await confirm({
          title: t('Confirm payments outside the bank file'),
          message: (
            <div className="space-y-2">
              {data.message && <p>{data.message}</p>}
              {list.length > 0 && <ul className="list-disc ps-5">{list}</ul>}
              <p>{t('Confirm that exactly {count, plural, one {# employee is} other {# employees are}} paid outside the bank file?', { count: data.outsideBankCount })}</p>
            </div>
          ),
          confirmLabel: t('Confirm paid outside the bank file'),
          tone: 'primary',
        });
        if (!confirmed) return;
        batch = await payrollApi.createPaymentBatch(runId, paymentMethod, undefined, data.outsideBankCount);
      }
      if (currentRunRef.current !== runId) return;
      batchCreatedRef.current = true;
      setBatches(b => [batch, ...b]);
      openBatch(batch);
      onRunsChanged?.();
    } catch (e) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      // A 403 is a missing permission, never "lock the run first" (the run may well be locked).
      const refusal = paymentActionRefusal(e, 'create');
      setError(refusal ? t(refusal) : msg ?? t('Could not create payment batch. The payroll run must be Locked first (Approvals tab → Lock Run).'));
    } finally {
      setCreating(false);
    }
  };

  const generateWps = async (batchId: string) => {
    setError('');
    try {
      await payrollApi.generateWpsFile(batchId);
    } catch (e) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message;
      const refusal = paymentActionRefusal(e, 'wps');
      setError(refusal ? t(refusal) : msg ?? t('File generation failed.'));
    }
    payrollApi.listPaymentBatches(runId).then(setBatches).catch(() => {});
  };

  // ── The payment batches of the run (a WPS/SIF batch's own layout).
  const batchList = (
    <div id="bank-wps-batches" tabIndex={-1} className="outline-none" data-testid="bank-batch-list">
      {!runId ? <p className="p-5 text-sm text-fg-muted">{t('Choose a payroll run to see its payment batches.')}</p>
        : batchesState.status === 'loading' ? <SkeletonRows rows={2} className="p-4" />
        : batchesState.status === 'failed' ? <LoadFailedNotice error={batchesState.error} onRetry={() => setReloadKey(n => n + 1)} />
        : batches.length === 0 ? <p className="p-5 text-sm text-slate-400">{t('No batches yet.')}</p> : (
        <div className="divide-y divide-slate-100 rounded-lg border border-line dark:divide-white/5">
          {batches.map(b => (
            <div key={b.id} role="button" tabIndex={0} onClick={() => openBatch(b)}
              onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openBatch(b); } }}
              className={`w-full cursor-pointer px-4 py-3 text-start hover:bg-slate-50 dark:hover:bg-white/5 ${selectedBatch?.id === b.id ? 'bg-sapphire/5 dark:bg-sapphire/10' : ''}`}>
              <div className="flex items-center justify-between">
                <p className="text-sm font-medium text-slate-800 dark:text-white">{b.batchNumber}</p>
                {/* One status: the lifecycle's. The file-generation flag is a step toward it, not a second status. */}
                <StatusBadge status={b.effectiveWpsStatus || b.wpsStatus || b.status} />
              </div>
              <p className="mt-1 text-xs text-slate-400">{b.paymentMethod} · {fmtAmt(b.totalAmount, b.currency)}</p>
              {b.status !== 'FileGenerated' && (
                <button type="button" onClick={e => { e.stopPropagation(); generateWps(b.id); }} className={`mt-1.5 ${btn.sm} h-6 px-2 text-xs disabled:opacity-50`}
                  disabled={!canExport}
                  title={!canExport ? t(PAYMENT_ACTION_DENIED.wps) : b.isSaudi ? t('For review only. The Saudi bank payroll file is the ANB Connect instruction.') : undefined}>
                  {b.isSaudi ? t('Generate payroll register (internal — not a bank/WPS file)') : t('Generate WPS/SIF (not verified with any gateway)')}
                </button>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );

  // ── The payment records table: a drawer in the guided flow, a panel in a WPS/SIF batch's layout.
  const recordsTable = selectedBatch && (
    <>
      {/* A long batch scrolls inside its own region (header stays in view) instead of making the whole tab tall. */}
      <div role="region" aria-label={t('Payment records')} tabIndex={0} className="max-h-[calc(100dvh-10rem)] overflow-auto outline-none focus-visible:ring-2 focus-visible:ring-primary" data-testid="bank-records-scroll">
      <table className="w-full min-w-[34rem] text-sm">
        <thead className="sticky top-0 z-10 bg-surface">
          <tr className="border-b border-slate-100 dark:border-white/[0.07]">
            {[t('Employee'), t('Amount'), 'IBAN', t('WPS Reference'), t('Status')].map(h => (
              <th key={h} className="px-4 py-2 text-start text-xs font-bold uppercase text-fg-muted">{h}</th>
            ))}
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
          {recordsState.status === 'loading' && <tr><td colSpan={5} className="p-4"><SkeletonRows rows={4} /></td></tr>}
          {recordsState.status === 'failed' && (isForbidden(recordsState.error)
            ? <tr><td colSpan={5} role="alert" data-testid="bank-records-forbidden" className="py-8 text-center text-sm text-fg-muted">{t('The payment records need the Payroll export permission. Ask an administrator to give it to your role.')}</td></tr>
            : <LoadFailedRow colSpan={5} error={recordsState.error} onRetry={() => openBatch(selectedBatch)} />)}
          {recordsState.status === 'ready' && records.length === 0 && <tr><td colSpan={5} className="py-8 text-center text-sm text-slate-400">{t('No records.')}</td></tr>}
          {records.map(r => (
            <tr key={r.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
              <td className="px-4 py-2 font-medium text-slate-900 dark:text-white">
                {r.employeeName
                  ? <><bdi>{r.employeeName}</bdi>{r.employeeCode && <span className="ms-1.5 whitespace-nowrap text-xs font-normal text-slate-400"><bdi>{r.employeeCode}</bdi></span>}</>
                  : runNames[r.employeeId] ? <EmployeeLabelText label={runNames[r.employeeId]} /> : t('Emp #{id}', { id: r.employeeId })}
              </td>
              <td className="px-4 py-2 font-semibold text-slate-700 dark:text-slate-300">{r.amount.toLocaleString('en-US', { minimumFractionDigits: 2 })}</td>
              <td className="px-4 py-2 font-mono text-xs text-slate-500">{r.iban || <span className="text-rose-500">{t('Missing IBAN')}</span>}</td>
              <td className="px-4 py-2 text-xs text-slate-400">{r.wpsReference}</td>
              <td className="px-4 py-2"><StatusBadge status={r.status} /></td>
            </tr>
          ))}
        </tbody>
      </table>
      </div>
    </>
  );

  const failureBanners = (
    <>
      {error && (
        <div role="alert" className="flex items-start gap-2 rounded-lg bg-rose-50 px-4 py-2.5 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-400">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{error}</span>
        </div>
      )}
      {runsState.status === 'failed' && (
        <div role="alert" data-testid="bank-runs-failed" className="flex flex-wrap items-center gap-2 rounded-lg bg-rose-50 px-4 py-2.5 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-400">
          <AlertTriangle className="h-4 w-4 shrink-0" aria-hidden="true" />
          <span>{t('The payroll runs could not be loaded.')}</span>
          <button type="button" className={btn.link} onClick={() => setReloadKey(n => n + 1)}>{t('Retry')}</button>
        </div>
      )}
    </>
  );

  const header = (menu?: GuideSlots['menu']) => (
    <PayrollSectionHeader
      group={t('Pay out')}
      title={t('Bank / WPS Files')}
      purpose={t('Prepare this run’s bank salary file, hand it to your bank, and follow the bank’s confirmation.')}
      actions={selectedBatch?.isSaudi === true || menu ? (
        <MoreMenu items={[
          menu?.setup && { label: t('Bank setup'), onSelect: menu.setup },
          menu?.employeeBank && { label: t('Employee bank details'), onSelect: menu.employeeBank },
          selectedBatch && recordsState.status === 'ready' && { label: t('Payment records'), onSelect: () => setRecordsOpen(true) },
          menu?.formats && { label: t('Bank file formats'), onSelect: menu.formats },
          menu?.about && { label: t('About this file'), onSelect: menu.about },
        ]} />
      ) : undefined}
    />
  );

  // A WPS/SIF batch (not the Saudi bank-file flow) keeps its own layout: the batch list, its records and the batch's own file button.
  if (runId && selectedBatch && selectedBatch.isSaudi !== true) {
    return (
      <div className="space-y-4">
        {confirmDialog}
        {header()}
        {hasExistingBatch && currentBatch && (
          <p role="status" data-testid="bank-batch-status" className="text-sm font-medium text-slate-700 dark:text-slate-200">
            {t(batchFileGenerated ? 'Payment batch {number} created; bank file generated.' : 'Payment batch {number} created; bank file not generated yet.', { number: currentBatch.batchNumber })}
          </p>
        )}
        {failureBanners}
        <div className="grid gap-4 lg:grid-cols-3">
          <div className="surface overflow-hidden">
            <div className="border-b border-slate-100 px-4 py-3 dark:border-white/5">
              <p className="text-sm font-semibold text-slate-800 dark:text-white">{t('Payment Batches')}</p>
            </div>
            {batchList}
          </div>
          <div id="bank-wps-records" tabIndex={-1} className="surface overflow-hidden outline-none lg:col-span-2">
            <div className="border-b border-slate-100 px-4 py-3 dark:border-white/5">
              <p className="text-sm font-semibold text-slate-800 dark:text-white">{t('Records — {batchNumber}', { batchNumber: selectedBatch.batchNumber })}</p>
            </div>
            {recordsTable}
          </div>
        </div>
        <SaudiBankExportGate key={selectedBatch.id} batchId={selectedBatch.id} isSaudi={false} onProgress={setFlow} suggestedReference={suggestedBatchReference(selectedBatch.batchNumber, selectedRun)}
          employeeIds={records.map(r => r.employeeId)} employeeLabels={{ ...runNames, ...employeeLabelsFromRecords(records) }} renderGuide={slots => <>{slots.panels?.check?.body ?? null}</>} />
      </div>
    );
  }

  if (!runId) {
    return (
      <div className="space-y-4">
        {confirmDialog}
        {header()}
        {failureBanners}
        <div className="surface" data-testid="bank-no-run">
          <EmptyState kind="first-use" icon={Landmark} title={t('Choose a payroll run')} description={t('The bank file is prepared for one run at a time.')}
            action={{ label: t('Choose a run'), onClick: () => document.getElementById('pipeline-run-picker')?.focus() }} />
        </div>
      </div>
    );
  }

  // The batch list could not be read: say so (never "create one" over a list that failed to load).
  if (batchesState.status === 'failed') {
    return (
      <div className="space-y-4">
        {confirmDialog}
        {header()}
        {failureBanners}
        <LoadFailedNotice error={batchesState.error} onRetry={() => setReloadKey(n => n + 1)} />
      </div>
    );
  }

  if (restricted) {
    return (
      <div className="space-y-4">
        {header()}
        <div className="surface" data-testid="bank-restricted">
          <EmptyState kind="restricted" icon={Landmark} title={t('You can’t prepare bank files')}
            description={t('Bank files need the payment batch or Payroll export permission. Your HR administrator can give it.')} />
        </div>
      </div>
    );
  }

  // ── What the tab itself puts in the flow: step 1 (the batch) and step 5 (the bank's response)
  const liveBatches = batches.filter(b => b.wpsStatus !== 'Voided');
  const runFacts = selectedRun && (
    <dl className="grid grid-cols-2 gap-3 text-body-sm">
      <div className="rounded-xl border border-line p-3"><dt className="text-meta text-fg-muted">{t('Employees')}</dt><dd className="mt-1 text-h4 font-semibold tabular-nums text-fg">{selectedRun.employeeCount}</dd></div>
      <div className="rounded-xl border border-line p-3"><dt className="text-meta text-fg-muted">{t('Run net pay')}</dt><dd className="mt-1 text-h4 font-semibold tabular-nums text-fg"><bdi dir="ltr">{fmtAmt(selectedRun.totalNetSalary, liveBatches[0]?.currency)}</bdi></dd></div>
    </dl>
  );
  const batchBody = payOut.steps[0].reason === 'run_not_locked' ? (
    <div className="space-y-3">
      <p className="text-body text-fg">{t('Run status')}: <strong><EnumLabel enum="PayrollRunStatus" value={selectedRun?.status} /></strong></p>
      {onNavigate && <button type="button" className="btn-secondary btn-sm" onClick={() => onNavigate('approvals')}>{t('Open Approvals')}</button>}
    </div>
  ) : liveBatches.length > 1 ? (
    <fieldset className="space-y-2" data-testid="bank-batch-choices">
      <legend className="sr-only">{t('Payment batches of this run')}</legend>
      {liveBatches.map(b => {
        const others = liveBatches.filter(o => o.id !== b.id);
        // Tagged only when it is the ONE batch that ties to the run: two that tie cannot both be right, so neither is tagged.
        const tying = liveBatches.filter(o => { const x = tieOut(o, selectedRun); return !!x && Math.abs(x.difference) < 0.005; });
        const isTheMatch = tying.length === 1 && tying[0].id === b.id;
        const count = batchCounts[b.id];
        return (
          <div key={b.id} className={`rounded-xl border p-3 ${chosenBatchId === b.id ? 'border-primary bg-primary-soft' : 'border-line'}`} data-batch-row={b.id}>
            <label className="flex cursor-pointer items-center gap-3">
              <input type="radio" name="bank-batch-choice" value={b.id} checked={chosenBatchId === b.id} onChange={() => setChosenBatchId(b.id)} />
              <span className="min-w-0 flex-1">
                <span className="flex flex-wrap items-center gap-2">
                  <span className="font-mono text-body font-semibold text-fg"><bdi>{b.batchNumber}</bdi></span>
                  {isTheMatch && <span className="badge badge-neutral" data-testid="bank-batch-matches">{t('Matches the run')}</span>}
                </span>
                <span className="block text-meta text-fg-muted">
                  {count !== undefined && <>{t('{count, plural, one {# employee} other {# employees}}', { count })} · </>}
                  {paymentMethodLabel(t, b.paymentMethod)}{chooseBank && <> · <bdi>{chooseBank}</bdi></>} · <bdi dir="ltr">{fmtAmt(b.totalAmount, b.currency)}</bdi>
                </span>
              </span>
              <StatusBadge status={b.effectiveWpsStatus || b.wpsStatus || b.status} />
            </label>
            {/* A warning, not a block: the server refuses a second live batch for a run, so two here are worth a look. */}
            {others.map(o => (
              <p key={o.id} role="note" className="mt-2 flex items-start gap-1.5 text-meta font-medium text-danger-fg" data-testid="bank-batch-overlap">
                <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
                {t('Batch {number} also pays this run. Each employee must be in one batch only, or they are paid twice.', { number: o.batchNumber })}
              </p>
            ))}
          </div>
        );
      })}
    </fieldset>
  ) : (
    <div className="space-y-4">
      {batchesState.status === 'ready' && (
        <Field label={t('Payment method')} info={t('How this batch pays employees. WPS and bank transfer go in the bank file; cash and cheque are paid outside it.')} size="md">
          <select id="bank-wps-method" className="select w-full max-w-xs" value={paymentMethod} onChange={e => setPaymentMethod(e.target.value)} disabled={!canExport}>
            {['WPS', 'BankTransfer', 'Cash'].map(m => <option key={m} value={m}>{m === 'BankTransfer' ? t('Bank transfer') : m === 'Cash' ? t('Cash') : m}</option>)}
          </select>
        </Field>
      )}
      {runFacts}
    </div>
  );
  const runSummary = selectedRun && (
    <div className="space-y-3" data-testid="bank-run-summary">
      <p className="text-body font-semibold text-fg">{t('This run')}</p>
      <dl className="space-y-2">
        {[
          [t('Payroll run'), monthLabel(selectedRun.month, selectedRun.year)],
          [t('Run status'), <EnumLabel key="s" enum="PayrollRunStatus" value={selectedRun.status} />],
          ...(selectedRun.lockedAtUtc ? [[t('Locked'), fx.dateTime(selectedRun.lockedAtUtc)]] : []),
          [t('Employees'), fx.integer(selectedRun.employeeCount)],
          [t('Run net pay'), <bdi key="n" dir="ltr">{fmtAmt(selectedRun.totalNetSalary, liveBatches[0]?.currency)}</bdi>],
          ...(selectedBatch ? [[t('Payment batch'), <bdi key="b" className="font-mono">{selectedBatch.batchNumber}</bdi>], [t('Payment method'), paymentMethodLabel(t, selectedBatch.paymentMethod)]] : []),
        ].map(([k, v]) => <div key={String(k)} className="grid grid-cols-[8.5rem_minmax(0,1fr)] items-baseline gap-3"><dt className="text-fg-muted">{k}</dt><dd className="text-end font-medium tabular-nums text-fg">{v}</dd></div>)}
      </dl>
    </div>
  );

  // Everyone the file pays (folded): code, name, IBAN tail and net, so joiners, leavers and partial pay can be seen before the check.
  const peopleInFile = recordsState.status === 'ready' && records.length > 0 && (
    <details className="rounded-lg border border-line" data-testid="bank-employees-in-file">
      <summary className="cursor-pointer px-3 py-2 text-body-sm font-medium text-fg">{t('Employees in this file ({count})', { count: records.length })}</summary>
      <div className="max-h-64 overflow-auto border-t border-line">
        <table className="w-full text-body-sm">
          <thead className="sticky top-0 bg-surface"><tr className="text-meta text-fg-muted">
            <th className="px-3 py-2 text-start font-medium">{t('Code')}</th>
            <th className="px-3 py-2 text-start font-medium">{t('Employee')}</th>
            <th className="px-3 py-2 text-start font-medium">IBAN</th>
            <th className="px-3 py-2 text-end font-medium">{t('Net pay')}</th>
          </tr></thead>
          <tbody className="divide-y divide-line">
            {records.map(r => (
              <tr key={r.id}>
                <td className="whitespace-nowrap px-3 py-1.5 font-mono text-fg-muted"><bdi>{r.employeeCode ?? '—'}</bdi></td>
                <td className="px-3 py-1.5 text-fg">{r.employeeName ? <bdi>{r.employeeName}</bdi> : runNames[r.employeeId] ? <EmployeeLabelText label={runNames[r.employeeId]} /> : t('Emp #{id}', { id: r.employeeId })}</td>
                <td className="px-3 py-1.5 font-mono text-fg-muted"><bdi dir="ltr">{r.iban ? `••${r.iban.replace(/\s+/g, '').slice(-4)}` : t('Missing IBAN')}</bdi></td>
                <td className="px-3 py-1.5 text-end tabular-nums text-fg"><bdi dir="ltr">{fx.number(r.amount, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</bdi></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </details>
  );

  const batchView = selectedBatch && (
    <div className="space-y-3">
      <dl className="grid grid-cols-2 gap-3 text-body-sm">
        <div><dt className="text-fg-muted">{t('Payment batch')}</dt><dd className="font-mono font-semibold text-fg"><bdi>{selectedBatch.batchNumber}</bdi></dd></div>
        <div><dt className="text-fg-muted">{t('Payment method')}</dt><dd className="font-medium text-fg">{paymentMethodLabel(t, selectedBatch.paymentMethod)}</dd></div>
        <div><dt className="text-fg-muted">{t('Amount')}</dt><dd className="font-medium tabular-nums text-fg"><bdi dir="ltr">{fmtAmt(selectedBatch.totalAmount, selectedBatch.currency)}</bdi></dd></div>
        <div><dt className="text-fg-muted">{t('Run status')}</dt><dd className="font-medium text-fg"><EnumLabel enum="PayrollRunStatus" value={selectedRun?.status} /></dd></div>
      </dl>
      {liveBatches.length > 1 && batchList}
    </div>
  );

  const batchRow = selectedBatch ? (batches.find(b => b.id === selectedBatch.id) ?? selectedBatch) : null;
  const wps = batchRow ? (batchRow.effectiveWpsStatus || batchRow.wpsStatus) : '';
  const responseBody = (data: BankPanelData | null | undefined) => {
    const split = payOut.split;
    const total = payOut.total;
    const answered = payOut.answered;
    const pct = total ? Math.round(((answered ?? 0) / total) * 100) : 0;
    const file = data?.file;
    const cur = batchRow?.currency;
    const fileAmount = file?.totalAmount ?? batchRow?.totalAmount ?? null;
    const gap = split && fileAmount !== null ? Math.round((fileAmount - split.totalAmount) * 100) / 100 : null;
    const answeredBad = records.filter(r => /^(returned|rejected)$/i.test(r.status ?? ''));
    return (
      <div className="space-y-5">
        {total !== undefined && answered !== undefined && (
          <div className="rounded-xl border border-line p-4" data-testid="bank-response-tracker">
            <p className="text-body font-semibold text-fg">{t('The bank has answered for {answered} of {total} payments', { answered, total })}</p>
            {/* One segment per bucket, sized by its money: green only for what the bank confirmed, amber for returned, red for rejected, grey for waiting. */}
            <div role="progressbar" aria-valuemin={0} aria-valuemax={total} aria-valuenow={answered} aria-label={t('Bank response')} className="mt-3 flex h-2 gap-px overflow-hidden rounded-full bg-line" data-essential-motion="">
              {split && split.totalAmount > 0 ? bankResponseBuckets(t, split).map(({ key, tone, amount }) => amount > 0 && (
                <div key={key} data-bucket={key} className={`h-full ${tone} transition-[width] duration-base ease-standard`} style={{ width: `${(amount / split.totalAmount) * 100}%` }} data-essential-motion="" />
              )) : (
                <div data-bucket="answered" className="h-full bg-primary transition-[width] duration-base ease-standard" style={{ width: `${pct}%` }} data-essential-motion="" />
              )}
            </div>
            {split ? (
              <>
                {/* Each bucket with its money; together they must be the money in the file. */}
                <dl className="mt-3 space-y-1.5 text-body-sm" data-testid="bank-response-buckets">
                  {bankResponseBuckets(t, split).map(({ tone: dot, label, amount }) => (
                    <div key={label} className="flex items-center justify-between gap-3">
                      <dt className="inline-flex items-center gap-1.5 text-fg"><span aria-hidden="true" className={`h-2.5 w-2.5 rounded ${dot}`} />{label}</dt>
                      <dd className="tabular-nums text-fg"><bdi dir="ltr">{fmtAmt(amount, cur)}</bdi></dd>
                    </div>
                  ))}
                </dl>
                {fileAmount !== null && (
                  <p className="mt-2 border-t border-line pt-2 text-body-sm text-fg-muted" data-testid="bank-response-tie">
                    {split.rejected > 0
                      ? t('Confirmed + Returned + Not paid + Waiting = {amount} (file)', { amount: fmtAmt(split.totalAmount, cur) })
                      : t('Confirmed + Returned + Waiting = {amount} (file)', { amount: fmtAmt(split.totalAmount, cur) })}
                  </p>
                )}
                {gap !== null && Math.abs(gap) >= 0.005 && (
                  <p role="alert" className="mt-1 text-body-sm font-semibold text-danger-fg" data-testid="bank-response-mismatch">
                    {t('Doesn’t match the file by {amount}', { amount: fmtAmt(gap, cur) })}
                  </p>
                )}
              </>
            ) : (
              // Without the payment records, only what the batch list says: answered and waiting (never a guessed split or amount).
              <p className="mt-3 text-body-sm text-fg">{t('{count} waiting', { count: total - answered })}</p>
            )}
          </div>
        )}
        {answeredBad.length > 0 && (
          <div className="rounded-xl border border-danger/40 p-4" data-testid="bank-returned-lines">
            <p className="text-body font-semibold text-fg">{t('Returned or rejected ({count})', { count: answeredBad.length })}</p>
            <ul className="mt-2 divide-y divide-line text-body-sm">
              {answeredBad.map(r => (
                <li key={r.id} className="flex items-start justify-between gap-3 py-1.5">
                  <span className="min-w-0 text-fg">
                    {r.employeeName ? <bdi>{r.employeeName}</bdi> : runNames[r.employeeId] ? <EmployeeLabelText label={runNames[r.employeeId]} /> : t('Emp #{id}', { id: r.employeeId })}
                    {(r.returnReasonText || r.returnReasonCode) && <span className="block text-meta text-fg-muted">{t('Reason')}: <bdi>{r.returnReasonText || r.returnReasonCode}</bdi></span>}
                  </span>
                  <span className="shrink-0 tabular-nums text-fg"><bdi dir="ltr">{fmtAmt(r.amount, cur)}</bdi></span>
                </li>
              ))}
            </ul>
            <p className="mt-2 text-meta text-fg-muted">{t('Pay them in a new payment batch linked to the original batch. It gets a new file reference; never upload this file again.')}</p>
            {onNavigate && <button type="button" className="mt-2 text-body-sm font-medium text-primary underline-offset-2 hover:underline" onClick={() => onNavigate('payment-tracking')}>{t('Open Payment Tracking')}</button>}
          </div>
        )}
        <dl className="grid grid-cols-2 gap-x-6 gap-y-3 text-body-sm sm:grid-cols-3">
          {data?.company && <div><dt className="text-fg-muted">{t('Company')}</dt><dd className="font-medium text-fg"><bdi>{data.company}</bdi></dd></div>}
          {wps === 'Submitted' && batchRow?.wpsStatusChangedAtUtc && <div><dt className="text-fg-muted">{t('Recorded as submitted on')}</dt><dd className="font-medium text-fg">{fx.dateTime(batchRow.wpsStatusChangedAtUtc)}</dd></div>}
          {batchRow?.wpsSubmissionReference && <div><dt className="text-fg-muted">{t('Bank reference')}</dt><dd className="font-mono font-medium text-fg"><bdi dir="ltr">{batchRow.wpsSubmissionReference}</bdi></dd></div>}
          {file && <div><dt className="text-fg-muted">{t('File reference')}</dt><dd className="font-mono font-medium text-fg"><bdi dir="ltr">{file.reference}</bdi></dd></div>}
          {file?.paymentDate && <div><dt className="text-fg-muted">{t('Credit date')}</dt><dd className="font-medium text-fg">{fx.gregorian(file.paymentDate, 'weekdayDate')}</dd></div>}
          {file && file.files.length > 0 && (
            <div className="col-span-2 sm:col-span-2"><dt className="text-fg-muted">{t('File fingerprint (SHA-256)')}</dt>
              <dd><details><summary className="cursor-pointer font-medium text-primary">{t('Show')}</summary><ul className="mt-1 space-y-0.5">{file.files.map(f => <li key={f.name} dir="ltr" className="break-all font-mono text-meta text-fg-muted">{f.name}: {f.sha256}</li>)}</ul></details></dd>
            </div>
          )}
        </dl>
        <div className="flex flex-wrap items-center gap-x-5 gap-y-2 text-body-sm">
          {panelMenu.current?.download && (
            <button type="button" className="font-medium text-primary underline-offset-2 hover:underline" aria-describedby="bank-submitted-banner" onClick={panelMenu.current.download}>{t('Download again (audit copy)')}</button>
          )}
          {recordsState.status === 'ready' && <button type="button" className="font-medium text-primary underline-offset-2 hover:underline" onClick={() => setRecordsOpen(true)} data-testid="bank-records-open">{t('View payment records ({count})', { count: records.length })}</button>}
        </div>
      </div>
    );
  };
  const responseAside = (data: BankPanelData | null | undefined) => (
    <div className="space-y-3">
      <p className="text-body font-semibold text-fg">{t('What happens next')}</p>
      <ol className="space-y-3">
        {[
          [t('The bank pays and answers for each payment'), null],
          [t('Record the bank’s response'), t('Record the bank’s response in Payment Tracking: confirmed, returned or rejected. Pay them in a new payment batch linked to the original batch. It gets a new file reference; never upload this file again.')],
          [t('Finance settles the payments'), t('Settle only payments the bank confirmed. Settling posts the net-pay settlement to the ledger and needs the payment settlement permission.')],
        ].map(([title, body], i) => (
          <li key={i} className="flex gap-3">
            <span className="grid h-5 w-5 shrink-0 place-items-center rounded-full border border-line-strong text-caption font-semibold tabular-nums text-fg-muted">{i + 1}</span>
            <span><span className="block font-medium text-fg">{title}</span>{body && <span className="block text-meta">{body}</span>}</span>
          </li>
        ))}
      </ol>
      {/* Auto-WPS is an employer choice: with it on the bank reports to Mudad; otherwise someone must upload the bank's WPS file. */}
      <p className="border-t border-line pt-3 text-meta" data-testid="bank-mudad-route">{data?.autoWps === true
        ? t('Your bank reports the salaries to WPS (Mudad). KynexOne does not send anything to Mudad.')
        : t('Your bank produces the signed WPS file. Upload it in Mudad yourself within the legal window. KynexOne does not send anything to Mudad.')}</p>
    </div>
  );
  const responsePurpose = (data: BankPanelData | null | undefined) => {
    switch (payOut.status) {
      case 'rejected': return rejectedFilePurpose(t, batchRow?.wpsRejectionReason);
      case 'confirmed': return confirmedResponsePurpose(t, payOut);
      case 'settled': return t('Paid and settled.');
      default: return data?.bankName
        ? t('Submitted to {bank} — waiting for the bank’s confirmation. Record what the bank confirms or returns in Payment Tracking.', { bank: data.bankName })
        : t('Submitted to the bank — waiting for the bank’s confirmation. Record what the bank confirms or returns in Payment Tracking.');
    }
  };

  const submitSummary = (data: BankPanelData | null | undefined) => {
    const f = data?.file;
    return t('{count} employees · {amount} · credit date {date} · file reference {ref}. Record this only after the bank has the file.', {
      count: f?.employeeCount ?? records.length, amount: fmtAmt(f?.totalAmount ?? selectedBatch?.totalAmount ?? 0, selectedBatch?.currency),
      date: f?.paymentDate ? fx.gregorian(f.paymentDate, 'weekdayDate') : '—', ref: f?.reference ?? '—',
    });
  };

  const tieRow = selectedBatch ? tieOut(batchRow ?? selectedBatch, selectedRun) : null;
  const tieGap = tieRow && Math.abs(tieRow.difference) >= 0.005 ? tieRow.difference : null;

  // Once the file is with the bank, a standing warning above the step: uploading it again can pay people twice.
  const submittedBanner = (data: BankPanelData | null | undefined) => {
    if (!['submitted', 'confirmed', 'rejected'].includes(payOut.status) || !batchRow) return null;
    const ref = batchRow.wpsSubmissionReference ?? '—';
    const when = wps === 'Submitted' && batchRow.wpsStatusChangedAtUtc ? fx.dateTime(batchRow.wpsStatusChangedAtUtc) : null;
    const bank = data?.bankName ?? null;
    const text = bank && when ? t('Recorded as submitted to {bank} on {date}, ref {ref}. Uploading this file again can pay employees twice.', { bank, date: when, ref })
      : bank ? t('Recorded as submitted to {bank}, ref {ref}. Uploading this file again can pay employees twice.', { bank, ref })
      : when ? t('Recorded as submitted on {date}, ref {ref}. Uploading this file again can pay employees twice.', { date: when, ref })
      : t('Recorded as submitted, ref {ref}. Uploading this file again can pay employees twice.', { ref });
    return (
      <p id="bank-submitted-banner" role="note" className="flex items-start gap-2 rounded-xl border border-warning/40 bg-warning-soft px-4 py-3 text-body-sm font-medium text-warning-fg" data-testid="bank-submitted-banner">
        <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />{text}
      </p>
    );
  };

  const guide = (panel: GuideSlots) => {
    panelData.current = panel.data ?? panelData.current;
    panelMenu.current = panel.menu ?? null;
    const data = panel.data ?? panelData.current;
    const response = { purpose: responsePurpose(data), body: responseBody(data), aside: responseAside(data) };
    return (
      <div className="space-y-3">
        {header(panel.menu)}
        <BankFileGuide
          payOut={payOut}
          loading={loadingGuide}
          confirmation={handOver.confirmation}
          confirmationSuccess={handOver.confirmationSuccess}
          onUserAction={handOver.onUserAction}
          banner={submittedBanner(data)}
          viewing={viewing}
          onView={setViewing}
          facts={selectedBatch && !loadingGuide ? (
            <BankFileFacts batch={batchRow ?? selectedBatch} run={selectedRun} employeesInFile={recordsState.status === 'ready' ? records.length : null}
              bankName={data?.bankName} accountTail={data?.accountTail} company={data?.company} checked={payOut.steps[1].state === 'done'}
              onShowLeftOut={payOut.steps[1].state === 'done' ? () => setViewing('check') : undefined} />
          ) : null}
          rail={selectedBatch && !loadingGuide ? (
            <BankFileFacts variant="rail" batch={batchRow ?? selectedBatch} run={selectedRun} employeesInFile={recordsState.status === 'ready' ? records.length : null}
              bankName={data?.bankName} accountTail={data?.accountTail} file={data?.file ?? null} company={data?.company} checked={payOut.steps[1].state === 'done'} />
          ) : null}
          slots={{
            ...panel,
            panels: {
              ...panel.panels,
              batch: {
                purpose: payOut.steps[0].reason === 'run_not_locked' ? t('Lock the run first: a payment batch can only be created once the run is Locked.')
                  : liveBatches.length > 1 ? t('Choose the payment batch you want to prepare.') : t('Create the payment batch for this run. It lists who is paid and how much.'),
                body: batchBody, view: batchView, aside: runSummary,
              },
              setup: { ...panel.panels?.setup, aside: runSummary },
              check: panel.panels?.check ? { ...panel.panels.check, body: <>{panel.panels.check.body}{peopleInFile}</> } : undefined,
              response, finished: response,
            },
            actions: {
              ...panel.actions,
              // Finance rule: a file that does not tie to the approved run is not created (derivePayOut turns the button off; this names the amount).
              ...(panel.actions?.create_file && tieGap !== null ? { create_file: { ...panel.actions.create_file, reason: t('The file total differs from the approved run by {amount}', { amount: fmtAmt(Math.abs(tieGap), selectedBatch?.currency) }) } } : {}),
              create_batch: { label: creating ? t('Creating…') : t('Create payment batch'), onClick: createBatch, busy: creating, disabled: creating || batchesState.status !== 'ready' },
              open_batch: { label: t('Open batch'), onClick: () => { const b = liveBatches.find(x => x.id === chosenBatchId); if (b) openBatch(b); }, disabled: !chosenBatchId, reason: chosenBatchId ? null : t('Choose the payment batch, then open it.') },
              record_submitted: { label: t('Record as submitted'), onClick: () => setSubmitOpen(true) },
              record_response: { label: t('Record the bank’s response'), onClick: () => onNavigate?.('payment-tracking') },
              prepare_corrected: { label: t('Prepare a corrected file'), onClick: () => onNavigate?.('payment-tracking') },
              settle: { label: t('Settle payments'), onClick: () => onNavigate?.('payment-tracking') },
            },
            live: (
              <>
                {error && <div role="alert" className="flex items-start gap-2 rounded-lg bg-danger-soft px-4 py-2.5 text-body-sm text-danger-fg"><AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" /><span>{error}</span></div>}
                {/* Payment records that could not be read are never "no records". */}
                {selectedBatch && recordsState.status === 'failed' && (isForbidden(recordsState.error)
                  ? <p role="alert" data-testid="bank-records-forbidden" className="text-body-sm text-fg-muted">{t('The payment records need the Payroll export permission. Ask an administrator to give it to your role.')}</p>
                  : <LoadFailedNotice error={recordsState.error} onRetry={() => openBatch(selectedBatch)} />)}
                {panel.live}
              </>
            ),
            overlays: (
              <>
                {panel.overlays}
                <SideDrawer isOpen={recordsOpen && !!selectedBatch} title={t('Records — {batchNumber}', { batchNumber: selectedBatch?.batchNumber ?? '' })} size="xl" placement="end" onClose={() => setRecordsOpen(false)} closeLabel={t('Close')}>
                  {recordsTable}
                </SideDrawer>
                {selectedBatch && (
                  <RecordSubmittedDialog open={submitOpen} batchId={selectedBatch.id} bankName={data?.bankName ?? null} summary={submitSummary(data)}
                    onClose={() => setSubmitOpen(false)}
                    onRecorded={ref => {
                      submittedRef.current = ref;
                      setSubmitOpen(false);
                      handOver.onUserAction();
                      onRunsChanged?.();
                      payrollApi.listPaymentBatches(runId).then(rows => { if (currentRunRef.current === runId) setBatches(rows); }).catch(() => {});
                    }} />
                )}
              </>
            ),
          }}
        />
      </div>
    );
  };

  return (
    <div className="space-y-4">
      {confirmDialog}
      {runsState.status === 'failed' && <>{header()}{failureBanners}</>}
      {runsState.status !== 'failed' && (
        selectedBatch
          ? <SaudiBankExportGate key={selectedBatch.id} batchId={selectedBatch.id} isSaudi onProgress={setFlow} suggestedReference={suggestedBatchReference(selectedBatch.batchNumber, selectedRun)}
              employeeIds={records.map(r => r.employeeId)} employeeLabels={{ ...runNames, ...employeeLabelsFromRecords(records) }} renderGuide={guide}
              batch={batchRow} recordsCount={recordsState.status === 'ready' ? records.length : null} onOpenRecords={() => setRecordsOpen(true)} />
          : guide({})
      )}
    </div>
  );
}

/** A section that works on the run bar's run, with none chosen: where to choose one. */
function NoRunChosen() {
  const t = useT();
  return (
    <div className="surface p-5 text-body text-fg-muted" data-testid="payroll-no-run">
      {t('Choose a payroll run in the run bar above.')}
    </div>
  );
}

/** "More ⌄": the section's secondary tools (drawers and dialogs). A menu button: arrows move, Escape closes and returns focus. */
function MoreMenu({ items }: { items: (false | null | undefined | { label: string; onSelect: () => void })[] }) {
  const t = useT();
  const list = items.filter((x): x is { label: string; onSelect: () => void } => !!x);
  const [open, setOpen] = useState(false);
  const button = useRef<HTMLButtonElement>(null);
  const menu = useRef<HTMLUListElement>(null);
  useEffect(() => {
    if (!open) return;
    menu.current?.querySelector<HTMLElement>('[role="menuitem"]')?.focus();
    const onDoc = (e: MouseEvent) => { if (!menu.current?.contains(e.target as Node) && !button.current?.contains(e.target as Node)) setOpen(false); };
    document.addEventListener('mousedown', onDoc);
    return () => document.removeEventListener('mousedown', onDoc);
  }, [open]);
  if (list.length === 0) return null;
  return (
    <div className="relative">
      <button ref={button} type="button" className="btn-secondary btn-sm" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(v => !v)} data-testid="bank-more">
        {t('More')}<ChevronDown className={`h-4 w-4 transition-transform motion-reduce:transition-none ${open ? 'rotate-180' : ''}`} aria-hidden="true" />
      </button>
      {open && (
        <ul ref={menu} role="menu" className="surface absolute end-0 top-full z-30 mt-1 min-w-[14rem] p-1 shadow-e3"
          onKeyDown={e => {
            if (e.key === 'Escape') { e.preventDefault(); setOpen(false); button.current?.focus(); }
            if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
              e.preventDefault();
              const all = Array.from(menu.current?.querySelectorAll<HTMLElement>('[role="menuitem"]') ?? []);
              const at = all.indexOf(document.activeElement as HTMLElement);
              all[(at + (e.key === 'ArrowDown' ? 1 : all.length - 1)) % all.length]?.focus();
            }
          }}>
          {list.map(item => (
            <li key={item.label} role="none">
              <button type="button" role="menuitem" className="flex min-h-9 w-full items-center rounded-lg px-3 text-start text-body text-fg hover:bg-surface-sunken" onClick={() => { setOpen(false); button.current?.focus(); item.onSelect(); }}>{item.label}</button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** A payment method as people say it ("Bank transfer", not "BankTransfer"). */
function paymentMethodLabel(t: ReturnType<typeof useT>, method: string): string {
  switch (method) {
    case 'WPS': return t('WPS bank file');
    case 'BankTransfer': return t('Bank transfer');
    case 'Cash': return t('Cash');
    case 'Cheque': return t('Cheque');
    default: return method;
  }
}

// ── Payment Tracking Tab ────────────────────────────────────────────────────────

function PaymentTrackingTab({ selectedRunId }: { selectedRunId?: string }) {
  const t = useT();
  const fx = useFormat();
  // WPS status, settlement and remittance need payroll.export (Admin) or the narrow key of their family: without one the row actions give way to the reason (the server refuses the wrong family).
  const { hasPermission } = useAuth();
  const canExport = paymentActionsAllowed(hasPermission);
  const fmtAmt = useFmtAmt();
  const { confirm, ask, dialog: confirmDialog } = useConfirm();
  type FinanceActionForm = { reference: string; date: string; group: 'GOSI' | 'TAX' | 'LOAN' | 'All'; reason: string };
  const [batches, setBatches] = useState<PayrollPaymentBatch[]>([]);
  const [loading, setLoading] = useState(true);
  type WpsForm = { status: string; reference: string; notes: string; evidenceKind: WpsEvidenceKind; evidenceFile: File | null };
  const [evidenceSaving, setEvidenceSaving] = useState<string | null>(null);
  type OutsideForm = { method: 'Cash' | 'Cheque'; reference: string; date: string };
  const [outsideForms, setOutsideForms] = useState<Record<string, OutsideForm>>({});
  const [outsideSaving, setOutsideSaving] = useState<string | null>(null);
  const [wpsForms, setWpsForms] = useState<Record<string, WpsForm>>({});
  const [wpsSaving, setWpsSaving] = useState<string | null>(null);
  const [financeSaving, setFinanceSaving] = useState<string | null>(null);
  const [financeForms, setFinanceForms] = useState<Record<string, FinanceActionForm>>({});

  // With a run selected on the page, this tab lists that run's batches (the same run the other tabs work on); "Show all runs" lifts that.
  const [showAllRuns, setShowAllRuns] = useState(false);
  const scopedRunId = !showAllRuns && selectedRunId ? selectedRunId : undefined;
  // A failed read is not an empty register: the tab says it could not load and offers Retry.
  const [loadError, setLoadError] = useState<unknown>(null);
  const loadBatches = () => {
    setLoading(true);
    payrollApi.listPaymentBatches(scopedRunId)
      .then(rows => { setBatches(rows); setLoadError(null); })
      .catch(error => { setBatches([]); setLoadError(error ?? new Error('load failed')); })
      .finally(() => setLoading(false));
  };

  useEffect(() => { loadBatches(); // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scopedRunId]);

  // R03 — each batch is labelled with its OWN currency (the same one its row shows), never the tenant
  // default, and batches in different currencies are totalled per currency rather than added together.
  const totals = totalsByCurrency(batches, b => b.currency, b => b.totalAmount);
  const totalLabel = totals.length === 1 && totals[0].currency ? fmtAmt(totals[0].total, totals[0].currency) : t('Mixed currencies');
  const fileGenerated = batches.filter(b => b.status === 'FileGenerated').length;
  // The server owns the WPS transition table and the effective status (a frozen ANB instruction counts as
  // Generated). The screen only renders what it is given, so the two can never drift apart again.
  const nextStatuses = (b: PayrollPaymentBatch) => b.allowedNextStatuses ?? [];
  const effectiveStatus = (b: PayrollPaymentBatch) => b.effectiveWpsStatus || b.wpsStatus || 'Draft';
  const emptyWpsForm: WpsForm = { status: '', reference: '', notes: '', evidenceKind: 'bank_output_file', evidenceFile: null };
  const setWpsForm = (batchId: string, patch: Partial<WpsForm>) =>
    setWpsForms(prev => ({ ...prev, [batchId]: { ...emptyWpsForm, ...(prev[batchId] ?? {}), ...patch } }));
  // Evidence is attached on its own: maker-checker means the person who uploads it (or generated the
  // file) cannot be the one who marks the batch Accepted.
  const uploadEvidence = async (batch: PayrollPaymentBatch) => {
    const form = wpsForms[batch.id] ?? emptyWpsForm;
    if (!form.evidenceFile) {
      notifyApiError({ response: { data: { message: t('Choose the bank\'s WPS output file or a Mudad compliance screenshot/PDF first.') } } });
      return;
    }
    setEvidenceSaving(batch.id);
    try {
      await payrollApi.uploadWpsEvidence(batch.id, form.evidenceKind, form.evidenceFile);
      setWpsForms(prev => ({ ...prev, [batch.id]: { ...emptyWpsForm, ...(prev[batch.id] ?? {}), evidenceFile: null } }));
      loadBatches();
    } catch (e) { notifyApiError(e); }
    finally { setEvidenceSaving(null); }
  };
  const outsideKey = (batchId: string, employeeId: number) => `${batchId}:${employeeId}`;
  const outsideForm = (key: string): OutsideForm =>
    outsideForms[key] ?? { method: 'Cash', reference: '', date: new Date().toISOString().slice(0, 10) };
  const recordOutside = async (batch: PayrollPaymentBatch, employeeId: number) => {
    const key = outsideKey(batch.id, employeeId);
    const form = outsideForm(key);
    if (!form.reference.trim()) {
      notifyApiError({ response: { data: { message: t('Enter the cheque number or cash receipt reference.') } } });
      return;
    }
    setOutsideSaving(key);
    try {
      await payrollApi.recordOutsidePayment(batch.id, { employeeId, method: form.method, reference: form.reference.trim(), paidDate: form.date || undefined });
      loadBatches();
    } catch (e) { notifyApiError(e); }
    finally { setOutsideSaving(null); }
  };
  const updateWps = async (batch: PayrollPaymentBatch) => {
    const form = wpsForms[batch.id] ?? emptyWpsForm;
    if (!form.status) return;
    // Accepted is recorded only against stored proof (the bank's WPS output file or a Mudad
    // screenshot/PDF already attached to this batch), and only by someone other than its maker.
    const evidenceId = batch.latestEvidenceId ?? undefined;
    if (form.status === 'Accepted' && !evidenceId) {
      notifyApiError({ response: { data: { message: t('Attach the bank\'s WPS output file or a Mudad compliance screenshot/PDF to this batch first. Another person then marks it Accepted.') } } });
      return;
    }
    if (form.status === 'Accepted' && batch.acceptBlockedReason) {
      notifyApiError({ response: { data: { message: batch.acceptBlockedReason } } });
      return;
    }
    setWpsSaving(batch.id);
    try {
      await payrollApi.updateWpsStatus(batch.id, {
        status: form.status,
        reference: form.reference || undefined,
        notes: form.notes || undefined,
        evidenceId: form.status === 'Accepted' ? evidenceId : undefined,
      });
      setWpsForms(prev => ({ ...prev, [batch.id]: emptyWpsForm }));
      loadBatches();
    } catch (e) { notifyApiError(e); }
    finally { setWpsSaving(null); }
  };
  const defaultFinanceForm: FinanceActionForm = { reference: '', date: new Date().toISOString().slice(0, 10), group: 'All', reason: '' };
  const financeForm = (batchId: string) => financeForms[batchId] ?? defaultFinanceForm;
  const setFinanceForm = (batchId: string, patch: Partial<FinanceActionForm>) =>
    setFinanceForms(prev => ({ ...prev, [batchId]: { ...defaultFinanceForm, ...(prev[batchId] ?? {}), ...patch } }));
  const runFinanceAction = async (batch: PayrollPaymentBatch, action: 'settle' | 'reverse-settle' | 'remit' | 'reverse-remit') => {
    const form = financeForm(batch.id);
    if (action.startsWith('reverse') && !form.reason.trim()) {
      notifyApiError({ response: { data: { message: t('A reason is required for reversal.') } } });
      return;
    }
    // Every action that posts to the general ledger says what will post before it does: the batch, the amount, the date and the journal.
    // The accounts are not part of the data this tab holds (they come from the GL setup), so they are not listed.
    const postingFacts = (rows: [string, string][]) => (
      <dl className="mt-3 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 rounded-lg bg-slate-50 p-3 text-sm dark:bg-white/5" data-testid="gl-post-confirm">
        {rows.map(([k, v]) => <React.Fragment key={k}><dt className="text-fg-muted">{k}</dt><dd className="font-medium text-fg"><bdi>{v}</bdi></dd></React.Fragment>)}
      </dl>
    );
    const when = form.date ? fx.date(form.date) : t('Today');
    const batchRow: [string, string] = [t('Payment batch'), batch.batchNumber];
    const amountRow: [string, string] = [t('Amount'), fmtAmt(batch.totalAmount, batch.currency)];
    const groupName = form.group === 'All' ? t('GOSI, tax and loan remittances') : form.group === 'GOSI' ? t('GOSI remittance') : form.group === 'TAX' ? t('Tax remittance') : t('Loan remittance');
    if (action === 'settle' && !(await confirm({
      title: t('Post the net pay settlement to the general ledger?'),
      message: (
        <div>
          <p>{t('This posts a journal to the general ledger. It is reversed with a contra journal, not deleted.')}</p>
          {postingFacts([batchRow, [t('Journal'), t('Net pay settlement')], amountRow, [t('Paid date'), when], ...(form.reference ? [[t('Reference'), form.reference] as [string, string]] : [])])}
        </div>
      ),
      confirmLabel: t('Post settlement'),
      tone: 'primary',
    }))) return;
    if (action === 'remit' && !(await confirm({
      title: t('Post the statutory remittance to the general ledger?'),
      message: (
        <div>
          <p>{t('This posts a journal to the general ledger. It is reversed with a contra journal, not deleted. The amounts come from the run and appear on the GL Journal tab once posted.')}</p>
          {postingFacts([batchRow, [t('Journal'), groupName], [t('Remittance date'), when], ...(form.reference ? [[t('Reference'), form.reference] as [string, string]] : [])])}
        </div>
      ),
      confirmLabel: t('Post remittance'),
      tone: 'primary',
    }))) return;
    if (action.startsWith('reverse') && !(await confirm({
      title: t('Post a contra journal?'),
      message: (
        <div>
          <p>{t('Reversing posts a contra journal that offsets the original entry. The reason you entered is sent with the reversal.')}</p>
          {postingFacts([batchRow, [t('Journal'), action === 'reverse-settle' ? t('Net pay settlement reversal') : groupName], ...(action === 'reverse-settle' ? [amountRow] : []), [t('Reason'), form.reason.trim()]])}
        </div>
      ),
      confirmLabel: t('Post contra journal'),
    }))) return;
    setFinanceSaving(`${batch.id}:${action}`);
    try {
      if (action === 'settle') await payrollApi.settlePaymentBatch(batch.id, { reference: form.reference || undefined, paidDate: form.date || undefined });
      if (action === 'reverse-settle') await payrollApi.reversePaymentSettlement(batch.id, form.reason);
      if (action === 'remit') await payrollApi.remitRun(batch.payrollRunId, { group: form.group, reference: form.reference || undefined, remitDate: form.date || undefined });
      if (action === 'reverse-remit') {
        if (form.group === 'All') throw new Error(t('Choose GOSI, TAX, or LOAN before reversing a remittance.'));
        await payrollApi.reverseRemittance(batch.payrollRunId, { group: form.group, reason: form.reason });
      }
      loadBatches();
    } catch (e) { notifyApiError(e); }
    finally { setFinanceSaving(null); }
  };

  return (
    <div className="space-y-4">
      {confirmDialog}
      {!canExport && (
        <div role="note" className="flex items-start gap-2 rounded-lg bg-amber-50 px-4 py-2.5 text-sm text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
          <span>{t('Only an administrator can create payment batches, generate WPS files, settle net pay and remit. You can still review them here.')}</span>
        </div>
      )}
      {scopedRunId && (
        <p role="note" data-testid="tracking-run-scope" className="flex flex-wrap items-center gap-2 text-xs text-slate-500 dark:text-slate-400">
          {t('Showing the payment batches of the selected run.')}
          <button type="button" className={btn.link} onClick={() => setShowAllRuns(true)}>{t('Show all runs')}</button>
        </p>
      )}
      {!scopedRunId && showAllRuns && selectedRunId && (
        <p role="note" className="flex flex-wrap items-center gap-2 text-xs text-slate-500 dark:text-slate-400">
          {t('Showing the payment batches of every run.')}
          <button type="button" className={btn.link} onClick={() => setShowAllRuns(false)}>{t('Show the selected run only')}</button>
        </p>
      )}
      {batches.length > 0 && (
        <div className="grid grid-cols-3 gap-4">
          <KpiCard label={t('Total Batches')} value={batches.length} icon={WalletCards} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
          <KpiCard label={t('Bank / payroll files generated')} value={fileGenerated} icon={CheckCircle2} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
          <KpiCard label={t('Total Amount')} value={totalLabel} sub={totalLabel === t('Mixed currencies') ? perCurrency(totals, fmtAmt, t) : undefined} icon={TrendingUp} color="bg-cyan-100 text-cyan-600 dark:bg-cyan-500/20 dark:text-cyan-400" />
        </div>
      )}
      {loading ? <SkeletonRows rows={3} />
        : loadError != null ? <div className="surface"><LoadFailedNotice error={loadError} onRetry={loadBatches} /></div>
        : batches.length === 0 ? (
        <div className="surface flex flex-col items-center py-16 text-center">
          <TrendingUp className="mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm font-medium text-slate-600 dark:text-slate-400">{t('No payment batches yet')}</p>
          <p className="text-xs text-slate-400">{t('Create payment batches from the Bank/WPS Files tab after processing a payroll run.')}</p>
        </div>
      ) : (
        <div className="surface overflow-hidden">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                {[t('Batch'), t('Method'), t('Total Amount'), t('Currency'), t('Status'), t('Created'), t('Action')].map(h => (
                  <th key={h} className="px-4 py-2 text-start text-xs font-bold uppercase text-fg-muted">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {batches.map(b => (
                <tr key={b.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-2 font-mono text-xs font-medium text-slate-900 dark:text-white">{b.batchNumber}</td>
                  <td className="px-4 py-2 text-slate-500">{b.paymentMethod}</td>
                  <td className="px-4 py-2 font-semibold text-slate-700 dark:text-slate-300">{b.totalAmount.toLocaleString('en-US', { minimumFractionDigits: 2 })}</td>
                  <td className="px-4 py-2 text-slate-500">{fx.currencyLabel(b.currency)}</td>
                  <td className="px-4 py-2">
                    <div className="space-y-1">
                      <StatusBadge status={effectiveStatus(b)} />
                      {wpsStatusNote(t, b.wpsStatusLabel, effectiveStatus(b)) && <p className="max-w-56 text-[11px] text-slate-500">{wpsStatusNote(t, b.wpsStatusLabel, effectiveStatus(b))}</p>}
                      {(b.paymentExclusions?.length ?? 0) > 0 && (
                        <p className="max-w-56 text-[11px] text-amber-600 dark:text-amber-400" title={b.paymentExclusions!.map(x => `${x.employeeCode}: ${x.reason}`).join('\n')}>
                          {t('{length} not in the bank file (cash/cheque or zero net): {toLocaleString}', { length: b.paymentExclusions!.length, toLocaleString: (b.excludedTotal ?? 0).toLocaleString('en-US', { minimumFractionDigits: 2 }) })}
                        </p>
                      )}
                      {b.wpsSubmissionReference && <p className="text-[11px] text-slate-500">{b.wpsSubmissionReference}</p>}
                      {(b.acceptanceEvidenceCount ?? 0) > 0 && <p className="text-[11px] text-emerald-600 dark:text-emerald-400">{t('{count, plural, one {Evidence on record: # file} other {Evidence on record: # files}}', { count: b.acceptanceEvidenceCount ?? 0 })}</p>}
                      {b.wpsRejectionReason && <p className="max-w-48 truncate text-[11px] text-rose-500">{b.wpsRejectionReason}</p>}
                    </div>
                  </td>
                  <td className="px-4 py-2 text-xs text-slate-400">{fx.date(b.createdAtUtc)}</td>
                  <td className="px-4 py-2">
                    {!canExport ? (
                      <p className="text-xs text-slate-500 dark:text-slate-400" title={t(PAYMENT_ACTION_DENIED.wps)}>{t('Needs an administrator')}</p>
                    ) : (
                    <div className="grid min-w-[290px] gap-2">
                    {effectiveStatus(b) === 'Submitted' && (
                      <div className="grid gap-1.5 rounded-lg bg-slate-50 p-2 dark:bg-white/5">
                        <p className="text-[11px] font-medium text-slate-600 dark:text-slate-300">{t('Acceptance evidence')}</p>
                        <select
                          aria-label={t('Evidence type for {batchNumber}', { batchNumber: b.batchNumber })}
                          className={sel}
                          value={wpsForms[b.id]?.evidenceKind ?? 'bank_output_file'}
                          onChange={e => setWpsForm(b.id, { evidenceKind: e.target.value as WpsEvidenceKind, evidenceFile: null })}
                        >
                          <option value="bank_output_file">{t('Bank WPS output file (upload it unopened)')}</option>
                          <option value="mudad_compliance_screenshot">{t('Mudad compliance screenshot or PDF')}</option>
                        </select>
                        <div className="grid grid-cols-[1fr_auto] gap-2">
                          <input
                            type="file"
                            aria-label={t('Evidence file for {batchNumber}', { batchNumber: b.batchNumber })}
                            className="text-xs"
                            accept={(wpsForms[b.id]?.evidenceKind ?? 'bank_output_file') === 'mudad_compliance_screenshot' ? '.png,.jpg,.jpeg,.pdf' : undefined}
                            onChange={e => setWpsForm(b.id, { evidenceFile: e.target.files?.[0] ?? null })}
                          />
                          <button type="button" className={`${btn.sm} disabled:opacity-50`} onClick={() => uploadEvidence(b)} disabled={evidenceSaving === b.id || !wpsForms[b.id]?.evidenceFile}>
                            {evidenceSaving === b.id ? t('Uploading…') : t('Attach evidence')}
                          </button>
                        </div>
                        <p className="text-[11px] text-slate-500">{t('The file is stored with its SHA-256 fingerprint. Mudad\'s verdict never reaches this system, so Accepted means “evidence attached”, and a second person records it.')}</p>
                      </div>
                    )}
                    {nextStatuses(b).length > 0 && (
                      <>
                        <select
                          title={t('Next WPS status')}
                          className={sel}
                          value={wpsForms[b.id]?.status ?? ''}
                          onChange={e => setWpsForm(b.id, { status: e.target.value })}
                        >
                          <option value="">{t('Next status')}</option>
                          {nextStatuses(b).map(s => (
                            <option key={s} value={s} disabled={s === 'Accepted' && (!b.latestEvidenceId || !!b.acceptBlockedReason)}>
                              {s === 'Accepted' ? t('Accepted — evidence attached (not verified by Mudad)') : enumLabel(t, 'Status', s)}
                            </option>
                          ))}
                        </select>
                        {nextStatuses(b).includes('Accepted') && !b.latestEvidenceId && (
                          <p className="text-[11px] text-slate-500">{t('Attach evidence above before the batch can be marked Accepted.')}</p>
                        )}
                        {nextStatuses(b).includes('Accepted') && b.latestEvidenceId && b.acceptBlockedReason && (
                          <p className="text-[11px] text-amber-600 dark:text-amber-400">{b.acceptBlockedReason}</p>
                        )}
                        <div className="grid grid-cols-[1fr_auto] gap-2">
                          <input
                            className={inp}
                            placeholder={(wpsForms[b.id]?.status ?? '') === 'Rejected' ? t('Rejection reason') : t('Reference')}
                            value={(wpsForms[b.id]?.status ?? '') === 'Rejected' ? (wpsForms[b.id]?.notes ?? '') : (wpsForms[b.id]?.reference ?? '')}
                            onChange={e => setWpsForm(b.id, (wpsForms[b.id]?.status ?? '') === 'Rejected' ? { notes: e.target.value } : { reference: e.target.value })}
                          />
                          <button type="button" className={btn.primary} onClick={() => updateWps(b)} disabled={wpsSaving === b.id || !(wpsForms[b.id]?.status)}>
                            {wpsSaving === b.id ? t('Saving…') : t('Save')}
                          </button>
                        </div>
                      </>
                    )}
                    {(b.wpsStatus === 'Accepted' || b.wpsStatus === 'Paid') && (
                      <div className="grid gap-2 border-t border-slate-100 pt-2 dark:border-white/10">
                        <div className="grid grid-cols-2 gap-2">
                          <input className={inp} type="date" aria-label={t('Finance action date for {batchNumber}', { batchNumber: b.batchNumber })} value={financeForm(b.id).date} onChange={e => setFinanceForm(b.id, { date: e.target.value })} />
                          <input className={inp} aria-label={t('Finance reference for {batchNumber}', { batchNumber: b.batchNumber })} placeholder={t(t('Bank / filing reference'))} value={financeForm(b.id).reference} onChange={e => setFinanceForm(b.id, { reference: e.target.value })} />
                        </div>
                        {b.wpsStatus === 'Accepted' && <button type="button" className={btn.primary} onClick={() => runFinanceAction(b, 'settle')} disabled={financeSaving !== null}>{t('Settle net pay to GL')}</button>}
                        {b.wpsStatus === 'Paid' && (
                          <>
                            <div className="grid grid-cols-[1fr_auto] gap-2">
                              <select className={sel} aria-label={t('Remittance group for {batchNumber}', { batchNumber: b.batchNumber })} value={financeForm(b.id).group} onChange={e => setFinanceForm(b.id, { group: e.target.value as 'GOSI' | 'TAX' | 'LOAN' | 'All' })}>
                                {['All', 'GOSI', 'TAX', 'LOAN'].map(g => <option key={g} value={g}>{g === 'All' ? t('All') : g}</option>)}
                              </select>
                              <button type="button" className={btn.primary} onClick={() => runFinanceAction(b, 'remit')} disabled={financeSaving !== null}>{t('Remit')}</button>
                            </div>
                            <input className={inp} aria-label={t('Reversal reason for {batchNumber}', { batchNumber: b.batchNumber })} placeholder={t(t('Required reversal reason'))} value={financeForm(b.id).reason} onChange={e => setFinanceForm(b.id, { reason: e.target.value })} />
                            <div className="flex gap-2">
                              <button type="button" className={btn.danger} onClick={() => runFinanceAction(b, 'reverse-settle')} disabled={financeSaving !== null}>{t('Reverse settlement')}</button>
                              <button type="button" className={btn.danger} onClick={() => runFinanceAction(b, 'reverse-remit')} disabled={financeSaving !== null || financeForm(b.id).group === 'All'}>{t('Reverse remit')}</button>
                            </div>
                          </>
                        )}
                      </div>
                    )}
                    {(b.paymentExclusions ?? []).some(x => x.canRecordOutsidePayment) && (
                      <div className="grid gap-1.5 border-t border-slate-100 pt-2 dark:border-white/10">
                        <p className="text-[11px] font-medium text-slate-600 dark:text-slate-300">{t('Paid outside the bank file')}</p>
                        {b.mudadNote && <p className="text-[11px] text-amber-600 dark:text-amber-400">{b.mudadNote}</p>}
                        {(b.paymentExclusions ?? []).filter(x => x.canRecordOutsidePayment).map(x => {
                          const key = outsideKey(b.id, x.employeeId);
                          const f = outsideForm(key);
                          return x.outsidePaymentRecorded ? (
                            <div key={key} className="flex items-center justify-between gap-2">
                              <p className="text-[11px] text-emerald-600 dark:text-emerald-400">{t('{employeeCode}: payment recorded', { employeeCode: x.employeeCode })}</p>
                              {b.wpsStatus !== 'Reconciled' && (
                                <button type="button" className={`${btn.sm} disabled:opacity-50`} disabled={outsideSaving === key}
                                  onClick={async () => {
                                    const answer = await ask({
                                      title: t('Reverse the payment recorded for {code}?', { code: x.employeeCode }),
                                      message: t("Why is this employee's recorded payment being reversed (for example, a cheque bounced)?"),
                                      confirmLabel: t('Reverse payment'),
                                      reason: { required: true },
                                    });
                                    if (!answer.confirmed) return;
                                    setOutsideSaving(key);
                                    try { await payrollApi.reverseOutsidePayment(b.id, x.employeeId, answer.reason); loadBatches(); }
                                    catch (err) { notifyApiError(err); }
                                    finally { setOutsideSaving(null); }
                                  }}>
                                  {t('Reverse payment')}
                                </button>
                              )}
                            </div>
                          ) : (
                            <div key={key} className="grid gap-1 rounded-lg bg-slate-50 p-2 dark:bg-white/5">
                              <p className="text-[11px] text-slate-600 dark:text-slate-300">
                                {x.employeeCode} — {x.amount.toLocaleString('en-US', { minimumFractionDigits: 2 })} {b.currency}
                              </p>
                              <div className="grid grid-cols-[auto_1fr] gap-1.5">
                                <select className={sel} aria-label={t('Payment method for {employeeCode}', { employeeCode: x.employeeCode })} value={f.method}
                                  onChange={e => setOutsideForms(p => ({ ...p, [key]: { ...f, method: e.target.value as 'Cash' | 'Cheque' } }))}>
                                  <option value="Cash">{t('Cash')}</option>
                                  <option value="Cheque">{t('Cheque')}</option>
                                </select>
                                <input className={inp} aria-label={t('Cheque or receipt reference for {employeeCode}', { employeeCode: x.employeeCode })} placeholder={t(t('Cheque no. / receipt ref.'))}
                                  value={f.reference} onChange={e => setOutsideForms(p => ({ ...p, [key]: { ...f, reference: e.target.value } }))} />
                              </div>
                              <div className="grid grid-cols-[1fr_auto] gap-1.5">
                                <input className={inp} type="date" aria-label={t('Date paid for {employeeCode}', { employeeCode: x.employeeCode })} value={f.date}
                                  onChange={e => setOutsideForms(p => ({ ...p, [key]: { ...f, date: e.target.value } }))} />
                                <button type="button" className={`${btn.sm} disabled:opacity-50`} onClick={() => recordOutside(b, x.employeeId)} disabled={outsideSaving === key}>
                                  {outsideSaving === key ? t('Recording…') : t('Record payment outside the bank file')}
                                </button>
                              </div>
                            </div>
                          );
                        })}
                      </div>
                    )}
                    {nextStatuses(b).length === 0 && effectiveStatus(b) !== 'Accepted' && effectiveStatus(b) !== 'Paid' && <span className="text-xs text-slate-400">{t('No action')}</span>}
                    </div>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ── Reports Tab ─────────────────────────────────────────────────────────────────

function ReportsTab({ selectedRunId, onRunChange }: { selectedRunId?: string; onRunChange?: (id: string | null) => void }) {
  const t = useT();
  const fmtAmt = useFmtAmt();
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [runId, setRunId] = useSharedRun(selectedRunId, onRunChange);
  const [slips, setSlips] = useState<PayrollSlip[]>([]);
  const [summary, setSummary] = useState<PayrollSummary | null>(null);
  const [loading, setLoading] = useState(false);
  const registerCurrency = useRunCurrency(runs.find(r => r.id === runId));

  // R03 — year to date is per legal entity: each company's locked runs are labelled with that company's
  // currency and currencies are never added together. An API without the per-company breakdown gets its
  // figure shown unlabelled rather than with the tenant default.
  const ytd = (pick: (row: { totalGrossYtd: number; totalNetYtd: number }) => number): { value: string; sub?: string } => {
    if (!summary) return { value: '' };
    if (!summary.ytdByCompany) return { value: unconfirmedAmount(pick(summary), t) };
    if (summary.ytdByCompany.length === 0) return { value: t('None yet'), sub: t('No locked run this year') };
    const totals = totalsByCurrency(summary.ytdByCompany, row => {
      const resolved = resolvePayrollRunCurrency({ companyId: row.companyId }, registerCurrency.companies, registerCurrency.companiesState);
      return resolved.status === 'resolved' ? resolved.currency : null;
    }, pick);
    if (totals.length === 1) return { value: totals[0].currency ? fmtAmt(totals[0].total, totals[0].currency) : unconfirmedAmount(totals[0].total, t) };
    return { value: t('Mixed currencies'), sub: perCurrency(totals, fmtAmt, t) };
  };
  const grossYtd = ytd(row => row.totalGrossYtd);
  const netYtd = ytd(row => row.totalNetYtd);

  useEffect(() => {
    payrollApi.listAllRuns().then(setRuns).catch(() => {});
    payrollApi.reportSummary().then(setSummary).catch(() => {});
  }, []);

  const loadRegister = () => {
    if (!runId) return;
    setLoading(true);
    payrollApi.reportRegister(runId).then(setSlips).catch(() => {}).finally(() => setLoading(false));
  };

  const fmt = (n: number) => n.toLocaleString('en-US', { minimumFractionDigits: 2 });

  return (
    <div className="space-y-6">
      {summary && (
        <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
          <KpiCard label={t('Total Runs')} value={summary.totalRuns} icon={FileText} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
          <KpiCard label={t('Locked Runs')} value={summary.lockedRuns} icon={Lock} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
          <KpiCard label={t('Gross YTD')} value={grossYtd.value} sub={grossYtd.sub} icon={WalletCards} color="bg-cyan-100 text-cyan-600 dark:bg-cyan-500/20 dark:text-cyan-400" />
          <KpiCard label={t('Net YTD')} value={netYtd.value} sub={netYtd.sub} icon={TrendingUp} color="bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400" />
        </div>
      )}

      <div className="surface p-5">
        <p className="mb-4 text-sm font-semibold text-slate-800 dark:text-white">{t('Payroll Register')}</p>
        <div className="flex items-center gap-3">
          <select aria-label={t('Select payroll run for register')} className={`${sel} max-w-xs flex-1`} value={runId} onChange={e => setRunId(e.target.value)}>
            <option value="">{t('Select a payroll run…')}</option>
            <RunSelectOptions runs={runs} />
          </select>
          <button type="button" className={btn.primary} onClick={loadRegister} disabled={!runId || loading}>
            {loading ? t('Loading…') : t('Load Register')}
          </button>
        </div>

        {slips.length > 0 && (
          <>
            <div className="mt-4 rounded-lg bg-slate-50 px-4 py-3 dark:bg-white/5">
              <p className="text-sm text-slate-600 dark:text-slate-400">
                {t('{length} employees · Gross {amount} · Net {amount2}', { length: slips.length, amount: registerCurrency.amount(slips.reduce((s, x) => s + x.grossSalary, 0)), amount2: registerCurrency.amount(slips.reduce((s, x) => s + x.netSalary, 0)) })}
              </p>
            </div>
            <RunCurrencyNotice resolution={registerCurrency.resolution} companiesState={registerCurrency.companiesState} onRetry={registerCurrency.retry} />
            <div className="mt-4 overflow-x-auto">
              <table className="w-full min-w-[640px] text-sm">
                <thead>
                  <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                    {/* COLUMN COMPOSITION — read off PayrollController.Process, not off the old headers:
                        · slip.OtherAllowances = otherAllowances + overtime + bonuses + adjustments +
                          arrears + settlement earnings + configured earnings (PayrollController.cs:2693).
                          It is NOT "other allowances", so it is headed "Other earnings".
                        · slip.GrossSalary = Basic + Housing + Transport + OtherAllowances (:2211-2212, :2694)
                          — the four earning columns are disjoint and sum exactly to Gross.
                        · slip.Deductions = everything withheld INCLUDING loan/advance EMIs and the employee
                          GOSI share (:2542-2543, :2635). Loans and GOSI are therefore subsets, headed "of which", and
                          printed unbracketed so nobody subtracts them a second time.
                        · Net = Gross − Deductions. */}
                    {[t('Code'), t('Employee'), t('Department'), t('Basic'), t('Housing'), t('Transport'), t('Other earnings'), t('Gross'), t('Deductions'), t('Of which: Loans'), t('Net'), t('YTD Gross'), t('YTD Net')].map(h => (
                      <th key={h} className="px-3 py-2 text-start text-xs font-bold uppercase text-fg-muted">{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                  {slips.map(s => (
                    <tr key={s.id} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                      <td className="px-3 py-2 font-mono text-xs text-slate-500">{s.employeeCode}</td>
                      <td className="px-3 py-2 font-medium text-slate-900 dark:text-white">{s.employeeName}</td>
                      <td className="px-3 py-2 text-xs text-slate-500">{s.department || '—'}</td>
                      <td className="px-3 py-2 text-end text-slate-700 dark:text-slate-300">{fmt(s.basicSalary)}</td>
                      <td className="px-3 py-2 text-end text-slate-700 dark:text-slate-300">{fmt(s.housingAllowance)}</td>
                      <td className="px-3 py-2 text-end text-slate-700 dark:text-slate-300">{fmt(s.transportAllowance)}</td>
                      <td className="px-3 py-2 text-end text-slate-700 dark:text-slate-300">{fmt(s.otherAllowances)}</td>
                      <td className="px-3 py-2 text-end font-semibold text-slate-900 dark:text-white">{fmt(s.grossSalary)}</td>
                      <td className="px-3 py-2 text-end text-rose-500">
                        ({fmt(s.deductions)})
                        {s.employeeStatutoryTotal > 0 && (
                          <p className="text-[10px] leading-tight text-rose-400">{t('Of which GOSI {employeeStatutoryTotal}', { employeeStatutoryTotal: fmt(s.employeeStatutoryTotal) })}</p>
                        )}
                      </td>
                      {/* Unbracketed on purpose: this is a slice of the Deductions cell to its left, not a
                          further subtraction. Bracketing it invited exactly the double-count P5 found. */}
                      <td className="px-3 py-2 text-end text-amber-600 dark:text-amber-400">{s.loanDeductions > 0 ? fmt(s.loanDeductions) : '—'}</td>
                      <td className="px-3 py-2 text-end font-bold text-emerald-600 dark:text-emerald-400">{fmt(s.netSalary)}</td>
                      <td className="px-3 py-2 text-end text-xs text-slate-500">{fmt(s.ytdGross)}</td>
                      <td className="px-3 py-2 text-end text-xs font-semibold text-slate-700 dark:text-slate-300">{fmt(s.ytdNet)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div className="mt-3 rounded-lg border border-slate-200 bg-slate-50 px-4 py-3 text-xs leading-relaxed text-slate-600 dark:border-white/10 dark:bg-white/5 dark:text-slate-400">
              <p><span className="font-semibold text-slate-700 dark:text-slate-300">{t('How a row adds up')}:</span> {t('Basic + Housing + Transport + Other earnings = Gross. Gross − Deductions = Net.')}</p>
              <p className="mt-1"><span className="font-semibold text-slate-700 dark:text-slate-300">{t('Other earnings')}</span>: {t('Everything paid this period that is not basic, housing or transport — other allowances, overtime, bonuses, arrears, adjustments and final-settlement earnings.')}</p>
              <p className="mt-1"><span className="font-semibold text-slate-700 dark:text-slate-300">{t('Loans and GOSI are already inside Deductions.')}</span> {t('They are shown so you can see what Deductions is made of — do not subtract them again.')}</p>
            </div>
          </>
        )}
      </div>

      <CostCentreJournalReport />
    </div>
  );
}

// ── AI Validation Tab ───────────────────────────────────────────────────────────

function AIValidationTab({ selectedRunId, onRunChange }: { selectedRunId?: string; onRunChange?: (id: string | null) => void }) {
  const t = useT();
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [runId, setRunId] = useSharedRun(selectedRunId, onRunChange);
  const [result, setResult] = useState<{ advisoryOnly: boolean; warnings: PayrollValidationResult[]; summary: string } | null>(null);
  const [loading, setLoading] = useState(false);
  const names = useRunEmployeeLabels(runId);

  useEffect(() => { payrollApi.listAllRuns().then(setRuns).catch(() => {}); }, []);

  const runAI = async () => {
    if (!runId) return;
    setLoading(true);
    payrollApi.aiValidation(runId).then(r => setResult(r as typeof result)).catch(() => {}).finally(() => setLoading(false));
  };

  return (
    <div className="space-y-4">
      <div className="surface p-4">
        <div className="flex items-start gap-3">
          <ShieldCheck className="mt-0.5 h-5 w-5 shrink-0 text-sapphire dark:text-cyanAccent" />
          <div>
            <p className="text-sm font-semibold text-slate-800 dark:text-white">{t('Payroll Anomaly Checks — Advisory Only')}</p>
            <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
              {t('These checks flag anomalies like salary variance, unusual overtime, missing IBANs, and duplicate entries. They do not approve payroll or change salaries automatically.')}
            </p>
          </div>
        </div>
      </div>

      <div className="flex items-center gap-3">
        <select aria-label={t('Select payroll run for anomaly checks')} className={`${sel} max-w-xs flex-1`} value={runId} onChange={e => setRunId(e.target.value)}>
          <option value="">{t('Select a payroll run…')}</option>
          <RunSelectOptions runs={runs} />
        </select>
        <button type="button" className={btn.primary} onClick={runAI} disabled={!runId || loading}>
          <Zap className="h-4 w-4" /> {loading ? t('Analyzing…') : t('Run Checks')}
        </button>
      </div>

      {result && (
        <>
          <div className="surface p-4">
            <div className="flex items-center gap-2">
              <Shield className="h-4 w-4 text-sapphire dark:text-cyanAccent" />
              <p className="text-sm font-semibold text-slate-800 dark:text-white">{result.summary}</p>
            </div>
            <p className="mt-1 text-xs text-amber-600 dark:text-amber-400">{t('Advisory: AI findings are recommendations only — HR/Finance must review and act.')}</p>
          </div>

          {result.warnings.length === 0 ? (
            <div className="surface flex flex-col items-center py-12 text-center">
              <CheckCircle2 className="mb-3 h-8 w-8 text-emerald-500" />
              <p className="text-sm font-medium text-slate-700 dark:text-slate-300">{t('No anomalies detected for this payroll run.')}</p>
            </div>
          ) : (
            <div className="space-y-3">
              {result.warnings.map(w => (
                <div key={w.id} className="surface p-4">
                  <div className="flex items-start gap-3">
                    <AlertTriangle className={`mt-0.5 h-4 w-4 shrink-0 ${w.severity === 'Error' ? 'text-rose-500' : 'text-amber-500'}`} />
                    <div className="min-w-0">
                      <div className="flex items-center gap-2">
                        <StatusBadge status={w.severity} />
                        <span className="font-mono text-xs text-slate-500">{w.code}</span>
                        {w.employeeId && <span className="text-xs text-slate-400"><EmployeeLabelText label={employeeLabel(w.employeeId, names, id => t('Emp #{employeeId}', { employeeId: id }))} /></span>}
                      </div>
                      <p className="mt-1 text-sm text-slate-700 dark:text-slate-300">{w.message}</p>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          )}
        </>
      )}
    </div>
  );
}

// ── EOSB / Gratuity Tab ──────────────────────────────────────────────────────────

function EOSBTab() {
  const t = useT();
  const [employeeId, setEmployeeId] = useState('');
  const [asOfDate, setAsOfDate] = useState(new Date().toISOString().substring(0, 10));
  const [result, setResult] = useState<Record<string, unknown> | null>(null);
  const [loading, setLoading] = useState(false);
  const [history, setHistory] = useState<unknown[]>([]);
  const [error, setError] = useState('');
  // The history is fetched for the employee just calculated, so it shares that calculation's currency.
  const historyCurrency = typeof result?.currency === 'string' ? result.currency : '';

  const calculate = async () => {
    if (!employeeId) return;
    setLoading(true); setError(''); setResult(null); setHistory([]);
    try {
      const res = await payrollApi.calculateEosb(Number(employeeId), asOfDate);
      setResult(res as Record<string, unknown>);
      payrollApi.listEosb(Number(employeeId)).then(h => setHistory(h as unknown[])).catch(() => {});
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? t('Calculation failed.');
      setError(msg);
    } finally { setLoading(false); }
  };

  return (
    <div className="space-y-5 max-w-2xl">
      <div className="surface p-4 space-y-3">
        <p className="text-sm font-semibold text-slate-800 dark:text-white">{t('EOSB / Gratuity Calculator')}</p>
        <p className="text-xs text-slate-500 dark:text-slate-400">
          {t('Calculates End-of-Service Benefit under the rules of the employee’s employing-company country. Statutory rates and wage-basis rules come from that country pack and your tenant settings (Setup → GCC Settings).')}
        </p>
        <div className="grid grid-cols-2 gap-3">
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{t('Employee ID *')}</label>
            <input type="number" className={inp} placeholder="e.g. 1" value={employeeId} onChange={e => setEmployeeId(e.target.value)} />
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{t('As of Date')}</label>
            <input type="date" className={inp} aria-label={t('As of date')} value={asOfDate} onChange={e => setAsOfDate(e.target.value)} />
          </div>
        </div>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <button type="button" className={btn.primary} onClick={calculate} disabled={!employeeId || loading}>
          {loading ? t('Calculating…') : t('Calculate EOSB')}
        </button>
      </div>

      {result && (
        <div className="surface p-5 space-y-3">
          <p className="font-semibold text-slate-900 dark:text-white">{result.employeeName as string}</p>
          <div className="grid grid-cols-2 gap-x-6 gap-y-2 text-sm">
            <div className="text-slate-500">{t('Total Service')}</div><div className="font-medium">{t('{value} years', { value: (result.totalYears as number).toFixed(2) })}</div>
            <div className="text-slate-500">{t('Eligible Salary')}</div><div className="font-medium">{result.currency as string} {(result.eligibleSalary as number).toLocaleString('en-US', { minimumFractionDigits: 2 })}</div>
            <div className="text-slate-500">{t('Daily Rate')}</div><div className="font-medium">{result.currency as string} {(result.dailySalary as number).toFixed(4)}</div>
            <div className="text-slate-500">{t('Rate (1–5 yrs)')}</div><div className="font-medium">{t('{rate1To5Years} days/year', { rate1To5Years: result.rate1To5Years as number })}</div>
            <div className="text-slate-500">{t('Rate (5+ yrs)')}</div><div className="font-medium">{t('{rateAbove5Years} days/year', { rateAbove5Years: result.rateAbove5Years as number })}</div>
          </div>
          <div className="rounded-xl bg-sapphire/10 p-4 dark:bg-cyanAccent/10">
            <p className="text-xs text-slate-500 dark:text-slate-400">{t('Calculated EOSB')}</p>
            <p className="text-2xl font-bold text-sapphire dark:text-cyanAccent">{result.currency as string} {(result.eosbAmount as number).toLocaleString('en-US', { minimumFractionDigits: 2 })}</p>
            <p className="mt-1 text-xs text-slate-500">{result.message as string}</p>
          </div>
          <p className="text-xs text-amber-600 dark:text-amber-400">{t('Advisory only. Consult legal/HR before processing EOSB payment.')}</p>
        </div>
      )}

      {history.length > 0 && (
        <div className="surface overflow-hidden">
          <div className="border-b border-slate-100 px-4 py-3 dark:border-white/10">
            <p className="text-sm font-semibold text-slate-900 dark:text-white">{t('Previous Calculations')}</p>
          </div>
          <table className="min-w-full text-sm">
            <thead><tr className="border-b border-slate-100 dark:border-white/10">
              {[t('Date'), t('Eligible Salary'), t('EOSB Amount'), t('Status')].map(h => <th key={h} className="px-4 py-2.5 text-start text-xs font-bold uppercase tracking-wide text-fg-muted">{h}</th>)}
            </tr></thead>
            <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
              {(history as Array<Record<string, unknown>>).map((h, i) => (
                <tr key={i} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                  <td className="px-4 py-2.5 text-slate-600 dark:text-slate-300">{h.calculationDate as string}</td>
                  {/* R03 — this employee's own calculations, in the currency the server just computed
                      their EOSB in (their salary's, i.e. their employing company's), not the tenant default. */}
                  <td className="px-4 py-2.5 text-slate-600 dark:text-slate-300">{historyCurrency} {(h.eligibleSalary as number).toLocaleString()}</td>
                  <td className="px-4 py-2.5 font-medium text-sapphire dark:text-cyanAccent">{historyCurrency} {(h.calculatedAmount as number).toLocaleString()}</td>
                  <td className="px-4 py-2.5"><StatusBadge status={h.status as string} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ── GL Journal Tab ──────────────────────────────────────────────────────────────

function GlJournalTab({ selectedRunId, onRunChange, onGoToApprovals }: { selectedRunId?: string; onRunChange?: (id: string | null) => void; onGoToApprovals?: () => void }) {
  const t = useT();
  const fmtAmt = useFmtAmt();
  const { hasPermission } = useAuth();
  // The ERP-export endpoints answer 403 without one of these; without them the panel (and its reads) is not mounted at all.
  const mayUseErpExport = hasPermission('finance.gl.read') || hasPermission('finance.gl.manage');
  const { companies: payrollCompanies } = usePayrollCompanies();
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [runId, setLocalRunId] = useState(selectedRunId ?? '');
  // A choice made here is the page's selected run too, but the journal is still generated only by the button. The page
  // hands the choice back as selectedRunId; ownChoiceRef tells that echo apart from a run picked elsewhere.
  const ownChoiceRef = useRef<string | null>(null);
  const setRunId = React.useCallback((id: string) => { ownChoiceRef.current = id || null; setLocalRunId(id); onRunChange?.(id || null); }, [onRunChange]);
  const [journal, setJournal] = useState<PayrollGLJournal | null>(null);
  const [loading, setLoading] = useState(false);
  // A failed read is shown with a Retry, never swallowed: an empty run list or journal would read as "nothing to post".
  const [runsError, setRunsError] = useState<string | null>(null);
  const [journalError, setJournalError] = useState<string | null>(null);

  const loadRuns = useCallback(() => {
    setRunsError(null);
    return payrollApi.listAllRuns().then(setRuns).catch((e: unknown) => setRunsError(serverMessage(e, t)));
  }, [t]);
  useEffect(() => { loadRuns(); }, [loadRuns]);
  // `t` changes identity when the dictionary finishes loading; the journal must not be read again for that.
  const tRef = useRef(t);
  tRef.current = t;
  const load = useCallback(async (id?: string) => {
    const target = id ?? runIdRef.current;
    if (!target) return;
    setLoading(true);
    setJournalError(null);
    await payrollApi.glJournal(target).then(j => setJournal(j)).catch((e: unknown) => { setJournal(null); setJournalError(serverMessage(e, tRef.current)); }).finally(() => setLoading(false));
  }, []);
  const runIdRef = useRef(runId);
  runIdRef.current = runId;
  // Arriving from a run card ("Review & lock") reads that run's journal at once; otherwise Generate Journal does.
  useEffect(() => {
    const echo = !!selectedRunId && selectedRunId === ownChoiceRef.current;
    ownChoiceRef.current = null;
    if (!selectedRunId || echo) return;
    setLocalRunId(selectedRunId);
    runIdRef.current = selectedRunId;
    void load(selectedRunId);
  }, [selectedRunId, load]);

  const debits  = journal?.entries.filter(e => e.entryType === 'DR') ?? [];
  const credits = journal?.entries.filter(e => e.entryType === 'CR') ?? [];
  const selectedRun = runs.find(r => r.id === runId);
  const exportPeriod = selectedRun ? `${selectedRun.year}-${String(selectedRun.month).padStart(2, '0')}` : undefined;
  const runCompany = payrollCompanies.find(c => c.id === selectedRun?.companyId);

  return (
    <div className="space-y-4">
      {/* The picker, the button and the one-line explanation share a row (the explanation was a card of its own above them). */}
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <select aria-label={t('Select payroll run for GL journal')} data-testid="cc-lock-run-select" className={`${sel} max-w-xs flex-1`} value={runId} onChange={e => setRunId(e.target.value)}>
          <option value="">{t('Select payroll run…')}</option>
          <RunSelectOptions runs={runs} />
        </select>
        <button type="button" className={btn.primary} onClick={() => void load()} disabled={!runId || loading}>
          <BookOpen className="h-4 w-4" /> {loading ? t('Loading…') : t('Generate Journal')}
        </button>
        <p className="min-w-56 flex-1 text-xs text-slate-500 dark:text-slate-400">
          {t('Double-entry GL journal generated from processed payroll earnings and deductions. Use this to post payroll to your accounting system.')}
        </p>
      </div>
      {runsError && <LoadError message={runsError} onRetry={loadRuns} />}
      {journalError && <LoadError message={journalError} onRetry={() => void load()} />}

      {journal?.costing?.enabled && (
        <CostCentreLockPreview
          journal={journal}
          run={selectedRun}
          companyName={runCompany?.tradeName || runCompany?.name || ''}
          mayLock={hasPermission('payroll.lock')}
          onReload={async () => { await Promise.all([load(), loadRuns()]); }}
          onLocked={updated => { setRuns(rs => rs.map(r => (r.id === updated.id ? updated : r))); void load(); }}
          onGoToApprovals={() => onGoToApprovals?.()}
        />
      )}
      {/* A posted run's note comes from what was posted (costing.enabled is the preview plan, always off once posted). */}
      {journal && costingNote(journal) === 'off' && <CostingOffNote />}
      {journal && costingNote(journal) === 'postedWithout' && <PostedWithoutCostCentresNote />}

      {/* A posted run holds one journal per event (accrual, settlement, remittance, void): each is shown on its own. */}
      {journal && !journal.costing?.enabled && journal.isPosted && (journal.journals?.length ?? 0) > 0 && <PostedJournalsView journal={journal} />}

      {journal && !journal.costing?.enabled && !(journal.isPosted && (journal.journals?.length ?? 0) > 0) && (
        <div className="space-y-4">
          <div className="grid grid-cols-3 gap-4">
            <div className="surface px-4 py-2.5">
              <p className="text-xs text-slate-400">{t('Period')}</p>
              <p className="text-base font-bold text-slate-900 dark:text-white">{journal.period}</p>
            </div>
            <div className="surface px-4 py-2.5">
              <p className="text-xs text-slate-400">{t('Total Debits')}</p>
              <p className="text-base font-bold text-sapphire dark:text-cyanAccent">{fmtAmt(journal.totalDebits, journal.currency)}</p>
            </div>
            <div className="surface px-4 py-2.5">
              <p className="text-xs text-slate-400">{t('Total Credits')}</p>
              <p className="text-base font-bold text-sapphire dark:text-cyanAccent">{fmtAmt(journal.totalCredits, journal.currency)}</p>
            </div>
          </div>

          {journal.isBalanced ? (
            <div className="flex items-center gap-2 rounded-lg bg-emerald-50 px-4 py-2.5 dark:bg-emerald-500/10">
              <CheckCircle2 className="h-4 w-4 text-emerald-500" />
              <p className="text-sm font-medium text-emerald-700 dark:text-emerald-400">{t('Journal is balanced — Debits = Credits')}</p>
            </div>
          ) : (
            <div className="flex items-center gap-2 rounded-lg bg-rose-50 px-4 py-2.5 dark:bg-rose-500/10">
              <AlertTriangle className="h-4 w-4 text-rose-500" />
              <p className="text-sm font-medium text-rose-700 dark:text-rose-400">{t('Journal is out of balance — investigate before posting')}</p>
            </div>
          )}

          <div className="grid gap-4 lg:grid-cols-2">
            {[{ title: t('Debit Entries (DR)'), entries: debits, color: 'text-sapphire dark:text-cyanAccent' }, { title: t('Credit Entries (CR)'), entries: credits, color: 'text-emerald-600 dark:text-emerald-400' }].map(side => (
              <div key={side.title} className="surface overflow-hidden">
                <div className="border-b border-slate-100 px-4 py-2.5 dark:border-white/5">
                  <p className="text-xs font-semibold text-slate-700 dark:text-slate-300">{side.title}</p>
                </div>
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b border-slate-100 dark:border-white/5">
                      {[t('GL Account'), t('Description'), t('Amount')].map(h => <th key={h} className="px-3 py-2 text-start text-xs font-bold uppercase text-fg-muted">{h}</th>)}
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                    {side.entries.map((e, i) => (
                      <tr key={i} className="hover:bg-slate-50 dark:hover:bg-white/[0.03]">
                        <td className="px-3 py-2 font-mono text-xs text-slate-500">{e.glAccount}</td>
                        <td className="px-3 py-2 text-slate-700 dark:text-slate-300">{e.glAccountName}</td>
                        <td className={`px-3 py-2 text-end font-semibold tabular-nums ${side.color}`}>{fmtAmt(e.amount, journal.currency)}</td>
                      </tr>
                    ))}
                    <tr className="border-t-2 border-slate-200 dark:border-white/10">
                      <td colSpan={2} className="px-3 py-2 text-xs font-bold text-slate-600 dark:text-slate-300">{t('Total')}</td>
                      <td className={`px-3 py-2 text-end text-sm font-extrabold tabular-nums ${side.color}`}>{fmtAmt(side.entries.reduce((s, e) => s + e.amount, 0), journal.currency)}</td>
                    </tr>
                  </tbody>
                </table>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* ERP hand-off: the posting file for the month, and what the ERP said about it. */}
      {mayUseErpExport
        ? <JournalExportsPanel defaultPeriod={exportPeriod} />
        : <p data-testid="journal-export-no-access" className="text-sm text-fg-muted">{t('Exporting the journal needs Finance access')}</p>}
    </div>
  );
}

// ── Reconciliation Tab ──────────────────────────────────────────────────────────

function ReconciliationTab({ selectedRunId, onRunChange }: { selectedRunId?: string; onRunChange?: (id: string | null) => void }) {
  const t = useT();
  const fx = useFormat();
  const [runs, setRuns] = useState<PayrollRun[]>([]);
  const [runId, setRunId] = useSharedRun(selectedRunId, onRunChange);
  const [report, setReport] = useState<PayrollReconciliation | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const runCurrency = useRunCurrency(runs.find(r => r.id === runId));

  useEffect(() => { payrollApi.listAllRuns().then(setRuns).catch(() => {}); }, []);

  const load = async () => {
    if (!runId) return;
    setLoading(true);
    setError('');
    try {
      setReport(await payrollApi.reconciliation(runId));
    } catch (e: unknown) {
      // A failed reconciliation used to leave the previous report (or nothing) on screen, silently.
      setReport(null);
      const response = (e as { response?: { status?: number; data?: { message?: string; error?: string } } })?.response;
      setError(
        response?.data?.message
        ?? (response?.status === 403
          ? t('You do not have permission to run payroll reconciliation.')
          : t('Payroll reconciliation could not be loaded. Try again.')),
      );
    } finally {
      setLoading(false);
    }
  };

  const fmt = (n: number) => n.toLocaleString('en-US', { minimumFractionDigits: 2 });

  return (
    <div className="space-y-4">
      <div className="surface p-4">
        <p className="text-xs text-slate-500 dark:text-slate-400">
          {t('Month-over-month payroll reconciliation. Compares current run headcount and compensation vs the prior month. Variances above 5% are flagged for review.')}
        </p>
      </div>

      <div className="flex items-center gap-3">
        <select aria-label={t('Select payroll run for reconciliation')} className={`${sel} max-w-xs flex-1`} value={runId} onChange={e => setRunId(e.target.value)}>
          <option value="">{t('Select payroll run…')}</option>
          <RunSelectOptions runs={runs} />
        </select>
        <button type="button" className={btn.primary} onClick={load} disabled={!runId || loading}>
          <RefreshCw className="h-4 w-4" /> {loading ? t('Loading…') : t('Reconcile')}
        </button>
      </div>

      {error && (
        <div role="alert" className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300">
          {error}
        </div>
      )}

      {report && <RunCurrencyNotice resolution={runCurrency.resolution} companiesState={runCurrency.companiesState} onRetry={runCurrency.retry} />}

      {report && (
        <div className="space-y-4">
          <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
            <KpiCard label={t('Headcount')} value={`${report.priorHeadcount} → ${report.currentHeadcount}`} icon={Users} color="bg-sapphire/10 text-sapphire dark:bg-sapphire/20" />
            <KpiCard label={t('Joiners')} value={report.joinerCount} icon={Plus} color="bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400" />
            <KpiCard label={t('Leavers')} value={report.leaverCount} icon={X} color="bg-rose-100 text-rose-600 dark:bg-rose-500/20 dark:text-rose-400" />
            <KpiCard label={t('Flagged Variances')} value={report.flaggedVariances} icon={AlertTriangle} color={report.flaggedVariances > 0 ? 'bg-amber-100 text-amber-600 dark:bg-amber-500/20 dark:text-amber-400' : 'bg-emerald-100 text-emerald-600 dark:bg-emerald-500/20 dark:text-emerald-400'} />
          </div>

          <div className="surface overflow-hidden">
            <div className="grid grid-cols-2 divide-x rtl:divide-x-reverse divide-slate-100 dark:divide-white/5">
              {/* Keyed and labelled by a stable id: comparing the translated label to 'Gross' made both cards read "Total net" in Arabic. */}
              {[{ id: 'gross', label: t('Total Gross'), prior: report.priorTotalGross, current: report.currentTotalGross }, { id: 'net', label: t('Total Net'), prior: report.priorTotalNet, current: report.currentTotalNet }].map(m => (
                <div key={m.id} className="p-4">
                  <p className="text-xs text-slate-400">{m.label}</p>
                  <p className="text-lg font-bold text-slate-900 dark:text-white">{runCurrency.amount(m.current)}</p>
                  <p className={`text-xs ${m.current >= m.prior ? 'text-emerald-500' : 'text-rose-500'}`}>
                    {m.current >= m.prior ? '+' : ''}{t('{amount} vs {period}', { amount: runCurrency.amount(m.current - m.prior), period: report.priorPeriod ? fx.period(report.priorPeriod, 'long') : t('Prior period') })}
                  </p>
                </div>
              ))}
            </div>
          </div>

          {report.variances.length > 0 && (
            <div className="surface overflow-hidden">
              <div className="border-b border-slate-100 px-4 py-3 dark:border-white/5">
                <p className="text-sm font-semibold text-slate-800 dark:text-white">{t('Employee Variance Detail')}</p>
              </div>
              <div className="overflow-x-auto">
                <table className="w-full min-w-[640px] text-sm">
                  <thead>
                    <tr className="border-b border-slate-100 dark:border-white/[0.07]">
                      {[t('Employee'), t('Prior Gross'), t('Current Gross'), t('Δ Gross'), t('Var %'), t('Prior Net'), t('Current Net'), t('Flag')].map(h => (
                        <th key={h} className="px-3 py-2 text-start text-xs font-bold uppercase text-fg-muted">{h}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 dark:divide-white/[0.05]">
                    {report.variances.map(v => (
                      <tr key={v.employeeId} className={`hover:bg-slate-50 dark:hover:bg-white/[0.03] ${v.isVarianceFlag ? 'bg-amber-50/50 dark:bg-amber-500/5' : ''}`}>
                        <td className="px-3 py-2">
                          <p className="font-medium text-slate-900 dark:text-white">{v.employeeName}</p>
                          <p className="text-xs text-slate-400">{v.employeeCode}</p>
                        </td>
                        <td className="px-3 py-2 text-end tabular-nums text-slate-600 dark:text-slate-300">{fmt(v.priorGross)}</td>
                        <td className="px-3 py-2 text-end tabular-nums font-semibold text-slate-900 dark:text-white">{fmt(v.currentGross)}</td>
                        <td className={`px-3 py-2 text-end tabular-nums ${v.grossDelta >= 0 ? 'text-emerald-600' : 'text-rose-500'}`}>{v.grossDelta >= 0 ? '+' : ''}{fmt(v.grossDelta)}</td>
                        <td className={`px-3 py-2 text-end tabular-nums font-semibold ${Math.abs(v.grossVariancePct) > 5 ? 'text-amber-600 dark:text-amber-400' : 'text-slate-500'}`}>{v.grossVariancePct >= 0 ? '+' : ''}{v.grossVariancePct.toFixed(1)}%</td>
                        <td className="px-3 py-2 text-end tabular-nums text-slate-500">{fmt(v.priorNet)}</td>
                        <td className="px-3 py-2 text-end tabular-nums text-slate-700 dark:text-slate-300">{fmt(v.currentNet)}</td>
                        <td className="px-3 py-2">{v.isVarianceFlag && <span className="inline-flex items-center gap-1 rounded-full bg-amber-100 px-2 py-0.5 text-xs font-medium text-amber-700 dark:bg-amber-500/20 dark:text-amber-400"><AlertTriangle className="h-3 w-3" />{t('Flag')}</span>}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// ── Final Settlement Tab ────────────────────────────────────────────────────────

function FinalSettlementTab() {
  const t = useT();
  const fmtAmt = useFmtAmt();
  const { confirm, dialog: confirmDialog } = useConfirm();
  // Completing a zero-net settlement is a payment decision: the same keys as settling a payment batch (the server checks again).
  const { hasPermission } = useAuth();
  const { locale } = useLocale();
  const maySettlePayments = paymentActionsAllowed(hasPermission, ['settle']);
  const [form, setForm] = useState({ employeeId: '', lastWorkingDay: new Date().toISOString().slice(0, 10), noticePeriodDaysShort: '0', terminationReason: '' });
  const [result, setResult] = useState<FinalSettlementResult | null>(null);
  const [settlements, setSettlements] = useState<FinalSettlementListRow[]>([]);
  const [outstandingTotal, setOutstandingTotal] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [actionSaving, setActionSaving] = useState<string | null>(null);
  const [actionForms, setActionForms] = useState<Record<string, {
    reason: string; accrualDate: string; acknowledgeWageBaseFloor: boolean;
    acknowledgeWagesUnpaid: boolean; wagesUnpaidReason: string;
  }>>({});

  const loadSettlements = () => payrollApi.listFinalSettlements().then(r => {
    setSettlements(r.rows); setOutstandingTotal(r.outstandingTotal);
  }).catch((e: unknown) => setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? t('Unable to load final settlements.')));
  // eslint-disable-next-line react-hooks/exhaustive-deps -- load once; the loader only reads the translator
  useEffect(() => { loadSettlements(); }, []);

  const defaultActionForm = {
    reason: '', accrualDate: new Date().toISOString().slice(0, 10),
    acknowledgeWageBaseFloor: false, acknowledgeWagesUnpaid: false,
    wagesUnpaidReason: '',
  };
  const actionForm = (id: string) => actionForms[id] ?? defaultActionForm;
  const setActionForm = (id: string, patch: Partial<typeof defaultActionForm>) =>
    setActionForms(prev => ({ ...prev, [id]: { ...defaultActionForm, ...(prev[id] ?? {}), ...patch } }));

  const transition = async (settlement: FinalSettlementListRow, action: 'submit' | 'approve' | 'cancel') => {
    const af = actionForm(settlement.id);
    if (action === 'cancel' && !af.reason.trim()) { setError(t('A cancellation reason is required.')); return; }
    if (action === 'cancel' && !(await confirm({
      title: t('Cancel this settlement?'),
      message: t('Any required contra journal is posted. The reason you entered is sent with the cancellation.'),
      confirmLabel: t('Cancel settlement'),
    }))) return;
    setActionSaving(`${settlement.id}:${action}`); setError('');
    try {
      if (action === 'submit') await payrollApi.submitFinalSettlement(settlement.id, af.reason || undefined);
      if (action === 'approve') await payrollApi.approveFinalSettlement(settlement.id, {
        confirmTerminationReason: settlement.terminationReason,
        acknowledgeWageBaseFloor: af.acknowledgeWageBaseFloor,
        acknowledgeWagesUnpaid: af.acknowledgeWagesUnpaid,
        wagesUnpaidReason: af.wagesUnpaidReason || undefined,
        accrualDate: af.accrualDate || undefined,
        reason: af.reason || undefined,
      });
      if (action === 'cancel') await payrollApi.cancelFinalSettlement(settlement.id, af.reason);
      await loadSettlements();
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? `${action} failed.`);
    } finally { setActionSaving(null); }
  };

  const completeWithoutPayment = async (settlement: FinalSettlementListRow) => {
    if (!(await confirm({
      title: t('Complete this settlement with nothing to pay?'),
      message: t("{employeeName}'s final pay was all recovered against their debts, so there is nothing to send to the bank. The settlement is marked paid with no payment, and any debt still owed moves to the employee receivable.", { employeeName: settlement.employeeName }),
      confirmLabel: t('Complete — nothing to pay'),
    }))) return;
    setActionSaving(`${settlement.id}:complete`); setError('');
    try {
      await payrollApi.completeFinalSettlementWithoutPayment(settlement.id);
      await loadSettlements();
    } catch (e: unknown) {
      // The server's refusal in the viewer's language (every refusal of this action carries messageAr).
      const data = (e as { response?: { data?: { message?: string; messageAr?: string } } })?.response?.data;
      setError((locale === 'ar' ? data?.messageAr || data?.message : data?.message) ?? t('Completing the settlement failed.'));
    } finally { setActionSaving(null); }
  };

  const calculate = async () => {
    if (!form.employeeId || !form.lastWorkingDay) return;
    setLoading(true); setError(''); setResult(null);
    try {
      // Empty terminationReason ⇒ backend uses the employee's recorded separation type.
      const res = await payrollApi.finalSettlement(Number(form.employeeId), form.lastWorkingDay, Number(form.noticePeriodDaysShort), form.terminationReason || undefined);
      // The calculation response does not carry unpaid-leave money no run collected; the settlement detail does.
      const detail = await payrollApi.getFinalSettlement(res.settlementId).catch(() => null);
      setResult({ ...res, outstandingLeaveDeductions: detail?.outstandingLeaveDeductions ?? null });
      await loadSettlements();
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? t('Calculation failed.'));
    } finally { setLoading(false); }
  };

  return (
    <div className="space-y-3">
      <SettlementRunDutyPanel settlements={settlements} onChanged={() => void loadSettlements()} />
      {confirmDialog}
      <div className="surface flex flex-wrap items-start justify-between gap-x-6 gap-y-1 px-4 py-3">
        <div className="min-w-0 flex-1 basis-80">
          <p className="text-sm font-semibold text-slate-800 dark:text-white">{t('Final Settlement Lifecycle')}</p>
          <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
            {t('Calculate and save a Draft payable, submit it, approve the GL accrual, disburse it through an off-cycle payroll run, then settle the accepted payment batch.')}{' '}
            {t('Approval needs a second person: whoever calculated, recalculated or submitted a settlement cannot approve it.')}
          </p>
        </div>
        <p className="shrink-0 text-sm font-semibold text-sapphire dark:text-cyanAccent">{t('Outstanding approved/disbursing payable: {toLocaleString}', { toLocaleString: outstandingTotal.toLocaleString('en-US', { minimumFractionDigits: 2 }) })}</p>
      </div>

      <div className="surface space-y-3 p-4">
        <div className="grid grid-cols-2 gap-x-4 gap-y-3 lg:grid-cols-4">
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{t('Employee ID *')}</label>
            <input type="number" className={inp} placeholder="e.g. 1" value={form.employeeId} onChange={e => setForm(f => ({ ...f, employeeId: e.target.value }))} />
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{t('Last Working Day *')}</label>
            <input type="date" className={inp} aria-label={t('Last working day')} value={form.lastWorkingDay} onChange={e => setForm(f => ({ ...f, lastWorkingDay: e.target.value }))} />
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{t('Notice Period Short (days)')}</label>
            <input type="number" className={inp} min="0" placeholder="0" value={form.noticePeriodDaysShort} onChange={e => setForm(f => ({ ...f, noticePeriodDaysShort: e.target.value }))} />
            <p className="mt-0.5 text-xs text-slate-400">{t('Days employee served less than the contractual notice period')}</p>
          </div>
          <div>
            <label className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-400">{t('Separation Reason')}</label>
            <select className={inp} aria-label={t('Separation reason')} value={form.terminationReason} onChange={e => setForm(f => ({ ...f, terminationReason: e.target.value }))}>
              <option value="">{t('Auto (from offboarding record)')}</option>
              <option value="Termination">{t('Termination (employer) — full gratuity')}</option>
              <option value="EndOfContract">{t('End of Contract — full gratuity')}</option>
              <option value="Retirement">{t('Retirement — full gratuity')}</option>
              <option value="Resignation">{t('Resignation — Art.85 reduction')}</option>
              <option value="Article80">{t('Dismissal for cause (Art.80) — forfeited')}</option>
            </select>
            <p className="mt-0.5 text-xs text-slate-400">{t('Drives the EOSB entitlement (KSA Art.80/84/85). Leave on Auto to use the recorded separation type.')}</p>
          </div>
        </div>
        {error && <p className="text-xs text-rose-500">{error}</p>}
        <button type="button" className={btn.primary} onClick={calculate} disabled={!form.employeeId || !form.lastWorkingDay || loading}>
          <Calculator className="h-4 w-4" /> {loading ? t('Creating draft…') : t('Calculate & Save Draft')}
        </button>
      </div>

      {result && (
        <div className="surface p-5 space-y-4">
          <div>
            <p className="text-sm font-semibold text-slate-900 dark:text-white">{result.employeeName}</p>
            <p className="text-xs text-slate-400">{t('Settlement {settlementId} · {status} · Last working day: {lastWorkingDay} · Service: {value} years · Reason: {terminationReason}', { settlementId: result.settlementId, status: result.status, lastWorkingDay: result.lastWorkingDay, value: result.totalYears.toFixed(2), terminationReason: result.terminationReason })}</p>
          </div>

          <div className="grid grid-cols-2 gap-x-6 gap-y-2 text-sm">
            <div className="text-slate-500">{t('Basic Salary')}</div><div className="font-medium text-end">{result.currency} {result.basicSalary.toLocaleString('en-US', { minimumFractionDigits: 2 })}</div>
            <div className="text-slate-500">{t('Gross Salary')}</div><div className="font-medium text-end">{result.currency} {result.grossSalary.toLocaleString('en-US', { minimumFractionDigits: 2 })}</div>
            <div className="text-slate-500">{t('Days Worked in Month')}</div><div className="font-medium text-end">{result.daysWorkedInMonth} / {result.daysInMonth}</div>
            <div className="text-slate-500">{t('Leave Balance')}</div><div className="font-medium text-end">{t('{value} days', { value: result.leaveBalanceDays.toFixed(2) })}</div>
          </div>

          <div className="divide-y divide-slate-100 rounded-xl border border-slate-200 dark:divide-white/5 dark:border-white/10">
            {result.breakdown.map(b => (
              <div key={b.component} className="flex items-center justify-between px-4 py-3">
                <span className="text-sm text-slate-600 dark:text-slate-300">{b.component}</span>
                <span className={`text-sm font-semibold tabular-nums ${b.amount < 0 ? 'text-rose-500' : 'text-emerald-600 dark:text-emerald-400'}`}>
                  {b.amount < 0 ? '-' : '+'}{result.currency} {Math.abs(b.amount).toLocaleString('en-US', { minimumFractionDigits: 2 })}
                </span>
              </div>
            ))}
          </div>

          <div className="rounded-xl bg-sapphire/10 px-5 py-4 dark:bg-cyanAccent/10">
            <p className="text-xs text-slate-500 dark:text-slate-400">{t('Total Settlement Payable')}</p>
            <p className="text-2xl font-extrabold text-sapphire dark:text-cyanAccent">{result.currency} {result.totalPayable.toLocaleString('en-US', { minimumFractionDigits: 2 })}</p>
          </div>

          {(() => {
            const exitLeave = exitLeaveMoney(result.outstandingLeaveDeductions);
            if (!exitLeave) return null;
            const money = (v: number) => fmtAmt(v, result.currency);
            return (
              <div className="rounded-xl border border-amber-200 px-4 py-3 text-sm dark:border-amber-500/30" data-testid="settlement-exit-arrears">
                {exitLeave.owedByEmployee > 0 && (
                  <div className="flex items-center justify-between gap-3">
                    <span className="text-slate-600 dark:text-slate-300">{t('Unpaid-leave arrears still owed at exit')}</span>
                    <span className="font-semibold tabular-nums text-rose-500">{money(exitLeave.owedByEmployee)}</span>
                  </div>
                )}
                {exitLeave.owedToEmployee > 0 && (
                  <div className="flex items-center justify-between gap-3">
                    <span className="text-slate-600 dark:text-slate-300">{t('Leave deduction refunds still owed to the employee')}</span>
                    <span className="font-semibold tabular-nums text-emerald-600 dark:text-emerald-400">{money(exitLeave.owedToEmployee)}</span>
                  </div>
                )}
                <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                  {t('Leave months: {months}. No payroll run has collected these. They are shown for your information and are not included in the payable above.', { months: exitLeave.months.join(', ') })}
                </p>
              </div>
            );
          })()}

          {result.openDebts && (
            <ExitDebtsPanel debts={result.openDebts} employeeName={result.employeeName} fallbackCurrency={result.currency}
              showPlanned unrecovered={result.unrecoveredDebt ?? 0} testId="settlement-open-debts" />
          )}
          <p className="text-xs text-amber-600 dark:text-amber-400">{t('This is a persisted Draft payable. Review every warning and component before submitting or approving it.')}</p>
          {result.warnings?.map(w => <p key={w} className="text-xs text-amber-600 dark:text-amber-400">{w}</p>)}
        </div>
      )}

      <div className="surface overflow-x-auto">
        <div className="border-b border-slate-100 px-4 py-3 dark:border-white/10">
          <p className="text-sm font-semibold text-slate-800 dark:text-white">{t('Settlement Register')}</p>
        </div>
        <table className="w-full min-w-[1100px] text-sm">
          <thead><tr className="border-b border-slate-100 dark:border-white/10">
            {[t('Employee'), t('Last day'), t('Due'), t('Net payable'), t('Status'), t('Workflow controls')].map(h => <th key={h} className="px-4 py-2 text-start text-xs font-bold uppercase text-fg-muted">{h}</th>)}
          </tr></thead>
          <tbody className="divide-y divide-slate-100 dark:divide-white/5">
            {settlements.length === 0 && <tr><td colSpan={6} className="py-8 text-center text-slate-400">{t('No final settlements.')}</td></tr>}
            {settlements.map(s => {
              const af = actionForm(s.id);
              const mutable = !['Paid', 'Cancelled', 'Disbursing'].includes(s.status);
              return <tr key={s.id} className="align-top">
                <td className="px-4 py-3"><p className="font-medium text-slate-900 dark:text-white">{s.employeeName}</p><p className="text-xs text-slate-400">{s.employeeCode}</p></td>
                <td className="px-4 py-3 text-xs text-slate-500">{s.lastWorkingDay}</td>
                <td className={`px-4 py-3 text-xs ${s.isOverdue ? 'font-semibold text-rose-500' : 'text-slate-500'}`}>{s.settlementDueDate}{s.isOverdue ? ` · ${t('{days} days overdue', { days: s.overdueDays })}` : ''}</td>
                <td className="px-4 py-3 font-semibold">{fmtAmt(s.netPayable, s.currency)}</td>
                <td className="px-4 py-3"><StatusBadge status={s.status} /></td>
                <td className="px-4 py-3">
                  {mutable ? <div className="grid min-w-[420px] gap-2">
                    <div className="grid grid-cols-2 gap-2">
                      <input className={inp} type="date" aria-label={t('Accrual date for {employeeName}', { employeeName: s.employeeName })} value={af.accrualDate} onChange={e => setActionForm(s.id, { accrualDate: e.target.value })} />
                      <input className={inp} aria-label={t('Workflow reason for {employeeName}', { employeeName: s.employeeName })} placeholder={t(t('Reason / approval note'))} value={af.reason} onChange={e => setActionForm(s.id, { reason: e.target.value })} />
                    </div>
                    {(s.status === 'Draft' || s.status === 'PendingApproval') && <>
                      <div className="flex flex-wrap gap-3 text-xs text-slate-600 dark:text-slate-300">
                        <label><input type="checkbox" checked={af.acknowledgeWageBaseFloor} onChange={e => setActionForm(s.id, { acknowledgeWageBaseFloor: e.target.checked })} /> {t('Accept flagged wage-base floor')}</label>
                        <label><input type="checkbox" checked={af.acknowledgeWagesUnpaid} onChange={e => setActionForm(s.id, { acknowledgeWagesUnpaid: e.target.checked })} /> {t('Add acknowledged unpaid wages')}</label>
                      </div>
                      {af.acknowledgeWagesUnpaid && <input className={inp} aria-label={t('Unpaid wages reason for {employeeName}', { employeeName: s.employeeName })} placeholder={t(t('Required unpaid-wages justification'))} value={af.wagesUnpaidReason} onChange={e => setActionForm(s.id, { wagesUnpaidReason: e.target.value })} />}
                    </>}
                    <div className="flex gap-2">
                      {s.status === 'Draft' && <button type="button" className={btn.primary} onClick={() => transition(s, 'submit')} disabled={actionSaving !== null}>{t('Submit')}</button>}
                      {(s.status === 'Draft' || s.status === 'PendingApproval') && <button type="button" className={btn.primary} onClick={() => transition(s, 'approve')} disabled={actionSaving !== null}>{t('Approve & Post GL')}</button>}
                      <button type="button" className={btn.danger} onClick={() => transition(s, 'cancel')} disabled={actionSaving !== null}>{t('Cancel')}</button>
                    </div>
                  </div> : s.status === 'Disbursing' && s.completableWithoutPayment ? <div className="grid gap-2">
                    <p className="text-xs text-slate-500 dark:text-slate-400">{t('Nothing to pay: the final pay was all recovered.')}</p>
                    {maySettlePayments && <button type="button" className={btn.primary} onClick={() => void completeWithoutPayment(s)} disabled={actionSaving !== null}>{t('Complete — nothing to pay')}</button>}
                  </div> : <p className="text-xs text-slate-400">{s.status === 'Approved' ? t('Create an off-cycle settlement run to disburse.') : t('No direct action.')}</p>}
                </td>
              </tr>;
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}

// ── Main Page ───────────────────────────────────────────────────────────────────

export function PayrollPage() {
  const t = useT();
  const { user } = useAuth();
  const { currencyCode } = useTenantSettings();
  const isAdmin    = user?.roles.some(r => r === 'Admin') ?? false;
  const isFinance  = user?.roles.some(r => ['Finance Controller', 'Finance Approver'].includes(r)) ?? false;
  const isHROrPayroll = !isAdmin && !isFinance && (user?.roles.some(r => ['HR Manager', 'Payroll Manager', 'Payroll Officer'].includes(r)) ?? false);

  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();

  const visibleTabs = TABS.filter(tab => {
    // Finance-only tabs
    if (['gl-journal', 'reconciliation'].includes(tab.key)) return isAdmin || isFinance || isHROrPayroll;
    return true;
  });
  const visibleTabKeys = visibleTabs.map(tab => tab.key);

  // ── Deep link: ?tab=<tab>&run=<runId> ───────────────────────────────────────────
  // The URL selects the tab and the run; selecting either updates the URL with replace (never push, so Back
  // leaves the page rather than stepping through selections). An unknown tab falls back to the default
  // (or to Payroll Runs when a run is named); a malformed run id is ignored.
  const link = parseDeepLink(searchParams, visibleTabKeys);
  const [activeTab, setActiveTab] = useState<Tab>(() => (link.tab as Tab | null) ?? (link.runId ? 'runs' : 'dashboard'));
  const [contextRunId, setContextRunId] = useState<string | undefined>(link.runId ?? undefined);
  // Bumped when a run changes outside the tab that shows it (the pipeline processed or locked it).
  const [runsVersion, setRunsVersion] = useState(0);
  const bumpRuns = React.useCallback(() => setRunsVersion(v => v + 1), []);
  // Approve refused for want of a current reconciliation sign-off: re-read the run and reopen the review.
  const [reviewRequest, setReviewRequest] = useState(0);
  const reconNotReviewed = React.useCallback(() => { setRunsVersion(v => v + 1); setReviewRequest(n => n + 1); }, []);

  // URL -> state: a link followed while the page is open (or Back/Forward) selects what it names.
  const queryString = searchParams?.toString() ?? '';
  useEffect(() => {
    const next = parseDeepLink(new URLSearchParams(queryString), visibleTabKeys);
    if (next.tab) setActiveTab(prev => (prev === next.tab ? prev : next.tab as Tab));
    setContextRunId(prev => (next.runId && prev !== next.runId ? next.runId : prev));
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [queryString]);

  // state -> URL (replace). Written only when it differs, so the two effects cannot chase each other.
  useEffect(() => {
    const wanted = withDeepLink(queryString, { tab: activeTab, runId: contextRunId ?? null }, 'dashboard');
    if (wanted !== queryString) router.replace(wanted ? `${pathname}?${wanted}` : pathname, { scroll: false });
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeTab, contextRunId]);

  // Changing tab never moves the page when the tab strip is in view. When something else changed the tab (the pipeline's next-step
  // button) and the strip has scrolled out of view, the strip is brought back to the top: the least movement that shows the new tab.
  const tabsAnchor = useRef<HTMLDivElement>(null);
  const firstTabRender = useRef(true);
  useEffect(() => {
    if (firstTabRender.current) { firstTabRender.current = false; return; }
    // From 1280px the section nav is a sticky column beside the section: it is always in view, so the page does not move.
    if (typeof window.matchMedia === 'function' && window.matchMedia('(min-width: 1280px)').matches) return;
    const el = tabsAnchor.current;
    if (!el) return;
    const top = el.getBoundingClientRect().top;
    if (top < 64 || top > window.innerHeight * 0.75) el.scrollIntoView({ block: 'start', behavior: 'auto' });
  }, [activeTab]);

  const handleSelectRun = (run: PayrollRun, tab: Tab) => {
    setContextRunId(run.id);
    setActiveTab(tab);
  };
  const selectRunId = React.useCallback((id: string | null) => setContextRunId(id ?? undefined), []);
  const navigateTab = React.useCallback((key: string) => { if (TABS.some(x => x.key === key)) setActiveTab(key as Tab); }, []);
  // What the Bank / WPS tab knows about the run's pay out: the run bar reads the same facts (one stage for the whole page).
  const [payOutContext, setPayOutContext] = useState<PayOutContext | null>(null);
  useEffect(() => { if (activeTab !== 'bank-wps') setPayOutContext(null); }, [activeTab]);
  // The section list folds away below 1280px (a button under the run bar opens it; a bottom sheet on a phone).
  const [navOpen, setNavOpen] = useState(false);
  const navToggle = useRef<HTMLButtonElement>(null);

  const renderTab = () => {
    switch (activeTab) {
      case 'dashboard':       return <DashboardTab onNavigate={setActiveTab} />;
      case 'salary-structures': return <GradePackagesTab />;
      case 'employee-salary': return <EmployeeSalaryTab />;
      case 'runs':            return <RunsTab onSelectRun={handleSelectRun} focusRunId={contextRunId} onFocusRun={selectRunId} version={runsVersion} onRunsChanged={bumpRuns} />;
      case 'validation':      return <ValidationTab selectedRunId={contextRunId} onRunChange={selectRunId} />;
      case 'approvals':       return <ApprovalsTab selectedRunId={contextRunId} onRunChange={selectRunId} isAdmin={isAdmin} isFinance={isFinance} isHROrPayroll={isHROrPayroll || isAdmin} onReconNotReviewed={reconNotReviewed} />;
      case 'payslips':        return <PayslipsTab selectedRunId={contextRunId} onRunChange={selectRunId} />;
      case 'bank-wps':        return <BankWpsTab selectedRunId={contextRunId} onRunChange={selectRunId} onNavigate={navigateTab} onRunsChanged={bumpRuns} onPayOutContext={setPayOutContext} />;
      case 'payment-tracking': return <PaymentTrackingTab selectedRunId={contextRunId} />;
      case 'reports':         return <ReportsTab selectedRunId={contextRunId} onRunChange={selectRunId} />;
      case 'eosb':            return <EOSBTab />;
      case 'gl-journal':      return <GlJournalTab selectedRunId={contextRunId} onRunChange={selectRunId} onGoToApprovals={() => setActiveTab('approvals')} />;
      case 'reconciliation':  return <ReconciliationTab selectedRunId={contextRunId} onRunChange={selectRunId} />;
      case 'final-settlement': return <FinalSettlementTab />;
      case 'ai-validation':   return <AIValidationTab selectedRunId={contextRunId} onRunChange={selectRunId} />;
      case 'audit-integrity': return <AuditIntegrityTab />;
    }
  };

  const activeLabel = t(TABS.find(x => x.key === activeTab)?.label ?? 'Dashboard');
  const activeGroup = groupOf(activeTab);
  const navGroups = TAB_GROUPS
    .map(g => ({
      id: g.id,
      label: t(g.label),
      items: visibleTabs.filter(x => g.ids.includes(x.key)).map(({ key, label, icon }) => ({
        id: key, label: t(label), icon,
        href: `${pathname}?${withDeepLink('', { tab: key, runId: contextRunId ?? null }, 'dashboard')}`,
      })),
    }))
    .filter(g => g.items.length > 0);

  return (
    <div className="kx-scroll-tables space-y-3">
      <RunPipeline
        runId={contextRunId ?? null}
        onSelectRun={selectRunId}
        onNavigate={navigateTab}
        version={runsVersion}
        onRunsChanged={bumpRuns}
        reviewRequestKey={reviewRequest}
        activeTab={activeTab}
        payOutContext={payOutContext}
      />

      {/* The Payroll sections: a sticky grouped column beside the section from 1280px; below that a "{group} › {section}" button opens
          the same list (inline from 640px, a bottom sheet on a phone). The app menu folds to icons inside Payroll to make the room. */}
      <div className="xl:grid xl:grid-cols-[208px_minmax(0,1fr)] xl:items-start xl:gap-6">
        {/* The grid item is what sticks (a sticky child of a wrapper of its own height never moves); it scrolls inside when taller than the screen. */}
        <div ref={tabsAnchor} data-testid="payroll-tabs-anchor" className="mb-3 min-w-0 xl:sticky xl:top-32 xl:mb-0 xl:max-h-[calc(100vh-9rem)] xl:self-start xl:overflow-y-auto">
          <button
            ref={navToggle}
            type="button"
            aria-expanded={navOpen}
            aria-controls="payroll-section-list"
            onClick={() => setNavOpen(v => !v)}
            className="surface flex min-h-11 w-full items-center gap-2 px-3 py-2 text-start text-body xl:hidden"
            data-testid="payroll-section-toggle"
          >
            <span className="sr-only">{t('Payroll section')}: </span>
            <span className="text-fg-muted">{t(activeGroup.label)}</span>
            <ChevronRight aria-hidden="true" className="h-3.5 w-3.5 shrink-0 text-fg-muted" />
            <span className="min-w-0 flex-1 font-semibold text-fg">{activeLabel}</span>
            <ChevronDown aria-hidden="true" className={`h-4 w-4 shrink-0 text-fg-muted transition-transform motion-reduce:transition-none ${navOpen ? 'rotate-180' : ''}`} />
          </button>
          <SectionNav
            groups={navGroups}
            selectedId={activeTab}
            currentLabel={activeLabel}
            open={navOpen}
            onOpenChange={setNavOpen}
            toggleRef={navToggle}
            onSelect={id => setActiveTab(id)}
            ariaLabel={t('Payroll sections')}
            sheetTitle={t('Payroll sections')}
            listId="payroll-section-list"
            headingId={PAYROLL_SECTION_HEADING_ID}
            classPrefix="payroll"
            testId="payroll-section-nav"
            stickyClass=""
            itemIdPrefix="payroll-tab-"
          />
        </div>

        <div className="min-w-0 space-y-3">
          {/* Bank / WPS Files draws its own header (its "More" menu opens the flow's drawers). */}
          {activeTab !== 'bank-wps' && (
            <PayrollSectionHeader group={t(activeGroup.label)} title={activeLabel} purpose={t(TAB_PURPOSE[activeTab] ?? activeGroup.purpose)} />
          )}
          <section role="region" aria-labelledby={PAYROLL_SECTION_HEADING_ID} id={`payroll-panel-${activeTab}`} data-testid="payroll-section-panel" className="min-w-0">
            {renderTab()}
          </section>
        </div>
      </div>
    </div>
  );
}
