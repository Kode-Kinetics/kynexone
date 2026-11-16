/**
 * Authenticated file download.
 *
 * WHY. The API authenticates by the `Authorization: Bearer` header only (see api/client.ts), so a plain
 * `<a href="/api/...">` is a browser navigation that carries no token and answers 401. A download must go
 * through the app's axios client as a blob, then be saved from an object URL.
 *
 * The DOM side is an injected `DownloadEnv` so the logic runs (and is tested) in plain Node.
 */

export interface DownloadEnv {
  createObjectURL: (blob: Blob) => string;
  revokeObjectURL: (url: string) => void;
  /** Click a temporary `<a download>` for the object URL. */
  triggerDownload: (url: string, fileName: string) => void;
  defer: (fn: () => void, ms: number) => void;
}

const browserEnv: DownloadEnv = {
  createObjectURL: blob => URL.createObjectURL(blob),
  revokeObjectURL: url => URL.revokeObjectURL(url),
  triggerDownload: (url, fileName) => {
    const a = document.createElement('a');
    a.href = url; a.download = fileName; a.rel = 'noopener';
    document.body.appendChild(a); a.click(); a.remove();
  },
  defer: (fn, ms) => { window.setTimeout(fn, ms); },
};

/** Save bytes the browser already holds. The object URL is revoked shortly after the click. */
export function saveBlob(blob: Blob, fileName: string, env: DownloadEnv = browserEnv): void {
  const url = env.createObjectURL(blob);
  env.triggerDownload(url, fileName);
  env.defer(() => env.revokeObjectURL(url), 10_000);
}

/**
 * With `responseType: 'blob'` axios hands an error body back as a Blob, which hides the API's `{ code, message }`.
 * Parse it so `describeApiError` / `problemOf` can read it. A body that is not JSON becomes an empty problem.
 * Anything that is not an error with a Blob body is returned unchanged.
 */
export async function unwrapBlobError(error: unknown): Promise<unknown> {
  const response = (error as { response?: { data?: unknown } } | null)?.response;
  const data = response?.data;
  if (!response || typeof Blob === 'undefined' || !(data instanceof Blob)) return error;
  try {
    const parsed: unknown = JSON.parse(await data.text());
    response.data = parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    response.data = {};
  }
  return error;
}

/**
 * Fetch a file with the caller's authenticated request, then save it. On failure nothing is saved and the (unwrapped)
 * error is rethrown for the screen to show.
 */
export async function downloadAuthenticated(fetchBlob: () => Promise<Blob>, fileName: string, env: DownloadEnv = browserEnv): Promise<void> {
  let blob: Blob;
  try {
    blob = await fetchBlob();
  } catch (error) {
    throw await unwrapBlobError(error);
  }
  saveBlob(blob, fileName, env);
}
