// Fingerprint (runtime version) inputs. See README "Releasing".
//
// eas.json is ignored: it holds build/submit plumbing, and editing it (a submit
// profile, a credentials source) must not orphan every installed binary from
// OTA updates. What eas.json feeds INTO the app still counts: app.config.js
// copies EXPO_PUBLIC_API_BASE_URL and EXPO_PUBLIC_APP_ENV into `extra`, and the
// expo config (including `extra`) stays in the fingerprint, so a binary built
// against one API URL can never receive an update built against another
// (e.g. localhost). That is also why `npm run update:<profile>` injects the
// profile's eas.json env before publishing.
/** @type {import('@expo/fingerprint').Config} */
module.exports = {
  ignorePaths: ['eas.json'],
};
