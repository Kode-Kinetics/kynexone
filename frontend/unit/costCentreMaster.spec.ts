import { expect, test } from '@playwright/test';
import {
  buildTree,
  codeProblem,
  descendantIds,
  filterItems,
  inUseCounts,
  inUseFromError,
  isFiltering,
  linksToClearFrom,
  parentOptions,
  saveBody,
  type MasterItem,
} from '../src/lib/costCentreMaster';

const item = (code: string, over: Partial<MasterItem> = {}): MasterItem => ({ id: code.toLowerCase(), code, name: `${code} name`, isActive: true, companyId: 'co', parentId: null, functionalClass: null, glSegment: null, ...over });
const sample = (): MasterItem[] => [
  item('SALES'),
  item('SALES-RY', { parentId: 'sales', functionalClass: 'SELLING' }),
  item('SALES-RY-N', { parentId: 'sales-ry' }),
  item('OPS', { isActive: false, functionalClass: 'OPERATIONS' }),
  item('ORPHAN', { companyId: null }),
  item('OTHER', { companyId: 'elsewhere' }),
];

test.describe('buildTree', () => {
  test('roots in code order with children under their parent, depth counted, collapsed children hidden', () => {
    const rows = buildTree(sample(), new Set(['sales', 'sales-ry']));
    expect(rows.map((r) => [r.item.code, r.depth])).toEqual([['OPS', 0], ['ORPHAN', 0], ['OTHER', 0], ['SALES', 0], ['SALES-RY', 1], ['SALES-RY-N', 2]]);
    expect(rows.find((r) => r.item.code === 'SALES')?.hasChildren).toBe(true);
    expect(rows.find((r) => r.item.code === 'OPS')?.hasChildren).toBe(false);
    expect(buildTree(sample(), new Set()).map((r) => r.item.code)).toEqual(['OPS', 'ORPHAN', 'OTHER', 'SALES']);
    expect(buildTree(sample(), new Set(['sales'])).map((r) => r.item.code)).toEqual(['OPS', 'ORPHAN', 'OTHER', 'SALES', 'SALES-RY']);
  });

  test('a parent that is not in the list (another company, deleted) leaves the child at the top, and a loop does not spin', () => {
    const rows = buildTree([item('A', { parentId: 'gone' }), item('B', { parentId: 'c' }), item('C', { parentId: 'b' })], new Set(['b', 'c']));
    expect(rows.map((r) => r.item.code)).toContain('A');
    expect(rows.length).toBeGreaterThanOrEqual(1);
    expect(rows.length).toBeLessThanOrEqual(3);
  });
});

test.describe('parent choices', () => {
  test('descendantIds are the whole subtree', () => {
    expect([...descendantIds(sample(), 'sales')].sort()).toEqual(['sales-ry', 'sales-ry-n']);
  });

  test('offers the same company only, never the item or its descendants, and only active ones (plus the current parent)', () => {
    const codes = (opts: MasterItem[]) => opts.map((o) => o.code).sort();
    expect(codes(parentOptions(sample(), { id: 'sales', companyId: 'co', parentId: null }))).toEqual([]);
    expect(codes(parentOptions(sample(), { id: 'sales-ry-n', companyId: 'co', parentId: 'sales-ry' }))).toEqual(['SALES', 'SALES-RY']);
    // The inactive OPS is not offered unless it is already the parent.
    expect(codes(parentOptions(sample(), { companyId: 'co', parentId: null }))).toEqual(['SALES', 'SALES-RY', 'SALES-RY-N']);
    expect(codes(parentOptions(sample(), { companyId: 'co', parentId: 'ops' }))).toContain('OPS');
  });

  test('a new cost centre with no company yet is offered no parents', () => {
    expect(parentOptions(sample(), { companyId: '', parentId: null })).toEqual([]);
  });
});

test.describe('filters', () => {
  test('company, class, status and search narrow the list; isFiltering says when to leave the tree', () => {
    const none = { company: '', cls: '', status: '', search: '' };
    expect(isFiltering(none)).toBe(false);
    expect(isFiltering({ ...none, search: ' ' })).toBe(false);
    expect(filterItems(sample(), { ...none, company: 'co' })).toHaveLength(4);
    expect(filterItems(sample(), { ...none, company: '__none__' }).map((i) => i.code)).toEqual(['ORPHAN']);
    expect(filterItems(sample(), { ...none, cls: 'SELLING' }).map((i) => i.code)).toEqual(['SALES-RY']);
    expect(filterItems(sample(), { ...none, status: 'inactive' }).map((i) => i.code)).toEqual(['OPS']);
    expect(filterItems(sample(), { ...none, search: 'ry-n' }).map((i) => i.code)).toEqual(['SALES-RY-N']);
    expect(filterItems([item('X', { glSegment: 'S110' })], { ...none, search: 's110' })).toHaveLength(1);
  });
});

test.describe('code', () => {
  test('required, then the server\'s format', () => {
    expect(codeProblem('')).toBe('required');
    expect(codeProblem('  ')).toBe('required');
    expect(codeProblem('SALES-RY.1_a')).toBeNull();
    expect(codeProblem('-SALES')).toBe('format');
    expect(codeProblem('SA LES')).toBe('format');
    expect(codeProblem('A'.repeat(31))).toBe('format');
    expect(codeProblem('A'.repeat(30))).toBeNull();
  });
});

test.describe('M-8 usage counts', () => {
  const err = (status: number, data: unknown) => ({ response: { status, data } });
  test('reads the counts out of a 409 cost_centre_in_use, in the design\'s order, leaving out zeros', () => {
    const e = err(409, { error: 'cost_centre_in_use', counts: { department: 2, employee: 14, position: 0, mapping: 1, companyDefault: 1, activeChild: 3 } });
    expect(inUseFromError(e)?.counts).toEqual({ department: 2, employee: 14, position: 0, mapping: 1, companyDefault: 1, activeChild: 3 });
    expect(inUseCounts(inUseFromError(e)!.counts)).toEqual([
      { key: 'departments', count: 2 }, { key: 'employees', count: 14 }, { key: 'rules', count: 1 }, { key: 'children', count: 3 }, { key: 'default', count: 1 },
    ]);
  });

  test('anything else is not an in-use refusal', () => {
    expect(inUseFromError(err(409, { error: 'cost_centre_code_locked' }))).toBeNull();
    expect(inUseFromError(err(422, { error: 'cost_centre_in_use' }))).toBeNull();
    expect(inUseFromError(new Error('x'))).toBeNull();
  });
});

test.describe('saveBody follows the API\'s null-means-unchanged, empty-means-clear contract', () => {
  const editing = item('SALES-RY', { parentId: 'sales', functionalClass: 'SELLING', glSegment: 'S110', nameAr: 'مبيعات' });
  const form = { companyId: 'co', code: 'SALES-RY', name: 'Riyadh Sales', nameAr: 'مبيعات', isActive: true, parentId: 'sales', functionalClass: 'SELLING', glSegment: 'S110', reason: '' };

  test('unchanged parent, class and ERP code are left out; a changed name is sent', () => {
    expect(saveBody(editing, form)).toEqual({ companyId: 'co', code: 'SALES-RY', name: 'Riyadh Sales', nameAr: 'مبيعات', isActive: true });
  });

  test('clearing the parent sends the empty Guid, clearing class or ERP code sends an empty string, with the reason', () => {
    expect(saveBody(editing, { ...form, parentId: '', functionalClass: '', glSegment: '', reason: ' Reclassified ' })).toEqual({
      companyId: 'co', code: 'SALES-RY', name: 'Riyadh Sales', nameAr: 'مبيعات', isActive: true,
      parentId: '00000000-0000-0000-0000-000000000000', functionalClass: '', glSegment: '', reason: 'Reclassified',
    });
  });

  test('re-pointing and re-classing send the new values; a reason travels whenever one was typed', () => {
    const body = saveBody(editing, { ...form, parentId: 'ops', functionalClass: 'COGS', glSegment: 'S999', reason: 'Re-org' });
    expect(body).toMatchObject({ parentId: 'ops', functionalClass: 'COGS', glSegment: 'S999', reason: 'Re-org' });
  });

  test('a new cost centre sends only what it has', () => {
    expect(saveBody(null, { companyId: 'co', code: 'NEW', name: 'New', nameAr: '', isActive: true, parentId: '', functionalClass: '', glSegment: '', reason: '' })).toEqual({ companyId: 'co', code: 'NEW', name: 'New', nameAr: '', isActive: true });
    expect(saveBody(null, { companyId: 'co', code: 'NEW', name: 'New', nameAr: '', isActive: true, parentId: 'sales', functionalClass: 'SELLING', glSegment: 'S1', reason: 'x' })).toMatchObject({ parentId: 'sales', functionalClass: 'SELLING', glSegment: 'S1', reason: 'x' });
  });
});

test.describe('the department import\'s links to clear', () => {
  test('uses the header when present, else counts the rows\' "Clears …" warnings', () => {
    const rows = [{ warnings: ['Clears the cost centre link of this existing department.'] }, { warnings: ['Clears the parent department link of this existing department.', 'Something else'] }, { warnings: [] }];
    expect(linksToClearFrom('3', rows)).toBe(3);
    expect(linksToClearFrom(undefined, rows)).toBe(2);
    expect(linksToClearFrom('0', rows)).toBe(2);
    expect(linksToClearFrom('x', [])).toBe(0);
  });
});
