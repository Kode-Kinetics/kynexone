import { expect, test } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

// Source contract: the bulk panels must not be <form>s. Inside a form, Enter in the search box (or on a
// checkbox) submits it, which would record punches or start a processing job the user never confirmed.
const read = (f: string) => fs.readFileSync(path.resolve(__dirname, '../src/components/attendance', f), 'utf8');

test('no bulk panel (or the multi-select) renders a <form>: Enter can never submit them', () => {
  for (const f of ['AttendanceMassEntryPanel.tsx', 'AttendanceProcessPanel.tsx', 'AttendanceCsvImportPanel.tsx', 'EmployeeMultiSelect.tsx']) {
    expect(read(f), f).not.toMatch(/<form[\s>]/);
    expect(read(f), f).not.toContain('type="submit"');
  }
});

test('Enter in the multi-select search box is swallowed as well', () => {
  // Formatting-tolerant: an onKeyDown handler that checks for Enter and calls preventDefault. The real
  // behaviour (Enter sends no request) is proven in the browser by e2e/attendance-bulk-ui.spec.ts.
  const src = read('EmployeeMultiSelect.tsx').replace(/\s+/g, ' ');
  expect(src).toMatch(/onKeyDown=\{[^}]*['"]Enter['"][^}]*preventDefault\(\)/);
});
