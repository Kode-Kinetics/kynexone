import type { CustomBankColumn, CustomBankTemplateDefinition } from '../api/saudiBankExports';

// Pure helpers for the custom bank template editor (no React, no network), so they can be unit tested.

export function blankColumn(field: string): CustomBankColumn {
  return {
    field, literal: field === 'literal' ? '' : null, title: '', width: null, align: 'left', pad: ' ', amountDecimals: 2,
    decimalSeparator: '.', thousandsSeparator: '', dateFormat: 'yyyyMMdd', upperCase: false, maxLength: null, allowBlank: false,
  };
}

export function blankDefinition(): CustomBankTemplateDefinition {
  return {
    name: '', sampleReference: '', layout: 'delimited', encoding: 'utf-8', lineEnding: 'crlf', endWithLineBreak: true,
    fileExtension: 'csv', delimiter: ',', quoteRule: 'minimal', headerRow: false,
    headerRecord: [], columns: [blankColumn('beneficiaryIban'), blankColumn('netPay')], trailerRecord: [],
  };
}

/** Pure list moves used by the column editor (kept pure so they can be unit tested). */
export function moveItem<T>(list: T[], index: number, delta: -1 | 1): T[] {
  const to = index + delta;
  if (index < 0 || index >= list.length || to < 0 || to >= list.length) return list;
  const next = list.slice();
  [next[index], next[to]] = [next[to], next[index]];
  return next;
}

export function removeItem<T>(list: T[], index: number): T[] {
  return list.filter((_, i) => i !== index);
}
