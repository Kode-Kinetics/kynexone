# KynexOne Mobile

> Store identity: bundle/package id `com.kodekinetics.kynexone`, URL scheme `kynexone`, and Expo slug `kynexone-mobile`. These identifiers must not change after the first store release.

Enterprise-grade React Native / Expo mobile app for GCC HR operations. Supports Employee Self-Service, Ground Staff punch-in/out, Supervisor/Manager approvals, and HR/Payroll/Finance workflows.

---

## Tech Stack

| Layer | Choice |
|---|---|
| Framework | React Native 0.86 + Expo SDK 57 |
| Language | TypeScript (strict) |
| Styling | React Native StyleSheet / inline styles |
| State | Zustand |
| HTTP | Axios (JWT auto-refresh) |
| Forms | react-hook-form + zod |
| Navigation | React Navigation (bottom tabs + native stack) |
| i18n | react-i18next (English + Arabic / RTL) |
| Storage | expo-secure-store (tokens), AsyncStorage (prefs) |
| Auth | JWT + refresh token rotation |

---

## Prerequisites

- Node.js ≥ 20
- npm ≥ 9 or yarn
- Expo CLI is invoked through `npx`; EAS CLI is invoked through `npx eas-cli`
- iOS: Xcode 26+ (macOS only)
- Android: JDK 17 and Android SDK/API 36

---

## Environment Setup

Create a `.env` file in the project root (read by `app.config.js`, which owns `expo.extra`):

```env
# Backend base URL including /api (no trailing slash)
EXPO_PUBLIC_API_BASE_URL=http://localhost:5117/api
# Optional until the EAS project exists; push-token registration is skipped without it
EXPO_PUBLIC_EAS_PROJECT_ID=
```

On the iOS simulator `localhost` reaches the host machine; on a physical device use the host's LAN IP.

### Backend capabilities

`src/config/features.ts` lists mobile features whose backend endpoint does not exist yet
(file upload, profile photo, notification preferences, approval send-back, biometric sign-in).
Their controls are hidden or disabled while the flag is `false`; flip a flag only once the
endpoint is live.

### Dev screen tour (QA)

`src/dev/ScreenTour.tsx` drives every screen against a live backend without taps and logs
`[TOUR] STEP …` / `[TOUR] RESULT …` lines (plus every `[API] <status> <method> <path>`) to Metro:

```bash
# EXPO_PUBLIC_SCREEN_TOUR: employee | manager | quick
env EXPO_PUBLIC_API_BASE_URL=http://localhost:5117/api \
    EXPO_PUBLIC_SCREEN_TOUR=employee \
    EXPO_PUBLIC_TOUR_TENANT=intelliflow \
    EXPO_PUBLIC_TOUR_EMAIL=employee1@intelliflow.com \
    EXPO_PUBLIC_TOUR_PASSWORD='…' \
    npx expo start --clear
```

It performs real writes (punch, leave, overtime, HR request, approval…) — point it only at a
disposable stack. Never set `EXPO_PUBLIC_SCREEN_TOUR` for an EAS/store build.

---

## Installation

```bash
# Install dependencies
npm install

# Start Expo dev server
npx expo start

# Open in Expo Go or a development build (SDK 57)
npx expo start --ios

# Run on Android emulator
npx expo run:android
```

---

## Project Structure

```
src/
├── api/
│   ├── client.ts          # Axios instance, JWT injection, 401→refresh flow
│   ├── services.ts        # All API endpoint definitions
│   └── adapters.ts        # Screen-friendly wrappers over raw services
├── auth/
│   └── authStore.ts       # Zustand auth store (login, logout, RBAC)
├── config/
│   ├── index.ts           # ENV config, COLORS, SPACING, FONT_SIZES
│   └── i18n.ts            # English + Arabic translations
├── features/
│   ├── ai-assistant/      # AI HR chatbot screen
│   ├── approvals/         # Manager/Supervisor approval inbox
│   ├── attendance/        # Attendance history + correction requests
│   ├── auth/              # Login, ForgotPassword, ChangePassword
│   ├── dashboard/         # Employee, Manager dashboards + Team screen
│   ├── documents/         # Employee documents (upload, expiry alerts)
│   ├── leave/             # Apply leave
│   ├── notifications/     # Push notification screens + token registration
│   ├── overtime/          # Apply OT + history
│   ├── payslips/          # Payslip list + detail + PDF download
│   ├── profile/           # Employee profile + update requests
│   ├── requests/          # HR request creation + detail/comments
│   └── settings/          # App settings (biometric, language, theme)
├── navigation/
│   ├── RootNavigator.tsx  # Auth vs Main routing based on auth state
│   ├── AuthStack.tsx      # Login + ForgotPassword
│   └── MainTabs.tsx       # Role-based bottom tabs + More stack
├── permissions/
│   └── index.ts           # Role/permission hooks (usePermission, etc.)
├── storage/
│   └── index.ts           # Secure token storage + AsyncStorage helpers
├── types/
│   └── index.ts           # All TypeScript interfaces and enums
└── utils/
    ├── date.ts            # Date formatting (Gregorian; Hijri placeholder)
    └── device.ts          # Device ID generation + device info
app/
├── _layout.tsx            # Root layout (i18n, SafeArea, Navigator)
└── index.tsx              # Entry point
```

---

## Authentication Flow

1. User enters **Company ID** (tenant), **username**, **password** on `LoginScreen`
2. `authStore.login()` calls `POST /api/auth/login` with `{ email, password, tenantSlug }`
3. On success: access token + refresh token stored in `expo-secure-store`
4. The Axios client reads the configured API base URL and attaches the access token
5. All subsequent requests auto-inject `Authorization: Bearer <token>`
6. On 401: the client queues requests, rotates via `POST /api/auth/refresh`, then replays
7. Logout clears all stored tokens and navigates back to Auth stack

### Two-step sign-in (privileged users)

Response handling lives in `src/auth/mfaFlow.ts` (pure, unit-tested in `tests/mfaFlow.test.ts`).

- `/auth/login` returns `mfaRequired` + `challengeToken` → `MfaChallengeScreen` → `POST /auth/mfa/challenge/verify`.
- `/auth/login` returns `mfaEnrollmentRequired` + `enrollmentToken` (enforced, not enrolled) → `MfaEnrollmentScreen`; after the first code the user signs in again.
- Grace period: login returns tokens; `GET /auth/mfa/status` drives `MfaSetupBanner`, which starts enrolment via `POST /auth/mfa/enrollment/start`. Enrolling ends the session (the server rotates the session stamp).
- Every rejected code is the same 401, so the app counts attempts (server limit 5) and tracks the 300 s expiry itself.
- Same-phone enrolment: "Open authenticator app" (`otpauth://` link) first; the grouped key is selectable (long-press to copy) and has a "Share key" button (React Native core `Share`). The app never writes to the clipboard itself. While the setup screen is open, `expo-screen-capture` blocks screenshots and recordings and, on iOS, blurs the app-switcher snapshot. That module is native (see [Releasing](#releasing)). The signed-in sheet is an RN `Modal`, a separate dialog window on Android that copies `FLAG_SECURE` from the activity only when it is created. So `MfaSetupBanner` secures the window before opening the sheet and releases it after closing (`secureSheet.ts`, order unit-tested). **Confirm on a device with the first Android build:** a screenshot of the open sheet must come out blank.
- The setup key stays in component state only and is never logged or stored.
- Release: the sign-in and enrolment logic is JavaScript, so after the first store build, fixes to it ship with `npm run update:production` and no App Store review. That matters because managers who sign in only on mobile must not wait for review past the enforcement date. The one native piece, screen-capture protection, must be in the first store binary.

---

## Role-Based Access

Users have one of five roles:

| Role | Tab Layout |
|---|---|
| `EMPLOYEE` | Home · Attendance · Leave · Payslips · More |
| `GROUND_STAFF` | Home · Attendance · Leave · Payslips · More |
| `SUPERVISOR` | Home · Team · Approvals · Attendance · More |
| `MANAGER` | Home · Team · Approvals · Attendance · More |
| `HR_PAYROLL` / `FINANCE` | Home · Approvals · More |

Permissions are checked at component level via `usePermission(module, action)`.

---

## Internationalisation

The app ships with full English and Arabic translations in `src/config/i18n.ts`.

- Language toggle in **Settings → Language**
- RTL layout is forced via `I18nManager.forceRTL(true)` when Arabic is selected — an app restart is required for RTL to take full effect on React Native
- Hijri calendar formatting is stubbed; integrate `@khawarizmus/hijri-moment` or similar and implement the `TODO` in `src/utils/date.ts`

---

## Push Notifications

- Handled by `src/features/notifications/pushNotifications.ts`
- On login: `registerPushToken(deviceId)` requests permission, gets the Expo push token, and calls `POST /api/mobile/register-device`
- Deep-link routing: `getNotificationRoute(type)` maps notification types to screen names
- Configure APNs credentials (iOS) and FCM (Android) in the Expo dashboard before building for production

---

## Biometric Login

Biometric prompt (`expo-local-authentication`) is implemented as a UI placeholder. For production:

1. Add a `/api/auth/biometric-token` endpoint that issues a short-lived token after the user proves biometric identity server-side
2. Store the biometric token in `expo-secure-store`
3. Wire `handleBiometricLogin()` in `LoginScreen.tsx` to call that endpoint

---

## Offline Punch Queue

Clock-in/out attempts while offline are stored in `AsyncStorage` via `offlinePunchStorage` (`src/storage/index.ts`) and should be replayed on reconnect. Wire `NetInfo` connectivity events to flush the queue.

---

## Building for Production

```bash
# EAS Build (recommended)
npm install -g eas-cli
eas build --platform ios
eas build --platform android

# Local build
npx expo run:ios --configuration Release
npx expo run:android --variant release
```

Production EAS profiles set `EXPO_PUBLIC_APP_ENV=production` and the HTTPS API URL.

---

## Releasing

Every store binary carries an OTA channel (`expo-updates`): `updates.url` is `https://u.expo.dev/<EAS projectId>` (derived in `app.config.js` from the same project ID the build links to), it checks on launch (`ON_LOAD`) and never blocks startup (`fallbackToCacheTimeout: 0`; a downloaded update applies on the next launch). Channels: `production`, `preview`, and `development` (the `simulator` profile). `tests/updatesConfig.test.ts` guards this in CI.

**Binaries built before the OTA channel commit can never receive an OTA update.** They have no update URL, so they need a store build. The app was not in either store when the channel was added, so the first store binary carries it.

### Runtime version

The runtime version uses the **fingerprint** policy: a hash of native code and the resolved app config. An update reaches only binaries whose fingerprint matches.

- `app.config.js` copies the profile's `EXPO_PUBLIC_API_BASE_URL` and `EXPO_PUBLIC_APP_ENV` into `extra`, and `extra` stays in the fingerprint on purpose. An update built against a different API URL (for example localhost) gets a different runtime version and reaches nobody.
- `eas update` does not read `eas.json` build-profile `env`. **Only publish with `npm run update:<profile>`**, which replaces every `EXPO_PUBLIC_*` and `EXPO_UPDATES_CODE_SIGNING_CERT` value with exactly that profile's `eas.json` env, and refuses if `.env` would add one.
- `fingerprint.config.js` ignores `eas.json` itself, so editing build or submit plumbing does not cut installed binaries off from updates. Changing a profile's `env` still changes the fingerprint, through `extra`.
- `npm run updates:check` validates the config and prints the iOS and Android runtime version for each profile. Compare it with the build on expo.dev: if they differ, you need a store build.
- Publishing sets `EXPO_NO_DOTENV=1`, and both `app.config.js` and Expo then skip `.env` files. It also refuses while `.env`, `.env.local`, `.env.production`, `.env.production.local` or the preview/development equivalents define any `EXPO_PUBLIC_*` or `EXPO_UPDATES_CODE_SIGNING_CERT`. Every `build.*.env` key must be `EXPO_PUBLIC_*` or allow-listed with a reason in `tests/updatesConfig.test.ts`.
- **Before the first `npm run update:production`:** check that the runtime version on the expo.dev page for the production build equals the production line of `npm run updates:check`. Then run `eas env:list` (each environment) to confirm no `EXPO_PUBLIC_*` variables are set on the EAS dashboard: a dashboard value would reach the build but not the local publish.

### What ships how

**OTA (`npm run update:production`):** JavaScript/TypeScript and bundled assets, with the fingerprint unchanged.

**Store build (`eas build`):** anything that changes the fingerprint:
- adding, removing or upgrading a dependency with native code (any `expo-*` module, `react-native-*`, the Expo SDK). Example: `expo-screen-capture`, which blocks screenshots of the MFA setup key, is native;
- config plugins (`plugins` in `app.json`);
- native keys in `app.json`/`app.config.js`: `ios`, `android`, permissions, `infoPlist`, `scheme`, icons/splash, `version`;
- a profile's `env` in `eas.json`, the code-signing certificate, and `.gitignore` (part of the fingerprint).

```bash
# Ship JS/asset changes to production binaries (the only supported way)
MSG="Fix: clearer two-step sign-in errors" npm run update:production
MSG="..." npm run update:preview

# New store binaries (native change, or first release)
eas build --profile production --platform all

# Roll back to an earlier, known-good update
eas update:list --branch production
eas update:republish --group <update-group-id>

# Roll back to the JS bundled inside the store binary. Fingerprints differ per
# platform, so run once per platform with that platform's runtime version
# (from npm run updates:check). Add --private-key-path once signing is on.
eas update:roll-back-to-embedded --branch production --platform ios --runtime-version <ios-runtime> --message "Roll back"
eas update:roll-back-to-embedded --branch production --platform android --runtime-version <android-runtime> --message "Roll back"
```

### Code signing (decide before the first store build)

Signed updates let the app reject any update not signed with our key, even one served by a compromised Expo account or CDN. The certificate is baked into the binary, so **turning signing on or off later needs a new store build**. Make the decision before the first store build.

The wiring is in `app.config.js` and is off unless `EXPO_UPDATES_CODE_SIGNING_CERT` is set. When it is set, it adds `updates.codeSigningCertificate` and `codeSigningMetadata: { keyid: 'main', alg: 'rsa-v1_5-sha256' }`.

1. Generate the key pair once, outside the repo:
   `npx expo-updates codesigning:generate --key-output-directory ~/kynexone-keys --certificate-output-directory certs --certificate-validity-duration-years 10 --certificate-common-name "KynexOne"`
2. **The private key (`private-key.pem`) must never be in this repo.** `.gitignore` ignores `keys/` and every `*.pem` except `certs/certificate.pem`. Keep it in the team secret manager; only the machine or CI job that publishes updates gets it.
3. Commit the public `certs/certificate.pem`. Add `"EXPO_UPDATES_CODE_SIGNING_CERT": "./certs/certificate.pem"` to the `env` of every build profile in `eas.json`.
4. Publish with `EXPO_UPDATES_CODE_SIGNING_KEY=/path/to/private-key.pem MSG="..." npm run update:production`. The script adds `--private-key-path` and refuses to publish without it.

Losing the private key means signed binaries can never be updated again. Back it up.

## TypeScript

```bash
# Type check (zero errors expected)
npx tsc --noEmit
```

The codebase runs clean with `strict: true`. All API shapes, navigation params, and component props are fully typed.

---

## Known TODOs

| Area | Description |
|---|---|
| Hijri calendar | Integrate a Hijri date library in `src/utils/date.ts` |
| Biometric backend | Implement `/api/auth/biometric-token` endpoint |
| Offline punch replay | Wire `NetInfo` to flush `offlinePunchStorage` |
| Leave history screen | Dedicated screen for leave request history |
| APNs / FCM certs | Configure in Expo dashboard before production build |
| WPS compliance | UAE Wages Protection System export from payslip detail |

---

## Branding

| Token | Value |
|---|---|
| Navy | `#0B1020` |
| Blue (primary) | `#2F6BFF` |
| Cyan (accent) | `#5EEBFF` |
| Background | `#F8FAFC` |
| Success | `#00C896` |
