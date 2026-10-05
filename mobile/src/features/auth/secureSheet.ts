// Screenshot protection for the signed-in enrolment sheet (MfaSetupBanner).
//
// The sheet is a React Native Modal. On Android a Modal is its own dialog
// window, and RN copies FLAG_SECURE from the activity window to it only when the
// dialog is created (ReactModalHostView). usePreventScreenCapture inside the
// sheet runs after that, so it would protect the activity but not the sheet. The
// activity is therefore secured BEFORE the Modal opens, and released after it
// closes. Import-free (the capture module is passed in) so the order is
// unit-tested under node.

export const MFA_SHEET_CAPTURE_KEY = 'mfa-sheet';

export interface ScreenCaptureApi {
  preventScreenCaptureAsync(key?: string): Promise<void>;
  allowScreenCaptureAsync(key?: string): Promise<void>;
}

export interface SecureSheet<T> {
  /** Secure the window, then show the sheet. Opens even if securing fails: enrolment must never be blocked. */
  open(value: T): Promise<void>;
  /** Hide the sheet, then release the window. */
  close(): void;
  /** Release on unmount without touching state. Any later open() releases and does nothing else. */
  dispose(): void;
}

export function createSecureSheet<T>(capture: ScreenCaptureApi, setSheet: (value: T | null) => void): SecureSheet<T> {
  let secured = false;
  // Set on unmount. An open() still in flight (the banner unmounted while the
  // enrolment token was being fetched) must neither show a sheet nor leave the
  // window secured.
  let disposed = false;
  const release = () => {
    if (!secured) return;
    secured = false;
    void capture.allowScreenCaptureAsync(MFA_SHEET_CAPTURE_KEY).catch(() => undefined);
  };
  return {
    async open(value) {
      if (disposed) {
        void capture.allowScreenCaptureAsync(MFA_SHEET_CAPTURE_KEY).catch(() => undefined);
        return;
      }
      try {
        await capture.preventScreenCaptureAsync(MFA_SHEET_CAPTURE_KEY);
        secured = true;
      } catch {
        // Module unavailable (e.g. web): continue unprotected rather than lock the user out.
      }
      if (disposed) {
        // Unmounted while securing: undo it and show nothing.
        secured = false;
        void capture.allowScreenCaptureAsync(MFA_SHEET_CAPTURE_KEY).catch(() => undefined);
        return;
      }
      setSheet(value);
    },
    close() {
      setSheet(null);
      release();
    },
    dispose() {
      disposed = true;
      release();
    },
  };
}
