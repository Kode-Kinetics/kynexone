'use client';

import { useCallback, useEffect, useState } from 'react';
import { essCompletionApi, type EssCompletion } from '../api/essCompletion';
import { useLocale } from '../contexts/LocaleContext';
import { EssLoadError, EssPageHeader, useCanWriteEss } from '../components/ess/EssParts';
import { LegacyCompletion } from './ess/completion/LegacyCompletion';
import { SectionForm } from './ess/completion/SectionForm';
import { SectionList } from './ess/completion/SectionList';

/** The server answered with the section model. Anything else (an API that predates it) falls back to the original page. */
const isSectionModel = (v: unknown): v is EssCompletion => !!v && typeof v === 'object' && Array.isArray((v as EssCompletion).sections) && typeof (v as EssCompletion).noticeVersion === 'string';

export function MyOnboardingPage() {
  const { t } = useLocale();
  const canWrite = useCanWriteEss();
  const [data, setData] = useState<EssCompletion | null>(null);
  const [legacy, setLegacy] = useState(false);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [selected, setSelected] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoadError(null);
    try {
      const result: unknown = await essCompletionApi.get();
      if (isSectionModel(result)) { setData(result); setLegacy(false); } else setLegacy(true);
    } catch (e) {
      const status = (e as { response?: { status?: number } } | null)?.response?.status;
      if (status === 404 || status === 405) setLegacy(true); else setLoadError(e);
    }
  }, []);
  useEffect(() => { void load(); }, [load]);

  if (legacy) return <LegacyCompletion />;
  const section = data?.sections.find(s => s.key === selected) ?? null;
  return (
    <div className="space-y-4" data-testid="ess-complete-profile">
      <EssPageHeader title={t('Complete your profile')} subtitle={t('Only the details HR still needs are listed. Nothing changes in your record until HR reviews it.')} />
      {loadError ? <EssLoadError error={loadError} onRetry={() => void load()} /> : !data ? <p aria-busy="true">{t('Loading…')}</p> : (
        <div className="grid gap-4 lg:grid-cols-[minmax(0,22rem)_minmax(0,1fr)]">
          <div className={section ? 'hidden lg:block' : undefined}><SectionList data={data} selected={selected} onSelect={s => setSelected(s.key)} /></div>
          <div>{section && <SectionForm key={section.key} section={section} meta={data} canWrite={canWrite} onBack={() => setSelected(null)} onChanged={() => void load()} />}</div>
        </div>
      )}
    </div>
  );
}
