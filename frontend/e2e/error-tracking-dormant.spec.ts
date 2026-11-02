import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

// Source lint: error tracking must stay dormant without a DSN. Needs neither a browser nor the docker stack.
const root = join(__dirname, '..');
const read = (file: string) => readFileSync(join(root, file), 'utf8');

test('the SDK is only ever imported lazily, behind a DSN check', () => {
  for (const file of ['instrumentation.ts', 'instrumentation-client.ts', 'src/lib/errorTracking/index.ts']) {
    const code = read(file);
    expect(code, `${file} must not import @sentry/nextjs statically`).not.toMatch(/^\s*import\s[^;]*from\s+['"]@sentry\/nextjs['"]/m);
  }
  expect(read('instrumentation-client.ts')).toMatch(/if \(dsn\)/);
  expect(read('instrumentation.ts')).toMatch(/if \(!dsn\) return;/);
});

test('source maps upload only when SENTRY_AUTH_TOKEN is set', () => {
  const config = read('next.config.ts');
  expect(config).toMatch(/if \(!process\.env\.SENTRY_AUTH_TOKEN\?\.trim\(\)\) return config;/);
  expect(config).not.toMatch(/^import .*withSentryConfig/m);
});

test('no DSN is committed', () => {
  for (const file of ['instrumentation.ts', 'instrumentation-client.ts', 'next.config.ts', 'vercel.json', 'src/lib/errorTracking/options.ts']) {
    expect(read(file), file).not.toMatch(/https:\/\/[0-9a-f]{20,}@/i);
  }
});

test('the response headers set no script or connect policy, so no ingest host needs allowing', () => {
  // next.config.ts sends only "frame-ancestors 'self'" and middleware.ts sets no CSP. If a connect-src is ever
  // added it must list the Sentry ingest host only when a DSN is set; this fails to prompt that review.
  expect(read('next.config.ts')).not.toMatch(/connect-src/);
  expect(read('middleware.ts')).not.toMatch(/Content-Security-Policy|connect-src/);
});
