# KynexOne Mobile — Build 6 Product Program

Build 6 is the productization release after the native iOS lifecycle fix. It is not a visual-only refresh.

## Parallel workstreams

1. **Mobile product architecture** — navigation, offline state, API contracts, caching, session reliability, feature flags, role-aware composition.
2. **UI/UX & design system** — Liquid Glass hierarchy, motion, haptics, contextual actions, accessibility, RTL/Arabic, dark mode, responsive device coverage.
3. **Attendance assurance** — front-camera selfie, GPS/geofence, device identity, evidence retention, QR/kiosk readiness, offline punch reconciliation.
4. **Employee/manager parity** — directory, roster, Who's Off, My Transactions, tasks, letters, announcements, richer approvals, performance/OKR actions.
5. **AI-native workflows** — permission-aware employee and manager actions with confirmation, auditability, fallback, and explainable results.
6. **Release/SDET/security** — real-device iOS/Android E2E, API-negative paths, privacy review, performance, crash-free launch, App Store/Play gates.

## Product principles

- Functional glass, not glass everywhere.
- Important actions complete in the fewest reasonable taps.
- Every screen has loading, empty, offline, permission-denied, validation, and API-failure states.
- No mock fallback in production for business data.
- Every sensitive action is tenant-scoped, permission-checked, and auditable.
- Camera evidence is verification-only; no emotion, age, gender, or unrelated face inference.
- Country capabilities plug in through KSA/UAE/GCC country packs rather than app forks.

## Build 6 P0

- Front-camera attendance evidence with GPS and device verification.
- Fix public-login 401 handling and complete end-to-end production login verification.
- Employee directory + team hierarchy.
- Schedule/roster + Who's Off.
- Unified My Transactions timeline.
- Actionable push notifications.
- HR letters/documents workflow.
- Manager command center improvements.
- Context menus / Haptic Touch for key cards and actions.
- Accessibility, Arabic/RTL, dark mode, reduced motion, and lower-end Android performance pass.

## Acceptance gates

Build 6 is not release-ready until it passes: TypeScript/lint/Expo Doctor, backend build/tests, iOS archive validation, Android release build, physical-device smoke, real API login, attendance selfie punch, leave, approvals, payslip, documents, notifications, profile, manager flows, accessibility sweep, and production endpoint verification.
