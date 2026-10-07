'use client';

import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { jawazatApi, jawazatError, type JawazatCapabilities, type JawazatCheck, type JawazatCreateInput, type JawazatEvaluation, type JawazatPolicyResponse, type JawazatRequest, type JawazatRoute } from '../../api/jawazat';
import { useAuth } from '../../contexts/AuthContext';
import { useCompany } from '../../contexts/CompanyContext';
import { EmployeeSearchSelect, type EmployeeSelection } from '../EmployeeSearchSelect';

const fieldClass = 'mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm dark:border-white/10 dark:bg-slate-900';
const primaryClass = 'rounded-lg bg-sapphire px-4 py-2 text-sm font-semibold text-white disabled:cursor-not-allowed disabled:opacity-40';
const secondaryClass = 'rounded-lg border border-slate-200 px-4 py-2 text-sm font-semibold dark:border-white/10 disabled:cursor-not-allowed disabled:opacity-40';
const stateLabels: Record<string, string> = { PendingApproval: 'Awaiting internal approval', Approved: 'Internally approved', Rejected: 'Internally rejected', NotificationRecorded: 'Notification recorded' };

export function JawazatPanel({ own = false }: { own?: boolean }) {
  const { selectedCompanyId } = useCompany();
  return <JawazatContent key={`${own}-${selectedCompanyId ?? 'all'}`} own={own} />;
}

function JawazatContent({ own }: { own: boolean }) {
  const { user } = useAuth();
  const canManage = !own && !!user?.roles.some(role => ['Admin', 'HR Director', 'HR Manager', 'HR Officer'].includes(role));
  const [employee, setEmployee] = useState<EmployeeSelection | null>(null);
  const [route, setRoute] = useState<JawazatRoute>('EmployerAssisted');
  const [departureDate, setDepartureDate] = useState('');
  const [returnDate, setReturnDate] = useState('');
  const [reason, setReason] = useState('');
  const [policy, setPolicy] = useState<JawazatPolicyResponse | null>(null);
  const [policyLoading, setPolicyLoading] = useState(false);
  const [policyError, setPolicyError] = useState('');
  const [capabilities, setCapabilities] = useState<JawazatCapabilities | null>(null);
  const [capabilityError, setCapabilityError] = useState('');
  const [requests, setRequests] = useState<JawazatRequest[]>([]);
  const [listLoading, setListLoading] = useState(true);
  const [listError, setListError] = useState('');
  const [reload, setReload] = useState(0);
  const [preview, setPreview] = useState<{ evaluation: JawazatEvaluation; input: JawazatCreateInput } | null>(null);
  const [evaluating, setEvaluating] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const [selected, setSelected] = useState<JawazatRequest | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const revision = useRef(0);
  const detailRevision = useRef(0);
  const requestKey = useRef<string | null>(null);
  const resultRef = useRef<HTMLElement | null>(null);
  const alive = useRef(true);

  useEffect(() => { alive.current = true; return () => { alive.current = false; revision.current++; detailRevision.current++; }; }, []);
  useEffect(() => { if (selected) resultRef.current?.focus(); }, [selected?.id]);
  useEffect(() => {
    const controller = new AbortController();
    setListLoading(true); setListError(''); setCapabilityError('');
    Promise.allSettled([jawazatApi.capabilities(controller.signal), jawazatApi.list(controller.signal)]).then(([capabilityResult, listResult]) => {
      if (controller.signal.aborted) return;
      if (capabilityResult.status === 'fulfilled') setCapabilities(capabilityResult.value);
      else { setCapabilities(null); setCapabilityError(jawazatError(capabilityResult.reason)); }
      if (listResult.status === 'fulfilled') setRequests(listResult.value);
      else setListError(jawazatError(listResult.reason));
      setListLoading(false);
    });
    return () => controller.abort();
  }, [reload]);

  useEffect(() => {
    const controller = new AbortController();
    setPolicy(null); setPolicyError('');
    if (canManage && !employee) { setPolicyLoading(false); return () => controller.abort(); }
    setPolicyLoading(true);
    jawazatApi.policy(canManage ? employee?.intId : undefined, controller.signal)
      .then(value => { if (!controller.signal.aborted) setPolicy(value); })
      .catch(failure => { if (!controller.signal.aborted) setPolicyError(jawazatError(failure)); })
      .finally(() => { if (!controller.signal.aborted) setPolicyLoading(false); });
    return () => controller.abort();
  }, [canManage, employee?.intId, reload]);

  const invalidate = () => { revision.current++; requestKey.current = null; setPreview(null); setEvaluating(false); setError(''); };
  const allowed = route === 'EmployerAssisted' ? policy?.canCreateEmployerAssisted : policy?.canRecordWorkerNotification;
  const validDates = !!departureDate && !!returnDate && returnDate >= departureDate;
  const canPreview = !!allowed && validDates && !!reason.trim() && (!canManage || !!employee) && !saving && !evaluating;
  const localChecksFailed = route === 'EmployerAssisted' && preview?.evaluation.checks.some(check => check.result === 'Failed');
  const createLabel = route === 'EmployerAssisted' ? 'Request employer assistance' : 'Record notification';

  const evaluate = async () => {
    if (!canPreview) return;
    const current = revision.current;
    requestKey.current ??= crypto.randomUUID();
    const input: JawazatCreateInput = { ...(canManage ? { employeeId: employee!.intId } : {}), route, service: 'ExitReentryIssue', departureDate, returnDate, reason: reason.trim(), idempotencyKey: requestKey.current };
    setEvaluating(true); setError(''); setPreview(null);
    try {
      const evaluation = await jawazatApi.evaluate(input);
      if (alive.current && current === revision.current) setPreview({ evaluation, input });
    } catch (failure) { if (alive.current && current === revision.current) setError(jawazatError(failure)); }
    finally { if (alive.current && current === revision.current) setEvaluating(false); }
  };
  const create = async () => {
    if (!preview || saving || localChecksFailed || !allowed) return;
    setSaving(true); setError('');
    try {
      // Retain this exact input and key after a network failure: retrying cannot duplicate a ticket.
      const created = await jawazatApi.create(preview.input);
      if (!alive.current) return;
      setRequests(previous => [created, ...previous.filter(item => item.id !== created.id)]);
      setSelected(created); setPreview(null); requestKey.current = null; revision.current++;
    } catch (failure) { if (alive.current) setError(jawazatError(failure)); }
    finally { if (alive.current) setSaving(false); }
  };
  const view = async (id: string) => {
    const current = ++detailRevision.current;
    setDetailLoading(true); setError(''); setSelected(null);
    try { const value = await jawazatApi.get(id); if (alive.current && current === detailRevision.current) setSelected(value); }
    catch (failure) { if (alive.current && current === detailRevision.current) setError(jawazatError(failure)); }
    finally { if (alive.current && current === detailRevision.current) setDetailLoading(false); }
  };
  const submit = async () => {
    if (!selected || !canManage || !capabilities?.isAvailable || saving) return;
    setSaving(true); setError('');
    try {
      const value = await jawazatApi.submit(selected.id);
      if (!alive.current) return;
      setSelected(value); setRequests(previous => previous.map(item => item.id === value.id ? value : item));
    } catch (failure) { if (alive.current) setError(jawazatError(failure)); }
    finally { if (alive.current) setSaving(false); }
  };

  return <section className="space-y-5 text-slate-800 dark:text-slate-200" aria-label="Jawazat requests">
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div><h2 className="text-xl font-bold">{own ? 'My Jawazat requests' : 'Jawazat requests'}</h2><p className="mt-1 text-sm text-slate-500">Exit and re-entry requests, internal review and travel notifications.</p></div>
      {canManage && <Link href="/compliance-profiles" className="text-sm font-semibold text-sapphire dark:text-cyanAccent">Company Jawazat policy</Link>}
    </div>
    <div className="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900 dark:border-amber-500/20 dark:bg-amber-500/10 dark:text-amber-200">
      <p className="font-semibold">{capabilities?.isAvailable ? 'Government connection available' : 'Government submission unavailable'}</p>
      <p className="mt-1">{capabilityError || capabilities?.message || 'Checking government connection availability…'}</p>
      <p className="mt-1">Internal approval does not issue a visa. Government eligibility and issuance must be verified separately.</p>
    </div>
    {error && <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 p-3 text-sm text-rose-800 dark:bg-rose-500/10 dark:text-rose-200">{error}</p>}
    <div className="grid items-start gap-5 xl:grid-cols-2">
      <div className="surface p-5">
        <h3 className="mb-4 font-semibold">New exit and re-entry request</h3>
        <fieldset disabled={saving} className="space-y-4">
          {canManage && <div><p className="mb-1 text-sm font-medium">Employee</p><EmployeeSearchSelect value={employee} onChange={value => { invalidate(); setEmployee(value); }} required /></div>}
          {policyLoading && <p role="status" className="text-sm text-slate-500">Loading company policy…</p>}
          {policyError && <p role="alert" className="text-sm text-rose-600">{policyError}</p>}
          {canManage && !employee && <p className="text-sm text-slate-500">Select an employee to load the policy for their company.</p>}
          {policy && <p className="text-sm text-slate-500">Policy {policy.policy.ruleVersion || 'not reviewed'} · effective {policy.effectiveFrom}{policy.effectiveTo ? ` to ${policy.effectiveTo}` : ''}. Company trip limit: {policy.policy.maximumTripDays} days.</p>}
          <label className="block text-sm font-medium">Request route<select className={fieldClass} value={route} onChange={event => { invalidate(); setRoute(event.target.value as JawazatRoute); }}>
            <option value="EmployerAssisted">Employer-assisted request</option><option value="WorkerSelfServiceNotification">Worker self-service notification</option>
          </select></label>
          <p className="text-sm text-slate-500">{route === 'EmployerAssisted' ? 'Request company assistance through the existing internal approval workflow.' : 'Record a notification of your own travel process. This does not request employer permission or certify government eligibility.'}</p>
          {policy && !allowed && <p className="text-sm text-amber-700 dark:text-amber-300">This route is not enabled for this employee under the current company policy.</p>}
          <div className="grid gap-4 sm:grid-cols-2">
            <label className="block text-sm font-medium">Departure date<input type="date" className={fieldClass} value={departureDate} onChange={event => { invalidate(); setDepartureDate(event.target.value); }} /></label>
            <label className="block text-sm font-medium">Return date<input type="date" className={fieldClass} value={returnDate} min={departureDate} onChange={event => { invalidate(); setReturnDate(event.target.value); }} /></label>
          </div>
          {departureDate && returnDate && !validDates && <p className="text-sm text-rose-600">Return date must be on or after departure.</p>}
          <label className="block text-sm font-medium">Travel reason<textarea className={fieldClass} rows={3} maxLength={1000} value={reason} onChange={event => { invalidate(); setReason(event.target.value); }} /></label>
          <div className="flex flex-wrap gap-2"><button type="button" className={secondaryClass} disabled={!canPreview} onClick={evaluate}>{evaluating ? 'Checking request…' : 'Preview request'}</button><button type="button" className={primaryClass} disabled={!preview || saving || !!localChecksFailed || !allowed} onClick={create}>{saving ? 'Saving…' : createLabel}</button></div>
        </fieldset>
        {preview && <section aria-label="Request preview" className="mt-5 space-y-3 border-t border-slate-200 pt-4 dark:border-white/10">
          <h4 className="font-semibold">Request preview</h4><p className="text-sm">{preview.input.departureDate} to {preview.input.returnDate} · {preview.input.reason}</p>
          <p className="text-xs text-slate-500">Policy {preview.evaluation.policySnapshot.policy.ruleVersion || 'unreviewed'} captured {new Date(preview.evaluation.policySnapshot.capturedAtUtc).toLocaleString()}.</p>
          <Checks checks={preview.evaluation.checks} />
          {localChecksFailed && <p className="text-sm text-rose-600">Resolve failed company checks before requesting employer assistance.</p>}
          <p className="text-sm text-slate-500">Unknown checks remain unverified. Creating this record does not submit an application to the government.</p>
        </section>}
        <p className="mt-5 text-xs text-slate-500">Fees require a separate verified payment process. This request does not deduct payroll or treat an employee loan as a travel restriction.</p>
      </div>
      <div className="space-y-4">
        <div className="surface p-5">
          <div className="mb-3 flex items-center justify-between gap-2"><h3 className="font-semibold">Request history</h3><button type="button" className="text-sm text-sapphire dark:text-cyanAccent" disabled={listLoading || saving} onClick={() => { invalidate(); setReload(value => value + 1); }}>Refresh</button></div>
          {listLoading ? <p role="status" className="text-sm text-slate-500">Loading requests…</p> : listError ? <p role="alert" className="text-sm text-rose-600">{listError}</p> : requests.length === 0 ? <p className="text-sm text-slate-500">No Jawazat requests recorded.</p> : <ul className="divide-y divide-slate-100 dark:divide-white/10">{requests.map(request => <li key={request.id}><button type="button" onClick={() => view(request.id)} disabled={saving} className="w-full py-3 text-start hover:text-sapphire" aria-label={`View request ${request.id}`}><span className="block text-sm font-medium">{request.subject}</span><span className="block text-xs text-slate-500">{request.data.departureDate} → {request.data.returnDate} · {stateLabels[request.data.internalState] ?? request.data.internalState}</span></button></li>)}</ul>}
        </div>
        {detailLoading && <p role="status" className="text-sm text-slate-500">Loading request details…</p>}
        {selected && <section ref={resultRef} tabIndex={-1} aria-label="Request details" className="surface scroll-mt-20 space-y-3 p-5 focus:outline-none focus-visible:ring-2 focus-visible:ring-sapphire">
          <h3 className="font-semibold">{stateLabels[selected.data.internalState] ?? selected.data.internalState}</h3>
          <p className="text-sm">{selected.data.departureDate} to {selected.data.returnDate}</p><p className="break-words text-sm">{selected.data.reason}</p>
          <p className="text-xs text-slate-500">Request {selected.id} · created {new Date(selected.createdAtUtc).toLocaleString()}</p>
          <p className="text-sm font-medium">Government status: {selected.data.providerState === 'ProviderUnavailable' ? 'Provider unavailable' : 'Not submitted'}</p>
          {selected.data.providerMessage && <p className="text-sm">{selected.data.providerMessage}</p>}
          {selected.data.route === 'WorkerSelfServiceNotification' ? <p className="text-sm text-slate-500">This notification is not employer consent or a government-issued visa.</p> : <>
            <p className="text-sm text-slate-500">An internal decision is separate from government eligibility and visa issuance.</p>
            {canManage && selected.approvalRequestId && <Link href="/approvals" className="inline-block text-sm font-semibold text-sapphire dark:text-cyanAccent">Open Approvals Center</Link>}
            {canManage && selected.data.internalState === 'Approved' && <div><button type="button" className={primaryClass} onClick={submit} disabled={saving || !capabilities?.isAvailable || !capabilities.supportedOperations.includes('ExitReentryIssue')}>Submit to government</button>{!capabilities?.isAvailable && <p className="mt-1 text-xs text-slate-500">Submission remains unavailable until a government provider is connected.</p>}</div>}
          </>}
          {selected.data.decisionNote && <p className="text-sm">Decision note: {selected.data.decisionNote}</p>}
          <p className="text-xs text-slate-500">Recorded policy: {selected.data.policySnapshot.policy.ruleVersion || 'unreviewed'} · reviewer {selected.data.policySnapshot.policy.reviewedBy || 'not recorded'}.</p>
          <Checks checks={selected.data.checks} />
        </section>}
      </div>
    </div>
  </section>;
}

function Checks({ checks }: { checks: JawazatCheck[] }) {
  return <ul className="space-y-2">{checks.map((check, index) => <li key={`${check.code}-${index}`} className="rounded-lg border border-slate-200 p-3 dark:border-white/10">
    <p className="text-sm font-semibold">{check.result} · {check.code.replace(/[_-]/g, ' ')}</p><p className="mt-1 text-sm">{check.reason}</p>
    <p className="mt-1 text-xs text-slate-500">Source: {check.evidenceSource || 'Not provided'} · {check.evidenceAtUtc ? new Date(check.evidenceAtUtc).toLocaleString() : 'No verification time recorded'}</p>
  </li>)}</ul>;
}
