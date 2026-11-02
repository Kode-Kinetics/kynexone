'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { AlertTriangle, RefreshCw, ShieldCheck } from 'lucide-react';
import { complianceProfilesApi, type CompanyComplianceProfile, type ComplianceReadiness } from '../api/governance';
import { notifyApiError } from '../api/client';
import { OrgRowActions, ShowTurnedOffToggle, useOrgRowRemoval } from '../components/setup/OrgRowActions';
import { msg } from '../i18n/translations';
import { useCompany } from '../contexts/CompanyContext';
import { useT } from '../hooks/useT';
import { JawazatPolicyEditor } from '../components/compliance/JawazatPolicyEditor';

/** Per-company compliance readiness. Configurable readiness tooling — NOT legal certification. Shown as the Setup section Compliance profiles, which carries the title. */
export function ComplianceProfilesPage() {
  const t = useT();
  const { companies, selectedCompanyId } = useCompany();
  const [companyId, setCompanyId] = useState<string | ''>('');
  const [readiness, setReadiness] = useState<ComplianceReadiness | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const loadRevision = useRef(0);
  const [profiles, setProfiles] = useState<CompanyComplianceProfile[]>([]);
  const [showOff, setShowOff] = useState(false);

  useEffect(() => {
    if (!companyId && (selectedCompanyId || companies.length > 0))
      setCompanyId(selectedCompanyId ?? companies[0]?.id ?? '');
  }, [companies, selectedCompanyId, companyId]);

  const load = useCallback(async (id: string) => {
    const revision = ++loadRevision.current;
    setLoading(true);
    setError(null);
    try {
      const value = await complianceProfilesApi.readiness(id);
      if (revision === loadRevision.current) setReadiness(value);
    } catch (e: unknown) {
      const status = (e as { response?: { status?: number } })?.response?.status;
      if (revision === loadRevision.current) {
        setError(status === 403 ? 'You do not have access to this company.' : 'Could not load compliance readiness.');
        setReadiness(null);
      }
    } finally {
      if (revision === loadRevision.current) setLoading(false);
    }
  }, []);

  const loadProfiles = useCallback(async (id: string) => {
    try { setProfiles(await complianceProfilesApi.list(id)); } catch (e) { notifyApiError(e, 'The compliance profiles could not be loaded.'); }
  }, []);
  useEffect(() => { if (companyId) { void load(companyId); void loadProfiles(companyId); } return () => { loadRevision.current++; }; }, [companyId, load, loadProfiles]);
  const removal = useOrgRowRemoval({ api: complianceProfilesApi.removal, noun: msg('compliance profile'), onDone: () => { if (companyId) { void load(companyId); void loadProfiles(companyId); } } });
  const isOff = (p: CompanyComplianceProfile) => p.status === 'Archived';
  const listed = profiles.filter(p => showOff || !isOff(p));

  const profile = readiness?.profile ?? null;
  const fields = readiness?.requiredFields ?? [];
  const ready = profile !== null && fields.every((f) => f.missingEmployeeCount === 0);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-xs text-slate-400">Per-legal-entity readiness</p>
        <label className="text-xs font-medium text-slate-500 dark:text-slate-400">
          Company{' '}
          <select
            value={companyId}
            onChange={(e) => { if (e.target.value !== companyId) setCompanyId(e.target.value); }}
            className="ms-1 rounded-lg border border-slate-200 bg-white px-2.5 py-1.5 text-xs font-semibold text-slate-700 dark:border-white/[0.08] dark:bg-white/[0.04] dark:text-slate-200"
          >
            {companies.map((c) => <option key={c.id} value={c.id}>{c.name} ({c.code})</option>)}
          </select>
        </label>
      </div>

      <div className="rounded-xl border border-blue-200 bg-blue-50 px-4 py-2.5 text-xs font-medium text-blue-800 dark:border-blue-500/20 dark:bg-blue-500/[0.06] dark:text-blue-300">
        Compliance profiles are configurable readiness tools — not legal certification. Country rule packs require legal validation before being treated as authoritative.
      </div>

      {loading ? (
        <div className="h-64 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />
      ) : error ? (
        <div className="flex flex-col items-center gap-3 rounded-2xl border border-amber-200 bg-amber-50 p-8 text-center dark:border-amber-500/20 dark:bg-amber-500/[0.06]">
          <AlertTriangle className="h-8 w-8 text-amber-500" />
          <p className="text-sm font-medium text-amber-800 dark:text-amber-300">{error}</p>
          <button type="button" onClick={() => companyId && void load(companyId)} className="flex items-center gap-1.5 rounded-lg bg-amber-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-700">
            <RefreshCw className="h-3.5 w-3.5" /> Retry
          </button>
        </div>
      ) : (
        <>
          <div className={`flex items-center gap-3 rounded-2xl border p-4 ${ready ? 'border-emerald-200 bg-emerald-50 dark:border-emerald-500/20 dark:bg-emerald-500/[0.06]' : 'border-rose-200 bg-rose-50 dark:border-rose-500/20 dark:bg-rose-500/[0.06]'}`}>
            <ShieldCheck className={`h-6 w-6 ${ready ? 'text-emerald-600' : 'text-rose-500'}`} />
            <div>
              <p className={`text-sm font-semibold ${ready ? 'text-emerald-800 dark:text-emerald-300' : 'text-rose-800 dark:text-rose-300'}`}>
                {profile === null ? 'No active compliance profile configured' : ready ? 'Readiness checks pass' : 'Readiness gaps found'}
              </p>
              <p className="text-xs text-slate-500 dark:text-slate-400">
                {profile === null
                  ? 'Create a profile for this legal entity to enable readiness checks.'
                  : `${readiness!.totalEmployees} active employee${readiness!.totalEmployees === 1 ? '' : 's'} evaluated`}
              </p>
            </div>
          </div>

          {profile && (
            <div className="grid gap-4 lg:grid-cols-2">
              <div className="rounded-2xl border border-slate-200/80 bg-white p-4 dark:border-white/[0.06] dark:bg-white/[0.03]">
                <h2 className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-400">Profile</h2>
                <dl className="space-y-2 text-sm">
                  <Row k="Country" v={profile.countryCode} />
                  <Row k="Jurisdiction" v={profile.jurisdiction || '—'} />
                  <Row k="Compliance pack" v={profile.compliancePack || '—'} />
                  <Row k="Effective from" v={profile.effectiveFrom} />
                  <Row k="Status" v={profile.status} />
                </dl>
              </div>
              <div className="rounded-2xl border border-slate-200/80 bg-white p-4 dark:border-white/[0.06] dark:bg-white/[0.03]">
                <h2 className="mb-3 text-xs font-semibold uppercase tracking-wide text-slate-400">Required fields</h2>
                {fields.length === 0 ? (
                  <p className="text-xs text-slate-400">No required fields declared on this profile.</p>
                ) : (
                  <table className="w-full text-start text-sm">
                    <thead>
                      <tr className="text-caption uppercase tracking-wide text-slate-400">
                        <th className="pb-2 font-semibold">Field</th>
                        <th className="pb-2 font-semibold">Enforcement</th>
                        <th className="pb-2 text-end font-semibold">Missing</th>
                      </tr>
                    </thead>
                    <tbody>
                      {fields.map((f) => (
                        <tr key={f.field} className="border-t border-slate-50 dark:border-white/[0.03]">
                          <td className="py-2 font-medium text-slate-700 dark:text-slate-200">{f.field}</td>
                          <td className="py-2 text-xs text-slate-500">{f.failClosed ? 'Fail closed' : 'Advisory'}</td>
                          <td className={`py-2 text-end font-semibold ${f.missingEmployeeCount > 0 ? 'text-rose-600 dark:text-rose-400' : 'text-emerald-600 dark:text-emerald-400'}`}>
                            {f.missingEmployeeCount}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </div>
            </div>
          )}
          <div className="rounded-2xl border border-slate-200/80 bg-white p-4 dark:border-white/[0.06] dark:bg-white/[0.03]" data-testid="compliance-profile-list">
            <div className="mb-3 flex items-center justify-between gap-3">
              <h2 className="text-xs font-semibold uppercase tracking-wide text-slate-400">{t('Profiles for this company')}</h2>
              <ShowTurnedOffToggle checked={showOff} onChange={setShowOff} count={profiles.filter(isOff).length} />
            </div>
            {removal.notice}
            {removal.confirmDialog}
            {listed.length === 0 ? <p className="text-xs text-slate-400">{t('No profiles to show.')}</p> : (
              <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
                {listed.map(p => (
                  <li key={p.id} className={`flex flex-wrap items-center justify-between gap-3 py-2 ${isOff(p) ? 'opacity-70' : ''}`}>
                    <span className="text-sm text-slate-700 dark:text-slate-200">
                      <bdi>{p.countryCode}{p.jurisdiction ? ` · ${p.jurisdiction}` : ''}</bdi>
                      <span className="ms-2 text-xs text-slate-500">{p.effectiveFrom}{p.effectiveTo ? ` → ${p.effectiveTo}` : ''} · {isOff(p) ? t('Off') : p.status}</span>
                    </span>
                    <OrgRowActions name={`${p.countryCode} ${p.jurisdiction}`} isActive={!isOff(p)} editable={false} busy={removal.busyId === p.id}
                      onEdit={() => {}} onRemove={() => removal.remove({ id: p.id, name: `${p.countryCode} ${p.jurisdiction}`.trim() })} onRestore={() => removal.restore({ id: p.id, name: `${p.countryCode} ${p.jurisdiction}`.trim() })} />
                  </li>
                ))}
              </ul>
            )}
          </div>
          {profile && <JawazatPolicyEditor key={profile.id} profile={profile} onSaved={updated => setReadiness(current => current?.profile?.id === updated.id ? { ...current, profile: updated } : current)} />}
        </>
      )}
    </div>
  );
}

function Row({ k, v }: { k: string; v: string }) {
  return (
    <div className="flex justify-between gap-3">
      <dt className="text-slate-400">{k}</dt>
      <dd className="font-medium text-slate-700 dark:text-slate-200">{v}</dd>
    </div>
  );
}
