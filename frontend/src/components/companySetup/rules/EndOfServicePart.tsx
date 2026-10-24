'use client';

import { useLocale } from '../../../contexts/LocaleContext';
import { msg } from '../../../i18n/translations';
import type { CompanySetupPlan } from '../../../api/companySetup';
import type { LeaveEditorChange } from '../../../views/LeavePage';
import { SetupEditorState } from '../../setup/SetupEditorState';
import { useCountryName } from '../CountryOptions';
import { Notice, useWizardForm } from '../wizardForm';
import { EmbeddedCountryRules } from './EmbeddedEditors';
import { PartHeading, PlanLines, useDirtyReporter, useRulesAccess } from './rulesShared';
import { SaveModePill } from './SaveModePill';

/**
 * End-of-service: what the country pack adds at Create / Update, then the existing Country rules editor
 * (Setup → Country rules), focused on this company's country, saving live.
 */
export function EndOfServicePart({ preview, previewState, onSaved }: {
  preview: CompanySetupPlan | null;
  previewState: 'idle' | 'loading' | 'error';
  onSaved?: (change: LeaveEditorChange) => void;
}) {
  const { t } = useLocale();
  const country = useCountryName();
  const { body } = useWizardForm();
  const { canEditCountryRules } = useRulesAccess();
  const report = useDirtyReporter(msg('Country rules'));
  const existing = !!body.existingCompanyId;
  const countryName = country(body.company.countryCode) || '—';

  return (
    <div className="space-y-8">
      <section aria-labelledby="rules-eos-pack">
        <PartHeading id="rules-eos-pack" title={t('End-of-service settings')}
          description={t('Gratuity rules for {country}. Created from the country pack only if they do not exist yet; existing settings are never changed.', { country: countryName })}
          aside={<SaveModePill mode={existing ? 'update' : 'create'} />} />
        <div className="rounded-xl border border-slate-200 px-4 py-3 dark:border-white/10" data-testid="pack-eos">
          <PlanLines preview={preview} previewState={previewState} area="gccSetting" created={msg('Will be created from the country pack')} reused={msg('Already set — kept as they are')} />
        </div>
      </section>

      <section aria-labelledby="rules-eos-rules" data-testid="country-rules-editor">
        <PartHeading id="rules-eos-rules" title={t('Country rules for {country}', { country: countryName })}
          description={t('End-of-service rates, WPS and document alerts. They apply to every company registered in {country}.', { country: countryName })} />
        {canEditCountryRules
          ? <SetupEditorState.Provider value={report}><EmbeddedCountryRules countryCode={body.company.countryCode || undefined} onSaved={onSaved} /></SetupEditorState.Provider>
          : <Notice tone="info">{t('Country rules are shared by every company, so only a group administrator can change them. The country pack above is still applied.')}</Notice>}
      </section>
    </div>
  );
}
