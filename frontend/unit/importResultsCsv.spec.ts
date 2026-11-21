import { test, expect } from '@playwright/test';
import { csvCell } from '../src/lib/attendanceImport';

// The import preview and results CSVs carry names and codes from an uploaded file. Opened in a spreadsheet,
// a value that starts with = + - @ (or a tab / carriage return) must be text, not a formula.

test('a name or code that starts with a formula character is written as text', () => {
  expect(csvCell('=cmd|"/c calc"!A1')).toBe(`"'=cmd|""/c calc""!A1"`);
  expect(csvCell('+SUM(A1)')).toBe(`'+SUM(A1)`);
  expect(csvCell('@SUM(A1)')).toBe(`'@SUM(A1)`);
  expect(csvCell('-2+3')).toBe(`'-2+3`);
  expect(csvCell('\tcmd')).toBe(`'\tcmd`);
  expect(csvCell(' =cmd')).toBe(`' =cmd`);
});

test('ordinary text, Arabic and plain numbers are unchanged', () => {
  expect(csvCell('Ahmad Al-Qahtani')).toBe('Ahmad Al-Qahtani');
  expect(csvCell('سارة العتيبي')).toBe('سارة العتيبي');
  expect(csvCell(-12.5)).toBe('-12.5');
  expect(csvCell('-12.5')).toBe('-12.5');
  expect(csvCell('a,b')).toBe('"a,b"');
  expect(csvCell(undefined)).toBe('');
});
