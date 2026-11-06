'use client';

import Link from 'next/link';
import { AlertTriangle } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import type { Resolution, SourceLevel } from '../../lib/employeeCostCentre';
import { msg } from '../../i18n/translations';
import { BdiMessage } from '../BdiMessage';

/** Where the cost-centre "add one" link goes: Setup → Company structure → Cost centres. */
export const COST_CENTRE_SETUP_HREF = '/setup?tab=costCenters';

/** "The <level> cost centre {code} is inactive, deleted or in another company." with the code in its own <bdi>. */
export function CostCentreInvalidSentence({ level, code }: { level: SourceLevel; code: string }) {
  switch (level) {
    case 'employee': return <BdiMessage message={msg('The employee cost centre {code} is inactive, deleted or in another company.')} values={{ code }} />;
    case 'position': return <BdiMessage message={msg('The position cost centre {code} is inactive, deleted or in another company.')} values={{ code }} />;
    case 'department': return <BdiMessage message={msg('The department cost centre {code} is inactive, deleted or in another company.')} values={{ code }} />;
    case 'parentDepartment': return <BdiMessage message={msg('The parent department cost centre {code} is inactive, deleted or in another company.')} values={{ code }} />;
    default: return <BdiMessage message={msg('The company default cost centre {code} is inactive, deleted or in another company.')} values={{ code }} />;
  }
}

/**
 * Where a cost centre comes from, as words. `chip` is the profile field's pill ("From department: Finance"); `sentence` is the
 * line under the edit flow's dropdown ("Inherited from Finance"), which for nothing resolved points to Setup. An unusable cost
 * centre is always a rose sentence naming the level and the code, never a silent blank.
 */
export function CostCentreSourceLine({ resolution, variant, testId, invalidTestId, setupLink = false }: {
  resolution: Resolution;
  variant: 'chip' | 'sentence';
  testId?: string;
  invalidTestId?: string;
  /** The "add one in Setup" sentence is a link (only for a reader who can set cost centres up). */
  setupLink?: boolean;
}) {
  const { t } = useLocale();
  if (resolution.kind === 'invalid') {
    return (
      <span data-testid={invalidTestId} className="inline-flex items-start gap-1 rounded-full bg-rose-50 px-2 py-0.5 text-xs font-semibold text-rose-700 dark:bg-rose-500/10 dark:text-rose-300">
        <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
        <span><CostCentreInvalidSentence level={resolution.level} code={resolution.code} /></span>
      </span>
    );
  }
  const name = 'sourceName' in resolution ? resolution.sourceName ?? '' : '';

  if (variant === 'chip') {
    const text = (() => {
      switch (resolution.kind) {
        case 'employee': return t('Set for this employee (override)');
        case 'department': return t('From department: {name}', { name });
        case 'parentDepartment': return t('From parent department: {name}', { name });
        case 'position': return t('From position: {name}', { name });
        case 'companyDefault': return t('Company default');
        case 'none': return t('No cost centre');
        default: return '';
      }
    })();
    if (!text) return null;
    const tone = resolution.kind === 'none' ? 'bg-amber-50 text-amber-800 dark:bg-amber-500/10 dark:text-amber-300'
      : resolution.kind === 'employee' ? 'bg-violet-50 text-violet-700 dark:bg-violet-500/10 dark:text-violet-300'
      : 'bg-slate-100 text-slate-700 dark:bg-white/10 dark:text-slate-200';
    return (
      <span data-testid={testId} className={`inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-xs font-semibold ${tone}`}>
        {resolution.kind === 'none' && <AlertTriangle className="h-3.5 w-3.5" aria-hidden />}
        {text}
      </span>
    );
  }

  const lineClass = 'text-xs text-slate-600 dark:text-slate-300';
  switch (resolution.kind) {
    case 'employee': return <p data-testid={testId} className={lineClass}>{t('Override: set for this employee')}</p>;
    case 'department': return <p data-testid={testId} className={lineClass}><BdiMessage message={msg('Inherited from {department}')} values={{ department: name }} /></p>;
    case 'parentDepartment': return <p data-testid={testId} className={lineClass}><BdiMessage message={msg('Inherited from {department} (parent department)')} values={{ department: name }} /></p>;
    case 'position': return <p data-testid={testId} className={lineClass}><BdiMessage message={msg('Inherited from position {title}')} values={{ title: name }} /></p>;
    case 'companyDefault': return <p data-testid={testId} className={lineClass}>{t('Company default')}</p>;
    case 'none': return (
      <p data-testid={testId} className="flex items-start gap-1.5 text-xs text-amber-800 dark:text-amber-300">
        <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden />
        {setupLink
          ? <Link href={COST_CENTRE_SETUP_HREF} className="font-semibold underline">{t('No cost centre: add one in Setup → Company structure')}</Link>
          : <span>{t('No cost centre: add one in Setup → Company structure')}</span>}
      </p>
    );
    default: return null;
  }
}
