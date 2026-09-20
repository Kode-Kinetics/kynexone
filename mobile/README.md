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
