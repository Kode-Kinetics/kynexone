import { test, expect } from '@playwright/test';
import {
  COMPANY_SETUP_RETURN_KEY, COMPANY_SETUP_RETURN_TTL_MS, companySetupReturnHref, forgetCompanySetupReturn, isCompanySetupPath,
  parseCompanySetupReturn, readCompanySetupReturn, rememberCompanySetupReturn, withCompanySetupReturn,
} from '../src/components/setup/companySetupReturn';

// "Back to company setup": the address is rebuilt from validated GUIDs and an allow-listed part, never followed raw.

const DRAFT = '7b0c8a52-1f7e-4a8e-9a39-0d6f2b4b8c11';
const COMPANY = '3f2504e0-4f89-11d3-9a0c-0305e82c3301';
const q = (query: string) => new URLSearchParams(query);

function memoryStore() {
  const map = new Map<string, string>();
  return { map, getItem: (k: string) => map.get(k) ?? null, setItem: (k: string, v: string) => { map.set(k, v); }, removeItem: (k: string) => { map.delete(k); } };
}

test('only the two wizard routes count as company setup', () => {
  expect(isCompanySetupPath('/setup/new-company')).toBe(true);
  expect(isCompanySetupPath(`/setup/company/${COMPANY}/complete`)).toBe(true);
  expect(isCompanySetupPath('/setup')).toBe(false);
  expect(isCompanySetupPath('/shifts')).toBe(false);
  expect(isCompanySetupPath(null)).toBe(false);
});

test('a valid return parses and rebuilds the wizard address', () => {
  const existing = parseCompanySetupReturn(q(`tab=x&returnTo=companySetup&company=${COMPANY}&draft=${DRAFT}&part=moreSettings`));
  expect(existing).toEqual({ draftId: DRAFT, companyId: COMPANY, part: 'moreSettings' });
  expect(companySetupReturnHref(existing!)).toBe(`/setup/company/${COMPANY}/complete?draft=${DRAFT}&part=moreSettings`);
  const created = parseCompanySetupReturn(q(`returnTo=companySetup&draft=${DRAFT}&part=leave`));
  expect(companySetupReturnHref(created!)).toBe(`/setup/new-company?draft=${DRAFT}&part=leave`);
});

test('anything outside the allow-list is ignored, never redirected to', () => {
  for (const bad of [
    `returnTo=https://evil.test&draft=${DRAFT}`,
    `returnTo=companySetup&draft=../../evil&part=moreSettings`,
    `returnTo=companySetup&draft=${DRAFT}&company=//evil.test&part=moreSettings`,
    `returnTo=companySetup&draft=${DRAFT}&part=javascript:alert(1)`,
    `returnTo=companySetup&part=moreSettings`,
    `draft=${DRAFT}&part=moreSettings`,
  ]) expect(parseCompanySetupReturn(q(bad)), bad).toBeNull();
});

test('links leaving the wizard carry the return; a bad value leaves the link alone', () => {
  expect(withCompanySetupReturn('/shifts', { draftId: DRAFT, companyId: COMPANY, part: 'moreSettings' }))
    .toBe(`/shifts?returnTo=companySetup&company=${COMPANY}&draft=${DRAFT}&part=moreSettings`);
  expect(withCompanySetupReturn('/setup?tab=statutoryRules#top', { draftId: DRAFT, part: 'moreSettings' }))
    .toBe(`/setup?tab=statutoryRules&returnTo=companySetup&draft=${DRAFT}&part=moreSettings#top`);
  expect(withCompanySetupReturn('/shifts', { draftId: 'draft-1', part: 'moreSettings' })).toBe('/shifts');
  expect(withCompanySetupReturn('/shifts', null)).toBe('/shifts');
});

test('the per-tab fallback expires after 12 hours and drops tampered values', () => {
  const store = memoryStore();
  rememberCompanySetupReturn({ draftId: DRAFT, part: 'moreSettings' }, store, 0);
  expect(readCompanySetupReturn(store, COMPANY_SETUP_RETURN_TTL_MS)).toEqual({ draftId: DRAFT, part: 'moreSettings' });
  expect(readCompanySetupReturn(store, COMPANY_SETUP_RETURN_TTL_MS + 1)).toBeNull();
  expect(store.map.has(COMPANY_SETUP_RETURN_KEY)).toBe(false);
  store.setItem(COMPANY_SETUP_RETURN_KEY, JSON.stringify({ draftId: 'x', part: 'moreSettings', at: 0 }));
  expect(readCompanySetupReturn(store, 1)).toBeNull();
  store.setItem(COMPANY_SETUP_RETURN_KEY, '{oops');
  expect(readCompanySetupReturn(store, 1)).toBeNull();
});

test('storage that throws never breaks the page', () => {
  const throwing = { getItem: () => { throw new Error('denied'); }, setItem: () => { throw new Error('denied'); }, removeItem: () => { throw new Error('denied'); } };
  expect(() => rememberCompanySetupReturn({ draftId: DRAFT, part: 'leave' }, throwing)).not.toThrow();
  expect(readCompanySetupReturn(throwing)).toBeNull();
  expect(() => forgetCompanySetupReturn(throwing)).not.toThrow();
  expect(readCompanySetupReturn(null)).toBeNull();
});
