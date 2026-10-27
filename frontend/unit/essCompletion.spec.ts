import { expect, test } from '@playwright/test';
import { getPathMatch } from 'next/dist/shared/lib/router/utils/path-match';
import nextConfig from '../next.config';
import { isStepUpRequired, problemOf, SECTION_ORDER } from '../src/api/essCompletion';
import { onboardingP2Ar, onboardingP2En } from '../src/i18n/onboardingP2';

type HeaderRule = { source: string; headers: { key: string; value: string }[] };
async function headersFor(pathname: string): Promise<Record<string, string>> {
  const rules = (await nextConfig.headers!()) as HeaderRule[];
  const out: Record<string, string> = {};
  for (const rule of rules) {
    if (!getPathMatch(rule.source, { strict: true, removeUnnamedParams: true })(pathname)) continue;
    for (const { key, value } of rule.headers) out[key.toLowerCase()] = value;
  }
  return out;
}

test('the ESS completion page cannot be framed at all', async () => {
  const headers = await headersFor('/ess/onboarding');
  expect(headers['x-frame-options']).toBe('DENY');
  expect(headers['content-security-policy']).toBe("frame-ancestors 'none'");
  expect(headers['cache-control']).toBe('no-store');
  // every other page keeps the same-origin framing the PDF previews rely on
  expect((await headersFor('/dashboard'))['x-frame-options']).toBe('SAMEORIGIN');
});

test('a step-up challenge is recognised, a wrong password is not one', () => {
  const challenge = { response: { status: 401, data: { code: 'step_up_required' } } };
  const wrong = { response: { status: 401, data: { code: 'invalid_credentials' } } };
  expect(isStepUpRequired(challenge)).toBe(true);
  expect(isStepUpRequired(wrong)).toBe(false);
  expect(isStepUpRequired(new Error('offline'))).toBe(false);
  expect(problemOf(wrong).code).toBe('invalid_credentials');
  expect(problemOf(undefined)).toEqual({});
});

test('every section has a title, purpose and hint in English and Arabic, and every state and error a label', () => {
  for (const s of SECTION_ORDER) for (const p of ['title', 'purpose', 'hint']) {
    const key = `ess.completion.section.${s}.${p}`;
    expect(onboardingP2En[key], key).toBeTruthy();
    expect(onboardingP2Ar[key], key).toMatch(/\p{Script=Arabic}/u);
  }
  for (const state of ['Missing', 'Confirm', 'Pending', 'ChangesRequested', 'Accepted', 'AwaitingFinance']) {
    expect(onboardingP2En[`ess.completion.state.${state}`], state).toBeTruthy();
    expect(onboardingP2Ar[`ess.completion.state.${state}`], state).toMatch(/\p{Script=Arabic}/u);
  }
  // Same key sets, and the Arabic keeps every {placeholder} of the English.
  expect(Object.keys(onboardingP2Ar).sort()).toEqual(Object.keys(onboardingP2En).sort());
  for (const [key, en] of Object.entries(onboardingP2En)) {
    const names = (s: string) => [...s.matchAll(/\{(\w+)\}/g)].map(m => m[1]).sort();
    expect(names(onboardingP2Ar[key]), key).toEqual(names(en));
  }
});
