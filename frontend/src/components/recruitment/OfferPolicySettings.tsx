'use client';
import { useEffect, useState } from 'react';
import { offerLifecycleApi, type OfferPolicy } from '../../api/offerLifecycle';
import { Modal } from '../Modal';
import { OfferField, offerInput } from './OfferComponentsEditor';
import { useOfferLifecycleT } from '../../i18n/offerLifecycle';
import { requestFailureReason } from '../../lib/requestFailure';
import { useNavigationGuard } from '../../contexts/NavigationGuardContext';
import { useNavigationBlocker } from '../../hooks/useNavigationBlocker';
export function OfferPolicySettings({ onClose }: { onClose: () => void }) {
  const t = useOfferLifecycleT(); const guard = useNavigationGuard(); const [policy, setPolicy] = useState<OfferPolicy | null>(null); const [codes, setCodes] = useState(''); const [baseline, setBaseline] = useState(''); const [busy, setBusy] = useState(false); const [error, setError] = useState('');
  const load = async () => { try { const p = await offerLifecycleApi.policy(); setPolicy(p); setCodes(p.requiredComponentCodes.join(', ')); setBaseline(JSON.stringify([p.precision, p.requiredComponentCodes.join(', ')])); setError(''); } catch (e) { setError(requestFailureReason(e)); } };
  useEffect(() => { void load(); }, []);
  useNavigationBlocker({ id: 'offer-policy', when: !!policy && JSON.stringify([policy.precision, codes]) !== baseline, message: t('Unsaved offer changes. Discard them and leave?'), onDiscard: () => setBaseline(JSON.stringify([policy?.precision, codes])) });
  return <Modal isOpen title={t('Offer settings')} onClose={() => guard.run('offer-settings-close', onClose)} closeDisabled={busy} closeLabel={t('Close')}><div className="space-y-4">{error && <div role="alert"><p>{error}</p><button className="btn-secondary" onClick={() => guard.run('offer-policy-reload', load)}>{t('Reload latest')}</button></div>}{policy && <><p className="text-sm">{t('Currency')}: <strong>{policy.currency}</strong> · {t('Policy version')}: {policy.expectedVersion}</p><OfferField label="Precision"><select className={offerInput} value={policy.precision} onChange={e => setPolicy({ ...policy, precision: Number(e.target.value) })}>{[0, 1, 2, 3].map(n => <option key={n}>{n}</option>)}</select></OfferField><OfferField label="Required component codes"><input className={offerInput} value={codes} onChange={e => setCodes(e.target.value)} /></OfferField><p className="text-sm text-slate-600 dark:text-slate-300">{t('Policy changes revalidate future draft saves. Frozen revisions remain unchanged.')}</p><button className="btn-primary" disabled={busy} onClick={async () => { setBusy(true); setError(''); try { const p = await offerLifecycleApi.savePolicy({ ...policy, requiredComponentCodes: codes.split(',').map(s => s.trim()).filter(Boolean) }); setPolicy(p); setCodes(p.requiredComponentCodes.join(', ')); setBaseline(JSON.stringify([p.precision, p.requiredComponentCodes.join(', ')])); } catch (e) { setError(requestFailureReason(e)); } finally { setBusy(false); } }}>{t(busy ? 'Working…' : 'Save')}</button></>}</div></Modal>;
}
