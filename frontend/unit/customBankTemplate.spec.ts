import { test, expect } from '@playwright/test';
import { blankColumn, blankDefinition, moveItem, removeItem } from '../src/lib/customBankTemplate';

test.describe('custom bank template editor helpers', () => {
  test('a blank template starts with the two columns every Saudi bank file needs', () => {
    const d = blankDefinition();
    expect(d.columns.map((c) => c.field)).toEqual(['beneficiaryIban', 'netPay']);
    expect(d.layout).toBe('delimited');
    expect(d.encoding).toBe('utf-8');
    expect(d.headerRecord).toEqual([]);
    expect(d.trailerRecord).toEqual([]);
  });

  test('blank definitions do not share column objects', () => {
    const a = blankDefinition();
    const b = blankDefinition();
    a.columns[0].upperCase = true;
    expect(b.columns[0].upperCase).toBe(false);
  });

  test('a literal column starts with empty text; other fields have none', () => {
    expect(blankColumn('literal').literal).toBe('');
    expect(blankColumn('netPay').literal).toBeNull();
    expect(blankColumn('netPay').amountDecimals).toBe(2);
  });

  test('moveItem swaps neighbours and refuses to leave the list', () => {
    const list = ['a', 'b', 'c'];
    expect(moveItem(list, 1, -1)).toEqual(['b', 'a', 'c']);
    expect(moveItem(list, 1, 1)).toEqual(['a', 'c', 'b']);
    expect(moveItem(list, 0, -1)).toBe(list);
    expect(moveItem(list, 2, 1)).toBe(list);
    expect(list).toEqual(['a', 'b', 'c']);
  });

  test('removeItem drops only the chosen index and leaves the input alone', () => {
    const list = ['a', 'b', 'c'];
    expect(removeItem(list, 1)).toEqual(['a', 'c']);
    expect(list).toHaveLength(3);
  });
});
