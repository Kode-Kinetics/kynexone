import { expect, test } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * Cost centre Wave A, D3: the employee's cached `costCenter` string is written inconsistently by the server (a clear writes only
 * the department's own code, a profile-form Remove leaves the old code), so a screen that reads it can disagree with payroll
 * costing. Every screen reads the server's resolution (`costCentreResolution`) or the preview endpoint instead.
 *
 * This scan fails on any property read of `.costCenter` / `['costCenter']` in src. The allow-list is for lines that are not
 * a read of the employee's cached copy; each entry says why. It may only shrink.
 */
const SRC = path.resolve(__dirname, '../src');
const READ = /\.costCenter\b(?![\w$])|\[\s*['"]costCenter['"]\s*\]/;
/**
 * Destructuring reads: `const { costCenter } = e`, a rename `const { costCenter: cc } = e`, a nested
 * `const { changes: { costCenter } } = p`, and parameters `({ costCenter }) =>` / `function f({ costCenter }: T) {`.
 * An object literal passed or returned (`fn({ costCenter: code })`, `{ changes: { costCenter: '' } }`) is a write, not a read.
 */
const DESTRUCTURE = [
  /\b(?:const|let|var)\s*\{[^=;]*\bcostCenter\b(?![\w$])[^=;]*\}\s*=(?![=>])/,
  /\(\s*\{[^()]*\bcostCenter\b(?![\w$])[^()]*\}[^()]*\)\s*(?:=>|\{)/,
];
const reads = (line: string) => READ.test(line) || DESTRUCTURE.some((re) => re.test(line));

const ALLOW: Record<string, string> = {
  // The edit patch's own `changes.costCenter` key (the code the PUT sends), not the employee's cached copy.
  'lib/employeeCostCentre.ts: return patch.changes ? patch.changes.costCenter : undefined;': 'PUT patch key',
};
/** Comment lines (JSDoc, //) describe the contract; they are not reads. */
const COMMENT = /^\s*(\*|\/\/|\/\*)/;

function files(dir: string): string[] {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) return files(full);
    return /\.(ts|tsx)$/.test(entry.name) ? [full] : [];
  });
}

test('nothing in src reads the cached employee.costCenter string (use costCentreResolution or the preview)', () => {
  const hits: string[] = [];
  for (const file of files(SRC)) {
    const rel = path.relative(SRC, file).split(path.sep).join('/');
    fs.readFileSync(file, 'utf8').split('\n').forEach((line, i) => {
      if (COMMENT.test(line) || !reads(line.replace(/\/\/.*$/, ''))) return;
      const key = `${rel}: ${line.trim()}`;
      if (!ALLOW[key]) hits.push(`${rel}:${i + 1}: ${line.trim()}`);
    });
  }
  expect(hits, hits.join('\n')).toEqual([]);
});

test('the scan itself catches the shapes it is meant to catch', () => {
  for (const line of ['employee.costCenter', 'x?.costCenter ?? ""', "row['costCenter']", 'form.costCenter)']) expect(reads(line), line).toBe(true);
  for (const line of ['employee.costCenterId', 'costCenterIsOverride', "key === 'costCenter'", 'costCenter: string;', 'employee.costCenters']) expect(reads(line), line).toBe(false);
});

test('the scan catches destructuring reads, and not object literals or types', () => {
  const positive = [
    'const { costCenter } = employee;',
    'let {costCenter}=e',
    'const { id, costCenter: cc } = employee;',
    'const { changes: { costCenter } } = patch;',
    'const show = ({ costCenter }) => costCenter;',
    'function show({ costCenter }: EmployeeDetail) {',
    'items.map(({ costCenter, id }) => id)',
  ];
  const negative = [
    'fn({ costCenter: code });',
    "return { changes: { costCenter: '' }, costCenterReason: reason.trim() };",
    'const { costCenterId } = employee;',
    'const { costCenterIsOverride: flag } = employee;',
    "const body = { costCenter: 'A' };",
    'type Row = { costCenter: string };',
    'interface Row { costCenter: string }',
    'if (a == { costCenter }) {}',
  ];
  for (const line of positive) expect(reads(line), line).toBe(true);
  for (const line of negative) expect(reads(line), line).toBe(false);
});
