'use client';

import { Modal as AccessibleModal } from '@/src/components/Modal';
import type { BenefitEnrollment } from '@/src/api/benefits';
import { useLocale } from '@/src/contexts/LocaleContext';

export const INPUT = 'mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-100';
export const LABEL = 'block text-xs font-medium text-slate-600 dark:text-slate-300';
export const PRIMARY = 'flex items-center gap-1.5 rounded-lg bg-sapphire px-3 py-2 text-xs font-semibold text-white transition hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50';
export const SECONDARY = 'flex items-center gap-1.5 rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs font-semibold text-slate-600 hover:bg-slate-50 disabled:opacity-50 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-300 dark:hover:bg-white/[0.08]';
export const CARD = 'rounded-2xl border border-slate-200/80 bg-white dark:border-white/[0.06] dark:bg-white/[0.03]';

export const COVERAGE_TIERS = ['Employee', 'Employee + Spouse', 'Employee + Children', 'Family'];
export const today = () => new Date().toISOString().slice(0, 10);
export const enrollmentStatus = (enrollment: Pick<BenefitEnrollment, 'status' | 'effectiveFrom' | 'effectiveTo'>) =>
  enrollment.status !== 'Active' ? enrollment.status : enrollment.effectiveFrom > today() ? 'Scheduled' : enrollment.effectiveTo && enrollment.effectiveTo < today() ? 'Ended' : 'Active';

export function StatusPill({ active, label }: { active: boolean; label?: string }) {
  const { t } = useLocale();
  return (
    <span className={`rounded-full px-2 py-0.5 text-[10px] font-semibold ${active
      ? 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/[0.12] dark:text-emerald-300'
      : 'bg-slate-200 text-slate-600 dark:bg-white/[0.08] dark:text-slate-300'}`}>
      {t(label ?? (active ? 'Active' : 'Inactive'))}
    </span>
  );
}

export function Modal({ title, onClose, children, wide = false }: { title: string; onClose: () => void; children: React.ReactNode; wide?: boolean }) {
  return <AccessibleModal isOpen title={title} onClose={onClose} size={wide ? 'lg' : 'md'}>{children}</AccessibleModal>;
}

export function FormError({ message }: { message: string | null }) {
  if (!message) return null;
  return <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-xs font-medium text-rose-700 dark:bg-rose-500/[0.08] dark:text-rose-300">{message}</p>;
}
