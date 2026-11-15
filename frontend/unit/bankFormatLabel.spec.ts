import { expect, test } from '@playwright/test';
import { translate, type MessageParams } from '../src/i18n/translations';
import { bankFormatLabel } from '../src/lib/payrollBatchDisplay';

const en = (k: string, p?: MessageParams) => translate('en', k, p);
const ar = (k: string, p?: MessageParams) => translate('ar', k, p);
const formats = [
  { id: 'anb-connect-csv-v1', name: 'ANB Connect payroll payment — header.csv + body.csv' },
  { id: 'custom:abc', name: 'Our bank layout' },
];

test.describe('the generated export shows the format name, not its id', () => {
  test('a known format reads as its display name, translated', () => {
    expect(bankFormatLabel(en, formats, 'anb-connect-csv-v1', 'custom template')).toBe('ANB Connect payroll payment — header.csv + body.csv');
    expect(bankFormatLabel(ar, formats, 'anb-connect-csv-v1', 'قالب مخصص')).toBe('دفع الرواتب عبر ANB Connect — header.csv + body.csv');
  });
  test('a customer template carries the same suffix as the format select', () => {
    expect(bankFormatLabel(en, formats, 'custom:abc', 'custom template')).toBe('Our bank layout (custom template)');
  });
  test('an unknown format falls back to its id', () => {
    expect(bankFormatLabel(en, formats, 'mystery-v9', 'custom template')).toBe('mystery-v9');
    expect(bankFormatLabel(en, [], 'anb-connect-csv-v1', 'custom template')).toBe('anb-connect-csv-v1');
  });
});
