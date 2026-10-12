'use client';

import { useCallback, useEffect, useState } from 'react';
import { Plus } from 'lucide-react';
import { successionApi, type Nomination, type PersonRef, type TalentPool } from '../../api/succession';
import { useAuth } from '../../contexts/AuthContext';
import { useLocale } from '../../contexts/LocaleContext';
import { longEnough, nominationStatusLabel, nominationTone, readinessLabel, successionError } from '../../lib/succession';
import { Modal } from '../Modal';
import { StatusChip } from '../StatusChip';
import { useAppToast } from '../ui/AppToast';
import { EmptyBlock, ErrorBlock, Field, FormError, LoadingRows, PersonName, PersonPicker } from './SuccessionParts';

/**
 * Talent pools (SUC-02): groups of people being developed for a family of roles rather than one position. The member
 * count opens the live nominations behind it (slate readers only — names are confidential).
 */
export function PoolsPanel({ canManage, canSeeNames }: { canManage: boolean; canSeeNames: boolean }) {
  const { t, locale } = useLocale();
  const [rows, setRows] = useState<TalentPool[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [showArchived, setShowArchived] = useState(false);
  const [creating, setCreating] = useState(false);
  const [archiving, setArchiving] = useState<TalentPool | null>(null);
  const [members, setMembers] = useState<{ pool: TalentPool; rows: Nomination[] | null; error: string | null } | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try { setRows(await successionApi.pools(showArchived)); } catch (e) { setError(successionError(e, t)); }
  }, [showArchived, t]);
  useEffect(() => { void load(); }, [load]);

  const openMembers = async (pool: TalentPool) => {
    setMembers({ pool, rows: null, error: null });
    try {
      const list = await successionApi.nominations({ poolId: pool.id });
      setMembers({ pool, rows: list.filter((n) => n.status !== 'Declined' && n.status !== 'Withdrawn'), error: null });
    } catch (e) {
      setMembers({ pool, rows: null, error: successionError(e, t) });
    }
  };

  return (
    <div className="space-y-3" data-testid="suc-pools">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <label className="flex items-center gap-2 text-xs text-slate-600 dark:text-slate-300">
          <input type="checkbox" checked={showArchived} onChange={(e) => setShowArchived(e.target.checked)} />
          {t('suc.pools.showArchived')}
        </label>
        {canManage && (
          <button type="button" className="btn-primary gap-1 text-xs" data-testid="suc-pool-create-open" onClick={() => setCreating(true)}>
            <Plus className="h-3.5 w-3.5" aria-hidden="true" />{t('suc.pools.create')}
          </button>
        )}
      </div>
      {error && <ErrorBlock message={error} onRetry={() => void load()} />}
      {!error && !rows && <LoadingRows label={t('suc.loading')} />}
      {rows && rows.length === 0 && <EmptyBlock title={t('suc.pools.emptyTitle')} body={t('suc.pools.emptyBody')} />}
      {rows && rows.length > 0 && (
        <ul className="space-y-2">
          {rows.map((p) => (
            <li key={p.id} className="flex flex-wrap items-start justify-between gap-3 rounded-xl border border-slate-200 p-3 dark:border-white/10" data-testid="suc-pool-row" data-pool-id={p.id}>
              <div className="min-w-0 flex-1">
                <p className="text-sm font-semibold text-slate-900 dark:text-white">
                  {locale === 'ar' ? p.nameAr || p.nameEn : p.nameEn} <span className="text-xs font-normal text-slate-400" dir="ltr">{p.code}</span>
                </p>
                <p className="text-xs text-slate-500">{[p.companyName, p.owner ? t('suc.pools.ownerIs', { name: locale === 'ar' ? p.owner.nameAr || p.owner.nameEn : p.owner.nameEn }) : null].filter(Boolean).join(' · ')}</p>
                <p className="mt-1 text-xs text-slate-600 dark:text-slate-300">{p.purpose}</p>
              </div>
              <div className="flex flex-wrap items-center gap-2">
                {p.status === 'Archived' && <StatusChip label={t('suc.pools.archived')} tone="slate" />}
                {canSeeNames
                  ? <button type="button" className="btn-secondary text-xs" data-testid="suc-pool-members" onClick={() => void openMembers(p)}>{t('suc.pools.members', { count: p.liveMembers ?? 0 })}</button>
                  : p.liveMembers !== null && <span className="text-xs text-slate-600 dark:text-slate-300">{t('suc.pools.members', { count: p.liveMembers })}</span>}
                {canManage && p.status === 'Active' && (
                  <button type="button" className="btn-secondary text-xs" data-testid="suc-pool-archive" onClick={() => setArchiving(p)}>{t('suc.pools.archive')}</button>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}

      {members && (
        <Modal isOpen title={t('suc.pools.membersTitle', { pool: (locale === 'ar' && members.pool.nameAr) || members.pool.nameEn })} onClose={() => setMembers(null)} size="lg">
          <div data-testid="suc-pool-member-list">
            {members.error && <ErrorBlock message={members.error} onRetry={() => void openMembers(members.pool)} />}
            {!members.error && !members.rows && <LoadingRows label={t('suc.loading')} rows={2} />}
            {members.rows?.length === 0 && <p className="text-xs text-slate-500">{t('suc.pools.noMembers')}</p>}
            {members.rows && members.rows.length > 0 && (
              <>
                <p className="mb-2 text-xs text-slate-500">{t('suc.drill.count', { count: members.rows.length })}</p>
                <ul className="divide-y divide-slate-100 dark:divide-white/[0.06]">
                  {members.rows.map((n) => (
                    <li key={n.id} className="flex flex-wrap items-center justify-between gap-2 py-2 text-sm" data-testid="suc-pool-member">
                      <PersonName person={n.candidate} />
                      <span className="flex items-center gap-1">
                        <StatusChip label={t(nominationStatusLabel[n.status])} tone={nominationTone(n.status)} />
                        {n.readiness && <StatusChip label={t(readinessLabel[n.readiness])} tone="blue" />}
                      </span>
                    </li>
                  ))}
                </ul>
              </>
            )}
          </div>
        </Modal>
      )}
      {creating && <CreatePoolDialog onClose={() => setCreating(false)} onDone={() => { setCreating(false); void load(); }} />}
      {archiving && <ArchivePoolDialog pool={archiving} onClose={() => setArchiving(null)} onDone={() => { setArchiving(null); void load(); }} />}
    </div>
  );
}

function CreatePoolDialog({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const { t } = useLocale();
  const { user } = useAuth();
  const toast = useAppToast();
  const companies = user?.companies ?? [];
  const [companyId, setCompanyId] = useState(companies.length === 1 ? companies[0].id : '');
  const [code, setCode] = useState('');
  const [nameEn, setNameEn] = useState('');
  const [nameAr, setNameAr] = useState('');
  const [purpose, setPurpose] = useState('');
  const [owner, setOwner] = useState<PersonRef | null>(null);
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const problems = {
    company: companyId ? null : t('suc.pools.chooseCompany'),
    code: /^[A-Za-z0-9_-]{2,40}$/.test(code.trim()) ? null : t('suc.pools.codeRule'),
    name: nameEn.trim().length >= 2 ? null : t('suc.pools.nameRule'),
    purpose: longEnough(purpose, 10) ? null : t('suc.check.min10'),
  };

  const submit = async () => {
    setTouched(true);
    if (Object.values(problems).some(Boolean)) return;
    setBusy(true);
    setError(null);
    try {
      await successionApi.createPool({ companyId, code: code.trim(), nameEn: nameEn.trim(), nameAr: nameAr.trim() || null, purpose: purpose.trim(), ownerEmployeeId: owner?.employeeId ?? null });
      toast.success(t('suc.pools.created'));
      onDone();
    } catch (e) {
      setError(successionError(e, t));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal isOpen title={t('suc.pools.createTitle')} onClose={onClose} size="lg" footer={(
      <>
        <button type="button" className="btn-secondary text-xs" onClick={onClose}>{t('suc.cancel')}</button>
        <button type="button" className="btn-primary text-xs" data-testid="suc-pool-submit" disabled={busy} onClick={() => void submit()}>{busy ? t('suc.saving') : t('suc.pools.create')}</button>
      </>
    )}>
      <div className="space-y-3">
        <Field id="suc-pool-company" label={t('suc.pools.company')} error={touched ? problems.company : null}>
          <select id="suc-pool-company" data-testid="suc-pool-company" className="select w-full" value={companyId} onChange={(e) => setCompanyId(e.target.value)}>
            <option value="">{t('suc.pools.pickCompany')}</option>
            {companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </Field>
        <div className="grid gap-3 sm:grid-cols-2">
          <Field id="suc-pool-code" label={t('suc.pools.code')} hint={t('suc.pools.codeHint')} error={touched ? problems.code : null}>
            <input id="suc-pool-code" data-testid="suc-pool-code" className="input w-full" dir="ltr" value={code} onChange={(e) => setCode(e.target.value)} />
          </Field>
          <Field id="suc-pool-name-en" label={t('suc.pools.nameEn')} error={touched ? problems.name : null}>
            <input id="suc-pool-name-en" data-testid="suc-pool-name-en" className="input w-full" dir="ltr" value={nameEn} onChange={(e) => setNameEn(e.target.value)} />
          </Field>
        </div>
        <Field id="suc-pool-name-ar" label={t('suc.pools.nameAr')} optional>
          <input id="suc-pool-name-ar" data-testid="suc-pool-name-ar" className="input w-full" dir="rtl" value={nameAr} onChange={(e) => setNameAr(e.target.value)} />
        </Field>
        <Field id="suc-pool-purpose" label={t('suc.pools.purpose')} hint={t('suc.pools.purposeHint')} error={touched ? problems.purpose : null}>
          <textarea id="suc-pool-purpose" data-testid="suc-pool-purpose" className="input min-h-[72px] w-full" value={purpose} onChange={(e) => setPurpose(e.target.value)} />
        </Field>
        <PersonPicker id="suc-pool-owner" testId="suc-pool-owner" value={owner} onChange={setOwner} label={t('suc.pools.owner')} optional />
        <FormError message={error} />
      </div>
    </Modal>
  );
}

function ArchivePoolDialog({ pool, onClose, onDone }: { pool: TalentPool; onClose: () => void; onDone: () => void }) {
  const { t, locale } = useLocale();
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const ok = longEnough(reason, 3);
  return (
    <Modal isOpen title={t('suc.pools.archiveTitle', { pool: (locale === 'ar' && pool.nameAr) || pool.nameEn })} onClose={onClose} footer={(
      <>
        <button type="button" className="btn-secondary text-xs" onClick={onClose}>{t('suc.cancel')}</button>
        <button type="button" className="btn-primary text-xs" data-testid="suc-pool-archive-confirm" disabled={busy || !ok} onClick={async () => {
          setBusy(true);
          setError(null);
          try { await successionApi.archivePool(pool.id, reason.trim()); onDone(); } catch (e) { setError(successionError(e, t)); } finally { setBusy(false); }
        }}>{t('suc.pools.archive')}</button>
      </>
    )}>
      <div className="space-y-3">
        <p className="text-xs text-slate-600 dark:text-slate-300">{t('suc.pools.archiveIntro')}</p>
        <Field id="suc-pool-archive-reason" label={t('suc.reason')}>
          <textarea id="suc-pool-archive-reason" data-testid="suc-pool-archive-reason" className="input min-h-[64px] w-full" value={reason} onChange={(e) => setReason(e.target.value)} />
        </Field>
        <FormError message={error} />
      </div>
    </Modal>
  );
}
