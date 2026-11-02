'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { BarChart3, ClipboardList, ListChecks, Plus, Rocket, ShieldCheck } from 'lucide-react';
import { useLocale } from '../contexts/LocaleContext';
import { useAuth } from '../contexts/AuthContext';
import { useFeatureFlags } from '../contexts/FeatureFlagContext';
import { useFormat } from '../hooks/useFormat';
import { describeApiError } from '../lib/apiError';
import { msg } from '../i18n/translations';
import { MODE_LABELS, SURVEY_STATUS_LABELS, pick } from '../lib/engagementDisplay';
import {
  engagementApi, type EngagementOptions, type Settings, type Survey, type Template,
} from '../api/engagement';
import { RovingTabList, TabPanel } from '../components/ui/RovingTabs';
import { Modal } from '../components/Modal';
import { StatusChip } from '../components/StatusChip';
import { ResultsPanel, type ActionSeed } from '../components/engagement/ResultsPanel';
import { ActionsPanel } from '../components/engagement/ActionsPanel';
import { SurveyBuilder } from '../components/engagement/SurveyBuilder';
import { TemplateBuilder } from '../components/engagement/TemplateBuilder';
import { LikertStyles } from '../components/engagement/LikertBar';
import { ModuleSettingsLink } from '../components/setup/ModuleSettingsLink';

export const ENGAGEMENT_FLAG = 'engagement';

type Tab = 'surveys' | 'results' | 'actions' | 'templates';

const TEMPLATE_STATUS_LABELS = { Draft: msg('Draft'), Published: msg('Published'), Retired: msg('Retired') } as const;

type Pending = { kind: 'launch' | 'close' | 'cancel' | 'delete'; survey: Survey } | null;

/**
 * Engagement surveys — HR and managers. Opt-in per tenant (the `engagement` flag); the API refuses the same tenant,
 * so the sentence below is the courtesy layer, not the gate. Readers (engagement.read) see Results and Actions; builders
 * (engagement.manage) also see Surveys and Templates; the privacy settings are in Setup.
 */
export function EngagementPage() {
  const { t } = useLocale();
  const { isLoading, isFeatureEnabled } = useFeatureFlags();
  if (isLoading) return <div className="h-24 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />;
  if (!isFeatureEnabled(ENGAGEMENT_FLAG)) {
    return (
      <div className="rounded-2xl border border-slate-200 bg-white p-6 text-sm text-slate-600 dark:border-white/10 dark:bg-white/[0.03] dark:text-slate-300">
        {t('This feature is not enabled for your account yet.')}
      </div>
    );
  }
  return <Engagement />;
}

function Engagement() {
  const { t, locale } = useLocale();
  const { hasPermission } = useAuth();
  const f = useFormat();
  const canManage = hasPermission('engagement.manage');
  const canRead = hasPermission('engagement.read');
  const tabs = useMemo(() => {
    const list: { id: Tab; label: string; icon: React.ElementType<{ className?: string }> }[] = [];
    if (canManage) list.push({ id: 'surveys', label: t('Surveys'), icon: ClipboardList });
    if (canRead) list.push({ id: 'results', label: t('Results'), icon: BarChart3 });
    if (canRead || hasPermission('engagement.actions.manage')) list.push({ id: 'actions', label: t('Actions'), icon: ListChecks });
    if (canManage) list.push({ id: 'templates', label: t('Templates'), icon: ClipboardList });
    return list;
  }, [canManage, canRead, hasPermission, t]);
  const [tab, setTab] = useState<Tab>(tabs[0]?.id ?? 'results');
  const [surveys, setSurveys] = useState<Survey[]>([]);
  const [templates, setTemplates] = useState<Template[]>([]);
  const [options, setOptions] = useState<EngagementOptions | null>(null);
  const [settings, setSettings] = useState<Settings | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [surveyEditor, setSurveyEditor] = useState<{ survey: Survey | null } | null>(null);
  const [templateEditor, setTemplateEditor] = useState<{ template: Template | null } | null>(null);
  const [pending, setPending] = useState<Pending>(null);
  const [busy, setBusy] = useState(false);
  const [resultsFor, setResultsFor] = useState<string | null>(null);
  const [actionSeed, setActionSeed] = useState<ActionSeed | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [s, o, st, tp] = await Promise.all([
        engagementApi.surveys(),
        engagementApi.options(),
        engagementApi.settings(),
        canManage ? engagementApi.templates() : Promise.resolve([] as Template[]),
      ]);
      setSurveys(s);
      setOptions(o);
      setSettings(st);
      setTemplates(tp);
    } catch (e) {
      setError(describeApiError(e, t));
    } finally {
      setLoading(false);
    }
  }, [canManage, t]);

  useEffect(() => { void load(); }, [load]);

  const runPending = async () => {
    if (!pending) return;
    setBusy(true);
    setError(null);
    try {
      const { kind, survey } = pending;
      if (kind === 'launch') {
        const s = await engagementApi.launch(survey.id, survey.version);
        setNotice(s.status === 'Open'
          ? t('{title} is open. {n} people were invited.', { title: pick(locale, s.titleEn, s.titleAr), n: s.invitationCount })
          : t('{title} is scheduled and opens on {date}.', { title: pick(locale, s.titleEn, s.titleAr), date: f.dateTime(s.opensAtUtc) }));
      } else if (kind === 'close') {
        await engagementApi.close(survey.id, survey.version);
        setNotice(t('The survey is closed. Unsubmitted drafts were discarded.'));
      } else if (kind === 'cancel') {
        await engagementApi.cancel(survey.id, survey.version);
        setNotice(t('The survey is cancelled.'));
      } else {
        await engagementApi.remove(survey.id);
        setNotice(t('The survey and its responses are deleted. Follow-up actions are kept.'));
      }
      setPending(null);
      await load();
    } catch (e) {
      setError(describeApiError(e, t));
      setPending(null);
    } finally {
      setBusy(false);
    }
  };

  const publish = async (tpl: Template) => {
    try {
      await engagementApi.publishTemplate(tpl.id, tpl.version);
      setNotice(t('Template published. It can no longer change; start a new version to edit it.'));
      await load();
    } catch (e) {
      setError(describeApiError(e, t));
    }
  };

  const newVersion = async (tpl: Template) => {
    try {
      const draft = await engagementApi.newTemplateVersion(tpl.id);
      await load();
      setTemplateEditor({ template: draft });
    } catch (e) {
      setError(describeApiError(e, t));
    }
  };

  const createAction = (seed: ActionSeed) => {
    setActionSeed(seed);
    setTab('actions');
  };

  const pendingCopy = pending && {
    launch: {
      title: t('Launch this survey?'),
      body: t('Everyone in the audience is invited when it opens, and the questions and privacy mode can no longer change.'),
      confirm: t('Launch survey'),
    },
    close: { title: t('Close this survey now?'), body: t('Nobody can respond after it closes, and unsubmitted drafts are discarded.'), confirm: t('Close survey') },
    cancel: { title: t('Cancel this survey?'), body: t('It will not open. Nobody has been asked to respond yet.'), confirm: t('Cancel survey') },
    delete: {
      title: t('Delete this survey?'),
      body: t('Its invitations and every response are deleted permanently. Follow-up actions are kept. This is recorded in the audit log.'),
      confirm: t('Delete survey'),
    },
  }[pending.kind];

  return (
    <div className="space-y-5" data-testid="engagement-page">
      <LikertStyles />
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold text-slate-950 dark:text-white">{t('Engagement surveys')}</h1>
          <p className="mt-1 max-w-3xl text-sm text-slate-600 dark:text-slate-300">
            {t('Ask your people how work is going, see the results without exposing anyone, and follow up with actions.')}
          </p>
        </div>
        {canManage && tab === 'surveys' && (
          <button type="button" className="btn-primary" onClick={() => setSurveyEditor({ survey: null })} data-testid="survey-new">
            <Plus className="h-4 w-4" aria-hidden="true" />{t('New survey')}
          </button>
        )}
        {canManage && tab === 'templates' && (
          <button type="button" className="btn-primary" onClick={() => setTemplateEditor({ template: null })} data-testid="template-new">
            <Plus className="h-4 w-4" aria-hidden="true" />{t('New template')}
          </button>
        )}
        <ModuleSettingsLink sections={['engagementPrivacy']} label={msg('Engagement settings')} />
      </header>

      {settings && (
        <p className="flex items-start gap-2 rounded-xl bg-emerald-50 px-4 py-3 text-sm text-emerald-900 dark:bg-emerald-900/20 dark:text-emerald-100">
          <ShieldCheck className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
          {t('Results for groups smaller than {k} people are never shown, here, in exports or in scheduled reports. Anonymous answers are stored without names.', { k: settings.minCohortSize })}
        </p>
      )}

      {error && <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-800/60 dark:bg-rose-900/20 dark:text-rose-300">{error}</p>}
      {notice && (
        <p role="status" className="flex items-start justify-between gap-3 rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-800 dark:border-emerald-800/60 dark:bg-emerald-900/20 dark:text-emerald-200">
          {notice}
          <button type="button" className="text-xs font-semibold underline" onClick={() => setNotice(null)}>{t('Dismiss')}</button>
        </p>
      )}

      {tabs.length > 0 && <RovingTabList items={tabs} activeId={tab} onChange={setTab} idPrefix="engagement" label={t('Engagement sections')} />}

      {loading ? (
        <div className="h-40 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />
      ) : (
        <>
          {tab === 'surveys' && canManage && (
            <TabPanel idPrefix="engagement" tabId="surveys">
              {surveys.length === 0 ? (
                <div className="surface rounded-xl p-8 text-center text-sm text-slate-600 dark:text-slate-300">
                  <p className="font-semibold text-slate-900 dark:text-white">{t('No surveys yet')}</p>
                  <p className="mt-1">{t('Publish a template, then create a survey from it.')}</p>
                </div>
              ) : (
                <ul className="space-y-2" data-testid="survey-list">
                  {surveys.map((s) => (
                    <li key={s.id} className="surface rounded-xl p-4" data-testid={`survey-${s.id}`}>
                      <div className="flex flex-wrap items-start justify-between gap-3">
                        <div className="min-w-0">
                          <p className="font-semibold text-slate-900 dark:text-white">{pick(locale, s.titleEn, s.titleAr)}</p>
                          <p className="mt-0.5 text-xs text-slate-500">
                            {t('Opens {opens} · closes {closes} · cycle {n}', { opens: f.dateTime(s.opensAtUtc), closes: f.dateTime(s.closesAtUtc), n: s.cycleNumber })}
                          </p>
                          {s.status !== 'Draft' && s.status !== 'Cancelled' && (
                            <p className="mt-0.5 text-xs text-slate-600 dark:text-slate-300">
                              {s.submitted == null
                                ? t('{invited} invited · the response count is shown once at least {k} people are invited', { invited: s.invitationCount, k: s.minCohortSize })
                                : t('{submitted} of {invited} responded · smallest group shown {k}', { submitted: s.submitted, invited: s.invitationCount, k: s.minCohortSize })}
                            </p>
                          )}
                        </div>
                        <div className="flex flex-wrap items-center gap-2">
                          <StatusChip label={t(MODE_LABELS[s.mode])} tone={s.mode === 'Identified' ? 'amber' : 'emerald'} dot />
                          <StatusChip label={t(SURVEY_STATUS_LABELS[s.status])} tone={s.status === 'Open' ? 'blue' : s.status === 'Scheduled' ? 'cyan' : 'slate'} />
                        </div>
                      </div>
                      <div className="mt-3 flex flex-wrap gap-2">
                        {s.status === 'Draft' && (
                          <>
                            <button type="button" className="btn-primary" onClick={() => setPending({ kind: 'launch', survey: s })} data-testid={`survey-launch-${s.id}`}>
                              <Rocket className="h-4 w-4" aria-hidden="true" />{t('Launch')}
                            </button>
                            <button type="button" className="btn-secondary" onClick={() => setSurveyEditor({ survey: s })}>{t('Edit')}</button>
                          </>
                        )}
                        {(s.status === 'Open' || s.status === 'Closed') && (
                          <button type="button" className="btn-secondary" onClick={() => { setResultsFor(s.id); setTab('results'); }} data-testid={`survey-results-${s.id}`}>
                            {t('View results')}
                          </button>
                        )}
                        {s.status === 'Open' && (
                          <button type="button" className="btn-secondary" onClick={() => setPending({ kind: 'close', survey: s })} data-testid={`survey-close-${s.id}`}>{t('Close now')}</button>
                        )}
                        {(s.status === 'Draft' || s.status === 'Scheduled') && (
                          <button type="button" className="btn-secondary" onClick={() => setPending({ kind: 'cancel', survey: s })}>{t('Cancel survey')}</button>
                        )}
                        {(s.status === 'Draft' || s.status === 'Cancelled' || s.status === 'Closed') && (
                          <button type="button" className="btn-secondary text-rose-700" onClick={() => setPending({ kind: 'delete', survey: s })}>{t('Delete')}</button>
                        )}
                      </div>
                    </li>
                  ))}
                </ul>
              )}
            </TabPanel>
          )}

          {tab === 'results' && canRead && (
            <TabPanel idPrefix="engagement" tabId="results">
              <ResultsPanel surveys={surveys} options={options} initialSurveyId={resultsFor} onCreateAction={createAction} />
            </TabPanel>
          )}

          {tab === 'actions' && (
            <TabPanel idPrefix="engagement" tabId="actions">
              <ActionsPanel surveys={surveys} seed={actionSeed} onSeedConsumed={() => setActionSeed(null)} />
            </TabPanel>
          )}

          {tab === 'templates' && canManage && (
            <TabPanel idPrefix="engagement" tabId="templates">
              {templates.length === 0 ? (
                <div className="surface rounded-xl p-8 text-center text-sm text-slate-600 dark:text-slate-300">
                  <p className="font-semibold text-slate-900 dark:text-white">{t('No templates yet')}</p>
                  <p className="mt-1">{t('Start from the suggested questions and adjust them.')}</p>
                </div>
              ) : (
                <ul className="space-y-2" data-testid="template-list">
                  {templates.map((tpl) => (
                    <li key={tpl.id} className="surface flex flex-wrap items-center justify-between gap-3 rounded-xl p-4">
                      <div className="min-w-0">
                        <p className="font-semibold text-slate-900 dark:text-white">{pick(locale, tpl.nameEn, tpl.nameAr)}</p>
                        <p className="mt-0.5 text-xs text-slate-500">
                          {t('Version {n} · {questions} questions', { n: tpl.versionNumber, questions: tpl.content.questions.length })}
                        </p>
                      </div>
                      <div className="flex flex-wrap items-center gap-2">
                        <StatusChip label={t(TEMPLATE_STATUS_LABELS[tpl.status])} tone={tpl.status === 'Published' ? 'emerald' : 'amber'} />
                        {tpl.status === 'Draft' ? (
                          <>
                            <button type="button" className="btn-secondary" onClick={() => setTemplateEditor({ template: tpl })}>{t('Edit')}</button>
                            <button type="button" className="btn-primary" onClick={() => void publish(tpl)} data-testid={`template-publish-${tpl.id}`}>{t('Publish')}</button>
                          </>
                        ) : (
                          <button type="button" className="btn-secondary" onClick={() => void newVersion(tpl)}>{t('Start new version')}</button>
                        )}
                      </div>
                    </li>
                  ))}
                </ul>
              )}
            </TabPanel>
          )}
        </>
      )}

      <SurveyBuilder
        open={!!surveyEditor}
        survey={surveyEditor?.survey ?? null}
        templates={templates}
        options={options}
        defaultK={settings?.minCohortSize ?? 5}
        floor={settings?.minCohortFloor ?? 3}
        onClose={() => setSurveyEditor(null)}
        onSaved={() => { setSurveyEditor(null); setNotice(t('Survey draft saved. Launch it when it is ready.')); void load(); }}
      />
      <TemplateBuilder
        open={!!templateEditor}
        template={templateEditor?.template ?? null}
        options={options}
        onClose={() => setTemplateEditor(null)}
        onSaved={() => { setTemplateEditor(null); setNotice(t('Template draft saved. Publish it to use it in a survey.')); void load(); }}
      />
      <Modal
        isOpen={!!pending}
        title={pendingCopy?.title ?? ''}
        onClose={() => setPending(null)}
        size="sm"
        footer={(
          <>
            <button type="button" className="btn-secondary" onClick={() => setPending(null)}>{t('Go back')}</button>
            <button type="button" className="btn-primary" onClick={() => void runPending()} disabled={busy} data-testid="confirm-pending">
              {busy ? t('Working…') : pendingCopy?.confirm}
            </button>
          </>
        )}
      >
        <p className="text-sm text-slate-700 dark:text-slate-200">{pendingCopy?.body}</p>
      </Modal>
    </div>
  );
}

/**
 * The survey privacy settings, loading their own values: the Setup section (Survey privacy) shows this, and the
 * module's header links to it. The survey screen reads the default again the next time it opens.
 */
export function EngagementPrivacyPanel() {
  const { t } = useLocale();
  const [settings, setSettings] = useState<Settings | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  useEffect(() => {
    let live = true;
    engagementApi.settings().then(value => { if (live) setSettings(value); }).catch(e => { if (live) setError(describeApiError(e, t)); });
    return () => { live = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  if (error) return <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-800/60 dark:bg-rose-900/20 dark:text-rose-300">{error}</p>;
  if (!settings) return <div className="h-40 animate-pulse rounded-xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />;
  return (
    <div className="space-y-3">
      {notice && <p role="status" className="rounded-lg border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-800 dark:border-emerald-800/60 dark:bg-emerald-900/20 dark:text-emerald-200">{notice}</p>}
      <SettingsPanel settings={settings} onSaved={value => { setSettings(value); setNotice(t('Privacy settings saved.')); }} />
    </div>
  );
}

function SettingsPanel({ settings, onSaved }: { settings: Settings; onSaved: (s: Settings) => void }) {
  const { t } = useLocale();
  const [k, setK] = useState(settings.minCohortSize);
  const [months, setMonths] = useState(settings.freeTextRetentionMonths);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      onSaved(await engagementApi.updateSettings(k, months));
    } catch (e) {
      setError(describeApiError(e, t));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="surface max-w-2xl space-y-4 rounded-xl p-5">
      {error && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:bg-rose-900/20 dark:text-rose-300">{error}</p>}
      <label className="flex flex-col gap-1 text-sm font-semibold text-slate-800 dark:text-slate-100">
        {t('Smallest group whose results are shown')}
        <input className="input max-w-[140px]" type="number" min={settings.minCohortFloor} max={100} value={k}
          onChange={(e) => setK(Number(e.target.value) || settings.minCohortFloor)} data-testid="settings-k" />
        <span className="text-xs font-normal text-slate-500">
          {t('Default for new surveys. It can never be set below {floor}. A survey may raise it. Changes are recorded in the audit log.', { floor: settings.minCohortFloor })}
        </span>
      </label>
      <label className="flex flex-col gap-1 text-sm font-semibold text-slate-800 dark:text-slate-100">
        {t('Keep comments for (months after a survey closes)')}
        <input className="input max-w-[140px]" type="number" min={1} max={120} value={months} onChange={(e) => setMonths(Number(e.target.value) || 1)} />
        <span className="text-xs font-normal text-slate-500">
          {t('After this, comment text is removed by the retention job. Scores and counts are kept.')}
        </span>
      </label>
      <button type="button" className="btn-primary" onClick={() => void save()} disabled={saving} data-testid="settings-save">
        {saving ? t('Saving…') : t('Save settings')}
      </button>
    </div>
  );
}
