'use client';

import { useEffect, useState } from 'react';
import { Pencil, Plus, Trash2 } from 'lucide-react';
import { helpTextsApi, type FieldHelpText } from '../api/helpTexts';
import { useHelpTexts } from '../contexts/HelpTextContext';

/**
 * Tenant Admin → Help Text: lets the company admin replace any built-in field
 * tooltip with their own policy wording. Every ⓘ tooltip in the app shows its
 * "key:" at the bottom — enter that key here with custom text to override it.
 */
export function HelpTextManager() {
  const { refresh } = useHelpTexts();
  const [items, setItems] = useState<FieldHelpText[]>([]);
  const [loading, setLoading] = useState(true);
  const [fieldKey, setFieldKey] = useState('');
  const [text, setText] = useState('');
  const [editingKey, setEditingKey] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  const load = async () => {
    setLoading(true);
    try { setItems(await helpTextsApi.list()); } catch { /**/ } finally { setLoading(false); }
  };
  useEffect(() => { load(); }, []);

  const startEdit = (item: FieldHelpText) => {
    setEditingKey(item.fieldKey);
    setFieldKey(item.fieldKey);
    setText(item.text);
    setError('');
  };

  const reset = () => { setEditingKey(null); setFieldKey(''); setText(''); setError(''); };

  const save = async () => {
    setError('');
    const key = fieldKey.trim().toLowerCase();
    if (key.length < 2) { setError('Enter the field key shown at the bottom of the tooltip you want to change (e.g. employees.joining_date).'); return; }
    if (!text.trim()) { setError('Enter the help text your users should see.'); return; }
    setSaving(true);
    try {
      await helpTextsApi.upsert(key, text.trim());
      reset();
      await load();
      await refresh();
    } catch (err: unknown) {
      setError((err as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Failed to save.');
    } finally {
      setSaving(false);
    }
  };

  const remove = async (key: string) => {
    if (!confirm(`Remove the custom help text for "${key}"? The built-in default will be shown again.`)) return;
    try { await helpTextsApi.remove(key); await load(); await refresh(); } catch { /**/ }
  };

  return (
    <div className="space-y-4">
      <div className="bg-white rounded-xl border border-gray-200 p-6">
        <h2 className="text-lg font-semibold text-gray-900 mb-1">Custom Field Help Text</h2>
        <p className="text-sm text-gray-500 mb-4">
          Every ⓘ tooltip in the app has a built-in explanation. Override any of them with your company&apos;s own wording —
          hover a tooltip anywhere in the app and copy the <span className="font-mono text-xs">key:</span> shown at the bottom,
          then add it here with the text your staff should see (e.g. your own leave policy or payroll cut-off rule).
        </p>

        <div className="grid gap-3 sm:grid-cols-[240px_1fr_auto]">
          <input
            value={fieldKey}
            onChange={e => setFieldKey(e.target.value)}
            disabled={editingKey !== null}
            placeholder="Field key, e.g. employees.joining_date"
            className="rounded-lg border border-gray-300 px-3 py-2 text-sm font-mono focus:border-indigo-500 focus:outline-none disabled:bg-gray-50 disabled:text-gray-500"
          />
          <input
            value={text}
            onChange={e => setText(e.target.value)}
            maxLength={500}
            placeholder="The explanation your users should see (max 500 characters)"
            className="rounded-lg border border-gray-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none"
          />
          <div className="flex gap-2">
            <button
              type="button"
              onClick={save}
              disabled={saving}
              className="inline-flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:opacity-50"
            >
              <Plus className="h-4 w-4" /> {saving ? 'Saving…' : editingKey ? 'Update' : 'Add'}
            </button>
            {editingKey && (
              <button type="button" onClick={reset} className="rounded-lg border border-gray-300 px-3 py-2 text-sm text-gray-600 hover:bg-gray-50">
                Cancel
              </button>
            )}
          </div>
        </div>
        {error && <p className="mt-2 text-sm text-red-600">{error}</p>}
      </div>

      <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
        {loading ? (
          <p className="p-6 text-sm text-gray-500">Loading…</p>
        ) : items.length === 0 ? (
          <p className="p-6 text-sm text-gray-500">No custom help texts yet — all tooltips show the built-in defaults.</p>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-gray-200 text-start text-xs text-gray-500">
                <th className="px-4 py-2.5 font-medium">Field Key</th>
                <th className="px-4 py-2.5 font-medium">Custom Text</th>
                <th className="px-4 py-2.5 font-medium">Updated</th>
                <th className="px-4 py-2.5" />
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {items.map(item => (
                <tr key={item.fieldKey}>
                  <td className="px-4 py-2.5 font-mono text-xs text-gray-700">{item.fieldKey}</td>
                  <td className="px-4 py-2.5 text-gray-700">{item.text}</td>
                  <td className="px-4 py-2.5 text-xs text-gray-400 whitespace-nowrap">{new Date(item.updatedAtUtc).toLocaleDateString()}</td>
                  <td className="px-4 py-2.5 whitespace-nowrap text-end">
                    <button type="button" onClick={() => startEdit(item)} aria-label={`Edit ${item.fieldKey}`} className="me-2 text-gray-400 hover:text-indigo-600">
                      <Pencil className="h-4 w-4" />
                    </button>
                    <button type="button" onClick={() => remove(item.fieldKey)} aria-label={`Delete ${item.fieldKey}`} className="text-gray-400 hover:text-red-600">
                      <Trash2 className="h-4 w-4" />
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
