import { expect, test } from '@playwright/test';
import { messageParts, MESSAGE_PART_MARK } from '../src/lib/messageParts';

test.describe('messageParts', () => {
  const m = (name: string) => `${MESSAGE_PART_MARK.open}${name}${MESSAGE_PART_MARK.close}`;

  test('splits a translated sentence into literal text and the named values, in the translation\'s order', () => {
    const parts = messageParts(`الحساب الاحتياطي ${m('account')}`, { account: '5001 — Basic Salary Expense' });
    expect(parts).toEqual([{ text: 'الحساب الاحتياطي ' }, { text: '5001 — Basic Salary Expense', value: true }]);
  });

  test('keeps the order of a translation that puts the values first, and several values', () => {
    const parts = messageParts(`${m('b')} ثم ${m('a')}`, { a: 'A1', b: 'B2' });
    expect(parts.map((p) => p.text)).toEqual(['B2', ' ثم ', 'A1']);
    expect(parts.filter((p) => p.value).map((p) => p.text)).toEqual(['B2', 'A1']);
  });

  test('a sentence with no value is one literal part, and an unknown name stays as written', () => {
    expect(messageParts('Hello', {})).toEqual([{ text: 'Hello' }]);
    expect(messageParts(`x ${m('nope')}`, {})).toEqual([{ text: 'x ' }, { text: 'nope' }]);
  });

  test('the same name used twice yields two value parts', () => {
    expect(messageParts(`${m('a')}/${m('a')}`, { a: 'Z' }).filter((p) => p.value)).toHaveLength(2);
  });
});
