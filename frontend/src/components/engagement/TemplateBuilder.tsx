'use client';

import { useEffect, useState } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { describeApiError } from '../../lib/apiError';
import { msg } from '../../i18n/translations';
import { engagementApi, type Driver, type Question, type QuestionType, type Template } from '../../api/engagement';
import { Modal } from '../Modal';

const TYPE_LABELS: Record<QuestionType, string> = {
  likert5: msg('Agreement (5-point scale)'),
  enps: msg('Recommend (0–10, eNPS)'),
  single: msg('Single choice'),
  multi: msg('Multiple choice'),
  text: msg('Free text (comment)'),
};

const STARTER: { drivers: Driver[]; questions: Question[] } = {
  drivers: [
    { key: 'recognition', labelEn: 'Recognition', labelAr: 'التقدير' },
    { key: 'growth', labelEn: 'Growth', labelAr: 'النمو' },
  ],
  questions: [
    { key: 'q1', type: 'likert5', textEn: 'I receive recognition for good work.', textAr: 'أحصل على التقدير عند إنجاز عمل جيد.', required: true, driver: 'recognition' },
    { key: 'q2', type: 'likert5', textEn: 'I have opportunities to learn and grow.', textAr: 'لدي فرص للتعلم والتطور.', required: true, driver: 'growth' },
    { key: 'q3', type: 'enps', textEn: 'How likely are you to recommend this company as a place to work?', textAr: 'ما مدى احتمال أن توصي بهذه الشركة كمكان للعمل؟', required: true },
    { key: 'q4', type: 'text', textEn: 'What one thing would make the biggest difference for you?', textAr: 'ما الشيء الواحد الذي سيُحدث أكبر فرق بالنسبة لك؟', required: false },
  ],
};

/**
 * Builds or edits a DRAFT template version: bilingual name, drivers and questions (5-point agreement, eNPS, single or
 * multiple choice, free text). Publishing freezes it (the database refuses later edits); changing a published template
 * starts a new version.
 */
export function TemplateBuilder({
  open, template, onClose, onSaved,
}: { open: boolean; template: Template | null; onClose: () => void; onSaved: (t: Template) => void }) {
  const { t } = useLocale();
  const [nameEn, setNameEn] = useState('');
  const [nameAr, setNameAr] = useState('');
  const [descriptionEn, setDescriptionEn] = useState('');
  const [descriptionAr, setDescriptionAr] = useState('');
  const [drivers, setDrivers] = useState<Driver[]>([]);
  const [questions, setQuestions] = useState<Question[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setError(null);
    setNameEn(template?.nameEn ?? '');
    setNameAr(template?.nameAr ?? '');
    setDescriptionEn(template?.descriptionEn ?? '');
    setDescriptionAr(template?.descriptionAr ?? '');
    setDrivers(template?.content.drivers ?? STARTER.drivers);
    setQuestions(template?.content.questions ?? STARTER.questions);
  }, [open, template]);

  const setQuestion = (i: number, patch: Partial<Question>) =>
    setQuestions((qs) => qs.map((q, j) => (j === i ? { ...q, ...patch } : q)));

  const addQuestion = () => {
    const used = new Set(questions.map((q) => q.key));
    let n = questions.length + 1;
    while (used.has(`q${n}`)) n++;
    setQuestions((qs) => [...qs, { key: `q${n}`, type: 'likert5', textEn: '', textAr: '', required: true, driver: drivers[0]?.key ?? null }]);
  };

  const save = async () => {
    setSaving(true);
    setError(null);
    const body = {
      nameEn, nameAr, descriptionEn, descriptionAr, companyId: template?.companyId ?? null, drivers,
      questions: questions.map((q) => ({ ...q, options: q.type === 'single' || q.type === 'multi' ? q.options ?? [] : null, driver: q.driver || null })),
    };
    try {
      const saved = template
        ? await engagementApi.updateTemplate(template.id, { ...body, version: template.version })
        : await engagementApi.createTemplate(body);
      onSaved(saved);
    } catch (e) {
      setError(describeApiError(e, t));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal
      isOpen={open}
      title={template ? t('Edit template draft') : t('New survey template')}
      onClose={onClose}
      size="xl"
      footer={(
        <>
          <button type="button" className="btn-secondary" onClick={onClose}>{t('Cancel')}</button>
          <button type="button" className="btn-primary" onClick={() => void save()} disabled={saving} data-testid="template-save">
            {saving ? t('Saving…') : t('Save draft')}
          </button>
        </>
      )}
    >
      <div className="space-y-5">
        {error && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:bg-rose-900/20 dark:text-rose-300">{error}</p>}
        <div className="grid gap-3 md:grid-cols-2">
          <Field label={t('Name (English)')}><input className="input" value={nameEn} onChange={(e) => setNameEn(e.target.value)} data-testid="template-name-en" /></Field>
          <Field label={t('Name (Arabic)')}><input className="input" dir="rtl" value={nameAr} onChange={(e) => setNameAr(e.target.value)} data-testid="template-name-ar" /></Field>
          <Field label={t('Description (English)')}><input className="input" value={descriptionEn} onChange={(e) => setDescriptionEn(e.target.value)} /></Field>
          <Field label={t('Description (Arabic)')}><input className="input" dir="rtl" value={descriptionAr} onChange={(e) => setDescriptionAr(e.target.value)} /></Field>
        </div>

        <section>
          <div className="mb-2 flex items-center justify-between gap-2">
            <h3 className="text-sm font-bold text-slate-900 dark:text-white">{t('Drivers')}</h3>
            <button type="button" className="btn-secondary" onClick={() => setDrivers((d) => [...d, { key: `driver${d.length + 1}`, labelEn: '', labelAr: '' }])}>
              <Plus className="h-4 w-4" aria-hidden="true" />{t('Add survey driver')}
            </button>
          </div>
          <p className="mb-2 text-xs text-slate-500">{t('A driver groups 5-point questions into one score, such as Recognition or Growth.')}</p>
          <ul className="space-y-2">
            {drivers.map((d, i) => (
              <li key={i} className="grid gap-2 md:grid-cols-[140px_1fr_1fr_auto]">
                <input className="input" aria-label={t('Driver key')} value={d.key} onChange={(e) => setDrivers((ds) => ds.map((x, j) => (j === i ? { ...x, key: e.target.value } : x)))} />
                <input className="input" aria-label={t('Driver name (English)')} value={d.labelEn} onChange={(e) => setDrivers((ds) => ds.map((x, j) => (j === i ? { ...x, labelEn: e.target.value } : x)))} />
                <input className="input" dir="rtl" aria-label={t('Driver name (Arabic)')} value={d.labelAr} onChange={(e) => setDrivers((ds) => ds.map((x, j) => (j === i ? { ...x, labelAr: e.target.value } : x)))} />
                <button type="button" className="btn-secondary" aria-label={t('Remove driver {name}', { name: d.labelEn || d.key })} onClick={() => setDrivers((ds) => ds.filter((_, j) => j !== i))}>
                  <Trash2 className="h-4 w-4" aria-hidden="true" />
                  <span className="sr-only md:not-sr-only">{t('Remove')}</span>
                </button>
              </li>
            ))}
          </ul>
        </section>

        <section>
          <div className="mb-2 flex items-center justify-between gap-2">
            <h3 className="text-sm font-bold text-slate-900 dark:text-white">{t('Questions')}</h3>
            <button type="button" className="btn-secondary" onClick={addQuestion} data-testid="template-add-question">
              <Plus className="h-4 w-4" aria-hidden="true" />{t('Add question')}
            </button>
          </div>
          <ol className="space-y-3">
            {questions.map((q, i) => (
              <li key={i} className="rounded-lg border border-slate-200 p-3 dark:border-white/10" data-testid={`template-question-${i}`}>
                <div className="grid gap-2 md:grid-cols-[100px_220px_1fr_auto]">
                  <input className="input" aria-label={t('Question key')} value={q.key} onChange={(e) => setQuestion(i, { key: e.target.value })} />
                  <select className="select" aria-label={t('Question type')} value={q.type} onChange={(e) => setQuestion(i, { type: e.target.value as QuestionType })}>
                    {(Object.keys(TYPE_LABELS) as QuestionType[]).map((type) => <option key={type} value={type}>{t(TYPE_LABELS[type])}</option>)}
                  </select>
                  <select className="select" aria-label={t('Survey driver')} value={q.driver ?? ''} onChange={(e) => setQuestion(i, { driver: e.target.value || null })} disabled={q.type !== 'likert5'}>
                    <option value="">{t('No driver')}</option>
                    {drivers.map((d) => <option key={d.key} value={d.key}>{d.labelEn || d.key}</option>)}
                  </select>
                  <button type="button" className="btn-secondary" aria-label={t('Remove question {key}', { key: q.key })} onClick={() => setQuestions((qs) => qs.filter((_, j) => j !== i))}>
                    <Trash2 className="h-4 w-4" aria-hidden="true" />
                    <span className="sr-only md:not-sr-only">{t('Remove')}</span>
                  </button>
                </div>
                <div className="mt-2 grid gap-2 md:grid-cols-2">
                  <input className="input" aria-label={t('Question (English)')} placeholder={t('Question (English)')} value={q.textEn} onChange={(e) => setQuestion(i, { textEn: e.target.value })} />
                  <input className="input" dir="rtl" aria-label={t('Question (Arabic)')} placeholder={t('Question (Arabic)')} value={q.textAr} onChange={(e) => setQuestion(i, { textAr: e.target.value })} />
                </div>
                {(q.type === 'single' || q.type === 'multi') && (
                  <div className="mt-2 space-y-2">
                    {(q.options ?? []).map((o, oi) => (
                      <div key={oi} className="grid gap-2 md:grid-cols-[120px_1fr_1fr_auto]">
                        <input className="input" aria-label={t('Option key')} value={o.key} onChange={(e) => setQuestion(i, { options: (q.options ?? []).map((x, j) => (j === oi ? { ...x, key: e.target.value } : x)) })} />
                        <input className="input" aria-label={t('Option (English)')} value={o.labelEn} onChange={(e) => setQuestion(i, { options: (q.options ?? []).map((x, j) => (j === oi ? { ...x, labelEn: e.target.value } : x)) })} />
                        <input className="input" dir="rtl" aria-label={t('Option (Arabic)')} value={o.labelAr} onChange={(e) => setQuestion(i, { options: (q.options ?? []).map((x, j) => (j === oi ? { ...x, labelAr: e.target.value } : x)) })} />
                        <button type="button" className="btn-secondary" aria-label={t('Remove option {key}', { key: o.key })} onClick={() => setQuestion(i, { options: (q.options ?? []).filter((_, j) => j !== oi) })}>
                          <Trash2 className="h-4 w-4" aria-hidden="true" />
                        </button>
                      </div>
                    ))}
                    <button type="button" className="text-xs font-semibold text-sapphire hover:underline"
                      onClick={() => setQuestion(i, { options: [...(q.options ?? []), { key: `o${(q.options?.length ?? 0) + 1}`, labelEn: '', labelAr: '' }] })}>
                      {t('Add option')}
                    </button>
                  </div>
                )}
                <label className="mt-2 flex items-center gap-2 text-sm text-slate-700 dark:text-slate-200">
                  <input type="checkbox" checked={q.required} onChange={(e) => setQuestion(i, { required: e.target.checked })} />
                  {t('Answer required')}
                </label>
              </li>
            ))}
          </ol>
        </section>
      </div>
    </Modal>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="flex flex-col gap-1 text-xs font-semibold text-slate-600 dark:text-slate-300">
      {label}
      {children}
    </label>
  );
}
