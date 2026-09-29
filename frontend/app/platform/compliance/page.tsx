'use client';
import { useEffect, useState } from 'react';
import { AlertTriangle, CheckCircle2, Clock, MinusCircle, Plus, ShieldCheck, XCircle } from 'lucide-react';
import {
  platformApi,
  type ComplianceControl,
  type ComplianceSummary,
  type SecurityIncident,
} from '@/src/api/platform';

const CONTROL_STATUSES = ['NotStarted', 'InProgress', 'Implemented', 'Waived', 'NotApplicable'] as const;
const SEVERITIES = ['Low', 'Medium', 'High', 'Critical'] as const;
const INCIDENT_STATUSES = ['Open', 'Investigating', 'Resolved', 'Closed'] as const;

function statusChip(status: string) {
  const map: Record<string, string> = {
    Implemented:   'bg-emerald-500/10 text-emerald-400 border-emerald-500/20',
    InProgress:    'bg-amber-500/10  text-amber-400  border-amber-500/20',
    NotStarted:    'bg-slate-500/10  text-slate-400  border-slate-500/20',
    Waived:        'bg-purple-500/10 text-purple-400 border-purple-500/20',
    NotApplicable: 'bg-zinc-500/10   text-zinc-400   border-zinc-500/20',
  };
  return (
    <span className={`inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-medium ${map[status] ?? 'bg-slate-500/10 text-slate-400'}`}>
      {status.replace(/([A-Z])/g, ' $1').trim()}
    </span>
  );
}

function severityChip(s: string) {
  const map: Record<string, string> = {
    Critical: 'bg-red-500/10 text-red-400 border-red-500/20',
    High:     'bg-orange-500/10 text-orange-400 border-orange-500/20',
    Medium:   'bg-amber-500/10  text-amber-400  border-amber-500/20',
    Low:      'bg-blue-500/10   text-blue-400   border-blue-500/20',
  };
  return (
    <span className={`inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-medium ${map[s] ?? 'bg-slate-500/10 text-slate-400'}`}>
      {s}
    </span>
  );
}

function incidentStatusChip(s: string) {
  const map: Record<string, string> = {
    Open:          'bg-red-500/10   text-red-400   border-red-500/20',
    Investigating: 'bg-amber-500/10 text-amber-400  border-amber-500/20',
    Resolved:      'bg-emerald-500/10 text-emerald-400 border-emerald-500/20',
    Closed:        'bg-slate-500/10 text-slate-400  border-slate-500/20',
  };
  return (
    <span className={`inline-flex items-center rounded-full border px-2 py-0.5 text-xs font-medium ${map[s] ?? 'bg-slate-500/10 text-slate-400'}`}>
      {s}
    </span>
  );
}

type Tab = 'controls' | 'incidents';

export default function CompliancePage() {
  const [tab, setTab] = useState<Tab>('controls');
  const [summary, setSummary] = useState<ComplianceSummary | null>(null);
  const [controls, setControls] = useState<ComplianceControl[]>([]);
  const [incidents, setIncidents] = useState<SecurityIncident[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  // Edit-control modal
  const [editControl, setEditControl] = useState<ComplianceControl | null>(null);
  const [editStatus, setEditStatus] = useState('');
  const [editOwner, setEditOwner] = useState('');
  const [editEvidence, setEditEvidence] = useState('');
  const [editSaving, setEditSaving] = useState(false);

  // New-incident modal
  const [showIncidentModal, setShowIncidentModal] = useState(false);
  const [incTitle, setIncTitle] = useState('');
  const [incDesc, setIncDesc] = useState('');
  const [incSeverity, setIncSeverity] = useState<string>('Low');
  const [incReporter, setIncReporter] = useState('');
  const [incAffected, setIncAffected] = useState('');
  const [incCreating, setIncCreating] = useState(false);

  // Edit-incident modal
  const [editIncident, setEditIncident] = useState<SecurityIncident | null>(null);
  const [editIncStatus, setEditIncStatus] = useState('');
  const [editIncResolution, setEditIncResolution] = useState('');
  const [editIncSaving, setEditIncSaving] = useState(false);

  const loadAll = async () => {
    setLoading(true);
    setError('');
    try {
      const [s, c, i] = await Promise.all([
        platformApi.getComplianceSummary(),
        platformApi.listComplianceControls(),
        platformApi.listSecurityIncidents(),
      ]);
      setSummary(s);
      setControls(c);
      setIncidents(i);
    } catch {
      setError('Failed to load compliance data.');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { loadAll(); }, []);

  const openEditControl = (c: ComplianceControl) => {
    setEditControl(c);
    setEditStatus(c.status);
    setEditOwner(c.owner ?? '');
    setEditEvidence(c.evidenceNote ?? '');
  };

  const saveControl = async () => {
    if (!editControl) return;
    setEditSaving(true);
    try {
      await platformApi.updateComplianceControl(editControl.id, {
        status: editStatus,
        owner: editOwner || undefined,
        evidenceNote: editEvidence || undefined,
      });
      await loadAll();
      setEditControl(null);
    } catch {
      setError('Could not save control update.');
    } finally {
      setEditSaving(false);
    }
  };

  const createIncident = async () => {
    if (!incTitle.trim()) return;
    setIncCreating(true);
    try {
      await platformApi.createSecurityIncident({
        title: incTitle.trim(),
        description: incDesc.trim() || undefined,
        severity: incSeverity,
        reporter: incReporter.trim() || undefined,
        affectedSystems: incAffected.trim() || undefined,
      });
      setShowIncidentModal(false);
      setIncTitle(''); setIncDesc(''); setIncSeverity('Low'); setIncReporter(''); setIncAffected('');
      await loadAll();
    } catch {
      setError('Could not create incident.');
    } finally {
      setIncCreating(false);
    }
  };

  const openEditIncident = (inc: SecurityIncident) => {
    setEditIncident(inc);
    setEditIncStatus(inc.status);
    setEditIncResolution(inc.resolution ?? '');
  };

  const saveIncident = async () => {
    if (!editIncident) return;
    setEditIncSaving(true);
    try {
      await platformApi.updateSecurityIncident(editIncident.id, {
        status: editIncStatus,
        resolution: editIncResolution || undefined,
      });
      await loadAll();
      setEditIncident(null);
    } catch {
      setError('Could not update incident.');
    } finally {
      setEditIncSaving(false);
    }
  };

  const grouped = controls.reduce<Record<string, ComplianceControl[]>>((acc, c) => {
    (acc[c.category] ??= []).push(c);
    return acc;
  }, {});

  return (
    <div className="space-y-5">
      <div className="flex items-center justify-between">
        <h1 className="text-lg font-bold text-white">Compliance Center</h1>
        {tab === 'incidents' && (
          <button
            type="button"
            onClick={() => setShowIncidentModal(true)}
            className="flex items-center gap-1.5 rounded-lg bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-500"
          >
            <Plus className="h-4 w-4" /> Log Incident
          </button>
        )}
      </div>

      {error && (
        <div className="rounded-lg border border-red-500/20 bg-red-500/10 px-4 py-3 text-sm text-red-400">{error}</div>
      )}

      {/* Summary cards */}
      {summary && (
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          {[
            { label: 'Implemented', value: `${summary.implementationPct}%`, sub: `${summary.implemented}/${summary.totalControls} controls`, icon: <CheckCircle2 className="h-5 w-5 text-emerald-400" /> },
            { label: 'In Progress', value: summary.inProgress, sub: 'controls', icon: <Clock className="h-5 w-5 text-amber-400" /> },
            { label: 'Not Started', value: summary.notStarted, sub: 'controls', icon: <MinusCircle className="h-5 w-5 text-slate-400" /> },
            { label: 'Open Incidents', value: summary.openIncidents, sub: summary.criticalIncidents > 0 ? `${summary.criticalIncidents} critical` : 'none critical', icon: <AlertTriangle className={`h-5 w-5 ${summary.criticalIncidents > 0 ? 'text-red-400' : 'text-slate-400'}`} /> },
          ].map((card) => (
            <div key={card.label} className="rounded-xl border border-white/[0.07] bg-[#161b22] p-4">
              <div className="flex items-start justify-between">
                <div>
                  <p className="text-xs text-slate-400">{card.label}</p>
                  <p className="mt-1 text-2xl font-bold text-white">{card.value}</p>
                  <p className="text-xs text-slate-500">{card.sub}</p>
                </div>
                {card.icon}
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Tab bar */}
      <div className="flex gap-1 border-b border-white/[0.07]">
        {(['controls', 'incidents'] as Tab[]).map((t) => (
          <button
            key={t}
            type="button"
            onClick={() => setTab(t)}
            className={`px-4 py-2 text-sm font-medium transition-colors ${tab === t ? 'border-b-2 border-blue-500 text-white' : 'text-slate-400 hover:text-white'}`}
          >
            {t === 'controls' ? 'SOC 2 Controls' : 'Security Incidents'}
          </button>
        ))}
      </div>

      {loading && <p className="py-8 text-center text-sm text-slate-500">Loading…</p>}

      {/* Controls tab */}
      {!loading && tab === 'controls' && (
        <div className="space-y-4">
          {controls.length === 0 && (
            <div className="rounded-xl border border-white/[0.07] bg-[#161b22] p-8 text-center">
              <ShieldCheck className="mx-auto mb-3 h-10 w-10 text-slate-600" />
              <p className="text-sm text-slate-400">No compliance controls yet.</p>
            </div>
          )}
          {Object.entries(grouped).map(([category, items]) => (
            <div key={category} className="rounded-xl border border-white/[0.07] bg-[#161b22] overflow-hidden">
              <div className="border-b border-white/[0.07] px-4 py-3">
                <h2 className="text-sm font-semibold text-white">{category}</h2>
              </div>
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-white/[0.05]">
                    {['Control ID', 'Title', 'Status', 'Owner', 'Evidence', 'Last Review', ''].map(h => (
                      <th key={h} className="px-4 py-2 text-start text-xs font-semibold uppercase text-slate-500">{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y divide-white/[0.04]">
                  {items.map((c) => (
                    <tr key={c.id} className="hover:bg-white/[0.02]">
                      <td className="px-4 py-3 font-mono text-xs text-slate-300">{c.controlId}</td>
                      <td className="px-4 py-3 text-white">{c.title}</td>
                      <td className="px-4 py-3">{statusChip(c.status)}</td>
                      <td className="px-4 py-3 text-slate-400 text-xs">{c.owner ?? '—'}</td>
                      <td className="px-4 py-3 text-slate-400 text-xs max-w-[180px] truncate">{c.evidenceNote ?? '—'}</td>
                      <td className="px-4 py-3 text-slate-500 text-xs">
                        {c.reviewedAtUtc ? new Date(c.reviewedAtUtc).toLocaleDateString() : '—'}
                      </td>
                      <td className="px-4 py-3">
                        <button
                          type="button"
                          onClick={() => openEditControl(c)}
                          className="text-xs text-blue-400 hover:text-blue-300"
                        >
                          Edit
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ))}
        </div>
      )}

      {/* Incidents tab */}
      {!loading && tab === 'incidents' && (
        <div className="rounded-xl border border-white/[0.07] bg-[#161b22] overflow-hidden">
          {incidents.length === 0 ? (
            <div className="p-8 text-center">
              <XCircle className="mx-auto mb-3 h-10 w-10 text-slate-600" />
              <p className="text-sm text-slate-400">No security incidents logged.</p>
            </div>
          ) : (
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-white/[0.07]">
                  {['Title', 'Severity', 'Status', 'Reporter', 'Occurred', 'Resolved', ''].map(h => (
                    <th key={h} className="px-4 py-3 text-start text-xs font-semibold uppercase text-slate-500">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-white/[0.04]">
                {incidents.map((inc) => (
                  <tr key={inc.id} className="hover:bg-white/[0.02]">
                    <td className="px-4 py-3 text-white font-medium">{inc.title}</td>
                    <td className="px-4 py-3">{severityChip(inc.severity)}</td>
                    <td className="px-4 py-3">{incidentStatusChip(inc.status)}</td>
                    <td className="px-4 py-3 text-slate-400 text-xs">{inc.reporter ?? '—'}</td>
                    <td className="px-4 py-3 text-slate-500 text-xs">{new Date(inc.occurredAtUtc).toLocaleDateString()}</td>
                    <td className="px-4 py-3 text-slate-500 text-xs">
                      {inc.resolvedAtUtc ? new Date(inc.resolvedAtUtc).toLocaleDateString() : '—'}
                    </td>
                    <td className="px-4 py-3">
                      <button
                        type="button"
                        onClick={() => openEditIncident(inc)}
                        className="text-xs text-blue-400 hover:text-blue-300"
                      >
                        Update
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      {/* Edit control modal */}
      {editControl && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4">
          <div className="w-full max-w-md rounded-xl border border-white/[0.1] bg-[#0d1117] p-6 space-y-4">
            <h2 className="font-bold text-white">{editControl.controlId} — {editControl.title}</h2>
            <label className="block">
              <span className="text-xs text-slate-400">Status</span>
              <select
                value={editStatus}
                onChange={e => setEditStatus(e.target.value)}
                className="mt-1 w-full rounded-lg border border-white/[0.1] bg-[#161b22] px-3 py-2 text-sm text-white"
              >
                {CONTROL_STATUSES.map(s => <option key={s} value={s}>{s.replace(/([A-Z])/g, ' $1').trim()}</option>)}
              </select>
            </label>
            <label className="block">
              <span className="text-xs text-slate-400">Owner</span>
              <input
                value={editOwner}
                onChange={e => setEditOwner(e.target.value)}
                className="mt-1 w-full rounded-lg border border-white/[0.1] bg-[#161b22] px-3 py-2 text-sm text-white"
                placeholder="Name or team"
              />
            </label>
            <label className="block">
              <span className="text-xs text-slate-400">Evidence note</span>
              <textarea
                value={editEvidence}
                onChange={e => setEditEvidence(e.target.value)}
                rows={3}
                className="mt-1 w-full rounded-lg border border-white/[0.1] bg-[#161b22] px-3 py-2 text-sm text-white"
                placeholder="Link to evidence, policy document, screenshot…"
              />
            </label>
            <div className="flex justify-end gap-2 pt-2">
              <button type="button" onClick={() => setEditControl(null)} className="rounded-lg border border-white/[0.1] px-4 py-2 text-sm text-slate-300 hover:bg-white/[0.05]">Cancel</button>
              <button
                type="button"
                onClick={saveControl}
                disabled={editSaving}
                className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-500 disabled:opacity-50"
              >
                {editSaving ? 'Saving…' : 'Save'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Log incident modal */}
      {showIncidentModal && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4">
          <div className="w-full max-w-md rounded-xl border border-white/[0.1] bg-[#0d1117] p-6 space-y-4">
            <h2 className="font-bold text-white">Log Security Incident</h2>
            <label className="block">
              <span className="text-xs text-slate-400">Title *</span>
              <input value={incTitle} onChange={e => setIncTitle(e.target.value)} className="mt-1 w-full rounded-lg border border-white/[0.1] bg-[#161b22] px-3 py-2 text-sm text-white" placeholder="Brief description of the incident" />
            </label>
            <label className="block">
              <span className="text-xs text-slate-400">Severity</span>
              <select value={incSeverity} onChange={e => setIncSeverity(e.target.value)} className="mt-1 w-full rounded-lg border border-white/[0.1] bg-[#161b22] px-3 py-2 text-sm text-white">
                {SEVERITIES.map(s => <option key={s} value={s}>{s}</option>)}
              </select>
            </label>
            <label className="block">
              <span className="text-xs text-slate-400">Reporter</span>
              <input value={incReporter} onChange={e => setIncReporter(e.target.value)} className="mt-1 w-full rounded-lg border border-white/[0.1] bg-[#161b22] px-3 py-2 text-sm text-white" placeholder="Name or email" />
            </label>
            <label className="block">
              <span className="text-xs text-slate-400">Affected systems</span>
              <input value={incAffected} onChange={e => setIncAffected(e.target.value)} className="mt-1 w-full rounded-lg border border-white/[0.1] bg-[#161b22] px-3 py-2 text-sm text-white" placeholder="Auth, payroll API, etc." />
            </label>
            <label className="block">
              <span className="text-xs text-slate-400">Description</span>
              <textarea value={incDesc} onChange={e => setIncDesc(e.target.value)} rows={3} className="mt-1 w-full rounded-lg border border-white/[0.1] bg-[#161b22] px-3 py-2 text-sm text-white" />
            </label>
            <div className="flex justify-end gap-2 pt-2">
              <button type="button" onClick={() => setShowIncidentModal(false)} className="rounded-lg border border-white/[0.1] px-4 py-2 text-sm text-slate-300 hover:bg-white/[0.05]">Cancel</button>
              <button type="button" onClick={createIncident} disabled={incCreating || !incTitle.trim()} className="rounded-lg bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-500 disabled:opacity-50">
                {incCreating ? 'Logging…' : 'Log Incident'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Update incident modal */}
      {editIncident && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4">
          <div className="w-full max-w-md rounded-xl border border-white/[0.1] bg-[#0d1117] p-6 space-y-4">
            <h2 className="font-bold text-white">{editIncident.title}</h2>
            <label className="block">
              <span className="text-xs text-slate-400">Status</span>
              <select value={editIncStatus} onChange={e => setEditIncStatus(e.target.value)} className="mt-1 w-full rounded-lg border border-white/[0.1] bg-[#161b22] px-3 py-2 text-sm text-white">
                {INCIDENT_STATUSES.map(s => <option key={s} value={s}>{s}</option>)}
              </select>
            </label>
            <label className="block">
              <span className="text-xs text-slate-400">Resolution / notes</span>
              <textarea value={editIncResolution} onChange={e => setEditIncResolution(e.target.value)} rows={4} className="mt-1 w-full rounded-lg border border-white/[0.1] bg-[#161b22] px-3 py-2 text-sm text-white" placeholder="Steps taken, root cause, remediation…" />
            </label>
            <div className="flex justify-end gap-2 pt-2">
              <button type="button" onClick={() => setEditIncident(null)} className="rounded-lg border border-white/[0.1] px-4 py-2 text-sm text-slate-300 hover:bg-white/[0.05]">Cancel</button>
              <button type="button" onClick={saveIncident} disabled={editIncSaving} className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-500 disabled:opacity-50">
                {editIncSaving ? 'Saving…' : 'Save'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
