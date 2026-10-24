'use client';

import { useEffect, useState } from 'react';
import { useT } from '../../hooks/useT';
import { usersApi, type UserListItem } from '../../api/identity';
import type { CompanyAdminMode, CompanySetupAdmin } from '../../api/companySetup';
import { eligibleCompanySuperAdmins } from '../../lib/companySetupWizard';
import { Notice, WizardField, useWizardForm } from './wizardForm';

/**
 * Step 6: the Company Super Admin. Group Super Admin (Admin, group scope) → Company Super Admin
 * (Admin, this company only). Shown only to a group-scope Admin who may grant the Admin role; the
 * server applies the same role ceiling as User Management.
 */
export function AdminStep() {
  const t = useT();
  const { body, update, readOnly, context } = useWizardForm();
  const admin = body.companyAdmin;
  // Existing mode: this step only adds an administrator (A3: none is removed here). A company super admin running it
  // for their own company may add another company-scoped admin, never group access (A5).
  const existing = !!body.existingCompanyId;
  const companyScoped = existing && context?.isGroupScope === false;
  const companyName = body.company.legalNameEn;
  const domain = (body.company.emailDomain ?? '').trim().toLowerCase();
  const set = (patch: Partial<CompanySetupAdmin>) => update(b => ({ ...b, companyAdmin: { ...b.companyAdmin, ...patch } }));
  const [users, setUsers] = useState<UserListItem[] | null>(null);
  const [usersError, setUsersError] = useState(false);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (admin.mode !== 'existing' || users) return;
    let active = true;
    setUsersError(false);
    usersApi.listAll({ status: 'Active' })
      .then(rows => { if (active) setUsers(eligibleCompanySuperAdmins(rows)); })
      .catch(() => { if (active) setUsersError(true); });
    return () => { active = false; };
  }, [admin.mode, users, attempt]);

  const option = (mode: CompanyAdminMode, title: string, description: string) => (
    <label className={`flex cursor-pointer gap-3 rounded-xl border p-4 ${admin.mode === mode ? 'border-sapphire bg-sapphire/[0.04] dark:border-cyanAccent' : 'border-slate-200 dark:border-white/10'}`}>
      <input type="radio" name="ccw-admin-mode" className="mt-1" checked={admin.mode === mode} disabled={readOnly} onChange={() => set({ mode })} />
      <span className="min-w-0">
        <span className="block text-sm font-semibold text-slate-900 dark:text-white">{title}</span>
        <span className="mt-0.5 block text-xs leading-5 text-slate-600 dark:text-slate-400">{description}</span>
      </span>
    </label>
  );

  return (
    <div className="space-y-5">
      <Notice tone="info">
        <p className="font-semibold">{t('Group super admin → company super admin')}</p>
        <p className="mt-1">{t('A company super admin has full administrator rights for this company only. They cannot see sister companies or give anyone group access.')}</p>
        {companyScoped && <p className="mt-1" data-testid="company-scoped-admin-note"><bdi>{t('You can add another administrator for {company} only. Group access is given only by a group super admin.', { company: companyName })}</bdi></p>}
      </Notice>
      <fieldset className="space-y-3">
        <legend className="text-base font-semibold text-slate-900 dark:text-white">{existing ? t('Add a company super admin?') : t('Who will run this company?')}</legend>
        <div className="grid gap-3 lg:grid-cols-3">
          {option('invite', t('Invite a new company super admin'), t('They receive a welcome code to sign in with their own company email. You never set their password.'))}
          {option('existing', existing ? t('Make an existing user a company super admin') : t('Make an existing user the company super admin'), t('Adds administrator rights for this company to someone who already signs in. Their other access stays the same.'))}
          {existing
            ? option('groupOnly', t('Keep the current administrators'), t('No one is added and nobody’s access changes. To remove an administrator, use User Management.'))
            : option('groupOnly', t('Group super admins will manage this company'), t('No company administrator for now. The checklist will recommend adding one.'))}
        </div>
      </fieldset>

      {admin.mode === 'invite' && (
        <div className="grid gap-x-5 gap-y-4 sm:grid-cols-2">
          <WizardField field="companyAdmin.fullNameEn" label={t('Full name (English)')} required>
            {control => <input {...control} className="input w-full" autoComplete="off" readOnly={readOnly} value={admin.fullNameEn ?? ''} onChange={e => set({ fullNameEn: e.target.value })} />}
          </WizardField>
          <WizardField field="companyAdmin.fullNameAr" label={t('Full name (Arabic)')}>
            {control => <input {...control} className="input w-full" dir="rtl" lang="ar" autoComplete="off" readOnly={readOnly} value={admin.fullNameAr ?? ''} onChange={e => set({ fullNameAr: e.target.value })} />}
          </WizardField>
          <WizardField field="companyAdmin.email" label={t('Official work email')} required className="sm:col-span-2"
            help={domain ? t('Must be an address on {domain}. KynexOne never creates mailboxes.', { domain }) : t('Their existing company email. KynexOne never creates mailboxes.')}>
            {control => <input {...control} type="email" dir="ltr" className="input w-full" autoComplete="off" readOnly={readOnly} value={admin.email ?? ''} onChange={e => set({ email: e.target.value.trim() })} />}
          </WizardField>
        </div>
      )}

      {admin.mode === 'existing' && (
        <div className="max-w-xl">
          <WizardField field="companyAdmin.existingUserId" label={t('User')} required
            help={t('Only users who already hold the Admin role are listed, because a role applies to every company a person can access.')}>
            {control => <select {...control} className="select w-full" disabled={readOnly || !users} value={admin.existingUserId ?? ''} onChange={e => set({ existingUserId: e.target.value || undefined })}>
              <option value="">{users ? t('Choose a user…') : t('Loading users…')}</option>
              {(users ?? []).map(u => <option key={u.id} value={u.id}>{`${u.fullName} · ${u.email}`}</option>)}
            </select>}
          </WizardField>
          {users && users.length === 0 && <div className="mt-2" data-testid="no-eligible-admins"><Notice tone="info">{t('No user in this workspace holds the Admin role yet. Invite a new company super admin instead.')}</Notice></div>}
          {usersError && <div className="mt-2"><Notice tone="error" role="alert">{t('The user list could not be loaded. Check your connection and try again, or invite a new administrator instead.')}<button type="button" className="btn-secondary ms-2" onClick={() => setAttempt(a => a + 1)}>{t('Try again')}</button></Notice></div>}
        </div>
      )}

      {admin.mode === 'groupOnly' && !existing && <Notice tone="warning">{t('Group super admins will manage this company. You can add a company super admin later from User Management; the checklist after creation links there.')}</Notice>}
    </div>
  );
}
