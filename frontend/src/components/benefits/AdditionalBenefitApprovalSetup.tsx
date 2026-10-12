'use client';

import { useEffect, useState } from 'react';
import { Settings2 } from 'lucide-react';
import client from '@/src/api/client';
import { benefitsErrorMessage } from '@/src/api/benefits';
import { requirePage } from '@/src/lib/listResponse';
import { useAuth } from '@/src/contexts/AuthContext';
import { useLocale } from '@/src/contexts/LocaleContext';
import { Modal } from '@/src/components/Modal';
import { FormError, INPUT, LABEL, PRIMARY, SECONDARY } from './benefitUi';

type Step = { stepOrder: number; stepName: string; approverRole: string; approverType: string; isFinalStep: boolean; escalationAfterHours?: number | null };
type Workflow = { id: string; code: string; name: string; isActive: boolean; isDefault: boolean; departmentId?: string | null; gradeId?: string | null; steps: Step[] };

/** A focused editor for the tenant's fallback route. Scoped routes remain authoritative. */
export function AdditionalBenefitApprovalSetup({ entityName = 'BenefitAdditionalGrant' }: { entityName?: 'BenefitAdditionalGrant' | 'BenefitClaim' }) {
  const isClaim = entityName === 'BenefitClaim';
  const anchor = isClaim ? 'benefit-claim-approval' : 'additional-benefit-approval';
  const heading = isClaim ? 'Benefit claim approvals' : 'Additional benefit approvals';
  const { hasPermission, hasRole } = useAuth();
  const { t } = useLocale();
  const allowed = hasPermission('approvals.manage') && (hasRole('Admin') || hasRole('HR Manager'));
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [existing, setExisting] = useState<Workflow | null>(null);
  const [editable, setEditable] = useState(false);
  const [roles, setRoles] = useState<string[]>(['HR Manager']);
  const close = () => {
    if (saving) return;
    setOpen(false);
    if (window.location.hash === `#${anchor}`) {
      window.history.replaceState(window.history.state, '', window.location.pathname + window.location.search);
    }
  };

  useEffect(() => {
    const onHash = () => { if (allowed && window.location.hash === `#${anchor}`) setOpen(true); };
    onHash();
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  }, [allowed, anchor]);

  useEffect(() => {
    if (!open) return;
    let live = true;
    setLoading(true); setError(null); setSaved(false); setEditable(false);
    async function load() {
      const routes: Workflow[] = [];
      let page = 1;
      let total = 0;
      do {
        const response = await client.get('/api/approval-workflows', { params: { entityName, page, pageSize: 100 } });
        const data = requirePage<{ items: Workflow[]; total: number; page: number; pageSize: number }>(response.data, 'benefit approval routes');
        routes.push(...data.items); total = data.total; page++;
        if (data.items.length === 0) break;
      } while (routes.length < total);
      if (!live) return;
      const defaults = routes.filter(route => !route.departmentId && !route.gradeId);
      if (defaults.length > 1) {
        setError(t('Multiple fallback approval routes exist. An administrator must resolve them before editing here.'));
        return;
      }
      const route = defaults[0] ?? null;
      setExisting(route);
      if (route && route.steps.some(step => step.approverType !== 'Role')) {
        setError(t('This route has individual or reporting-line approvers and cannot be edited here.'));
        return;
      }
      setRoles(route ? [...route.steps].sort((a, b) => a.stepOrder - b.stepOrder).map(step => step.approverRole) : ['HR Manager']);
      setEditable(true);
    }
    void load().catch(err => { if (live) setError(benefitsErrorMessage(err, t('Could not load the approval route.'))); })
      .finally(() => { if (live) setLoading(false); });
    return () => { live = false; };
  }, [open, t, entityName]);

  const save = async () => {
    setSaving(true); setError(null);
    try {
      const payload = {
        code: existing?.code ?? (isClaim ? 'BENEFIT_CLAIM_DEFAULT' : 'BENEFIT_ADDITIONAL_DEFAULT'), name: existing?.name ?? (isClaim ? 'Benefit claim approval' : 'Additional benefit approval'),
        entityName, isActive: true, isDefault: true, departmentId: null, gradeId: null,
        steps: roles.map((role, index) => ({ stepOrder: index + 1, stepName: role.trim(), approverRole: role.trim(), approverType: 'Role', isFinalStep: index === roles.length - 1, escalationAfterHours: existing?.steps.find(step => step.stepOrder === index + 1)?.escalationAfterHours ?? null })),
      };
      const response = existing
        ? await client.put<Workflow>(`/api/approval-workflows/${existing.id}`, payload)
        : await client.post<Workflow>('/api/approval-workflows', payload);
      setExisting(response.data); setSaved(true);
    } catch (err) { setError(benefitsErrorMessage(err, t('Could not save the approval route.'))); }
    finally { setSaving(false); }
  };

  if (!allowed) return null;
  return <>
    <button id={anchor} type="button" className={SECONDARY} onClick={() => setOpen(true)}>
      <Settings2 className="h-4 w-4" />{t(heading)}
    </button>
    <Modal isOpen={open} title={t(heading)} onClose={close} size="lg"
      footer={<><button type="button" className={SECONDARY} disabled={saving} onClick={close}>{t('Close')}</button>
        <button type="button" className={PRIMARY} disabled={loading || saving || !editable || roles.some(role => !role.trim() || role.trim().toLowerCase() === 'any')} onClick={() => void save()}>{saving ? t('Saving…') : t('Save approval route')}</button></>}>
      <div className="space-y-4">
        <p className="text-sm text-slate-600 dark:text-slate-300">{t(isClaim ? 'Set the default approval sequence for benefit claims. Department and grade-specific routes take priority.' : 'Set the default approval sequence for additional benefits. Department and grade-specific routes take priority.')}</p>
        <p className="rounded-lg bg-blue-50 p-3 text-sm text-blue-800 dark:bg-blue-500/10 dark:text-blue-200">{t('Each approver must hold the named role and employee approval permission. The requester, beneficiary and earlier approvers cannot approve the next step.')}</p>
        <FormError message={error} />
        {saved && <p role="status" className="text-sm text-emerald-700 dark:text-emerald-300">{t('Approval route saved.')}</p>}
        {loading ? <p role="status" className="text-sm">{t('Loading approval route…')}</p> : editable && <div className="space-y-3">
          {roles.map((role, index) => <div key={index} className="flex items-end gap-2">
            <label className={`${LABEL} flex-1`}>{t('Approver role for step {step}', { step: index + 1 })}
              <input className={INPUT} value={role} maxLength={80} disabled={saving} onChange={event => { setSaved(false); setRoles(values => values.map((value, i) => i === index ? event.target.value : value)); }} />
            </label>
            {roles.length > 1 && <button type="button" className={SECONDARY} disabled={saving} aria-label={t('Remove approval step {step}', { step: index + 1 })} onClick={() => { setSaved(false); setRoles(values => values.filter((_, i) => i !== index)); }}>{t('Remove')}</button>}
          </div>)}
          {roles.length < 5 && <button type="button" className={SECONDARY} disabled={saving} onClick={() => { setSaved(false); setRoles(values => [...values, '']); }}>{t('Add approval step')}</button>}
          <p className="text-xs text-slate-500">{t('Changes are blocked while this route has pending requests. Complete or withdraw those requests first.')}</p>
        </div>}
      </div>
    </Modal>
  </>;
}
