# OTA update safety runbook

Three layers protect the phones that hold payroll data from a bad or malicious JS update.

## 1. Signed updates (owner action, before the next store build)

The wiring already exists in `app.config.js` and is off until a certificate is configured. The
certificate is baked into the binary, so turning signing on needs a new store build, and it should
be done before more binaries ship. Without it, anyone who controls the Expo account or the update
CDN can push code to every installed app.

1. On a trusted machine, outside this repo:
   `npx expo-updates codesigning:generate --key-output-directory ~/kynexone-keys --certificate-output-directory certs --certificate-validity-duration-years 10 --certificate-common-name "KynexOne"`
2. Store `~/kynexone-keys/private-key.pem` in the company password manager (two named holders). It is
   never committed (`*.pem` is git-ignored except `certs/certificate.pem`) and never put in CI logs.
3. Commit only the public `mobile/certs/certificate.pem`.
4. Add `"EXPO_UPDATES_CODE_SIGNING_CERT": "./certs/certificate.pem"` to the `env` of every build
   profile in `eas.json`.
5. Build and submit new store binaries. Publish updates with
   `EXPO_UPDATES_CODE_SIGNING_KEY=/path/to/private-key.pem MSG="..." npm run update:production`
   (the script refuses to publish a signed profile without the key).
6. Turn the reminder into a gate: set `REQUIRE_CODE_SIGNING=1` for `npm run store:check` in CI.
   Until then `store:check` prints a warning.

Key loss means no further OTA updates reach signed binaries until a new store build ships with a new
certificate, so keep the escrow copy.

## 2. Startup health check (in the app)

`src/updates/startupHealth.ts` + `launchHealth.ts`. expo-updates already falls back to the previous
or embedded bundle when an update throws a JS error very early in launch. This adds the cases it
cannot see: an update that renders into the ErrorBoundary, or crash-loops after that window.

- Each launch of an OTA update is recorded. It becomes "healthy" only after the app has stayed up
  for 8 seconds with no ErrorBoundary hit.
- If the same update fails to become healthy twice in a row, the app reports it
  (`updates.crashLoop`), asks the update server for its current verdict (a fix, or the
  rollback-to-embedded directive) and reloads straight into it instead of waiting for the next
  launch.
- The SDK does not let the client choose the embedded bundle by itself; the server directive does
  that. So the health check only helps if you publish the rollback (below).

## 3. Rolling back a bad update (operator steps)

1. Confirm the bad update id in crash reporting (events are tagged `updateId` and `runtimeVersion`).
2. Roll back the channel to the embedded bundle:
   `npx eas-cli update:rollback --channel production --message "Roll back <id>"`
   (or republish the last good update with `npm run update:production`).
3. Phones pick the directive up on the next launch check; phones already crash-looping apply it
   immediately through the health check.
4. Always publish to `preview` first (`npm run update:preview`), open it on a device, then to
   `production`. Use EAS rollouts for percentage-based release when the user base grows.

Native code changes are never OTA-able; the fingerprint runtime version keeps updates away from
binaries they do not fit.
