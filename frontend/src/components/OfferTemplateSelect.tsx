'use client';
import { useEffect, useId, useState } from 'react';
import { offerTemplatesApi, type OfferTemplateSummary } from '../api/offerTemplates';
import { useCompany } from '../contexts/CompanyContext';
import { requestFailureReason } from '../lib/requestFailure';
import { useOfferLetterT } from '../i18n/offerLetter';

export function OfferTemplateSelect({ value, onChange }: { value: string; onChange: (id: string) => void }) {
  const t = useOfferLetterT();
  const id = useId();
  const { companyVersion } = useCompany();
  const [templates, setTemplates] = useState<OfferTemplateSummary[]>([]);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    let active = true;
    setLoading(true);
    offerTemplatesApi.list().then(items => { if (active) { setTemplates(items); setError(''); } })
      .catch(e => { if (active) setError(requestFailureReason(e)); }).finally(() => { if (active) setLoading(false); });
    return () => { active = false; };
  }, [companyVersion]);
  return <div className="space-y-1">
    <label htmlFor={id} className="block text-xs font-medium">{t("Letter template")}</label>
    <select id={id} className="input w-full" value={value} onChange={e => onChange(e.target.value)} disabled={loading}>
      <option value="">{t("Standard letter")}</option>
      {templates.map(template => <option key={template.id} value={template.id}>{template.name} · v{template.version}</option>)}
    </select>
    <p className="text-xs text-slate-500">{t("Create branded designs in Offers → Letter designer. The selected design is copied into this offer.")}</p>
    {error && <p role="alert" className="text-xs text-rose-600">{t("Templates could not load")}{' '}{t(error)}{' '}{t("Standard letter remains available.")}</p>}
  </div>;
}
