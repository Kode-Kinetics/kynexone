import { expect, test } from '@playwright/test';
import { erpFormatDisclaimer, erpFormatName, erpOptionText } from '../src/lib/erpFormatText';
import { translate } from '../src/i18n/translations';

type P = Record<string, string | number | null | undefined>;
const en = (key: string, params?: P) => translate('en', key, params);
const ar = (key: string, params?: P) => translate('ar', key, params);
const latin = (s: string) => s.match(/[A-Za-z]{4,}/g) ?? [];

const GENERIC = 'Generic journal CSV (line-per-row)';
const DISCLAIMER = "Neutral line-level export. Debit and Credit are separate columns and exactly one is non-zero per row. Import using AccountingDate (not SystemPostedDate) — SystemPostedDate is when our system wrote the row, AccountingDate is the accounting month the line belongs to. CompanyCode is per line (the company's ERP code); ExternalAccountCode is your ERP account when one is mapped. ExportId + EntryId + DR/CR identify a line uniquely: use them to refuse a second import of the same file. When any line carries a cost centre the file ends with CostCentre (code), CostCentreName and CostCentreErpCode (the ERP cost-centre value: the cost centre's ERP segment when set, else its code); they are blank on lines with no cost centre, and absent when no line has one.";
const SUFFIX = ' (template – validate in your ERP test instance)';

test.describe('ERP export format text', () => {
  test('English is exactly what the API sends', () => {
    expect(erpFormatName(en, GENERIC)).toBe(GENERIC);
    expect(erpFormatDisclaimer(en, DISCLAIMER)).toBe(DISCLAIMER);
    expect(erpFormatName(en, 'SAP S/4HANA Upload General Journal Entries (CSV)' + SUFFIX)).toBe('SAP S/4HANA Upload General Journal Entries (CSV)' + SUFFIX);
  });

  test('the generic format reads in Arabic, keeping column names as code', () => {
    expect(latin(erpFormatName(ar, GENERIC))).toEqual([]);
    const text = erpFormatDisclaimer(ar, DISCLAIMER);
    expect(text).not.toBe(DISCLAIMER);
    // Column names stay Latin (they are the file's own headers); everything else is Arabic.
    expect(latin(text.replace(/AccountingDate|SystemPostedDate|CompanyCode|ExternalAccountCode|ExportId|EntryId|CostCentreName|CostCentreErpCode|CostCentre/g, '').replace(/[⁦⁩]/g, ''))).toEqual([]);
  });

  test('a template format keeps its product name and translates the template note', () => {
    const name = erpFormatName(ar, 'Xero manual journal import CSV' + SUFFIX);
    expect(name).toContain('Xero');
    expect(name).not.toContain('template');
  });

  test('the option label and description read in Arabic', () => {
    const label = erpOptionText(ar, 'UTF-8 byte-order mark (for Excel)');
    expect(label).not.toBe('UTF-8 byte-order mark (for Excel)');
    expect(erpOptionText(ar, 'true adds a UTF-8 byte-order mark so Excel opens Arabic text correctly. Leave it off (false) for an ERP import.')).not.toMatch(/Leave it off/);
    expect(erpOptionText(ar, 'Some option nobody translated')).toBe('Some option nobody translated');
  });
});
