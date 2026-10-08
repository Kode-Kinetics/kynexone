// ============================================================
// Selfie photo lifetime on the phone — pure rules (no imports, tested under node --test)
// ============================================================
//
// The photo never stays on the phone on purpose. selfieFiles.ts does the file work; this module only
// decides which files are ours and whether a capture is still wanted:
// - right after capture the camera file is moved into a dedicated cache subfolder with a recognisable
//   prefix, so a launch sweep can delete leftovers without touching anything else;
// - expo-camera writes `<cache>/Camera/<uuid>.jpg` first (both platforms). CameraView is used only by
//   SelfieCaptureModal, so a UUID-named JPEG in that folder is a selfie that was never moved (the app
//   was killed mid-capture); the sweep deletes only names of exactly that shape;
// - a photo that arrives after the capture is no longer wanted (modal closed, app backgrounded,
//   retake) is deleted, never shown or uploaded.

/** Our subfolder of the app's cache directory. */
export const SELFIE_CACHE_DIR = 'kynexone-selfie';
/** Every selfie file the app keeps, even briefly, starts with this. */
export const SELFIE_FILE_PREFIX = 'kx-selfie-';
/** expo-camera's own output subfolder (see ExpoCameraUtils.swift / ResolveTakenPicture.kt). */
export const CAMERA_CACHE_DIR = 'Camera';

const OWN_FILE = /^kx-selfie-[0-9a-z]+(?:-[0-9a-z]+)*\.jpe?g$/i;
const CAMERA_FILE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.jpe?g$/i;

/** A file name for a captured selfie: prefix, time, a random suffix. */
export function selfieFileName(now: number, random: number): string {
  const suffix = Math.floor(Math.abs(random) * 36 ** 6).toString(36).padStart(6, '0');
  return `${SELFIE_FILE_PREFIX}${Math.max(0, Math.floor(now)).toString(36)}-${suffix}.jpg`;
}

/** True only for a name this app gave a selfie file. */
export function isOwnSelfieFile(name: string): boolean {
  return OWN_FILE.test(name);
}

/** True only for expo-camera's own capture names (`<uuid>.jpg`). */
export function isCameraCaptureFile(name: string): boolean {
  return CAMERA_FILE.test(name);
}

/** The names to delete from one folder on launch; anything that does not match is left alone. */
export function selfieSweepTargets(folder: 'own' | 'camera', names: readonly string[]): string[] {
  const match = folder === 'own' ? isOwnSelfieFile : isCameraCaptureFile;
  return names.filter((name) => typeof name === 'string' && !name.includes('/') && match(name));
}

/**
 * Tracks whether a capture that is in flight is still wanted. `begin()` returns a ticket for one
 * takePictureAsync call; `cancel()` (modal closed, app backgrounded, retake) makes every outstanding
 * ticket stale; `isCurrent(ticket)` says whether the photo it produced may be kept.
 */
export class CaptureGuard {
  private generation = 0;
  private active = true;

  begin(): number {
    return this.generation;
  }

  cancel(): void {
    this.generation += 1;
  }

  /** The modal is visible again: new captures are wanted (outstanding tickets stay stale). */
  open(): void {
    this.active = true;
  }

  /** The modal closed: outstanding and future captures are unwanted until open(). */
  close(): void {
    this.active = false;
    this.generation += 1;
  }

  isCurrent(ticket: number): boolean {
    return this.active && ticket === this.generation;
  }
}

/**
 * What the selfie screen does with the photo on screen for an AppState change (review 3, 12-note):
 * - `inactive` (iOS app switcher, Control Centre, an incoming call): COVER the preview. iOS takes the
 *   app-switcher snapshot in this state, so the face must already be hidden when it does;
 * - `background`: DELETE the photo (and keep it covered);
 * - `active`: SHOW again (only a photo that still exists).
 * Any other value (unknown, extension) covers, failing safe.
 */
export type PreviewAction = 'show' | 'cover' | 'delete';

export function previewActionFor(appState: string): PreviewAction {
  if (appState === 'active') return 'show';
  if (appState === 'background') return 'delete';
  return 'cover';
}
