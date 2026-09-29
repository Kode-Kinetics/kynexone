// ============================================================
// KynexOne Mobile — Backend capability flags
// ============================================================
//
// Each flag gates a mobile feature on its backend endpoint. The control is hidden
// or shown disabled while its flag is false, so no button errors in front of a
// user. Flip a flag only once the endpoint named in the comment is live — the
// contracts are the M1 report's Wave-2 specs (S1–S8), built in stream W2-D.

export const FEATURES = {
  /** POST /api/ess/documents (multipart). Also unlocks the leave and HR-request attachment pickers. */
  FILE_UPLOAD: true,
  /** GET /api/ess/documents/{id}/download — own documents only; a colleague's id is 404. */
  DOCUMENT_DOWNLOAD: true,
  /** POST/GET /api/ess/profile/photo (server re-encodes to JPEG ≤512px, EXIF stripped). */
  PROFILE_PHOTO_UPLOAD: true,
  /** GET/PUT /api/ess/notification-preferences (+ the channel master switch at /api/notifications/preferences). */
  NOTIFICATION_PREFERENCES: true,
  /**
   * POST /api/approval-requests/{id}/send-back { comments } — built by stream W2-E, not on this
   * backend yet. The client call is implemented against the S5 spec; switch this on at integration.
   */
  APPROVAL_SEND_BACK: false,
  /**
   * Face ID / fingerprint sign-in. The login-screen handler is a placeholder that
   * shows an alert; no backend is strictly required (unlock the stored refresh
   * token behind LocalAuthentication), but it is not built yet.
   */
  BIOMETRIC_LOGIN: false,
} as const;

export class FeatureUnavailableError extends Error {
  constructor(feature: string) {
    super(`${feature} is not available in the mobile app yet.`);
    this.name = 'FeatureUnavailableError';
  }
}
