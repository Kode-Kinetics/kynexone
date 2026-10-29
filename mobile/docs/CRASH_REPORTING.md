# Crash reporting

The app calls `reportError(error, { source, tags })` (`src/observability/reportError.ts`). By
default this is a **no-op**: nothing leaves the device. A Sentry reporter is installed only when a
DSN is configured.

## What the owner needs to do

1. Create a Sentry account and a React Native project (EU data region if customer data residency
   requires it). Do not enable "send default PII".
2. Set, as EAS environment variables for the `preview` and `production` profiles (not in the repo):
   - `EXPO_PUBLIC_SENTRY_DSN` - the project DSN
   - `EXPO_PUBLIC_SENTRY_TRACES_SAMPLE_RATE` - optional, 0 to 1; unset means no performance data
   - `SENTRY_ORG`, `SENTRY_PROJECT`, `SENTRY_AUTH_TOKEN` - for source-map upload at build time
3. Build new store binaries. `@sentry/react-native` is a native module, so it ships with a store
   build (the runtime fingerprint changes); an OTA update alone cannot turn it on.
4. Add Sentry as a processor in the privacy policy, and review the `NSPrivacyCollectedData*`
   entries in `app.json` (`ios.privacyManifests`) with the final data list before App Store review.

## What is and is not sent

- Sent: exception type, scrubbed message and stack, the route name of an API shape mismatch,
  `runtimeVersion`, `updateId`, app version, environment.
- Never sent: request or response bodies, headers, URLs with query strings, user object, screenshots, view hierarchy, console output, failed-request events. Breadcrumbs carrying a query string or personal data are dropped. The only tenant tag is an opaque GUID.
- Redaction: emails,
  bearer tokens, JWTs, IBANs and runs of 6+ digits are redacted from messages and stacks
  (`scrub`, `scrubSentryEvent`).

## Using it

```ts
import { reportError } from '@/observability/reportError';
reportError(error, { source: 'payslips.load' });
```

Wired already: the root ErrorBoundary, API response-shape mismatches (`api.schema`), and OTA
crash-loops (`updates.crashLoop`).

## OTA safety

Wiring Sentry added no native dependency (`@sentry/react-native` was already declared), so the runtime
fingerprint is unchanged and these edits ship as an ordinary JS-only update (`docs/OTA_SAFETY.md`). With no
`EXPO_PUBLIC_SENTRY_DSN` the adapter stays the no-op and the SDK is never required. A binary built before the
module was linked cannot report even if an update carries a DSN; the `try/catch` in `initErrorReporting` keeps
that case from affecting startup.
