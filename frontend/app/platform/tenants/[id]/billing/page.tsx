'use client';

import { useState, useEffect, useCallback } from 'react';
import { useParams, useRouter } from 'next/navigation';
import Link from 'next/link';
import {
  ArrowLeft, Plus, Download, Send, RefreshCw, X,
  Pencil, Trash2, CheckCircle, AlertCircle, Clock, Ban,
} from 'lucide-react';
import { platformApi, type TenantInvoice, type TenantInvoiceLine, type TenantPayment } from '@/src/api/platform';

// ── Helpers ───────────────────────────────────────────────────────────────────

const TODAY = () => new Date().toISOString().slice(0, 10);
const DUE30 = () => new Date(Date.now() + 30 * 86400000).toISOString().slice(0, 10);
const YM    = () => new Date().toISOString().slice(0, 7).replace('-', '');

const STATUSES = ['Draft', 'Sent', 'Paid', 'Overdue', 'Cancelled'] as const;
type Status = typeof STATUSES[number] | 'PartiallyPaid';

const STATUS_BADGE: Record<string, string> = {
  Draft:         'text-slate-400 bg-slate-700/50 border-slate-600',
  Sent:          'text-blue-400 bg-blue-900/30 border-blue-700/30',
  Paid:          'text-emerald-400 bg-emerald-900/30 border-emerald-700/30',
  PartiallyPaid: 'text-amber-400 bg-amber-900/30 border-amber-700/30',
  Overdue:       'text-rose-400 bg-rose-900/30 border-rose-700/30',
  Cancelled:     'text-slate-600 bg-transparent border-slate-800',
};

const STATUS_ICON: Record<string, React.ElementType> = {
  Draft:         Clock,
  Sent:          Send,
  Paid:          CheckCircle,
  PartiallyPaid: Clock,
  Overdue:       AlertCircle,
  Cancelled:     Ban,
};

const CURRENCIES = ['USD', 'SAR', 'AED', 'EUR', 'GBP', 'QAR', 'KWD', 'BHD', 'OMR'];
const PAYMENT_METHODS = ['Bank Transfer', 'Credit Card', 'Cheque', 'Online', 'Cash', 'Other'];

function fmtDate(d: string) {
  return new Date(d).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
}

function fmtAmount(amount: number, currency: string) {
  return `${currency} ${amount.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

// ── Form primitives ───────────────────────────────────────────────────────────

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <label className="block text-xs text-slate-400 mb-1">{label}</label>
      {children}
    </div>
  );
}

function FInput({ value, onChange, type = 'text', required, placeholder, min }: {
  value: string | number; onChange: (v: string) => void;
  type?: string; required?: boolean; placeholder?: string; min?: string;
}) {
  return (
    <input
      type={type} value={value} onChange={e => onChange(e.target.value)}
      required={required} placeholder={placeholder} min={min}
      className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white placeholder-slate-600 focus:outline-none focus:border-blue-500/60 transition-colors"
    />
  );
}

function FSelect({ value, onChange, options, placeholder, label }: {
  value: string; onChange: (v: string) => void;
  options: readonly string[]; placeholder?: string; label?: string;
}) {
  return (
    <select
      value={value} onChange={e => onChange(e.target.value)}
      aria-label={label ?? placeholder ?? 'Select option'}
      title={label ?? placeholder ?? 'Select option'}
      className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:border-blue-500/60 transition-colors"
    >
      {placeholder && <option value="">{placeholder}</option>}
      {options.map(o => <option key={o} value={o}>{o}</option>)}
    </select>
  );
}

// ── Create Invoice Modal ──────────────────────────────────────────────────────

interface CreateForm {
  invoiceNumber: string;
  amount: string;
  currencyCode: string;
  status: string;
  periodDescription: string;
  invoiceDate: string;
  dueDate: string;
  recipientEmail: string;
  notes: string;
}

function CreateModal({ tenantId, onClose, onCreated }: {
  tenantId: string; onClose: () => void; onCreated: () => void;
}) {
  const [form, setForm] = useState<CreateForm>({
    invoiceNumber: '',
    amount: '',
    currencyCode: 'USD',
    status: 'Draft',
    periodDescription: '',
    invoiceDate: TODAY(),
    dueDate: DUE30(),
    recipientEmail: '',
    notes: '',
  });
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState('');

  const set = (k: keyof CreateForm) => (v: string) => setForm(f => ({ ...f, [k]: v }));

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const amount = parseFloat(form.amount);
    if (isNaN(amount) || amount <= 0) { setErr('Amount must be a positive number.'); return; }
    setSaving(true); setErr('');
    try {
      await platformApi.createInvoice(tenantId, {
        invoiceNumber: form.invoiceNumber.trim() || undefined,
        amount,
        currencyCode: form.currencyCode,
        status: form.status,
        periodDescription: form.periodDescription.trim() || undefined,
        invoiceDate: form.invoiceDate,
        dueDate: form.dueDate,
        recipientEmail: form.recipientEmail.trim() || undefined,
        notes: form.notes.trim() || undefined,
      });
      onCreated(); onClose();
    } catch (ex: unknown) {
      setErr((ex as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Failed to create invoice.');
    } finally { setSaving(false); }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-lg bg-[#0d1117] border border-white/10 rounded-2xl shadow-2xl overflow-hidden">
        <div className="flex items-center justify-between px-6 py-4 border-b border-white/[0.07]">
          <div>
            <h2 className="text-sm font-semibold text-white">New Invoice</h2>
            <p className="text-[11px] text-slate-500 mt-0.5">Fill in the details below to create an invoice</p>
          </div>
          <button type="button" onClick={onClose} aria-label="Close" title="Close" className="text-slate-500 hover:text-white transition-colors">
            <X className="h-4 w-4" />
          </button>
        </div>

        <form onSubmit={submit} className="px-6 py-5 space-y-4 max-h-[80vh] overflow-y-auto">
          {err && (
            <div className="text-xs text-rose-400 bg-rose-500/10 border border-rose-500/20 rounded-lg px-3 py-2">{err}</div>
          )}

          <div className="grid grid-cols-2 gap-3">
            <div className="col-span-2">
              <Field label="Invoice Number">
                <FInput value={form.invoiceNumber} onChange={set('invoiceNumber')} placeholder="Auto-generated (INV-YYYY-NNNN)" />
              </Field>
            </div>

            <Field label="Amount *">
              <input
                type="text"
                inputMode="decimal"
                value={form.amount}
                onChange={e => set('amount')(e.target.value)}
                required
                placeholder="0.00"
                className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white placeholder-slate-600 focus:outline-none focus:border-blue-500/60 transition-colors"
              />
            </Field>

            <Field label="Currency">
              <FSelect value={form.currencyCode} onChange={set('currencyCode')} options={CURRENCIES} label="Currency" />
            </Field>

            <Field label="Invoice Date *">
              <FInput value={form.invoiceDate} onChange={set('invoiceDate')} type="date" required />
            </Field>

            <Field label="Due Date *">
              <FInput value={form.dueDate} onChange={set('dueDate')} type="date" required />
            </Field>

            <div className="col-span-2">
              <Field label="Recipient Email">
                <FInput value={form.recipientEmail} onChange={set('recipientEmail')} type="email" placeholder="billing@client.com" />
              </Field>
            </div>

            <div className="col-span-2">
              <Field label="Period / Description">
                <FInput value={form.periodDescription} onChange={set('periodDescription')} placeholder="e.g. June 2026 — Monthly Subscription" />
              </Field>
            </div>

            <div className="col-span-2">
              <Field label="Initial Status">
                <FSelect value={form.status} onChange={set('status')} options={STATUSES} label="Initial Status" />
              </Field>
            </div>

            <div className="col-span-2">
              <Field label="Internal Notes">
                <textarea
                  value={form.notes} onChange={e => set('notes')(e.target.value)} rows={2}
                  placeholder="Internal notes visible to platform admins only…"
                  className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white placeholder-slate-600 focus:outline-none focus:border-blue-500/60 transition-colors resize-none"
                />
              </Field>
            </div>
          </div>

          <div className="flex gap-3 pt-1 border-t border-white/[0.07]">
            <button type="button" onClick={onClose}
              className="flex-1 border border-white/10 text-slate-400 hover:text-white rounded-lg py-2 text-sm transition-colors">
              Cancel
            </button>
            <button type="submit" disabled={saving}
              className="flex-1 bg-blue-600 hover:bg-blue-500 text-white rounded-lg py-2 text-sm font-semibold transition-colors disabled:opacity-40">
              {saving ? 'Creating…' : 'Create Invoice'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

// ── Edit Invoice Modal ────────────────────────────────────────────────────────

interface EditForm {
  status: Status;
  paymentMethod: string;
  paymentReference: string;
  paidDate: string;
  recipientEmail: string;
  notes: string;
}

function EditModal({ tenantId, invoice, onClose, onSaved }: {
  tenantId: string; invoice: TenantInvoice; onClose: () => void; onSaved: () => void;
}) {
  const [form, setForm] = useState<EditForm>({
    status: invoice.status as Status,
    paymentMethod: invoice.paymentMethod ?? '',
    paymentReference: invoice.paymentReference ?? '',
    paidDate: invoice.paidDate ? invoice.paidDate.slice(0, 10) : '',
    recipientEmail: invoice.recipientEmail ?? '',
    notes: invoice.notes ?? '',
  });
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState('');

  const set = (k: keyof EditForm) => (v: string) => setForm(f => ({ ...f, [k]: v }));
  const showPayment = form.status === 'Paid' || !!form.paymentMethod;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setSaving(true); setErr('');
    try {
      await platformApi.updateInvoice(tenantId, invoice.id, {
        status: form.status,
        paymentMethod: form.paymentMethod.trim() || undefined,
        paymentReference: form.paymentReference.trim() || undefined,
        paidDate: form.paidDate || undefined,
        recipientEmail: form.recipientEmail.trim() || undefined,
        notes: form.notes.trim() || undefined,
      });
      onSaved(); onClose();
    } catch (ex: unknown) {
      setErr((ex as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Failed to save.');
    } finally { setSaving(false); }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-lg bg-[#0d1117] border border-white/10 rounded-2xl shadow-2xl overflow-hidden">
        <div className="flex items-center justify-between px-6 py-4 border-b border-white/[0.07]">
          <div>
            <h2 className="text-sm font-semibold text-white">Edit Invoice</h2>
            <p className="text-[11px] text-slate-500 mt-0.5 font-mono">{invoice.invoiceNumber}</p>
          </div>
          <button type="button" onClick={onClose} aria-label="Close" title="Close" className="text-slate-500 hover:text-white transition-colors">
            <X className="h-4 w-4" />
          </button>
        </div>

        <form onSubmit={submit} className="px-6 py-5 space-y-4 max-h-[80vh] overflow-y-auto">
          {/* Read-only invoice summary */}
          <div className="grid grid-cols-2 gap-3 bg-white/[0.02] border border-white/[0.06] rounded-xl p-4 text-sm">
            <div>
              <p className="text-[10px] text-slate-600 uppercase tracking-widest mb-1">Amount</p>
              <p className="text-white font-semibold">{fmtAmount(invoice.amount, invoice.currencyCode)}</p>
            </div>
            <div>
              <p className="text-[10px] text-slate-600 uppercase tracking-widest mb-1">Invoice Date</p>
              <p className="text-slate-300">{fmtDate(invoice.invoiceDate)}</p>
            </div>
            <div>
              <p className="text-[10px] text-slate-600 uppercase tracking-widest mb-1">Due Date</p>
              <p className="text-slate-300">{fmtDate(invoice.dueDate)}</p>
            </div>
            {invoice.periodDescription && (
              <div>
                <p className="text-[10px] text-slate-600 uppercase tracking-widest mb-1">Period</p>
                <p className="text-slate-300 text-xs">{invoice.periodDescription}</p>
              </div>
            )}
          </div>

          {err && (
            <div className="text-xs text-rose-400 bg-rose-500/10 border border-rose-500/20 rounded-lg px-3 py-2">{err}</div>
          )}

          <div className="grid grid-cols-2 gap-3">
            <div className="col-span-2">
              <Field label="Status">
                <FSelect value={form.status} onChange={v => set('status')(v as Status)} options={STATUSES} label="Status" />
              </Field>
            </div>

            {showPayment && (
              <>
                <Field label="Payment Method">
                  <FSelect value={form.paymentMethod} onChange={set('paymentMethod')} options={PAYMENT_METHODS} placeholder="— select method —" label="Payment Method" />
                </Field>

                <Field label="Payment Reference">
                  <FInput value={form.paymentReference} onChange={set('paymentReference')} placeholder="TXN-12345 / cheque #" />
                </Field>

                <div className="col-span-2">
                  <Field label="Date Paid">
                    <FInput value={form.paidDate} onChange={set('paidDate')} type="date" />
                  </Field>
                </div>
              </>
            )}

            <div className="col-span-2">
              <Field label="Recipient Email">
                <FInput value={form.recipientEmail} onChange={set('recipientEmail')} type="email" placeholder="billing@client.com" />
              </Field>
            </div>

            <div className="col-span-2">
              <Field label="Internal Notes">
                <textarea
                  value={form.notes} onChange={e => set('notes')(e.target.value)} rows={3}
                  placeholder="Internal notes…"
                  className="w-full bg-white/[0.04] border border-white/[0.08] rounded-lg px-3 py-2 text-sm text-white placeholder-slate-600 focus:outline-none focus:border-blue-500/60 transition-colors resize-none"
                />
              </Field>
            </div>
          </div>

          <div className="flex gap-3 pt-1 border-t border-white/[0.07]">
            <button type="button" onClick={onClose}
              className="flex-1 border border-white/10 text-slate-400 hover:text-white rounded-lg py-2 text-sm transition-colors">
              Cancel
            </button>
            <button type="submit" disabled={saving}
              className="flex-1 bg-blue-600 hover:bg-blue-500 text-white rounded-lg py-2 text-sm font-semibold transition-colors disabled:opacity-40">
              {saving ? 'Saving…' : 'Save Changes'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

// ── Delete Confirm ────────────────────────────────────────────────────────────

function DeleteDialog({ invoice, onClose, onDeleted }: {
  invoice: TenantInvoice; onClose: () => void; onDeleted: () => void;
}) {
  const [deleting, setDeleting] = useState(false);
  const [err, setErr]           = useState('');

  async function confirm() {
    setDeleting(true); setErr('');
    try {
      await platformApi.deleteInvoice(invoice.tenantId, invoice.id);
      onDeleted(); onClose();
    } catch {
      setErr('Failed to delete. The invoice may have already been removed.');
      setDeleting(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-black/70 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-sm bg-[#0d1117] border border-white/10 rounded-2xl shadow-2xl p-6 space-y-4">
        <div className="flex items-center gap-3">
          <div className="h-9 w-9 rounded-xl bg-rose-500/10 border border-rose-500/20 flex items-center justify-center shrink-0">
            <Trash2 className="h-4 w-4 text-rose-400" />
          </div>
          <div>
            <h3 className="text-sm font-semibold text-white">Delete Invoice</h3>
            <p className="text-xs text-slate-500 mt-0.5 font-mono">{invoice.invoiceNumber}</p>
          </div>
        </div>
        <p className="text-sm text-slate-400">
          Permanently delete{' '}
          <span className="text-white font-mono">{invoice.invoiceNumber}</span> for{' '}
          <span className="text-white">{fmtAmount(invoice.amount, invoice.currencyCode)}</span>?
          This cannot be undone.
        </p>
        {err && <p className="text-xs text-rose-400">{err}</p>}
        <div className="flex gap-3">
          <button type="button" onClick={onClose}
            className="flex-1 border border-white/10 text-slate-400 hover:text-white rounded-lg py-2 text-sm transition-colors">
            Cancel
          </button>
          <button type="button" onClick={confirm} disabled={deleting}
            className="flex-1 bg-rose-600 hover:bg-rose-500 text-white rounded-lg py-2 text-sm font-semibold transition-colors disabled:opacity-40">
            {deleting ? 'Deleting…' : 'Delete'}
          </button>
        </div>
      </div>
    </div>
  );
}

// ── Line Form (add / edit) ────────────────────────────────────────────────────

interface LineFormState {
  description: string;
  quantity: string;
  unitPrice: string;
  taxRate: string;
}

const BLANK_LINE: LineFormState = { description: '', quantity: '1', unitPrice: '', taxRate: '0' };

function LineForm({ initial, saving, err, onSave, onCancel }: {
  initial?: LineFormState;
  saving: boolean;
  err: string;
  onSave: (f: LineFormState) => void;
  onCancel: () => void;
}) {
  const [f, setF] = useState<LineFormState>(initial ?? BLANK_LINE);
  const set = (k: keyof LineFormState) => (v: string) => setF(prev => ({ ...prev, [k]: v }));

  function submit(e: React.FormEvent) {
    e.preventDefault();
    onSave(f);
  }

  return (
    <form onSubmit={submit} className="mt-2 bg-white/[0.03] border border-white/[0.07] rounded-lg p-3 space-y-2">
      {err && <p className="text-xs text-rose-400">{err}</p>}
      <div className="grid grid-cols-2 gap-2">
        <div className="col-span-2">
          <Field label="Description *">
            <FInput value={f.description} onChange={set('description')} required placeholder="e.g. Monthly subscription" />
          </Field>
        </div>
        <Field label="Quantity *">
          <FInput value={f.quantity} onChange={set('quantity')} type="number" required min="0" placeholder="1" />
        </Field>
        <Field label="Unit Price *">
          <FInput value={f.unitPrice} onChange={set('unitPrice')} type="number" required min="0" placeholder="0.00" />
        </Field>
        <div className="col-span-2">
          <Field label="Tax %">
            <FInput value={f.taxRate} onChange={set('taxRate')} type="number" min="0" placeholder="0" />
          </Field>
        </div>
      </div>
      <div className="flex gap-2 pt-1">
        <button type="button" onClick={onCancel}
          className="border border-white/10 text-slate-400 hover:text-white rounded-lg px-3 py-1.5 text-xs transition-colors">
          Cancel
        </button>
        <button type="submit" disabled={saving}
          className="bg-blue-600 hover:bg-blue-500 text-white rounded-lg px-3 py-1.5 text-xs font-semibold transition-colors disabled:opacity-40">
          {saving ? 'Saving…' : 'Save Line'}
        </button>
      </div>
    </form>
  );
}

// ── Payment Form ──────────────────────────────────────────────────────────────

interface PaymentFormState {
  amount: string;
  method: string;
  reference: string;
  paidAt: string;
}

const PAYMENT_METHOD_OPTIONS = ['BankTransfer', 'Cash', 'Card', 'Cheque', 'Online'] as const;

function PaymentForm({ saving, err, onSave, onCancel }: {
  saving: boolean;
  err: string;
  onSave: (f: PaymentFormState) => void;
  onCancel: () => void;
}) {
  const [f, setF] = useState<PaymentFormState>({ amount: '', method: 'BankTransfer', reference: '', paidAt: TODAY() });
  const set = (k: keyof PaymentFormState) => (v: string) => setF(prev => ({ ...prev, [k]: v }));

  function submit(e: React.FormEvent) {
    e.preventDefault();
    onSave(f);
  }

  return (
    <form onSubmit={submit} className="mt-2 bg-white/[0.03] border border-white/[0.07] rounded-lg p-3 space-y-2">
      {err && <p className="text-xs text-rose-400">{err}</p>}
      <div className="grid grid-cols-2 gap-2">
        <Field label="Amount *">
          <FInput value={f.amount} onChange={set('amount')} type="number" required min="0" placeholder="0.00" />
        </Field>
        <Field label="Method *">
          <FSelect value={f.method} onChange={set('method')} options={PAYMENT_METHOD_OPTIONS} label="Payment Method" />
        </Field>
        <Field label="Transaction Ref">
          <FInput value={f.reference} onChange={set('reference')} placeholder="TXN-12345 (optional)" />
        </Field>
        <Field label="Paid Date *">
          <FInput value={f.paidAt} onChange={set('paidAt')} type="date" required />
        </Field>
      </div>
      <div className="flex gap-2 pt-1">
        <button type="button" onClick={onCancel}
          className="border border-white/10 text-slate-400 hover:text-white rounded-lg px-3 py-1.5 text-xs transition-colors">
          Cancel
        </button>
        <button type="submit" disabled={saving}
          className="bg-emerald-700 hover:bg-emerald-600 text-white rounded-lg px-3 py-1.5 text-xs font-semibold transition-colors disabled:opacity-40">
          {saving ? 'Recording…' : 'Record Payment'}
        </button>
      </div>
    </form>
  );
}

// ── Invoice Lines & Payments Panel ───────────────────────────────────────────

type LineEditMode = { kind: 'add' } | { kind: 'edit'; line: TenantInvoiceLine };

function InvoiceDetailPanel({ tenantId, invoice, currency }: {
  tenantId: string; invoice: TenantInvoice; currency: string;
}) {
  const [lines, setLines]       = useState<TenantInvoiceLine[] | null>(null);
  const [payments, setPayments] = useState<TenantPayment[] | null>(null);
  const [loading, setLoading]   = useState(true);

  // Line item form state
  const [lineMode, setLineMode]   = useState<LineEditMode | null>(null);
  const [lineSaving, setLineSaving] = useState(false);
  const [lineErr, setLineErr]     = useState('');

  // Payment form state
  const [showPayForm, setShowPayForm]   = useState(false);
  const [paySaving, setPaySaving]       = useState(false);
  const [payErr, setPayErr]             = useState('');

  const isDraft = invoice.status === 'Draft';
  const canPay  = invoice.status !== 'Paid';

  function loadLines() {
    return platformApi.listInvoiceLines(tenantId, invoice.id).then(setLines).catch(() => setLines([]));
  }

  function loadPayments() {
    return platformApi.listInvoicePayments(tenantId, invoice.id).then(setPayments).catch(() => setPayments([]));
  }

  useEffect(() => {
    setLoading(true);
    Promise.all([loadLines(), loadPayments()]).finally(() => setLoading(false));
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tenantId, invoice.id]);

  async function handleSaveLine(f: LineFormState) {
    const qty  = parseFloat(f.quantity);
    const price = parseFloat(f.unitPrice);
    const tax  = parseFloat(f.taxRate || '0');
    if (!f.description.trim()) { setLineErr('Description is required.'); return; }
    if (isNaN(qty) || qty <= 0) { setLineErr('Quantity must be positive.'); return; }
    if (isNaN(price) || price < 0) { setLineErr('Unit price must be 0 or greater.'); return; }
    setLineSaving(true); setLineErr('');
    try {
      if (lineMode?.kind === 'edit') {
        await platformApi.updateInvoiceLine(tenantId, invoice.id, lineMode.line.id, {
          description: f.description.trim(),
          quantity: qty,
          unitPrice: price,
          taxRate: isNaN(tax) ? 0 : tax,
        });
      } else {
        await platformApi.addInvoiceLine(tenantId, invoice.id, {
          description: f.description.trim(),
          quantity: qty,
          unitPrice: price,
          taxRate: isNaN(tax) ? 0 : tax,
        });
      }
      setLineMode(null);
      await loadLines();
    } catch (ex: unknown) {
      setLineErr((ex as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Failed to save line.');
    } finally {
      setLineSaving(false);
    }
  }

  async function handleDeleteLine(line: TenantInvoiceLine) {
    if (!window.confirm(`Delete line "${line.description}"?`)) return;
    try {
      await platformApi.deleteInvoiceLine(tenantId, invoice.id, line.id);
      await loadLines();
    } catch {
      // silently ignore — user can retry
    }
  }

  async function handleSavePayment(f: PaymentFormState) {
    const amt = parseFloat(f.amount);
    if (isNaN(amt) || amt <= 0) { setPayErr('Amount must be a positive number.'); return; }
    if (!f.method) { setPayErr('Method is required.'); return; }
    setPaySaving(true); setPayErr('');
    try {
      await platformApi.createPayment(tenantId, invoice.id, {
        amount: amt,
        currencyCode: currency,
        method: f.method,
        reference: f.reference.trim() || undefined,
        paidAt: f.paidAt || undefined,
        status: 'Completed',
      });
      setShowPayForm(false);
      await loadPayments();
    } catch (ex: unknown) {
      setPayErr((ex as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Failed to record payment.');
    } finally {
      setPaySaving(false);
    }
  }

  const totalPaid  = (payments ?? []).filter(p => p.status === 'Completed').reduce((s, p) => s + p.amount, 0);
  const balanceDue = invoice.amount - totalPaid;

  if (loading) return (
    <tr><td colSpan={8} className="px-8 py-4">
      <div className="h-3 w-3 animate-spin rounded-full border border-blue-500 border-t-transparent" />
    </td></tr>
  );

  return (
    <tr className="bg-[#0d1117] border-b border-white/[0.04]">
      <td colSpan={8} className="px-6 pb-4 pt-2">
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">

          {/* Line Items */}
          <div>
            <div className="flex items-center justify-between mb-2">
              <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest">Line Items</p>
              {isDraft && lineMode === null && (
                <button type="button" onClick={() => { setLineErr(''); setLineMode({ kind: 'add' }); }}
                  className="flex items-center gap-1 text-[10px] text-blue-400 border border-blue-500/20 hover:border-blue-500/50 px-2 py-0.5 rounded transition-colors">
                  <Plus className="h-2.5 w-2.5" /> Add Line
                </button>
              )}
            </div>

            {lines && lines.length > 0 ? (
              <table className="w-full text-xs">
                <thead>
                  <tr className="text-[10px] text-slate-600 uppercase">
                    <th className="text-left pb-1 font-medium">Description</th>
                    <th className="text-right pb-1 font-medium pr-2">Qty</th>
                    <th className="text-right pb-1 font-medium pr-2">Unit</th>
                    <th className="text-right pb-1 font-medium pr-2">Disc</th>
                    <th className="text-right pb-1 font-medium">Total</th>
                    {isDraft && <th className="pb-1 w-12" aria-label="Actions" />}
                  </tr>
                </thead>
                <tbody className="divide-y divide-white/[0.04]">
                  {lines.map(l => (
                    <tr key={l.id}>
                      <td className="py-1 text-slate-300">{l.description}</td>
                      <td className="py-1 text-right pr-2 text-slate-500 tabular-nums">{l.quantity}</td>
                      <td className="py-1 text-right pr-2 text-slate-400 tabular-nums">{fmtAmount(l.unitPrice, currency)}</td>
                      <td className="py-1 text-right pr-2 text-slate-600 tabular-nums">{l.discountAmount > 0 ? `-${fmtAmount(l.discountAmount, currency)}` : '—'}</td>
                      <td className="py-1 text-right text-white font-medium tabular-nums">{fmtAmount(l.lineTotal, currency)}</td>
                      {isDraft && (
                        <td className="py-1 pl-2">
                          <div className="flex items-center gap-1">
                            <button type="button" title="Edit line"
                              onClick={() => {
                                setLineErr('');
                                setLineMode({
                                  kind: 'edit',
                                  line: l,
                                });
                              }}
                              className="h-5 w-5 flex items-center justify-center text-slate-600 hover:text-blue-400 transition-colors">
                              <Pencil className="h-2.5 w-2.5" />
                            </button>
                            <button type="button" title="Delete line"
                              onClick={() => handleDeleteLine(l)}
                              className="h-5 w-5 flex items-center justify-center text-slate-600 hover:text-rose-400 transition-colors">
                              <Trash2 className="h-2.5 w-2.5" />
                            </button>
                          </div>
                        </td>
                      )}
                    </tr>
                  ))}
                </tbody>
                <tfoot className="border-t border-white/[0.06]">
                  <tr>
                    <td colSpan={isDraft ? 5 : 4} className="pt-1.5 text-right text-slate-500 pr-2">Total</td>
                    <td className="pt-1.5 text-right text-emerald-400 font-bold tabular-nums">{fmtAmount(invoice.amount, currency)}</td>
                    {isDraft && <td />}
                  </tr>
                </tfoot>
              </table>
            ) : (
              <p className="text-xs text-slate-700 italic">No line items — legacy flat invoice.</p>
            )}

            {/* Line item inline form */}
            {isDraft && lineMode !== null && (
              <LineForm
                initial={lineMode.kind === 'edit'
                  ? {
                      description: lineMode.line.description,
                      quantity: String(lineMode.line.quantity),
                      unitPrice: String(lineMode.line.unitPrice),
                      taxRate: String(lineMode.line.taxRate),
                    }
                  : undefined
                }
                saving={lineSaving}
                err={lineErr}
                onSave={handleSaveLine}
                onCancel={() => { setLineMode(null); setLineErr(''); }}
              />
            )}
          </div>

          {/* Payment History */}
          <div>
            <div className="flex items-center justify-between mb-2">
              <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest">Payment History</p>
              {canPay && !showPayForm && (
                <button type="button" onClick={() => { setPayErr(''); setShowPayForm(true); }}
                  className="flex items-center gap-1 text-[10px] text-emerald-400 border border-emerald-500/20 hover:border-emerald-500/50 px-2 py-0.5 rounded transition-colors">
                  <Plus className="h-2.5 w-2.5" /> Record Payment
                </button>
              )}
            </div>

            {payments && payments.length > 0 ? (
              <>
                <table className="w-full text-xs mb-2">
                  <thead>
                    <tr className="text-[10px] text-slate-600 uppercase">
                      <th className="text-left pb-1 font-medium">Date</th>
                      <th className="text-left pb-1 font-medium">Method</th>
                      <th className="text-left pb-1 font-medium">Status</th>
                      <th className="text-right pb-1 font-medium">Amount</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-white/[0.04]">
                    {payments.map(p => (
                      <tr key={p.id}>
                        <td className="py-1 text-slate-500">{p.paidAt ? fmtDate(p.paidAt) : '—'}</td>
                        <td className="py-1 text-slate-400">{p.method}</td>
                        <td className="py-1">
                          <span className={`text-[10px] font-semibold px-1.5 py-0.5 rounded border ${
                            p.status === 'Completed' ? 'text-emerald-400 border-emerald-700/30 bg-emerald-900/20' :
                            p.status === 'Failed'    ? 'text-rose-400 border-rose-700/30 bg-rose-900/20' :
                            p.status === 'Refunded'  ? 'text-amber-400 border-amber-700/30 bg-amber-900/20' :
                            'text-slate-400 border-slate-700/30 bg-slate-900/20'
                          }`}>{p.status}</span>
                        </td>
                        <td className={`py-1 text-right tabular-nums font-medium ${p.status === 'Completed' ? 'text-emerald-400' : 'text-slate-500'}`}>
                          {fmtAmount(p.amount, p.currencyCode)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                <div className="flex justify-between text-xs pt-1 border-t border-white/[0.06]">
                  <span className="text-slate-500">Paid</span>
                  <span className="text-emerald-400 font-semibold tabular-nums">{fmtAmount(totalPaid, currency)}</span>
                </div>
                {balanceDue > 0 && (
                  <div className="flex justify-between text-xs pt-1">
                    <span className="text-slate-500">Balance Due</span>
                    <span className="text-rose-400 font-semibold tabular-nums">{fmtAmount(balanceDue, currency)}</span>
                  </div>
                )}
              </>
            ) : (
              <p className="text-xs text-slate-700 italic">No payments recorded.</p>
            )}

            {/* Payment inline form */}
            {canPay && showPayForm && (
              <PaymentForm
                saving={paySaving}
                err={payErr}
                onSave={handleSavePayment}
                onCancel={() => { setShowPayForm(false); setPayErr(''); }}
              />
            )}
          </div>
        </div>
      </td>
    </tr>
  );
}

// ── Main Page ─────────────────────────────────────────────────────────────────

export default function TenantBillingPage() {
  const { id } = useParams<{ id: string }>();
  const router  = useRouter();

  const [invoices, setInvoices]       = useState<TenantInvoice[]>([]);
  const [loading, setLoading]         = useState(true);
  const [showCreate, setShowCreate]   = useState(false);
  const [editing, setEditing]         = useState<TenantInvoice | null>(null);
  const [deleting, setDeleting]       = useState<TenantInvoice | null>(null);
  const [sending, setSending]         = useState<string | null>(null);
  const [downloading, setDownloading] = useState<string | null>(null);
  const [toasts, setToasts]           = useState<Record<string, { text: string; ok: boolean }>>({});
  const [expanded, setExpanded]       = useState<string | null>(null);

  useEffect(() => {
    const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
    if (!token) { router.replace('/platform/login'); return; }
    load();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);

  const load = useCallback(async () => {
    setLoading(true);
    try { setInvoices(await platformApi.listInvoices(id)); }
    finally { setLoading(false); }
  }, [id]);

  function toast(invoiceId: string, text: string, ok: boolean) {
    setToasts(t => ({ ...t, [invoiceId]: { text, ok } }));
    setTimeout(() => setToasts(t => { const n = { ...t }; delete n[invoiceId]; return n; }), 5000);
  }

  async function downloadPdf(inv: TenantInvoice) {
    setDownloading(inv.id);
    try { await platformApi.downloadInvoicePdf(id, inv.id, inv.invoiceNumber); }
    catch { toast(inv.id, 'PDF download failed.', false); }
    finally { setDownloading(null); }
  }

  async function sendEmail(inv: TenantInvoice) {
    setSending(inv.id);
    try {
      const r = await platformApi.sendInvoiceEmail(id, inv.id);
      if (r.smtpRequired) {
        toast(inv.id, 'SMTP not configured — download PDF and email manually.', false);
      } else {
        toast(inv.id, `Sent to ${r.billingEmail} ✓`, true);
        await load();
      }
    } catch { toast(inv.id, 'Failed to send email.', false); }
    finally { setSending(null); }
  }

  async function quickStatus(inv: TenantInvoice, status: Status) {
    try {
      await platformApi.updateInvoice(id, inv.id, { status });
      await load();
    } catch { toast(inv.id, `Could not mark as ${status}.`, false); }
  }

  const currency = invoices[0]?.currencyCode ?? 'USD';
  const totals = invoices.reduce(
    (a, inv) => {
      a.total += inv.amount;
      if (inv.status === 'Paid') a.paid += inv.amount;
      if (inv.status === 'Overdue') a.overdue += inv.amount;
      if (inv.status === 'Draft' || inv.status === 'Sent') a.outstanding += inv.amount;
      return a;
    },
    { total: 0, paid: 0, overdue: 0, outstanding: 0 },
  );

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <Link href={`/platform/tenants/${id}`}
            className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors shrink-0">
            <ArrowLeft className="h-4 w-4" />
          </Link>
          <div>
            <h1 className="text-lg font-bold text-white">Billing & Invoices</h1>
            <p className="text-xs text-slate-500 mt-0.5">{invoices.length} invoice{invoices.length !== 1 ? 's' : ''}</p>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <button type="button" onClick={load} disabled={loading} aria-label="Refresh" title="Refresh"
            className="h-8 w-8 flex items-center justify-center text-slate-500 hover:text-white border border-white/10 rounded-lg transition-colors disabled:opacity-40">
            <RefreshCw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} />
          </button>
          <button type="button" onClick={() => setShowCreate(true)}
            className="flex items-center gap-1.5 bg-blue-600 hover:bg-blue-500 text-white px-3 py-1.5 rounded-lg text-sm font-semibold transition-colors">
            <Plus className="h-3.5 w-3.5" /> New Invoice
          </button>
        </div>
      </div>

      {/* Summary cards */}
      {invoices.length > 0 && (
        <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
          {([
            { label: 'Total Billed', value: fmtAmount(totals.total, currency), cls: 'text-white' },
            { label: 'Paid',         value: fmtAmount(totals.paid, currency),  cls: 'text-emerald-400' },
            { label: 'Outstanding',  value: fmtAmount(totals.outstanding, currency), cls: 'text-blue-400' },
            { label: 'Overdue',      value: fmtAmount(totals.overdue, currency), cls: totals.overdue > 0 ? 'text-rose-400' : 'text-slate-600' },
          ] as const).map(c => (
            <div key={c.label} className="bg-[#161b22] border border-white/[0.07] rounded-xl p-4">
              <p className="text-[10px] font-semibold text-slate-600 uppercase tracking-widest mb-2">{c.label}</p>
              <p className={`text-lg font-bold tabular-nums ${c.cls}`}>{c.value}</p>
            </div>
          ))}
        </div>
      )}

      {/* Invoice table */}
      <div className="bg-[#161b22] border border-white/[0.07] rounded-xl overflow-hidden">
        {loading ? (
          <div className="flex items-center justify-center py-12">
            <div className="h-4 w-4 animate-spin rounded-full border-2 border-blue-500 border-t-transparent" />
          </div>
        ) : invoices.length === 0 ? (
          <div className="text-center py-14">
            <div className="h-10 w-10 mx-auto mb-3 rounded-xl bg-white/[0.03] border border-white/[0.07] flex items-center justify-center">
              <Plus className="h-5 w-5 text-slate-600" />
            </div>
            <p className="text-sm text-slate-500 mb-1">No invoices yet</p>
            <p className="text-xs text-slate-600 mb-4">Create the first invoice for this tenant</p>
            <button type="button" onClick={() => setShowCreate(true)}
              className="inline-flex items-center gap-1.5 bg-blue-600 hover:bg-blue-500 text-white px-4 py-2 rounded-lg text-sm font-semibold transition-colors">
              <Plus className="h-3.5 w-3.5" /> New Invoice
            </button>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[860px]">
              <thead>
                <tr className="border-b border-white/[0.06]">
                  {['Invoice #', 'Period', 'Amount', 'Status', 'Issued', 'Due', 'Payment', 'Actions'].map(h => (
                    <th key={h} className="px-4 py-3 text-left text-[10px] font-semibold text-slate-600 uppercase tracking-widest whitespace-nowrap">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {invoices.map(inv => {
                  const Icon = STATUS_ICON[inv.status] ?? Clock;
                  const isExpanded = expanded === inv.id;
                  return (
                    <>
                      <tr key={inv.id}
                        className={`border-b border-white/[0.04] hover:bg-white/[0.015] transition-colors cursor-pointer ${isExpanded ? 'bg-white/[0.02]' : ''}`}
                        onClick={() => setExpanded(isExpanded ? null : inv.id)}
                      >
                        {/* Invoice # */}
                        <td className="px-4 py-3">
                          <p className="text-sm text-white font-mono font-medium">{inv.invoiceNumber}</p>
                          <p className="text-[10px] text-slate-600 mt-0.5">
                            {new Date(inv.createdAtUtc).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })}
                          </p>
                        </td>

                        {/* Period */}
                        <td className="px-3 py-3 text-xs text-slate-400 max-w-[140px]">
                          {inv.periodDescription ?? <span className="text-slate-700">—</span>}
                        </td>

                        {/* Amount */}
                        <td className="px-3 py-3">
                          <p className="text-sm text-white font-semibold tabular-nums">{fmtAmount(inv.amount, inv.currencyCode)}</p>
                          {inv.paidDate && (
                            <p className="text-[10px] text-emerald-600 mt-0.5">Paid {fmtDate(inv.paidDate)}</p>
                          )}
                        </td>

                        {/* Status */}
                        <td className="px-3 py-3">
                          <span className={`inline-flex items-center gap-1 text-[10px] font-semibold uppercase px-1.5 py-0.5 rounded border ${STATUS_BADGE[inv.status] ?? ''}`}>
                            <Icon className="h-2.5 w-2.5" />
                            {inv.status}
                          </span>
                        </td>

                        {/* Issued */}
                        <td className="px-3 py-3 text-xs text-slate-500 whitespace-nowrap">{fmtDate(inv.invoiceDate)}</td>

                        {/* Due */}
                        <td className="px-3 py-3 text-xs whitespace-nowrap">
                          <span className={inv.status === 'Overdue' ? 'text-rose-400 font-medium' : 'text-slate-500'}>
                            {fmtDate(inv.dueDate)}
                          </span>
                        </td>

                        {/* Payment */}
                        <td className="px-3 py-3 text-xs max-w-[120px]">
                          {inv.paymentMethod ? (
                            <div>
                              <p className="text-slate-400">{inv.paymentMethod}</p>
                              {inv.paymentReference && (
                                <p className="text-[10px] text-slate-600 font-mono truncate">{inv.paymentReference}</p>
                              )}
                            </div>
                          ) : (
                            <span className="text-slate-700">—</span>
                          )}
                        </td>

                        {/* Actions */}
                        <td className="px-3 py-3" onClick={e => e.stopPropagation()}>
                          <div className="flex items-center gap-1 flex-wrap">
                            {inv.status === 'Draft' && (
                              <button type="button" onClick={() => quickStatus(inv, 'Sent')}
                                className="text-[10px] text-blue-400 border border-blue-500/20 hover:border-blue-500/50 px-2 py-1 rounded transition-colors whitespace-nowrap">
                                Mark Sent
                              </button>
                            )}
                            {(inv.status === 'Draft' || inv.status === 'Sent') && (
                              <button type="button" onClick={() => quickStatus(inv, 'Paid')}
                                className="text-[10px] text-emerald-400 border border-emerald-500/20 hover:border-emerald-500/50 px-2 py-1 rounded transition-colors whitespace-nowrap">
                                Mark Paid
                              </button>
                            )}
                            {inv.status === 'Sent' && (
                              <button type="button" onClick={() => quickStatus(inv, 'Overdue')}
                                className="text-[10px] text-rose-400 border border-rose-500/20 hover:border-rose-500/50 px-2 py-1 rounded transition-colors whitespace-nowrap">
                                Overdue
                              </button>
                            )}

                            <button type="button" onClick={() => downloadPdf(inv)} disabled={downloading === inv.id} title="Download PDF"
                              className="h-7 w-7 flex items-center justify-center text-slate-500 hover:text-white border border-white/[0.08] hover:border-white/20 rounded transition-colors disabled:opacity-40">
                              <Download className="h-3 w-3" />
                            </button>

                            {(inv.status === 'Draft' || inv.status === 'Sent' || inv.status === 'Overdue') && (
                              <button type="button" onClick={() => sendEmail(inv)} disabled={sending === inv.id} title="Email to billing contact"
                                className="h-7 w-7 flex items-center justify-center text-slate-500 hover:text-blue-400 border border-white/[0.08] hover:border-blue-500/30 rounded transition-colors disabled:opacity-40">
                                <Send className="h-3 w-3" />
                              </button>
                            )}

                            <button type="button" onClick={() => setEditing(inv)} title="Edit invoice"
                              className="h-7 w-7 flex items-center justify-center text-slate-500 hover:text-white border border-white/[0.08] hover:border-white/20 rounded transition-colors">
                              <Pencil className="h-3 w-3" />
                            </button>

                            {(inv.status === 'Draft' || inv.status === 'Cancelled') && (
                              <button type="button" onClick={() => setDeleting(inv)} title="Delete invoice"
                                className="h-7 w-7 flex items-center justify-center text-slate-500 hover:text-rose-400 border border-white/[0.08] hover:border-rose-500/30 rounded transition-colors">
                                <Trash2 className="h-3 w-3" />
                              </button>
                            )}
                          </div>
                        </td>
                      </tr>

                      {toasts[inv.id] && (
                        <tr key={`toast-${inv.id}`} className="border-b border-white/[0.04]">
                          <td colSpan={8} className="px-4 py-2">
                            <p className={`text-xs ${toasts[inv.id].ok ? 'text-emerald-400' : 'text-rose-400'}`}>{toasts[inv.id].text}</p>
                          </td>
                        </tr>
                      )}

                      {inv.notes && (
                        <tr key={`notes-${inv.id}`} className="border-b border-white/[0.04]">
                          <td colSpan={8} className="px-4 pb-2.5 pt-0">
                            <p className="text-[11px] text-slate-600 italic">Note: {inv.notes}</p>
                          </td>
                        </tr>
                      )}
                      {isExpanded && (
                        <InvoiceDetailPanel key={`detail-${inv.id}`} tenantId={id} invoice={inv} currency={inv.currencyCode} />
                      )}
                    </>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {showCreate && <CreateModal tenantId={id} onClose={() => setShowCreate(false)} onCreated={load} />}
      {editing    && <EditModal tenantId={id} invoice={editing} onClose={() => setEditing(null)} onSaved={load} />}
      {deleting   && <DeleteDialog invoice={deleting} onClose={() => setDeleting(null)} onDeleted={load} />}
    </div>
  );
}
