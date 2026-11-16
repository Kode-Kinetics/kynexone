import { test, expect } from '@playwright/test';
import { readOtherFormatLive } from '../src/api/financeErp';

// C7: the server refuses (HTTP 409, error other_format_export_live) an export in another format while a file over the same
// unconfirmed rows still waits for the ERP. The panel turns that into a confirm button that resends with the acknowledgement.
const body = {
  error: 'other_format_export_live', message: 'A generic-csv export …',
  liveExports: [{ exportId: 'e1', format: 'generic-csv', fileName: 'journal-GLP01-2026-11-SAR-generic-csv-8A2D975A.csv', status: 'Exported' }],
};

test('reads the live file the server names', () => {
  expect(readOtherFormatLive({ response: { status: 409, data: body } })).toEqual({
    format: 'generic-csv', fileName: 'journal-GLP01-2026-11-SAR-generic-csv-8A2D975A.csv',
  });
});

test('any other refusal is not the acknowledgement case', () => {
  expect(readOtherFormatLive({ response: { status: 409, data: { error: 'concurrent_export' } } })).toBeNull();
  expect(readOtherFormatLive({ response: { status: 422, data: body } })).toBeNull();
  expect(readOtherFormatLive({ response: { status: 409, data: { error: 'other_format_export_live', liveExports: [] } } })).toEqual({ format: '', fileName: '' });
  expect(readOtherFormatLive(new Error('x'))).toBeNull();
  expect(readOtherFormatLive(null)).toBeNull();
});
