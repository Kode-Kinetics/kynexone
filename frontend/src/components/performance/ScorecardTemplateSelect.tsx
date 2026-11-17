'use client';

import { useCallback, useEffect, useState } from 'react';
import { Field, Select } from '../ui/Field';
import { Skeleton } from '../ui/Skeleton';
import { EmptyState } from '../ui/EmptyState';
import { LoadFailedNotice } from '../ui/LoadFailedRow';
import { useT } from '../../hooks/useT';
import { templatesApi } from '../../api/performance';
import { defaultTemplateId, selectableTemplates } from '../../lib/appraisalsUi';
import type { ScorecardTemplate } from '../../api/performance';

/**
 * The scorecard a performance cycle is scored on. A cycle without one cannot be launched, so the New Cycle form asks for it
 * up front and pre-selects the company default (or the only template there is). With no active template the form says where
 * to make one, rather than leaving a cycle that can never start.
 */
export function ScorecardTemplateSelect({ value, onChange, onReady }: {
  value: string;
  onChange: (id: string) => void;
  /** Called once the templates have loaded, with how many can be picked. */
  onReady?: (available: number) => void;
}) {
  const t = useT();
  const [templates, setTemplates] = useState<ScorecardTemplate[] | null>(null);
  const [failure, setFailure] = useState<unknown>(null);

  const load = useCallback(() => {
    setTemplates(null); setFailure(null);
    templatesApi.list()
      .then(list => {
        setTemplates(list);
        const active = selectableTemplates(list);
        onReady?.(active.length);
        const preset = defaultTemplateId(list);
        if (preset) onChange(preset);
      })
      .catch(e => setFailure(e ?? new Error('failed')));
    // eslint-disable-next-line react-hooks/exhaustive-deps -- load once; the parent's callbacks are not dependencies
  }, []);
  useEffect(load, [load]);

  if (failure) return <LoadFailedNotice error={failure} onRetry={load} />;
  if (!templates) {
    return (
      <div role="status" aria-busy="true" data-testid="template-loading">
        <span className="sr-only">{t('apr.template.loading')}</span>
        <Skeleton className="h-9 w-full" />
      </div>
    );
  }
  const active = selectableTemplates(templates);
  if (active.length === 0) {
    return (
      <EmptyState
        kind="first-use"
        title={t('apr.template.noneTitle')}
        description={t('apr.template.noneBody')}
        action={{ label: t('apr.template.noneAction'), href: '/setup?tab=performanceScorecards' }}
      />
    );
  }
  return (
    <Field label={t('apr.template.label')} required info={t('apr.template.info')}>
      <Select value={value} onChange={e => onChange(e.target.value)} data-testid="cycle-template">
        <option value="">{t('apr.template.choose')}</option>
        {active.map(tpl => (
          <option key={tpl.id} value={tpl.id}>{tpl.isDefault ? t('apr.template.defaultSuffix', { name: tpl.name }) : tpl.name}</option>
        ))}
      </Select>
    </Field>
  );
}
