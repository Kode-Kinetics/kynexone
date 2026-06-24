'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  Upload, FileText, Trash2, MessageSquareText, CheckCircle, XCircle, Clock, Send,
} from 'lucide-react';
import { policyDocumentsApi } from '../api/policyDocuments';
import type { PolicyDocument, PolicyAskResponse } from '../api/policyDocuments';

// ── Helpers ───────────────────────────────────────────────────────────────────

function fmtBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function StatusBadge({ status }: { status: PolicyDocument['status'] }) {
  if (status === 'Ready') {
    return (
      <span className="inline-flex items-center gap-1 rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-700 dark:bg-emerald-500/10 dark:text-emerald-400">
        <CheckCircle className="h-3 w-3" />
        Ready
      </span>
    );
  }
  if (status === 'Failed') {
    return (
      <span className="inline-flex items-center gap-1 rounded-full bg-rose-50 px-2 py-0.5 text-xs font-medium text-rose-700 dark:bg-rose-500/10 dark:text-rose-400">
        <XCircle className="h-3 w-3" />
        Failed
      </span>
    );
  }
  return (
    <span className="inline-flex items-center gap-1 rounded-full bg-amber-50 px-2 py-0.5 text-xs font-medium text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">
      <Clock className="h-3 w-3" />
      Processing
    </span>
  );
}

// ── Component ─────────────────────────────────────────────────────────────────

export function PolicyDocumentManager() {
  const [documents, setDocuments] = useState<PolicyDocument[]>([]);
  const [loading, setLoading] = useState(true);
  const [uploading, setUploading] = useState(false);
  const [uploadError, setUploadError] = useState('');
  const [dragOver, setDragOver] = useState(false);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const [question, setQuestion] = useState('');
  const [asking, setAsking] = useState(false);
  const [answer, setAnswer] = useState<PolicyAskResponse | null>(null);
  const [askError, setAskError] = useState('');

  const load = useCallback(async () => {
    try {
      const docs = await policyDocumentsApi.list();
      setDocuments(docs);
    } catch {
      // silently fail — list may be empty
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  const handleUpload = async (file: File) => {
    setUploadError('');

    // Validate type
    const allowed = ['application/pdf', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', 'text/plain'];
    if (!allowed.includes(file.type)) {
      setUploadError('Only PDF, DOCX, and TXT files are supported.');
      return;
    }

    // Validate size (20 MB)
    if (file.size > 20 * 1024 * 1024) {
      setUploadError('File size must be 20 MB or less.');
      return;
    }

    setUploading(true);
    try {
      const doc = await policyDocumentsApi.upload(file);
      setDocuments(prev => [doc, ...prev]);
    } catch {
      setUploadError('Upload failed. Please try again.');
    } finally {
      setUploading(false);
    }
  };

  const handleFileInput = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) handleUpload(file);
    e.target.value = '';
  };

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault();
    setDragOver(false);
    const file = e.dataTransfer.files?.[0];
    if (file) handleUpload(file);
  };

  const handleDelete = async (id: string, name: string) => {
    if (!confirm(`Delete "${name}"? This action cannot be undone.`)) return;
    try {
      await policyDocumentsApi.delete(id);
      setDocuments(prev => prev.filter(d => d.id !== id));
    } catch {
      alert('Failed to delete document.');
    }
  };

  const handleAsk = async () => {
    if (!question.trim() || asking) return;
    setAsking(true);
    setAskError('');
    setAnswer(null);
    try {
      const res = await policyDocumentsApi.ask(question.trim());
      setAnswer(res);
    } catch {
      setAskError('Failed to get an answer. Make sure at least one document is ready.');
    } finally {
      setAsking(false);
    }
  };

  return (
    <div className="space-y-6">
      {/* Upload zone */}
      <div
        className={`rounded-xl border-2 border-dashed p-8 text-center transition-colors ${
          dragOver
            ? 'border-sapphire bg-sapphire/5 dark:border-sapphire dark:bg-sapphire/10'
            : 'border-slate-200 hover:border-sapphire/50 dark:border-white/[0.12] dark:hover:border-sapphire/40'
        }`}
        onDragOver={e => { e.preventDefault(); setDragOver(true); }}
        onDragLeave={() => setDragOver(false)}
        onDrop={handleDrop}
      >
        <input
          ref={fileInputRef}
          type="file"
          accept=".pdf,.docx,.txt,application/pdf,application/vnd.openxmlformats-officedocument.wordprocessingml.document,text/plain"
          className="hidden"
          onChange={handleFileInput}
        />
        <div className="flex flex-col items-center gap-3">
          <div className="flex h-12 w-12 items-center justify-center rounded-full bg-sapphire/10 text-sapphire dark:bg-sapphire/20">
            <Upload className="h-6 w-6" />
          </div>
          <div>
            <p className="text-sm font-semibold text-slate-800 dark:text-white">
              Drop a policy document here
            </p>
            <p className="mt-0.5 text-xs text-slate-500 dark:text-slate-400">
              PDF, DOCX, or TXT — max 20 MB
            </p>
          </div>
          <button
            type="button"
            disabled={uploading}
            onClick={() => fileInputRef.current?.click()}
            className="inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white hover:bg-sapphire/90 disabled:opacity-50"
          >
            <Upload className="h-3.5 w-3.5" />
            {uploading ? 'Uploading…' : 'Browse files'}
          </button>
        </div>
        {uploadError && (
          <p className="mt-3 text-xs text-rose-600 dark:text-rose-400">{uploadError}</p>
        )}
      </div>

      {/* Document list */}
      <div className="rounded-xl border border-slate-200 bg-white dark:border-white/[0.08] dark:bg-white/[0.02]">
        <div className="flex items-center justify-between border-b border-slate-100 px-5 py-3 dark:border-white/[0.07]">
          <p className="text-sm font-semibold text-slate-800 dark:text-white">
            Uploaded Documents
          </p>
          <button
            type="button"
            onClick={load}
            className="text-xs text-sapphire hover:underline dark:text-cyan-400"
          >
            Refresh
          </button>
        </div>

        {loading ? (
          <div className="py-12 text-center text-sm text-slate-400">Loading…</div>
        ) : documents.length === 0 ? (
          <div className="flex flex-col items-center gap-3 py-14 text-center">
            <FileText className="h-10 w-10 text-slate-200 dark:text-slate-700" />
            <p className="text-sm font-medium text-slate-600 dark:text-slate-400">
              No policy documents yet
            </p>
            <p className="text-xs text-slate-400 dark:text-slate-500">
              Upload a PDF, DOCX, or TXT file above to enable policy search.
            </p>
          </div>
        ) : (
          <div className="divide-y divide-slate-100 dark:divide-white/[0.06]">
            {documents.map(doc => (
              <div key={doc.id} className="flex items-center gap-4 px-5 py-4">
                <FileText className="h-5 w-5 shrink-0 text-sapphire dark:text-cyan-400" />
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium text-slate-800 dark:text-slate-200">
                    {doc.originalName}
                  </p>
                  <div className="mt-0.5 flex items-center gap-2">
                    <span className="text-xs text-slate-400">{fmtBytes(doc.fileSizeBytes)}</span>
                    {doc.status === 'Ready' && doc.chunkCount > 0 && (
                      <span className="text-xs text-slate-400">{doc.chunkCount} chunks</span>
                    )}
                    {doc.errorMessage && (
                      <span className="truncate text-xs text-rose-500">{doc.errorMessage}</span>
                    )}
                  </div>
                </div>
                <StatusBadge status={doc.status} />
                <button
                  type="button"
                  onClick={() => handleDelete(doc.id, doc.originalName)}
                  className="grid h-7 w-7 place-items-center rounded-lg text-slate-400 hover:bg-rose-50 hover:text-rose-500 dark:hover:bg-rose-500/10 dark:hover:text-rose-400"
                  aria-label={`Delete ${doc.originalName}`}
                >
                  <Trash2 className="h-4 w-4" />
                </button>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Policy Assistant */}
      <div className="rounded-xl border border-slate-200 bg-white dark:border-white/[0.08] dark:bg-white/[0.02]">
        <div className="flex items-center gap-2 border-b border-slate-100 px-5 py-3 dark:border-white/[0.07]">
          <MessageSquareText className="h-4 w-4 text-sapphire dark:text-cyan-400" />
          <p className="text-sm font-semibold text-slate-800 dark:text-white">Policy Assistant</p>
          <span className="ml-auto rounded-full bg-amber-50 px-2 py-0.5 text-xs font-medium text-amber-700 dark:bg-amber-500/10 dark:text-amber-400">
            Advisory
          </span>
        </div>

        <div className="p-5 space-y-4">
          <div className="flex gap-2">
            <input
              value={question}
              onChange={e => setQuestion(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); handleAsk(); } }}
              placeholder="Ask a question about your uploaded policies…"
              disabled={asking}
              className="flex-1 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-sapphire focus:outline-none dark:border-white/10 dark:bg-white/5 dark:text-white dark:placeholder:text-slate-500"
            />
            <button
              type="button"
              onClick={handleAsk}
              disabled={asking || !question.trim()}
              className="inline-flex items-center gap-1.5 rounded-lg bg-sapphire px-4 py-2 text-sm font-medium text-white hover:bg-sapphire/90 disabled:opacity-50"
            >
              <Send className="h-3.5 w-3.5" />
              {asking ? 'Asking…' : 'Ask'}
            </button>
          </div>

          {askError && (
            <p className="text-xs text-rose-600 dark:text-rose-400">{askError}</p>
          )}

          {answer && (
            <div className="rounded-lg border border-sapphire/20 bg-sapphire/5 p-4 dark:border-sapphire/30 dark:bg-sapphire/10 space-y-3">
              <div className="flex items-center gap-2">
                <MessageSquareText className="h-4 w-4 text-sapphire dark:text-cyan-400" />
                <span className="text-xs font-semibold text-sapphire dark:text-cyan-400">Assistant</span>
                <span className="ml-auto rounded-full bg-amber-100 px-2 py-0.5 text-[10px] font-medium text-amber-700 dark:bg-amber-500/20 dark:text-amber-400">
                  Advisory Only
                </span>
                {!answer.isGrounded && (
                  <span className="rounded-full bg-rose-50 px-2 py-0.5 text-[10px] font-medium text-rose-600 dark:bg-rose-500/10 dark:text-rose-400">
                    Low confidence
                  </span>
                )}
              </div>
              <p className="text-sm text-slate-800 dark:text-slate-200 whitespace-pre-wrap">
                {answer.answer}
              </p>
              {answer.sources.length > 0 && (
                <div>
                  <p className="text-xs font-semibold text-slate-500 dark:text-slate-400 mb-1">Sources:</p>
                  <div className="flex flex-wrap gap-1.5">
                    {answer.sources.map((src, i) => (
                      <span key={i} className="inline-flex items-center gap-1 rounded bg-white px-2 py-0.5 text-xs text-slate-600 border border-slate-200 dark:bg-white/5 dark:border-white/10 dark:text-slate-300">
                        <FileText className="h-3 w-3" />
                        {src}
                      </span>
                    ))}
                  </div>
                </div>
              )}
            </div>
          )}

          <p className="text-xs text-amber-600 dark:text-amber-400">
            Answers are advisory only. Always verify important policy information with HR.
          </p>
        </div>
      </div>
    </div>
  );
}
