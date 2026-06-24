'use client';

import { useState, useEffect } from 'react';
import { notifyApiError } from '../api/client';
import {
  hrRequestApi,
  type HRRequest,
  type HRRequestCategory,
} from '../api/intelligence';

type Tab = 'dashboard' | 'requests' | 'create';

const STATUS_COLORS: Record<string, string> = {
  Open: 'bg-blue-100 text-blue-700',
  InProgress: 'bg-amber-100 text-amber-700',
  Resolved: 'bg-green-100 text-green-700',
  Closed: 'bg-gray-100 text-gray-600',
};

const PRIORITY_COLORS: Record<string, string> = {
  Low: 'text-gray-500',
  Normal: 'text-blue-600',
  High: 'text-orange-600',
  Urgent: 'text-red-600',
};

export default function HRRequestCenterPage() {
  const [tab, setTab] = useState<Tab>('dashboard');
  const [requests, setRequests] = useState<HRRequest[]>([]);
  const [categories, setCategories] = useState<HRRequestCategory[]>([]);
  const [dashboard, setDashboard] = useState<{ open: number; inProgress: number; resolved: number; overdue: number; recentRequests: HRRequest[] } | null>(null);
  const [loading, setLoading] = useState(false);
  const [statusFilter, setStatusFilter] = useState('');
  const [selectedRequest, setSelectedRequest] = useState<string | null>(null);
  const [requestDetail, setRequestDetail] = useState<{ request: HRRequest; comments: { id: string; comment: string; createdAtUtc: string }[]; attachments: unknown[] } | null>(null);
  const [newComment, setNewComment] = useState('');
  const [form, setForm] = useState({ employeeId: '', categoryId: '', subject: '', description: '', priority: 'Normal' });
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    loadCategories();
  }, []);

  useEffect(() => {
    if (tab === 'dashboard') loadDashboard();
    if (tab === 'requests') loadRequests();
  }, [tab, statusFilter]);

  async function loadDashboard() {
    try {
      const d = await hrRequestApi.dashboard();
      setDashboard(d);
    } catch {}
  }

  async function loadRequests() {
    setLoading(true);
    try {
      const r = await hrRequestApi.list({ status: statusFilter || undefined, page: 1, pageSize: 30 });
      setRequests(r.items ?? []);
    } catch {} finally {
      setLoading(false);
    }
  }

  async function loadCategories() {
    try {
      const cats = await hrRequestApi.listCategories();
      setCategories(cats);
    } catch {}
  }

  async function loadRequestDetail(id: string) {
    setSelectedRequest(id);
    try {
      const detail = await hrRequestApi.get(id);
      setRequestDetail(detail);
    } catch {}
  }

  async function addComment() {
    if (!selectedRequest || !newComment.trim() || !requestDetail) return;
    try {
      await hrRequestApi.addComment(selectedRequest, requestDetail.request.employeeId, newComment.trim());
      setNewComment('');
      await loadRequestDetail(selectedRequest);
    } catch (e) { notifyApiError(e); }
  }

  async function updateStatus(id: string, status: string) {
    try {
      await hrRequestApi.updateStatus(id, status);
      if (requestDetail) setRequestDetail(prev => prev ? { ...prev, request: { ...prev.request, status } } : null);
      await loadRequests();
    } catch (e) { notifyApiError(e); }
  }

  async function createRequest() {
    if (!form.subject || !form.description || !form.employeeId) return;
    setSubmitting(true);
    try {
      await hrRequestApi.create({
        employeeId: parseInt(form.employeeId),
        categoryId: form.categoryId || undefined,
        categoryName: categories.find(c => c.id === form.categoryId)?.name,
        subject: form.subject,
        description: form.description,
        priority: form.priority,
      });
      setForm({ employeeId: '', categoryId: '', subject: '', description: '', priority: 'Normal' });
      setTab('requests');
    } catch (e) { notifyApiError(e); } finally {
      setSubmitting(false);
    }
  }

  const tabs: { id: Tab; label: string }[] = [
    { id: 'dashboard', label: 'Dashboard' },
    { id: 'requests', label: 'All Requests' },
    { id: 'create', label: '+ New Request' },
  ];

  return (
    <div className="p-6 max-w-6xl mx-auto space-y-4">
      <div>
        <h1 className="text-2xl font-extrabold text-slate-950 dark:text-white">HR Request Center</h1>
        <p className="text-sm text-gray-500">Employee service desk — submit, track, and resolve HR requests</p>
      </div>

      <div className="flex gap-1 border-b border-gray-200">
        {tabs.map(t => (
          <button
            key={t.id}
            onClick={() => setTab(t.id)}
            className={`px-4 py-2.5 text-sm font-medium border-b-2 transition-colors ${
              tab === t.id ? 'border-blue-600 text-blue-600' : 'border-transparent text-gray-500 hover:text-gray-700'
            }`}
          >
            {t.label}
          </button>
        ))}
      </div>

      {/* Dashboard */}
      {tab === 'dashboard' && dashboard && (
        <div className="space-y-4">
          <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
            {[
              { label: 'Open', value: dashboard.open, color: 'text-blue-600', bg: 'bg-blue-50' },
              { label: 'In Progress', value: dashboard.inProgress, color: 'text-amber-600', bg: 'bg-amber-50' },
              { label: 'Resolved', value: dashboard.resolved, color: 'text-green-600', bg: 'bg-green-50' },
              { label: 'Overdue', value: dashboard.overdue, color: 'text-red-600', bg: 'bg-red-50' },
            ].map(stat => (
              <div key={stat.label} className={`${stat.bg} rounded-xl p-4`}>
                <p className="text-sm text-gray-600">{stat.label}</p>
                <p className={`text-3xl font-bold mt-1 ${stat.color}`}>{stat.value}</p>
              </div>
            ))}
          </div>

          <div className="bg-white rounded-xl border border-gray-200">
            <div className="p-4 border-b border-gray-100">
              <h2 className="text-sm font-semibold text-gray-700">Recent Requests</h2>
            </div>
            <div className="divide-y divide-gray-100">
              {(dashboard.recentRequests ?? []).map(r => (
                <div key={r.id} className="px-4 py-3 flex items-center justify-between hover:bg-gray-50 cursor-pointer" onClick={() => { setTab('requests'); loadRequestDetail(r.id); }}>
                  <div>
                    <p className="text-sm font-medium text-gray-900">{r.subject}</p>
                    <p className="text-xs text-gray-500">{r.categoryName} — {new Date(r.createdAtUtc).toLocaleDateString()}</p>
                  </div>
                  <div className="flex items-center gap-2">
                    <span className={`text-xs font-medium ${PRIORITY_COLORS[r.priority]}`}>{r.priority}</span>
                    <span className={`text-xs px-2 py-0.5 rounded-full ${STATUS_COLORS[r.status] || STATUS_COLORS.Open}`}>{r.status}</span>
                  </div>
                </div>
              ))}
              {(dashboard.recentRequests ?? []).length === 0 && (
                <div className="px-4 py-8 text-center text-sm text-gray-400">No recent requests</div>
              )}
            </div>
          </div>
        </div>
      )}

      {/* All Requests */}
      {tab === 'requests' && (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-4">
          <div className="lg:col-span-1 space-y-3">
            <div className="flex gap-2">
              <select
                value={statusFilter}
                onChange={e => setStatusFilter(e.target.value)}
                className="flex-1 border border-gray-200 rounded-lg px-3 py-2 text-sm"
              >
                <option value="">All Statuses</option>
                {['Open', 'InProgress', 'Resolved', 'Closed'].map(s => (
                  <option key={s} value={s}>{s}</option>
                ))}
              </select>
            </div>

            <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
              {loading ? (
                <div className="p-8 text-center text-sm text-gray-400">Loading...</div>
              ) : requests.length === 0 ? (
                <div className="p-8 text-center text-sm text-gray-400">No requests found</div>
              ) : (
                <div className="divide-y divide-gray-100 max-h-[500px] overflow-y-auto">
                  {requests.map(r => (
                    <div
                      key={r.id}
                      className={`px-4 py-3 cursor-pointer hover:bg-gray-50 ${selectedRequest === r.id ? 'bg-blue-50 border-l-2 border-blue-500' : ''}`}
                      onClick={() => loadRequestDetail(r.id)}
                    >
                      <p className="text-sm font-medium text-gray-900 truncate">{r.subject}</p>
                      <div className="flex items-center gap-2 mt-1">
                        <span className={`text-xs px-1.5 py-0.5 rounded-full ${STATUS_COLORS[r.status] || STATUS_COLORS.Open}`}>{r.status}</span>
                        <span className={`text-xs ${PRIORITY_COLORS[r.priority]}`}>{r.priority}</span>
                        <span className="text-xs text-gray-400 ml-auto">{new Date(r.createdAtUtc).toLocaleDateString()}</span>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>

          <div className="lg:col-span-2">
            {!requestDetail ? (
              <div className="bg-white rounded-xl border border-gray-200 flex items-center justify-center h-64 text-gray-400 text-sm">
                Select a request to view details
              </div>
            ) : (
              <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
                <div className="p-4 border-b border-gray-100">
                  <div className="flex items-start justify-between gap-4">
                    <div>
                      <h2 className="font-semibold text-gray-900">{requestDetail.request.subject}</h2>
                      <p className="text-xs text-gray-500 mt-0.5">{requestDetail.request.categoryName} — Employee #{requestDetail.request.employeeId}</p>
                    </div>
                    <div className="flex items-center gap-2">
                      <span className={`text-xs px-2 py-1 rounded-full ${STATUS_COLORS[requestDetail.request.status] || STATUS_COLORS.Open}`}>
                        {requestDetail.request.status}
                      </span>
                    </div>
                  </div>
                  <p className="text-sm text-gray-700 mt-3">{requestDetail.request.description}</p>
                  <div className="flex gap-2 mt-3">
                    {['Open', 'InProgress', 'Resolved'].map(s => s !== requestDetail.request.status && (
                      <button
                        key={s}
                        onClick={() => updateStatus(requestDetail.request.id, s)}
                        className="text-xs px-3 py-1 border border-gray-200 rounded-lg hover:bg-gray-50"
                      >
                        Mark {s}
                      </button>
                    ))}
                  </div>
                </div>

                <div className="p-4 space-y-3 max-h-48 overflow-y-auto">
                  {(requestDetail.comments ?? []).map(c => (
                    <div key={c.id} className="bg-gray-50 rounded-lg px-3 py-2">
                      <p className="text-sm text-gray-700">{c.comment}</p>
                      <p className="text-xs text-gray-400 mt-1">{new Date(c.createdAtUtc).toLocaleString()}</p>
                    </div>
                  ))}
                  {(requestDetail.comments ?? []).length === 0 && (
                    <p className="text-xs text-gray-400">No comments yet</p>
                  )}
                </div>

                <div className="p-4 border-t border-gray-100 flex gap-2">
                  <input
                    value={newComment}
                    onChange={e => setNewComment(e.target.value)}
                    placeholder="Add a comment..."
                    className="flex-1 border border-gray-200 rounded-lg px-3 py-2 text-sm"
                    onKeyDown={e => { if (e.key === 'Enter') addComment(); }}
                  />
                  <button onClick={addComment} className="px-4 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700">
                    Send
                  </button>
                </div>
              </div>
            )}
          </div>
        </div>
      )}

      {/* Create Request */}
      {tab === 'create' && (
        <div className="max-w-xl bg-white rounded-xl border border-gray-200 p-6 space-y-4">
          <h2 className="text-lg font-semibold text-gray-900">Submit HR Request</h2>

          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-xs font-medium text-gray-700 mb-1">Employee ID</label>
              <input
                value={form.employeeId}
                onChange={e => setForm(p => ({ ...p, employeeId: e.target.value }))}
                className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
                placeholder="123"
                type="number"
              />
            </div>
            <div>
              <label className="block text-xs font-medium text-gray-700 mb-1">Priority</label>
              <select
                value={form.priority}
                onChange={e => setForm(p => ({ ...p, priority: e.target.value }))}
                className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
              >
                {['Low', 'Normal', 'High', 'Urgent'].map(p => <option key={p}>{p}</option>)}
              </select>
            </div>
          </div>

          <div>
            <label className="block text-xs font-medium text-gray-700 mb-1">Category</label>
            <select
              value={form.categoryId}
              onChange={e => setForm(p => ({ ...p, categoryId: e.target.value }))}
              className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
            >
              <option value="">Select category...</option>
              {categories.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </div>

          <div>
            <label className="block text-xs font-medium text-gray-700 mb-1">Subject</label>
            <input
              value={form.subject}
              onChange={e => setForm(p => ({ ...p, subject: e.target.value }))}
              className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm"
              placeholder="Brief description of your request"
            />
          </div>

          <div>
            <label className="block text-xs font-medium text-gray-700 mb-1">Description</label>
            <textarea
              value={form.description}
              onChange={e => setForm(p => ({ ...p, description: e.target.value }))}
              rows={4}
              className="w-full border border-gray-200 rounded-lg px-3 py-2 text-sm resize-none"
              placeholder="Provide details about your request..."
            />
          </div>

          <button
            onClick={createRequest}
            disabled={submitting || !form.subject || !form.description || !form.employeeId}
            className="w-full py-2.5 bg-blue-600 text-white rounded-lg text-sm font-medium disabled:opacity-50 hover:bg-blue-700"
          >
            {submitting ? 'Submitting...' : 'Submit Request'}
          </button>
        </div>
      )}
    </div>
  );
}
