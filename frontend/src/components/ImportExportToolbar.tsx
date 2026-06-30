'use client';

import { useRef, useState } from 'react';
import { Download, FileUp, Upload } from 'lucide-react';

export interface ImportResult {
  received: number;
  created: number;
  skipped: number;
  errors: string[];
}

export interface ImportExportToolbarProps {
  entityName: string;
  onExport: () => Promise<void>;
  /** Omit to hide the "Template" button (e.g. transactional entities with no bulk-import path). */
  onDownloadTemplate?: () => Promise<void>;
  /** Omit to hide the "Import CSV" button. */
  onImport?: (csvContent: string) => Promise<ImportResult>;
}

interface Toast {
  type: 'success' | 'error';
  message: string;
}

function downloadCsv(content: string, filename: string) {
  const blob = new Blob([content], { type: 'text/csv' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

export { downloadCsv };

export function ImportExportToolbar({
  entityName,
  onExport,
  onDownloadTemplate,
  onImport,
}: ImportExportToolbarProps) {
  const [exporting, setExporting] = useState(false);
  const [templating, setTemplating] = useState(false);
  const [importing, setImporting] = useState(false);
  const [toast, setToast] = useState<Toast | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const showToast = (t: Toast) => {
    setToast(t);
    setTimeout(() => setToast(null), 5000);
  };

  const handleExport = async () => {
    setExporting(true);
    try {
      await onExport();
    } catch {
      showToast({ type: 'error', message: `Failed to export ${entityName}.` });
    } finally {
      setExporting(false);
    }
  };

  const handleTemplate = async () => {
    if (!onDownloadTemplate) return;
    setTemplating(true);
    try {
      await onDownloadTemplate();
    } catch {
      showToast({ type: 'error', message: 'Failed to download template.' });
    } finally {
      setTemplating(false);
    }
  };

  const handleFileChange = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = '';
    if (!file || !onImport) return;

    setImporting(true);
    try {
      const csvContent = await file.text();
      const result = await onImport(csvContent);

      if (result.errors.length > 0) {
        showToast({
          type: 'error',
          message: `Imported: Created ${result.created}, Skipped ${result.skipped}. Errors: ${result.errors.slice(0, 3).join('; ')}${result.errors.length > 3 ? ' …' : ''}`,
        });
      } else {
        showToast({
          type: 'success',
          message: `Import complete — Created ${result.created}, Skipped ${result.skipped} of ${result.received} rows.`,
        });
      }
    } catch {
      showToast({ type: 'error', message: `Failed to import ${entityName}.` });
    } finally {
      setImporting(false);
    }
  };

  const btnBase =
    'inline-flex items-center gap-1.5 rounded-lg border px-3 py-1.5 text-xs font-medium transition-colors disabled:opacity-50';
  const btnOutline =
    `${btnBase} border-slate-200 text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5`;

  return (
    <div className="relative flex items-center gap-2">
      <input
        ref={fileInputRef}
        type="file"
        accept=".csv,text/csv"
        className="hidden"
        onChange={handleFileChange}
      />

      <button
        type="button"
        className={btnOutline}
        disabled={exporting}
        onClick={handleExport}
        title={`Export ${entityName} as CSV`}
      >
        <Download className="h-3.5 w-3.5" />
        {exporting ? 'Exporting…' : 'Export'}
      </button>

      {onDownloadTemplate && (
        <button
          type="button"
          className={btnOutline}
          disabled={templating}
          onClick={handleTemplate}
          title="Download blank CSV import template"
        >
          <FileUp className="h-3.5 w-3.5" />
          {templating ? 'Downloading…' : 'Template'}
        </button>
      )}

      {onImport && (
        <button
          type="button"
          className={btnOutline}
          disabled={importing}
          onClick={() => fileInputRef.current?.click()}
          title={`Import ${entityName} from CSV`}
        >
          <Upload className="h-3.5 w-3.5" />
          {importing ? 'Importing…' : 'Import CSV'}
        </button>
      )}

      {/* Toast */}
      {toast && (
        <div
          className={`absolute right-0 top-10 z-50 max-w-sm rounded-lg border px-4 py-3 text-xs shadow-lg ${
            toast.type === 'success'
              ? 'border-emerald-200 bg-emerald-50 text-emerald-800 dark:border-emerald-500/30 dark:bg-emerald-500/10 dark:text-emerald-300'
              : 'border-rose-200 bg-rose-50 text-rose-800 dark:border-rose-500/30 dark:bg-rose-500/10 dark:text-rose-300'
          }`}
        >
          {toast.message}
        </div>
      )}
    </div>
  );
}
