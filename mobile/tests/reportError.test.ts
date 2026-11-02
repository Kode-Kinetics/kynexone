import test from 'node:test';
import assert from 'node:assert/strict';
import {
  noopReporter,
  reportBreadcrumb,
  reportError,
  scrub,
  setErrorReporter,
  isReportingEnabled,
  createSentryReporter,
  scrubSentryEvent,
  scrubSentryBreadcrumb,
  setErrorTrackingTenant,
} from '../src/observability/reportError.ts';

test('default reporter is a no-op and reporting is off', () => {
  setErrorReporter(null);
  assert.equal(isReportingEnabled(), false);
  assert.doesNotThrow(() => reportError(new Error('x')));
  assert.equal(noopReporter.captureException(new Error('x')), undefined);
});

test('scrub removes emails, tokens, IBANs and long numbers', () => {
  const dirty = 'ana@evostel.com Bearer abc.def.ghi SA0380000000608010167519 id 1234567890 net 9500';
  const clean = scrub(dirty);
  assert.ok(!clean.includes('ana@'));
  assert.ok(!clean.includes('abc.def'));
  assert.ok(!clean.includes('SA0380'));
  assert.ok(!clean.includes('1234567890'));
  assert.ok(!clean.includes('9500')); // a pay figure after a pay word is redacted too
});

test('an installed reporter receives scrubbed errors and context; a throwing reporter is contained', () => {
  const seen: any[] = [];
  setErrorReporter({
    captureException: (e, c) => seen.push([e.message, c]),
    addBreadcrumb: (m) => seen.push(m),
  });
  reportError(new Error('failed for ana@evostel.com'), { source: 'api.schema', tags: { route: 'ess.payslips' } });
  reportBreadcrumb('opened 1234567');
  assert.deepEqual(seen[0], ['failed for [email]', { source: 'api.schema', tags: { route: 'ess.payslips' } }]);
  assert.equal(seen[1], 'opened [number]');
  setErrorReporter({ captureException() { throw new Error('boom'); }, addBreadcrumb() { throw new Error('boom'); } });
  assert.doesNotThrow(() => reportError(new Error('x')));
  assert.doesNotThrow(() => reportBreadcrumb('x'));
  setErrorReporter(null);
});

test('the Sentry adapter maps context to string tags and the event scrubber drops request/user', () => {
  const calls: any[] = [];
  const reporter = createSentryReporter({
    captureException: (e, hint) => calls.push([e.message, hint]),
    addBreadcrumb: (b) => calls.push(b),
  });
  reporter.captureException(new Error('m'), { source: 's', tags: { n: 3, skip: undefined } });
  assert.deepEqual(calls[0], ['m', { tags: { source: 's', n: '3' } }]);
  const event = scrubSentryEvent({ request: { url: 'x' }, user: { email: 'a' }, message: 'a@b.co', exception: { values: [{ value: 'a@b.co' }] } });
  assert.equal('request' in event, false);
  assert.equal('user' in event, false);
  assert.equal(event.message, '[email]');
  assert.equal(event.exception.values[0].value, '[email]');
});

const IBAN = 'SA0380000000608010167519';
const NATIONAL_ID = '1087654321';
const IQAMA = '2123456789';
const EMAIL = 'first.last@example-corp.sa';
const SALARY = '15750.50';
// A fake token assembled at runtime so secret scanners do not flag the fixture.
const BEARER = ['eyJhbGciOiJIUzI1NiJ9', 'eyJzdWIiOiIxMjM0NTY3ODkwIn0', 'c2lnbmF0dXJlLXZhbHVl'].join('.');
const PAYROLL_URL = `https://api.example.test/api/payroll/runs?employee=${NATIONAL_ID}&iban=${IBAN}&salary=${SALARY}`;

test('beforeSend leaves no sensitive value anywhere on a full event', () => {
  const event = {
    message: `failed for ${EMAIL} iban ${IBAN} id ${NATIONAL_ID}`,
    transaction: `/payroll?salary=${SALARY}`,
    request: { url: PAYROLL_URL, query_string: `salary=${SALARY}`, cookies: BEARER, headers: { Authorization: `Bearer ${BEARER}` }, data: { iban: IBAN } },
    user: { email: EMAIL, username: 'first.last' },
    exception: { values: [{ type: 'Error', value: `bad row for ${EMAIL} iqama ${IQAMA}` }] },
    extra: { salary: 15750.5, note: `Bearer ${BEARER}`, iban: IBAN },
    breadcrumbs: [
      { category: 'fetch', data: { url: PAYROLL_URL } },
      { category: 'console', message: `log ${EMAIL}` },
      { category: 'navigation', data: { from: 'Home', to: 'Payslips' } },
    ],
    tags: { tenant_id: 'Acme Payroll Ltd', route: 'payslips' },
  };
  const out = scrubSentryEvent(event);
  const wire = JSON.stringify(out);
  for (const secret of [IBAN, NATIONAL_ID, IQAMA, EMAIL, SALARY, BEARER, 'first.last']) {
    assert.ok(!wire.includes(secret), `${secret} survived`);
  }
  assert.equal('request' in out, false);
  assert.equal('user' in out, false);
  assert.deepEqual(out.breadcrumbs, [{ category: 'navigation', data: { from: 'Home', to: 'Payslips' } }]);
  assert.equal('tenant_id' in out.tags, false, 'a tenant name is never kept');
});

test('breadcrumbs with a query string or personal data are dropped, clean ones kept', () => {
  assert.equal(scrubSentryBreadcrumb({ category: 'xhr', data: { url: PAYROLL_URL } }), null);
  assert.equal(scrubSentryBreadcrumb({ category: 'navigation', data: { to: `/x?q=${EMAIL}` } }), null);
  assert.notEqual(scrubSentryBreadcrumb({ category: 'ui.click', message: 'Save' }), null);
});

test('only an opaque GUID is ever handed to the reporter as the tenant', () => {
  const seen: (string | null)[] = [];
  setErrorReporter({ captureException() {}, addBreadcrumb() {}, setTenant: (id) => seen.push(id) });
  const guid = '3f2a1b4c-1234-4abc-9def-123456789012';
  setErrorTrackingTenant(guid);
  setErrorTrackingTenant('acme-payroll');
  setErrorTrackingTenant(undefined);
  assert.deepEqual(seen, [guid, null, null]);
  assert.equal(scrubSentryEvent({ tags: { tenant_id: guid } }).tags.tenant_id, guid);
  setErrorReporter(null);
});

test('query strings are redacted from free text; questions are not', () => {
  assert.ok(!scrub(`GET /api/payroll?salary=${SALARY}`).includes(SALARY));
  assert.equal(scrub('Why? Because.'), 'Why? Because.');
});

test('scrub redacts money amounts in free text', () => {
  for (const text of ['salary 15750.50 for March', 'net: 6,900.00', 'salary 15750', 'الراتب 12000', 'basic=8500']) {
    assert.doesNotMatch(scrub(text), /\d{3,}/);
  }
});
