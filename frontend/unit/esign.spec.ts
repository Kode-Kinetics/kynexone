import { expect, test } from '@playwright/test';
import { getPathMatch } from 'next/dist/shared/lib/router/utils/path-match';
import nextConfig from '../next.config';
import { asSignatureRows, problemOf, sha256Hex } from '../src/api/esign';
import { onboardingP4Ar, onboardingP4En } from '../src/i18n/onboardingP4';
import { ESS_SECTIONS, activeEssPage } from '../src/routes/essSections';

test('a problem body is read from the response, and nothing else is trusted', () => {
  expect(problemOf({ response: { data: { code: 'typed_name_mismatch', titleKey: 'esign.error.typed_name_mismatch' } } }).code).toBe('typed_name_mismatch');
  expect(problemOf({ response: { data: 'oops' } })).toEqual({});
  expect(problemOf(null)).toEqual({});
});

test('the hash is of the exact bytes shown (SHA-256, lower-case hex)', async () => {
  const empty = await sha256Hex(new Blob([]), 'fallback');
  expect(empty).toBe('e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855');
  expect(await sha256Hex(new Blob(['abc']), 'fallback')).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
});

test('every English entry has an Arabic one with the same placeholders, and Arabic text is Arabic', () => {
  expect(Object.keys(onboardingP4En).filter(k => !(k in onboardingP4Ar))).toEqual([]);
  expect(Object.keys(onboardingP4Ar).filter(k => !(k in onboardingP4En))).toEqual([]);
  const placeholders = (s: string) => (s.match(/\{\w+\}/g) ?? []).sort().join(',');
  for (const [k, en] of Object.entries(onboardingP4En)) {
    expect(placeholders(onboardingP4Ar[k]), k).toBe(placeholders(en));
    expect(onboardingP4Ar[k], k).toMatch(/[؀-ۿ]/);
  }
});

test('the wording never claims more than an acknowledgement', () => {
  const all = [...Object.values(onboardingP4En)].join(' ').toLowerCase();
  expect(all).not.toMatch(/legally binding|digitally signed|e-?signature/);
  // wherever "qualified" appears it is in a "not a qualified" sentence
  for (const v of Object.values(onboardingP4En)) if (/qualified/i.test(v)) expect(v.toLowerCase()).toMatch(/not a qualified/);
});

test('the sign pages sit under the Requests section of the employee workspace', () => {
  expect(activeEssPage('/ess/sign')?.path).toBe('/ess/sign');
  expect(activeEssPage('/ess/sign/3f2b')?.path).toBe('/ess/sign');
  expect(ESS_SECTIONS.find(s => s.pages.some(p => p.path === '/ess/sign'))?.id).toBe('requests');
});

test('the sign pages cannot be framed at all, and never cached; the rest of the site keeps same-origin framing for PDF previews', async () => {
  const rules = (await nextConfig.headers!()) as { source: string; headers: { key: string; value: string }[] }[];
  const headersFor = (pathname: string) => {
    const out: Record<string, string> = {};
    for (const rule of rules) if (getPathMatch(rule.source, { strict: true, removeUnnamedParams: true })(pathname)) for (const { key, value } of rule.headers) out[key.toLowerCase()] = value;
    return out;
  };
  for (const path of ['/ess/sign', '/ess/sign/3f2b9c1e-0000-4000-8000-000000000001']) {
    const h = headersFor(path);
    expect(h['x-frame-options'], path).toBe('DENY');
    expect(h['content-security-policy'], path).toBe("frame-ancestors 'none'");
    expect(h['cache-control'], path).toBe('no-store');
  }
  expect(headersFor('/ess/documents')['x-frame-options']).toBe('SAMEORIGIN');
  // no rule relaxes the policy for the preview: nothing sets frame-src, default-src or child-src anywhere
  expect(rules.flatMap(r => r.headers).filter(h => /frame-src|default-src|child-src/.test(h.value))).toEqual([]);
});

test('a list answer that is not an array reads as no rows, never a crash', () => {
  expect(asSignatureRows({ items: [], total: 0 })).toEqual([]);
  expect(asSignatureRows(undefined)).toEqual([]);
  expect(asSignatureRows([{ documentId: 'd1' }])).toHaveLength(1);
});
