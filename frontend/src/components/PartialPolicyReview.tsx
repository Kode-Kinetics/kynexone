'use client';

import { useEffect, useState } from 'react';
import { useT } from '../hooks/useT';
import { partialPolicyShapes, missingPolicyFields, type PolicyShape, type PartialReviewEntry } from '../lib/partialPolicy';

const labelFor = (key: string) => key.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, c => c.toUpperCase());

function PolicyFields({ shape, value, onChange, name }: {
  shape: PolicyShape; value: unknown; onChange: (value: unknown) => void; name: string;
}) {
  const t = useT();
  if (shape.kind === 'object') {
    const record = value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {};
    return <div className="grid gap-3 sm:grid-cols-2">{Object.entries(shape.fields).map(([key, child]) =>
      <PolicyFields key={key} shape={child} name={key} value={record[key]} onChange={next => onChange({ ...record, [key]: next })} />)}</div>;
  }
  if (shape.kind === 'array') {
    const rows = Array.isArray(value) ? value : [];
    return <div className="space-y-3 sm:col-span-2">{rows.map((row, index) => <fieldset key={index} className="rounded-lg border p-3 dark:border-white/15">
      <legend className="px-1 text-sm font-medium">{t(labelFor(name))} {index + 1}</legend>
      <PolicyFields shape={shape.item} name={name} value={row} onChange={next => onChange(rows.map((item, i) => i === index ? next : item))} />
      {name === 'multipliers' && <button type="button" className="btn-secondary mt-2" onClick={() => onChange(rows.filter((_, i) => i !== index))}>{t('Remove entry {number}', { number: index + 1 })}</button>}
    </fieldset>)}{(rows.length === 0 || name === 'multipliers') && <button type="button" className="btn-secondary" disabled={name === 'multipliers' && rows.length >= 3} onClick={() => onChange([...rows, {}])}>{t('Add missing entry')}</button>}</div>;
  }
  const unknown = missingPolicyFields(shape, value).length > 0;
  return <label className="block text-sm">
    <span className="mb-1 block font-medium">{t(labelFor(name))}{unknown && <span className="ms-2 text-xs text-amber-700 dark:text-amber-300">{t('Needs your answer')}</span>}</span>
    {shape.kind === 'boolean' ? <select className="input w-full" value={typeof value === 'boolean' ? String(value) : ''} onChange={event => onChange(event.target.value === '' ? undefined : event.target.value === 'true')}>
      <option value="">{t('Not stated in policy')}</option><option value="true">{t('Yes')}</option><option value="false">{t('No')}</option>
    </select> : shape.kind === 'text' && shape.options ? <select className="input w-full" value={typeof value === 'string' ? value : ''} onChange={event => onChange(event.target.value || undefined)}>
      <option value="">{t('Not stated in policy')}</option>{shape.options.map(option => <option key={option} value={option}>{t(option)}</option>)}
    </select> : <input className="input w-full" type={shape.kind === 'number' ? 'number' : name === 'effectiveFrom' || name === 'effectiveTo' ? 'date' : 'text'} min={shape.kind === 'number' ? 0 : undefined} step={shape.kind === 'number' ? shape.integer ? 1 : 'any' : undefined}
      disabled={shape.nullable && value === null} placeholder={t('Not stated in policy')}
      value={Array.isArray(value) ? value.join(', ') : typeof value === 'string' || typeof value === 'number' ? value : ''}
      onChange={event => onChange(shape.kind === 'number' ? event.target.value === '' ? undefined : event.target.valueAsNumber : shape.kind === 'strings' ? event.target.value.split(',').map(item => item.trim()).filter(Boolean) : event.target.value || undefined)} />}
    {shape.kind === 'strings' && <span className="mt-1 block text-xs text-slate-500">{t('Separate grade codes with commas.')}</span>}
    {shape.nullable && <span className="mt-1 flex items-center gap-2 text-xs"><input type="checkbox" checked={value === null} onChange={event => onChange(event.target.checked ? null : undefined)} />{t(name === 'effectiveTo' ? 'No end date' : 'No restriction for this field')}</span>}
  </label>;
}

function PartialEntry({ entry, onComplete, onEdit }: { entry: PartialReviewEntry; onComplete: (entry: PartialReviewEntry) => void; onEdit?: (entry: PartialReviewEntry) => void }) {
  const t = useT();
  const [value, setValue] = useState<unknown>(entry.value);
  const [accepted, setAccepted] = useState(false);
  const shape = partialPolicyShapes[entry.field];
  if (!shape || accepted) return null;
  const missing = missingPolicyFields(shape, value, entry.field);
  return <details className="rounded-lg border border-amber-300 p-3 dark:border-amber-700" open>
    <summary className="cursor-pointer font-semibold">{t(labelFor(entry.field))} — {t('Complete extracted fields')}</summary>
    <p className="my-2 text-xs text-slate-600 dark:text-slate-300">{entry.sourceName}</p>
    <p className="mb-2 text-xs text-slate-600 dark:text-slate-300">{t('The passage supports extracted values. Answers or edits you add are your decisions and will be marked as user-completed.')}</p>
    <blockquote className="mb-3 border-s-2 border-sapphire/30 ps-3 text-sm">{entry.sourceQuote}</blockquote>
    <PolicyFields shape={shape} value={value} name={entry.field} onChange={next => { setValue(next); onEdit?.({ ...entry, value: next }); }} />
    <p className="mt-3 text-xs" role="status">{t('{count} fields still need your answer.', { count: missing.length })}</p>
    <button type="button" className="btn-primary mt-3" disabled={missing.length > 0} onClick={() => { onComplete({ ...entry, value, missingFields: [] }); setAccepted(true); }}>{t('Review completed suggestion')}</button>
  </details>;
}

export function PartialPolicyReview({ entries, onComplete, onEdit }: {
  entries: PartialReviewEntry[]; onComplete: (entry: PartialReviewEntry) => void; onEdit?: (entry: PartialReviewEntry) => void;
}) {
  const t = useT();
  const [page, setPage] = useState(0);
  const pages = Math.max(1, Math.ceil(entries.length / 5));
  useEffect(() => { setPage(current => Math.min(current, pages - 1)); }, [pages]);
  if (!entries.length) return null;
  return <section aria-label={t('Partially extracted settings')} className="space-y-3">
    <h4 className="font-semibold">{t('Partially extracted settings')}</h4>
    <p className="text-sm text-slate-600 dark:text-slate-300">{t('Known policy values are filled below. Answer only the missing fields; nothing is applied until you review the completed draft.')}</p>
    {entries.slice(page * 5, page * 5 + 5).map((entry, index) => <PartialEntry key={`${entry.documentId}:${entry.field}:${entry.sourceStart}:${page * 5 + index}`} entry={entry} onComplete={onComplete} onEdit={onEdit} />)}
    {pages > 1 && <div className="flex items-center justify-between gap-3"><button type="button" className="btn-secondary" disabled={page === 0} onClick={() => setPage(page - 1)}>{t('Previous')}</button><span className="text-sm">{t('Page {page} of {pages}', { page: page + 1, pages })}</span><button type="button" className="btn-secondary" disabled={page + 1 >= pages} onClick={() => setPage(page + 1)}>{t('Next')}</button></div>}
  </section>;
}
