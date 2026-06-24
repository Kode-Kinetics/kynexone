'use client';

import { useState, useRef, useEffect } from 'react';
import {
  aiAssistantApi,
  type AIProviderStatus,
  type AIQueryResponse,
  type AIInsight,
  type EmployeeRiskScore,
  type AIHRQueryLog,
} from '../api/intelligence';
import { PolicyDocumentManager } from '../components/PolicyDocumentManager';

type Tab = 'assistant' | 'insights' | 'risk' | 'history' | 'policy-docs';

interface ChatMessage {
  role: 'user' | 'assistant';
  text: string;
  intent?: string;
  wasBlocked?: boolean;
  timestamp: Date;
}

const SEVERITY_COLORS: Record<string, string> = {
  Info: 'bg-blue-50 border-blue-200 text-blue-700',
  Warning: 'bg-amber-50 border-amber-200 text-amber-700',
  Critical: 'bg-red-50 border-red-200 text-red-700',
};

const RISK_COLORS: Record<string, string> = {
  Low: 'bg-green-100 text-green-700',
  Medium: 'bg-amber-100 text-amber-700',
  High: 'bg-orange-100 text-orange-700',
  Critical: 'bg-red-100 text-red-700',
};

const SUGGESTIONS = [
  'How many employees are currently active?',
  'Who is on leave today?',
  'How many leave requests are pending approval?',
  'What public holidays are coming up?',
  'Which departments do we have?',
];

export default function AIAssistantPage() {
  const [tab, setTab] = useState<Tab>('assistant');
  const [query, setQuery] = useState('');
  const [messages, setMessages] = useState<ChatMessage[]>([
    {
      role: 'assistant',
      text: 'Hello! I\'m your workspace assistant. I can help you with headcount, leave status, pending approvals, department information, and more. Responses are advisory only — no automated HR decisions are made.\n\nWhat would you like to know?',
      timestamp: new Date(),
    },
  ]);
  const [loading, setLoading] = useState(false);
  const [insights, setInsights] = useState<AIInsight[]>([]);
  const [riskScores, setRiskScores] = useState<EmployeeRiskScore[]>([]);
  const [history, setHistory] = useState<AIHRQueryLog[]>([]);
  const [computing, setComputing] = useState(false);
  const [providerStatus, setProviderStatus] = useState<AIProviderStatus | null>(null);
  const messagesEndRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    aiAssistantApi.status().then(setProviderStatus).catch(() => setProviderStatus({ enabled: false, provider: 'unknown' }));
  }, []);

  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  useEffect(() => {
    if (tab === 'insights') loadInsights();
    if (tab === 'risk') loadRiskScores();
    if (tab === 'history') loadHistory();
  }, [tab]);

  async function loadInsights() {
    try {
      const r = await aiAssistantApi.listInsights({ page: 1 });
      setInsights(r.items);
    } catch {}
  }

  async function loadRiskScores() {
    try {
      const r = await aiAssistantApi.listRiskScores();
      setRiskScores(r);
    } catch {}
  }

  async function loadHistory() {
    try {
      const r = await aiAssistantApi.queryHistory({ page: 1 });
      setHistory(r.items);
    } catch {}
  }

  async function sendQuery(text: string) {
    if (!text.trim() || loading) return;
    const userMsg: ChatMessage = { role: 'user', text: text.trim(), timestamp: new Date() };
    setMessages(prev => [...prev, userMsg]);
    setQuery('');
    setLoading(true);

    try {
      const res: AIQueryResponse = await aiAssistantApi.query(text.trim());
      const assistantMsg: ChatMessage = {
        role: 'assistant',
        text: res.wasBlocked ? `Sorry — ${res.blockedReason}` : res.answer,
        intent: res.intent,
        wasBlocked: res.wasBlocked,
        timestamp: new Date(),
      };
      setMessages(prev => [...prev, assistantMsg]);
    } catch {
      setMessages(prev => [...prev, {
        role: 'assistant',
        text: 'I encountered an error processing your request. Please try again.',
        timestamp: new Date(),
      }]);
    } finally {
      setLoading(false);
    }
  }

  async function acknowledgeInsight(id: string) {
    try {
      await aiAssistantApi.acknowledgeInsight(id);
      setInsights(prev => prev.map(i => i.id === id ? { ...i, isAcknowledged: true } : i));
    } catch {}
  }

  async function computeRisk() {
    setComputing(true);
    try {
      await aiAssistantApi.computeRiskScores();
      await loadRiskScores();
    } catch {} finally {
      setComputing(false);
    }
  }

  const tabs: { id: Tab; label: string }[] = [
    { id: 'assistant', label: 'Assistant' },
    { id: 'insights', label: 'Insights' },
    { id: 'risk', label: 'Risk Scores' },
    { id: 'history', label: 'Query Log' },
    { id: 'policy-docs', label: 'Policy Documents' },
  ];

  return (
    <div className="p-6 max-w-6xl mx-auto space-y-4">
      <div className="flex items-center gap-3">
        <div className="w-10 h-10 rounded-xl bg-sapphire flex items-center justify-center text-white text-xl font-bold">Z</div>
        <div>
          <h1 className="text-2xl font-extrabold text-slate-950 dark:text-white">Workspace Assistant</h1>
          <p className="text-sm text-gray-500">Advisory only — assists HR decisions but never replaces them</p>
        </div>
        <div className="ml-auto flex items-center gap-2 px-3 py-1 bg-amber-50 border border-amber-200 rounded-full text-xs text-amber-700 font-medium">
          Advisory Label Active
        </div>
      </div>

      {/* Tabs */}
      <div className="flex gap-1 border-b border-gray-200">
        {tabs.map(t => (
          <button
            key={t.id}
            onClick={() => setTab(t.id)}
            className={`px-4 py-2.5 text-sm font-medium border-b-2 transition-colors ${
              tab === t.id
                ? 'border-sapphire text-sapphire'
                : 'border-transparent text-gray-500 hover:text-gray-700'
            }`}
          >
            {t.label}
          </button>
        ))}
      </div>

      {/* Provider-unavailable banner */}
      {providerStatus !== null && !providerStatus.enabled && (
        <div className="flex items-start gap-3 px-4 py-3 bg-amber-50 border border-amber-300 rounded-xl text-sm text-amber-800">
          <span className="text-amber-500 text-lg leading-none mt-0.5">⚠</span>
          <div>
            <span className="font-semibold">Assistant not configured.</span>{' '}
            The assistant is disabled because no provider key is set (<code className="font-mono text-xs bg-amber-100 px-1 rounded">AI_PROVIDER=none</code>).
            Set <code className="font-mono text-xs bg-amber-100 px-1 rounded">AI_PROVIDER</code> and an API key in Render to enable live responses.
          </div>
        </div>
      )}

      {/* Assistant Chat */}
      {tab === 'assistant' && (
        <div className="grid grid-cols-1 lg:grid-cols-4 gap-4">
          <div className="lg:col-span-3 flex flex-col bg-white rounded-xl border border-gray-200 overflow-hidden" style={{ height: 540 }}>
            {/* Messages */}
            <div className="flex-1 overflow-y-auto p-4 space-y-3">
              {messages.map((msg, i) => (
                <div key={i} className={`flex ${msg.role === 'user' ? 'justify-end' : 'justify-start'}`}>
                  <div className={`max-w-[80%] rounded-xl px-4 py-3 text-sm ${
                    msg.role === 'user'
                      ? 'bg-sapphire text-white'
                      : msg.wasBlocked
                        ? 'bg-red-50 border border-red-200 text-red-800'
                        : 'bg-gray-50 border border-gray-200 text-gray-800'
                  }`}>
                    {msg.role === 'assistant' && (
                      <div className="flex items-center gap-1.5 mb-1.5">
                        <div className="w-4 h-4 rounded-full bg-sapphire"></div>
                        <span className="text-xs font-semibold text-sapphire">Assistant</span>
                        <span className="text-xs text-gray-400 ml-auto">Advisory</span>
                      </div>
                    )}
                    <p className="whitespace-pre-wrap">{msg.text}</p>
                    <p className="text-xs opacity-60 mt-1">
                      {msg.timestamp.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                    </p>
                  </div>
                </div>
              ))}
              {loading && (
                <div className="flex justify-start">
                  <div className="bg-gray-50 border border-gray-200 rounded-xl px-4 py-3 flex items-center gap-2">
                    <div className="flex gap-1">
                      {[0, 1, 2].map(i => (
                        <div key={i} className="w-1.5 h-1.5 bg-sapphire rounded-full animate-bounce" style={{ animationDelay: `${i * 0.1}s` }} />
                      ))}
                    </div>
                    <span className="text-sm text-gray-500">Thinking...</span>
                  </div>
                </div>
              )}
              <div ref={messagesEndRef} />
            </div>

            {/* Input */}
            <div className="border-t border-gray-100 p-3 bg-white">
              <div className="flex gap-2">
                <input
                  value={query}
                  onChange={e => setQuery(e.target.value)}
                  onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendQuery(query); } }}
                  placeholder={providerStatus?.enabled === false ? 'Assistant not configured — see banner above' : 'Ask anything about your workforce...'}
                  className="flex-1 border border-gray-200 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-sapphire disabled:bg-gray-50 disabled:text-gray-400"
                  disabled={loading || providerStatus?.enabled === false}
                />
                <button
                  onClick={() => sendQuery(query)}
                  disabled={loading || !query.trim() || providerStatus?.enabled === false}
                  className="px-4 py-2 bg-sapphire text-white rounded-lg text-sm font-medium disabled:opacity-50 hover:bg-sapphire/90"
                >
                  Send
                </button>
              </div>
            </div>
          </div>

          {/* Suggestions panel */}
          <div className="space-y-3">
            <div className="bg-white rounded-xl border border-gray-200 p-4">
              <h3 className="text-sm font-semibold text-gray-700 mb-3">Try asking</h3>
              <div className="space-y-2">
                {SUGGESTIONS.map((s, i) => (
                  <button
                    key={i}
                    onClick={() => sendQuery(s)}
                    className="w-full text-left text-xs px-3 py-2 bg-gray-50 hover:bg-sapphire/10 hover:text-sapphire rounded-lg border border-gray-100 transition-colors"
                  >
                    {s}
                  </button>
                ))}
              </div>
            </div>
            <div className="bg-amber-50 border border-amber-200 rounded-xl p-4">
              <p className="text-xs text-amber-700 font-medium mb-1">Advisory Notice</p>
              <p className="text-xs text-amber-600">
                All responses are advisory only and do not make or enforce HR decisions. All sensitive queries are logged and subject to RBAC.
              </p>
            </div>
          </div>
        </div>
      )}

      {/* Insights */}
      {tab === 'insights' && (
        <div className="space-y-4">
          <div className="flex items-center justify-between">
            <h2 className="text-lg font-semibold text-gray-900">Insights</h2>
            <button onClick={loadInsights} className="text-sm text-sapphire hover:underline">Refresh</button>
          </div>
          {insights.length === 0 ? (
            <div className="text-center py-16 bg-white rounded-xl border border-gray-200">
              <p className="text-gray-500 text-sm">No insights available. Insights are generated automatically as your modules accumulate data.</p>
            </div>
          ) : (
            <div className="space-y-3">
              {insights.map(insight => (
                <div key={insight.id} className={`rounded-xl border p-4 ${SEVERITY_COLORS[insight.severity] || SEVERITY_COLORS.Info}`}>
                  <div className="flex items-start justify-between gap-4">
                    <div className="flex-1">
                      <div className="flex items-center gap-2 mb-1">
                        <span className={`text-xs font-bold px-2 py-0.5 rounded-full ${SEVERITY_COLORS[insight.severity]}`}>{insight.severity}</span>
                        <span className="text-xs text-gray-500">{insight.module} — {insight.insightType}</span>
                        {insight.isAcknowledged && <span className="text-xs text-green-600">Acknowledged</span>}
                      </div>
                      <h3 className="text-sm font-semibold">{insight.title}</h3>
                      <p className="text-xs mt-1 opacity-80">{insight.summary}</p>
                      <p className="text-xs mt-1 text-gray-400">{new Date(insight.createdAtUtc).toLocaleDateString()}</p>
                    </div>
                    {!insight.isAcknowledged && (
                      <button
                        onClick={() => acknowledgeInsight(insight.id)}
                        className="text-xs px-3 py-1 border border-current rounded-lg whitespace-nowrap"
                      >
                        Acknowledge
                      </button>
                    )}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {/* Risk Scores */}
      {tab === 'risk' && (
        <div className="space-y-4">
          <div className="flex items-center justify-between">
            <div>
              <h2 className="text-lg font-semibold text-gray-900">Employee Risk Scores</h2>
              <p className="text-xs text-amber-600">Advisory only — all scores are heuristic estimates, not final assessments</p>
            </div>
            <button
              onClick={computeRisk}
              disabled={computing}
              className="px-4 py-2 bg-sapphire text-white text-sm rounded-lg disabled:opacity-50 hover:bg-sapphire/90"
            >
              {computing ? 'Computing...' : 'Recompute Scores'}
            </button>
          </div>

          {riskScores.length === 0 ? (
            <div className="text-center py-16 bg-white rounded-xl border border-gray-200">
              <p className="text-gray-500 text-sm mb-3">No risk scores yet.</p>
              <button onClick={computeRisk} className="px-4 py-2 bg-sapphire text-white text-sm rounded-lg">
                Compute Now
              </button>
            </div>
          ) : (
            <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
              <table className="w-full text-sm">
                <thead className="bg-gray-50 border-b border-gray-200">
                  <tr>
                    <th className="px-4 py-3 text-left text-xs font-semibold text-gray-500">Employee</th>
                    <th className="px-4 py-3 text-left text-xs font-semibold text-gray-500">Department</th>
                    <th className="px-4 py-3 text-center text-xs font-semibold text-gray-500">Churn Risk</th>
                    <th className="px-4 py-3 text-center text-xs font-semibold text-gray-500">Burnout Risk</th>
                    <th className="px-4 py-3 text-center text-xs font-semibold text-gray-500">Overall</th>
                    <th className="px-4 py-3 text-left text-xs font-semibold text-gray-500">Recommendation</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {riskScores.map(r => (
                    <tr key={r.id} className="hover:bg-gray-50">
                      <td className="px-4 py-3 font-medium text-gray-900">{r.employeeName}</td>
                      <td className="px-4 py-3 text-gray-500">{r.departmentName}</td>
                      <td className="px-4 py-3 text-center">
                        <div className="flex items-center justify-center gap-2">
                          <div className="w-16 bg-gray-200 rounded-full h-1.5">
                            <div className="bg-orange-500 h-1.5 rounded-full" style={{ width: `${r.churnRiskScore}%` }} />
                          </div>
                          <span className="text-xs text-gray-600">{r.churnRiskScore.toFixed(0)}%</span>
                        </div>
                      </td>
                      <td className="px-4 py-3 text-center">
                        <div className="flex items-center justify-center gap-2">
                          <div className="w-16 bg-gray-200 rounded-full h-1.5">
                            <div className="bg-red-500 h-1.5 rounded-full" style={{ width: `${r.burnoutRiskScore}%` }} />
                          </div>
                          <span className="text-xs text-gray-600">{r.burnoutRiskScore.toFixed(0)}%</span>
                        </div>
                      </td>
                      <td className="px-4 py-3 text-center">
                        <span className={`text-xs font-semibold px-2 py-1 rounded-full ${RISK_COLORS[r.overallRiskLevel]}`}>
                          {r.overallRiskLevel}
                        </span>
                      </td>
                      <td className="px-4 py-3 text-xs text-gray-500 max-w-xs truncate">{r.recommendations}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {/* Policy Documents */}
      {tab === 'policy-docs' && (
        <div className="space-y-4">
          <div>
            <h2 className="text-lg font-semibold text-gray-900">Policy Documents</h2>
            <p className="text-xs text-gray-500">Upload policy documents (PDF, DOCX, TXT) and ask questions grounded in their content.</p>
          </div>
          <PolicyDocumentManager />
        </div>
      )}

      {/* Query Log */}
      {tab === 'history' && (
        <div className="space-y-4">
          <h2 className="text-lg font-semibold text-gray-900">Assistant Query Log</h2>
          {history.length === 0 ? (
            <div className="text-center py-16 bg-white rounded-xl border border-gray-200">
              <p className="text-gray-500 text-sm">No queries logged yet. Use the assistant to get started.</p>
            </div>
          ) : (
            <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
              <table className="w-full text-sm">
                <thead className="bg-gray-50 border-b border-gray-200">
                  <tr>
                    <th className="px-4 py-3 text-left text-xs font-semibold text-gray-500">Time</th>
                    <th className="px-4 py-3 text-left text-xs font-semibold text-gray-500">Role</th>
                    <th className="px-4 py-3 text-left text-xs font-semibold text-gray-500">Query</th>
                    <th className="px-4 py-3 text-left text-xs font-semibold text-gray-500">Intent</th>
                    <th className="px-4 py-3 text-center text-xs font-semibold text-gray-500">Blocked</th>
                    <th className="px-4 py-3 text-center text-xs font-semibold text-gray-500">Tokens</th>
                    <th className="px-4 py-3 text-center text-xs font-semibold text-gray-500">ms</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {history.map(h => (
                    <tr key={h.id} className={`hover:bg-gray-50 ${h.wasBlocked ? 'bg-red-50' : ''}`}>
                      <td className="px-4 py-3 text-xs text-gray-500 whitespace-nowrap">
                        {new Date(h.createdAtUtc).toLocaleString([], { dateStyle: 'short', timeStyle: 'short' })}
                      </td>
                      <td className="px-4 py-3 text-xs">{h.userRole}</td>
                      <td className="px-4 py-3 text-xs text-gray-700 max-w-xs truncate">{h.query}</td>
                      <td className="px-4 py-3 text-xs text-sapphire">{h.intentClassified}</td>
                      <td className="px-4 py-3 text-center">
                        {h.wasBlocked ? (
                          <span className="text-xs text-red-600 font-semibold">Yes</span>
                        ) : (
                          <span className="text-xs text-green-600">No</span>
                        )}
                      </td>
                      <td className="px-4 py-3 text-center text-xs text-gray-500">{h.tokensUsed}</td>
                      <td className="px-4 py-3 text-center text-xs text-gray-500">{h.responseTimeMs}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
