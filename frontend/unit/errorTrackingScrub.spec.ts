import { test, expect } from '@playwright/test';
import { buildSentryOptions } from '../src/lib/errorTracking/options';
import { isOpaqueTenantId, parseSampleRate, scrubBreadcrumb, scrubEvent, scrubText } from '../src/lib/errorTracking/scrub';

// The scrub is the single point every error-tracking event passes through (browser and server). Fake values only.
const IBAN = 'SA0380000000608010167519';
const NATIONAL_ID = '1087654321';
const IQAMA = '2123456789';
const EMAIL = 'first.last@example-corp.sa';
const SALARY = '15750.50';
// A fake token assembled at runtime so secret scanners do not flag the fixture.
const BEARER = ['eyJhbGciOiJIUzI1NiJ9', 'eyJzdWIiOiIxMjM0NTY3ODkwIn0', 'c2lnbmF0dXJlLXZhbHVl'].join('.');
const SECRETS = [IBAN, NATIONAL_ID, IQAMA, EMAIL, SALARY, BEARER, 'first.last'];
const PAYROLL_URL = `https://app.example.test/api/payroll/runs?employee=${NATIONAL_ID}&iban=${IBAN}&salary=${SALARY}`;

test('a full event keeps none of the sensitive values', () => {
  const event = {
    message: `failed for ${EMAIL} iban ${IBAN} id ${NATIONAL_ID}`,
    transaction: `/payroll?salary=${SALARY}`,
    request: {
      url: PAYROLL_URL,
      query_string: `salary=${SALARY}&iban=${IBAN}`,
      cookies: { session: BEARER },
      data: { iban: IBAN, salary: SALARY },
      headers: { Authorization: `Bearer ${BEARER}`, Cookie: `s=${BEARER}` },
    },
    user: { email: EMAIL, username: 'first.last', ip_address: '203.0.113.9' },
    exception: { values: [{ type: 'Error', value: `bad row for ${EMAIL} iqama ${IQAMA}` }] },
    extra: { salary: 15750.5, note: `Bearer ${BEARER}`, iban: IBAN },
    breadcrumbs: [
      { category: 'fetch', data: { url: PAYROLL_URL } },
      { category: 'console', message: `log ${EMAIL}` },
      { category: 'navigation', data: { from: '/people', to: '/payroll' } },
    ],
    tags: { tenant_id: 'Acme Payroll Ltd', route: '/payroll' },
  };

  const out = scrubEvent(event) as Record<string, any>;
  const wire = JSON.stringify(out);
  for (const secret of SECRETS) expect(wire).not.toContain(secret);

  expect(out.request.url).toBe('https://app.example.test/api/payroll/runs');
  expect(out.request.data).toBeUndefined();
  expect(out.request.cookies).toBeUndefined();
  expect(out.request.headers).toBeUndefined();
  expect(out.request.query_string).toBeUndefined();
  expect(out.user).toBeUndefined();
  expect(out.breadcrumbs).toEqual([{ category: 'navigation', data: { from: '/people', to: '/payroll' } }]);
  expect(out.tags.tenant_id, 'a tenant name is never kept').toBeUndefined();
});

test('text scrub redacts each personal-data shape on its own', () => {
  for (const secret of [IBAN, NATIONAL_ID, IQAMA, EMAIL, BEARER]) {
    expect(scrubText(`value ${secret} end`)).not.toContain(secret);
  }
  expect(scrubText(`Authorization: Bearer ${BEARER}`)).toBe('Authorization: Bearer [redacted]');
  expect(scrubText(`GET /api/payroll?salary=${SALARY}`)).not.toContain(SALARY);
  expect(scrubText('Why? Because it failed.')).toBe('Why? Because it failed.');
  expect(scrubText('request 3f2a1b4c-1234-4abc-9def-123456789012')).toContain('3f2a1b4c-1234-4abc-9def-123456789012');
});

test('breadcrumbs with a query string or personal data are dropped, clean ones kept', () => {
  expect(scrubBreadcrumb({ category: 'xhr', data: { url: PAYROLL_URL } })).toBeNull();
  expect(scrubBreadcrumb({ category: 'navigation', data: { to: `/people?q=${EMAIL}` } })).toBeNull();
  expect(scrubBreadcrumb({ category: 'ui.click', message: 'button.save' })).not.toBeNull();
});

test('the only tenant tag kept is an opaque GUID', () => {
  const guid = '3f2a1b4c-1234-4abc-9def-123456789012';
  expect(isOpaqueTenantId(guid)).toBe(true);
  expect(isOpaqueTenantId('Acme Payroll Ltd')).toBe(false);
  expect(isOpaqueTenantId(EMAIL)).toBe(false);
  expect((scrubEvent({ tags: { tenant_id: guid } }) as any).tags.tenant_id).toBe(guid);
});

test('options: no default PII, no replay, tracing off unless the rate is set, scrub wired in', () => {
  const options = buildSentryOptions({ dsn: 'https://publickey@o0.ingest.example.test/1' });
  expect(options.sendDefaultPii).toBe(false);
  expect(options.tracesSampleRate).toBe(0);
  expect(options.replaysSessionSampleRate).toBe(0);
  expect(options.replaysOnErrorSampleRate).toBe(0);
  expect(options.tracePropagationTargets).toEqual([]);
  expect(options.beforeSend).toBe(scrubEvent);
  expect(options.beforeBreadcrumb).toBe(scrubBreadcrumb);
  expect(buildSentryOptions({ dsn: 'x', tracesSampleRate: '0.1' }).tracesSampleRate).toBe(0.1);
  for (const bad of [undefined, '', 'abc', '2', '-1']) expect(parseSampleRate(bad)).toBe(0);
});

test('money amounts in free text are redacted', () => {
  for (const text of ['salary 15750.50 for March', 'net: 6,900.00', 'salary 15750', 'الراتب 12000', 'basic=8500']) {
    expect(scrubText(text)).not.toMatch(/\d{3,}/);
  }
  expect(scrubText('Why? Because the run 3 failed')).toBe('Why? Because the run 3 failed');
});
