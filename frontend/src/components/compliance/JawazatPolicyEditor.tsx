'use client';

import { useState } from 'react';
import { complianceProfilesApi, type CompanyComplianceProfile } from '../../api/governance';
import { jawazatError, type JawazatPolicy } from '../../api/jawazat';
import { useAuth } from '../../contexts/AuthContext';

function readPolicy(json?: string): { value: Partial<JawazatPolicy>; error: string } {
  if (!json?.trim()) return { value: {}, error: '' };
  try {
    const value = JSON.parse(json);
    if (!value || Array.isArray(value) || value.schemaVersion !== 1) throw new Error('Unsupported policy');
    return { value, error: '' };
  } catch { return { value: {}, error: 'The stored Jawazat policy cannot be edited safely. Reconcile its format before replacing it.' }; }
}

/** Patch only Jawazat settings so concurrent compliance edits are preserved. */
export function JawazatPolicyEditor({ profile, onSaved }: { profile: CompanyComplianceProfile; onSaved: (profile: CompanyComplianceProfile) => void }) {
  const { user } = useAuth();
  const [original] = useState(() => readPolicy(profile.jawazatPolicyJson));
  const [employerEnabled, setEmployerEnabled] = useState(original.value.employerAssistedEnabled ?? false);
  const [notificationEnabled, setNotificationEnabled] = useState(original.value.workerNotificationEnabled ?? false);
  const [maximumTripDays, setMaximumTripDays] = useState(original.value.maximumTripDays?.toString() ?? '');
  const [passportDays, setPassportDays] = useState(original.value.minimumPassportValidityDays?.toString() ?? '');
  const [ruleVersion, setRuleVersion] = useState(original.value.ruleVersion ?? '');
  const [reviewed, setReviewed] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [saved, setSaved] = useState(false);
  const canEdit = !!user?.roles.some(role => role === 'Admin' || role === 'Compliance Officer');
  const lastReview = readPolicy(profile.jawazatPolicyJson).value;
  const integerRange = (value: string, minimum: number, maximum: number) => value.trim() !== '' && Number.isInteger(Number(value)) && Number(value) >= minimum && Number(value) <= maximum;
  const valid = integerRange(maximumTripDays, 1, 365) && integerRange(passportDays, 90, 3650) && !!ruleVersion.trim() && reviewed && !!user?.id && !original.error;
  const changed = () => { setReviewed(false); setSaved(false); setError(''); };
  const save = async () => {
    if (!valid || !canEdit || saving) return;
    setSaving(true); setError(''); setSaved(false);
    const policy: JawazatPolicy = { schemaVersion: 1, employerAssistedEnabled: employerEnabled, workerNotificationEnabled: notificationEnabled,
      maximumTripDays: Number(maximumTripDays), minimumPassportValidityDays: Number(passportDays), ruleVersion: ruleVersion.trim(),
      reviewedBy: user!.id, reviewedAtUtc: new Date().toISOString() };
    try {
      const updated = await complianceProfilesApi.updateJawazatPolicy(profile.id, JSON.stringify(policy));
      onSaved(updated); setSaved(true); setReviewed(false);
    } catch (failure) { setError(jawazatError(failure)); }
    finally { setSaving(false); }
  };
  const fieldClass = 'mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-white/10 dark:bg-slate-900';
  return <section aria-label="Jawazat company policy" className="rounded-2xl border border-slate-200 bg-white p-5 dark:border-white/10 dark:bg-white/[0.03]">
    <h2 className="font-bold text-slate-800 dark:text-slate-100">Jawazat company policy</h2>
    <p className="mt-1 text-sm text-slate-500">Configure employer assistance and company review rules for this profile. These settings do not certify government eligibility.</p>
    {profile.countryCode !== 'SA' && profile.countryCode !== 'SAU' && <p className="mt-2 text-sm text-amber-700">Jawazat services apply to Saudi Arabia. Verify the profile jurisdiction before enabling assistance.</p>}
    {!canEdit && <p className="mt-3 text-sm text-slate-500">An administrator or Compliance Officer can update this policy.</p>}
    {original.error && <p role="alert" className="mt-3 text-sm text-rose-600">{original.error}</p>}
    {error && <p role="alert" className="mt-3 text-sm text-rose-600">{error}</p>}
    {saved && <p role="status" className="mt-3 text-sm text-emerald-700">Jawazat policy saved with your review record.</p>}
    <fieldset disabled={!canEdit || saving || !!original.error} className="mt-4 space-y-4 text-sm text-slate-700 dark:text-slate-200">
      <label className="flex items-center gap-2"><input type="checkbox" checked={employerEnabled} onChange={event => { changed(); setEmployerEnabled(event.target.checked); }} />Enable employer-assisted requests</label>
      <label className="flex items-center gap-2"><input type="checkbox" checked={notificationEnabled} onChange={event => { changed(); setNotificationEnabled(event.target.checked); }} />Enable worker notification tracking in company policy</label>
      <p className="text-xs text-slate-500">Employees can record their own notification regardless of this company preference. Notification is not an employer-consent workflow.</p>
      <div className="grid gap-4 sm:grid-cols-3">
        <label className="block font-medium">Maximum trip days<input type="number" min={1} max={365} step={1} className={fieldClass} value={maximumTripDays} onChange={event => { changed(); setMaximumTripDays(event.target.value); }} /></label>
        <label className="block font-medium">Minimum passport validity days<input type="number" min={90} max={3650} step={1} className={fieldClass} value={passportDays} onChange={event => { changed(); setPassportDays(event.target.value); }} /></label>
        <label className="block font-medium">Policy rule version<input type="text" maxLength={120} className={fieldClass} value={ruleVersion} onChange={event => { changed(); setRuleVersion(event.target.value); }} placeholder="Your reviewed rule version" /></label>
      </div>
      <p className="text-xs text-slate-500">Choose a company trip limit of 1–365 days and a passport-validity check of 90–3,650 days. Review these settings against the rules applicable to this company.</p>
      <p className="text-xs text-slate-500">Last review: {lastReview.reviewedBy ? `${lastReview.reviewedBy} · ${lastReview.reviewedAtUtc ? new Date(lastReview.reviewedAtUtc).toLocaleString() : 'time not recorded'}` : 'No review recorded'}.</p>
      <label className="flex items-start gap-2"><input type="checkbox" checked={reviewed} onChange={event => setReviewed(event.target.checked)} className="mt-0.5" />I have reviewed this company policy and its rule version.</label>
      <p className="text-xs text-slate-500">Saving records {user?.fullName || 'the signed-in user'} as reviewer with the current review time.</p>
      {canEdit && <button type="button" disabled={!valid || saving} onClick={save} className="rounded-lg bg-sapphire px-4 py-2 font-semibold text-white disabled:opacity-40">{saving ? 'Saving policy…' : 'Save Jawazat policy'}</button>}
    </fieldset>
  </section>;
}
