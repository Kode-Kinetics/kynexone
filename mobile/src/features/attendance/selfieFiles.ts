import * as FileSystem from 'expo-file-system/legacy';
import {
  CAMERA_CACHE_DIR,
  SELFIE_CACHE_DIR,
  selfieFileName,
  selfieSweepTargets,
} from './selfiePhotoPolicy';

// File work for the selfie photo. The rules (which names are ours, whether a capture is still wanted)
// live in selfiePhotoPolicy.ts so they can be tested without a device.

function cacheRoot(): string | null {
  const root = FileSystem.cacheDirectory;
  if (!root) return null;
  return root.endsWith('/') ? root : `${root}/`;
}

/** Deletes a selfie temp file. Best effort: the photo is never kept on purpose, and the launch sweep is the backstop. */
export async function deleteTempPhoto(uri: string | null | undefined): Promise<void> {
  if (!uri) return;
  try {
    await FileSystem.deleteAsync(uri, { idempotent: true });
  } catch (error) {
    console.warn('[Selfie] Could not delete the temporary photo:', error);
  }
}

/**
 * Moves a just-captured camera file into the app's own selfie folder under a recognisable name, so the
 * launch sweep can find it if the app dies before it is deleted. If the move fails, the camera file is
 * used where it is (the sweep also knows the camera's own folder).
 */
export async function adoptCapturedPhoto(uri: string): Promise<string> {
  const root = cacheRoot();
  if (!root) return uri;
  const dir = `${root}${SELFIE_CACHE_DIR}/`;
  const target = `${dir}${selfieFileName(Date.now(), Math.random())}`;
  try {
    await FileSystem.makeDirectoryAsync(dir, { intermediates: true });
    await FileSystem.moveAsync({ from: uri, to: target });
    return target;
  } catch (error) {
    console.warn('[Selfie] Could not move the photo into the selfie folder; using it in place:', error);
    return uri;
  }
}

async function sweepFolder(folder: 'own' | 'camera', dir: string): Promise<number> {
  let names: string[];
  try {
    const info = await FileSystem.getInfoAsync(dir);
    if (!info.exists || !info.isDirectory) return 0;
    names = await FileSystem.readDirectoryAsync(dir);
  } catch {
    return 0;
  }
  const targets = selfieSweepTargets(folder, names);
  await Promise.all(targets.map((name) => deleteTempPhoto(`${dir}${name}`)));
  return targets.length;
}

/**
 * On launch: deletes selfie files left behind by a crash or a kill mid-capture. Only files the app
 * created are touched: our prefixed files in our folder, and expo-camera's UUID-named JPEGs in its own
 * folder (CameraView is used only for the selfie). Never throws.
 */
export async function sweepSelfieCache(): Promise<number> {
  const root = cacheRoot();
  if (!root) return 0;
  try {
    const counts = await Promise.all([
      sweepFolder('own', `${root}${SELFIE_CACHE_DIR}/`),
      sweepFolder('camera', `${root}${CAMERA_CACHE_DIR}/`),
    ]);
    return counts[0] + counts[1];
  } catch (error) {
    console.warn('[Selfie] Launch sweep failed:', error);
    return 0;
  }
}
