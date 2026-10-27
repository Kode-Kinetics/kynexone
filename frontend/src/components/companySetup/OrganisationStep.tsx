'use client';

import { useCallback, useMemo, useRef, useState } from 'react';
import { Copy, Languages } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { msg } from '../../i18n/translations';
import {
  companySetupApi, type CompanySetupCostCentre, type CompanySetupDepartment, type CompanySetupDesignation, type CompanySetupDraftBody,
  type CompanySetupGrade, type OrgSection,
} from '../../api/companySetup';
import {
  PART_COPY, appendCopiedRows, arabicProgress, gradeWarnings, sortedGradeIndexes, suggestCostCentre, suggestedMidpoint,
} from '../../lib/companySetupWizard';
import { OrgRowsEditor, type OrgDetailField } from './OrgRowsEditor';
import { useWizardForm } from './wizardForm';
import { UseDefaultsButton } from './UseDefaultsButton';

/** A suggestion button keeps focus where it is on press: leaving a field can show its message and move the button. */
const keepFocus = (event: React.MouseEvent) => event.preventDefault();

const ADD_LABEL: Record<OrgSection, string> = {
  costCentres: msg('Add cost centre'), departments: msg('Add department'), grades: msg('Add grade'), designations: msg('Add designation'),
};
const ROW_LABEL: Record<OrgSection, string> = {
  costCentres: msg('Cost centre {number}'), departments: msg('Department {number}'), grades: msg('Grade {number}'), designations: msg('Designation {number}'),
};
/** The Setup tab each part is managed in, and its (dictionary key) name. */
const SETUP_TAB: Record<OrgSection, string> = { costCentres: 'costCenters', departments: 'departments', grades: 'grades', designations: 'designations' };
const SETUP_TAB_LABEL: Record<OrgSection, string> = { costCentres: msg('Cost centres'), departments: msg('Departments'), grades: msg('Grades'), designations: msg('Designations') };
const EMPTY_TEXT: Record<OrgSection, string> = {
  costCentres: msg('No cost centres yet. They are optional: add them if finance reports by cost centre.'),
  departments: msg('No departments yet. Add the teams people belong to.'),
  grades: msg('No grades yet. Add the pay levels this company uses.'),
  designations: msg('No designations yet. Add the job titles people hold.'),
};

/**
 * Step 4, one part at a time (Addendum B4): cost centres, departments, grades, designations — all this company's
 * own (B). Group convenience is "Copy from" a sister company, by value (B3); the country pack is "Use defaults".
 * Everything the screen suggests is deterministic and shown for the reader to confirm, never applied silently.
 */
export function OrganisationStep({ defaults, part }: { defaults: CompanySetupDraftBody | null; part: OrgSection }) {
  const { t, locale } = useLocale();
  const fmt = useFormat();
  const { body, update, context, readOnly, baseline, rowStates, restoreRow } = useWizardForm();
  const existing = !!body.existingCompanyId;
  const set = useCallback(<K extends OrgSection>(key: K, rows: CompanySetupDraftBody[K]) => update(b => ({ ...b, [key]: rows })), [update]);

  // ── Copy from a sister company (group scope) ──
  const sisters = (context?.isGroupScope ? context.sisterCompanies : []).filter(c => c.id !== body.existingCompanyId);
  const [copyFrom, setCopyFrom] = useState(sisters[0]?.id ?? '');
  const [copyState, setCopyState] = useState<{ busy: boolean; text: string; tone: 'ok' | 'error' } | null>(null);
  const copy = async () => {
    const source = sisters.find(c => c.id === copyFrom) ?? sisters[0];
    if (!source) return;
    setCopyState({ busy: true, text: t('Copying from {company}…', { company: source.legalNameEn }), tone: 'ok' });
    try {
      const result = await companySetupApi.copyRows(source.id, part);
      const copied = (result[part] ?? []) as CompanySetupDraftBody[typeof part];
      // Counts from what is on screen now; the merge itself runs on the latest rows.
      const { added, skipped } = appendCopiedRows(body[part] as { code: string }[], copied as { code: string }[]);
      update(b => ({ ...b, [part]: appendCopiedRows(b[part] as { code: string }[], copied as { code: string }[]).rows }));
      setCopyState({ busy: false, tone: 'ok', text: copied.length === 0
        ? t('{company} has no {section} to copy.', { company: source.legalNameEn, section: t(PART_COPY[part].title) })
        : skipped > 0
          ? t('{added, plural, one {Copied # row} other {Copied # rows}} from {company}. {skipped, plural, one {# was skipped because its code is already here.} other {# were skipped because their codes are already here.}}', { added, skipped, company: source.legalNameEn })
          : t('{added, plural, one {Copied # row} other {Copied # rows}} from {company}. Check them, then save.', { added, company: source.legalNameEn }) });
    } catch {
      setCopyState({ busy: false, tone: 'error', text: t('Nothing was copied. Check your connection and try again.') });
    }
  };

  // ── Arabic names: meter + walk through the empty cells ──
  const arKey = part === 'designations' ? 'titleAr' : 'nameAr';
  const rowsOfPart = body[part] as { nameAr?: string; titleAr?: string }[];
  const arabic = arabicProgress(rowsOfPart, r => (part === 'designations' ? r.titleAr : r.nameAr));
  const walker = useRef<(() => boolean) | null>(null);
  const registerWalker = useCallback((walk: () => boolean) => { walker.current = walk; }, []);

  // ── Designations: only those with no grade ──
  const [onlyNoGrade, setOnlyNoGrade] = useState(false);
  const noGrade = body.designations.filter(d => !(d.gradeCode ?? '').trim()).length;

  // ── Grades: in level order, kept while the reader types (re-sorted when a row is added or removed) ──
  const gradeOrder = useMemo(() => sortedGradeIndexes(body.grades),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [body.grades.length]);
  const warnings = useMemo(() => new Map(gradeWarnings(body.grades).map(w => [w.field, w])), [body.grades]);

  const codeOptions = (rows: { code: string; nameEn?: string; titleEn?: string; nameAr?: string; titleAr?: string }[]) => {
    const seen = new Set<string>();
    const out: { value: string; label: string }[] = [];
    for (const r of rows) {
      const code = r.code.trim();
      if (!code || seen.has(code.toUpperCase())) continue;
      seen.add(code.toUpperCase());
      const name = (locale === 'ar' ? r.nameAr || r.titleAr : '') || r.nameEn || r.titleEn || '';
      out.push({ value: code, label: name ? `${code} · ${name}` : code });
    }
    return out;
  };

  const defaultsButton = defaults && defaults[part].length > 0 && !readOnly && !existing
    ? <UseDefaultsButton current={body[part] as unknown[]} defaults={defaults[part] as unknown[]} section={t(PART_COPY[part].title)} onApply={() => set(part, defaults[part] as never)} />
    : null;

  const toolbar = (
    <div className="mb-3 flex min-w-0 flex-wrap items-center justify-between gap-2">
      <div className="flex min-w-0 flex-wrap items-center gap-2 text-xs text-slate-600 dark:text-slate-400">
        {arabic.total > 0 && <>
          <span className="inline-flex items-center gap-1.5 tabular-nums" data-testid="arabic-meter">
            <Languages className="h-3.5 w-3.5 text-slate-400" aria-hidden="true" />
            {t('Arabic names: {filled} of {total}', { filled: arabic.filled, total: arabic.total })}
          </span>
          {arabic.filled < arabic.total && !readOnly && (
            <button type="button" className="rounded-lg px-1.5 py-0.5 font-semibold text-blue-700 transition-colors duration-150 hover:bg-sapphire/[0.06] dark:text-primary-fg"
              onClick={() => walker.current?.()}>{t('Fill Arabic names')}</button>
          )}
        </>}
        {part === 'designations' && noGrade > 0 && (
          <button type="button" aria-pressed={onlyNoGrade} onClick={() => setOnlyNoGrade(v => !v)} data-testid="no-grade-filter"
            className={`rounded-full border px-2.5 py-0.5 font-medium transition-colors duration-150 ${onlyNoGrade ? 'border-amber-400 bg-amber-50 text-amber-900 dark:bg-amber-500/15 dark:text-amber-100' : 'border-amber-300 text-amber-800 hover:bg-amber-50 dark:text-amber-200 dark:hover:bg-amber-500/10'}`}>
            {onlyNoGrade ? t('Show all designations') : t('{count, plural, one {# designation has no grade.} other {# designations have no grade.}} Show only these', { count: noGrade })}
          </button>
        )}
      </div>
      {!readOnly && (sisters.length > 0 || defaultsButton) && (
        <div className="flex flex-wrap items-center gap-2">
          {sisters.length > 1 && (
            <label className="flex items-center gap-1.5 text-xs text-slate-600 dark:text-slate-400">
              <span>{t('Copy from')}</span>
              <select className="select h-8 max-w-[12rem] py-0 text-xs" value={copyFrom} onChange={e => setCopyFrom(e.target.value)}>
                {sisters.map(c => <option key={c.id} value={c.id}>{c.legalNameEn}</option>)}
              </select>
            </label>
          )}
          {sisters.length > 0 && (
            <button type="button" className="btn-secondary" disabled={copyState?.busy} onClick={() => void copy()} data-testid="copy-from-sister">
              <Copy className="h-4 w-4" aria-hidden="true" />
              {sisters.length > 1 ? t('Copy') : t('Copy from {company}', { company: sisters[0].legalNameEn })}
            </button>
          )}
          {defaultsButton}
        </div>
      )}
    </div>
  );

  const copyLine = (
    <p role="status" data-testid="copy-status" className={`min-h-[1.25rem] text-xs ${copyState?.tone === 'error' ? 'text-rose-700 dark:text-rose-300' : 'text-slate-600 dark:text-slate-400'}`}>
      {copyState?.text ?? ''}
    </p>
  );

  const rowLabel = (i: number) => t(ROW_LABEL[part], { number: i + 1 });
  // Existing mode: Remove works on every saved row. The row is marked here; the server deletes it (nothing uses it) or
  // turns it off (in use) when the part is saved, the same rule as Setup.
  const removedIds = body.removals?.[part] ?? [];
  const setRemoved = (ids: string[]) => update(b => ({ ...b, removals: { ...(b.removals ?? {}), [part]: ids.length > 0 ? ids : undefined } }));
  const shared = {
    addLabel: t(ADD_LABEL[part]), emptyText: t(EMPTY_TEXT[part]), rowLabel, onWalkArabic: registerWalker,
    ...(existing ? {
      rowStates, removedIds, onRemovedIdsChange: setRemoved,
      onRestore: restoreRow ? (id: string) => restoreRow(part, id) : undefined,
      manage: { href: `/setup?tab=${SETUP_TAB[part]}&companyId=${encodeURIComponent(body.existingCompanyId!)}`, label: SETUP_TAB_LABEL[part] },
    } : {}),
  };

  let editor: React.ReactNode = null;
  if (part === 'costCentres') {
    editor = <OrgRowsEditor<CompanySetupCostCentre> key="costCentres" section="costCentres" rows={body.costCentres} onChange={rows => set('costCentres', rows)}
      nameKey="nameEn" arKey="nameAr" codePrefix="CC_" newRow={() => ({ code: '', nameEn: '', nameAr: '' })}
      baselineRows={existing ? baseline?.costCentres ?? [] : undefined} {...shared} />;
  } else if (part === 'departments') {
    const details: OrgDetailField<CompanySetupDepartment>[] = [
      { key: 'costCentreCode', label: msg('Cost centre'), kind: 'select', options: codeOptions(body.costCentres), wanted: body.costCentres.length > 0, emptyLabel: msg('No cost centre') },
      { key: 'parentCode', label: msg('Parent department'), kind: 'select', options: codeOptions(body.departments), emptyLabel: msg('None') },
      { key: 'branchCode', label: msg('Branch'), kind: 'select', options: codeOptions(body.branches), emptyLabel: msg('Any branch') },
    ];
    editor = <OrgRowsEditor<CompanySetupDepartment> key="departments" section="departments" rows={body.departments} onChange={rows => set('departments', rows)}
      nameKey="nameEn" arKey="nameAr" details={details} newRow={() => ({ code: '', nameEn: '', nameAr: '' })}
      baselineRows={existing ? baseline?.departments ?? [] : undefined}
      detailNote={(row, index) => {
        if ((row.costCentreCode ?? '').trim() || readOnly) return null;
        const code = suggestCostCentre(row.nameEn, body.costCentres);
        if (!code) return null;
        const centre = body.costCentres.find(c => c.code.trim() === code);
        return (
          <p className="flex flex-wrap items-center gap-2 text-xs text-slate-600 dark:text-slate-400" data-testid="cost-centre-suggestion">
            <span><bdi>{t('Suggested: {code} · {name}', { code, name: centre?.nameEn ?? '' })}</bdi> <span className="text-slate-500">{t('(names match)')}</span></span>
            <button type="button" onMouseDown={keepFocus} className="font-semibold text-blue-700 hover:underline dark:text-primary-fg"
              onClick={() => set('departments', body.departments.map((d, i) => (i === index ? { ...d, costCentreCode: code } : d)))}>
              {t('Use this cost centre')}<span className="sr-only"> {rowLabel(index)}</span>
            </button>
          </p>
        );
      }}
      {...shared} />;
  } else if (part === 'grades') {
    const details: OrgDetailField<CompanySetupGrade>[] = [
      { key: 'level', label: msg('Level'), kind: 'number', inputMode: 'numeric' },
      { key: 'minSalary', label: msg('Minimum'), kind: 'number', inputMode: 'decimal', amount: true, wanted: true },
      { key: 'midSalary', label: msg('Midpoint'), kind: 'number', inputMode: 'decimal', amount: true },
      { key: 'maxSalary', label: msg('Maximum'), kind: 'number', inputMode: 'decimal', amount: true, wanted: true },
    ];
    editor = <OrgRowsEditor<CompanySetupGrade> key="grades" section="grades" rows={body.grades} onChange={rows => set('grades', rows)}
      nameKey="nameEn" arKey="nameAr" details={details} order={gradeOrder} rowWarnings={warnings}
      newRow={() => ({ code: '', nameEn: '', nameAr: '', level: Math.max(0, ...body.grades.map(g => g.level || 0)) + 1, minSalary: 0, midSalary: 0, maxSalary: 0 })}
      baselineRows={existing ? baseline?.grades ?? [] : undefined}
      detailNote={(row, index) => {
        const middle = suggestedMidpoint(row);
        if (middle === null || readOnly) return null;
        return (
          <button type="button" onMouseDown={keepFocus} className="text-xs font-semibold text-blue-700 hover:underline dark:text-primary-fg" data-testid="use-middle"
            onClick={() => set('grades', body.grades.map((g, i) => (i === index ? { ...g, midSalary: middle } : g)))}>
            <bdi className="tabular-nums">{t('Use the middle: {amount}', { amount: fmt.plain.integer(middle) })}</bdi><span className="sr-only"> {rowLabel(index)}</span>
          </button>
        );
      }}
      {...shared} />;
  } else {
    const details: OrgDetailField<CompanySetupDesignation>[] = [
      { key: 'departmentCode', label: msg('Department'), kind: 'select', options: codeOptions(body.departments), wanted: true, emptyLabel: msg('No department') },
      { key: 'gradeCode', label: msg('Grade'), kind: 'select', options: codeOptions(body.grades), wanted: true, emptyLabel: msg('No grade') },
    ];
    editor = <OrgRowsEditor<CompanySetupDesignation> key="designations" section="designations" rows={body.designations} onChange={rows => set('designations', rows)}
      nameKey="titleEn" arKey="titleAr" details={details} newRow={() => ({ code: '', titleEn: '', titleAr: '' })}
      baselineRows={existing ? baseline?.designations ?? [] : undefined}
      visible={onlyNoGrade ? row => !(row.gradeCode ?? '').trim() : undefined}
      {...shared} />;
  }

  return (
    <div className="min-w-0" data-testid={`org-part-${part}`} data-arabic-key={arKey}>
      {toolbar}
      {part === 'grades' && <p className="mb-3 text-xs text-slate-600 dark:text-slate-400">{t('Salary bands are in {currency}, this company’s currency.', { currency: body.company.currency || '—' })}</p>}
      {editor}
      {copyLine}
    </div>
  );
}
