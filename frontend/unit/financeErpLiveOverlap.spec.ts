import { test, expect } from '@playwright/test';
import { splitManifestWarnings, type JournalExportLiveOverlap } from '../src/api/financeErp';

// C-1: the check (preview) names a file of another scope that is still waiting for the ERP's result and already holds some of
// the rows. The server puts the English sentence in `warnings` and both languages in `liveExportOverlaps`; the panel shows it
// once, in the viewer's language.
const overlap: JournalExportLiveOverlap = {
  exportId: 'e1', fileName: 'journal-SA01-run-f15b4846-SAR-generic-csv-FB8A0869.csv', format: 'generic-csv', rows: 2,
  relation: 'other_scope',
  message: '2 of these ledger rows are already in export journal-SA01-run-f15b4846-SAR-generic-csv-FB8A0869.csv, which is waiting for its ERP result.',
  messageAr: '2 من قيود دفتر الأستاذ هذه موجودة بالفعل في ملف التصدير journal-SA01-run-f15b4846-SAR-generic-csv-FB8A0869.csv الذي ما زال بانتظار نتيجة نظام تخطيط الموارد.',
};
const mapping = 'GL account 1400 has no ERP account code mapped, so the file carries the internal code 1400.';

test('an Arabic viewer gets the waiting-file notice in Arabic, once', () => {
  const split = splitManifestWarnings({ warnings: [mapping, overlap.message], liveExportOverlaps: [overlap] }, 'ar');
  expect(split.liveOverlaps).toEqual([overlap.messageAr]);
  expect(split.others).toEqual([mapping]);
});

test('an English viewer gets the English sentence, once', () => {
  const split = splitManifestWarnings({ warnings: [overlap.message, mapping], liveExportOverlaps: [overlap] }, 'en');
  expect(split.liveOverlaps).toEqual([overlap.message]);
  expect(split.others).toEqual([mapping]);
});

test('an API without the list leaves the warnings as they are', () => {
  expect(splitManifestWarnings({ warnings: [mapping] }, 'ar')).toEqual({ liveOverlaps: [], others: [mapping] });
});
