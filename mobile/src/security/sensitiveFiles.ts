const SENSITIVE_CACHE_DIRECTORY = 'kynexone-sensitive/';

export interface SensitiveDownloadOptions {
  url: string;
  headers?: Record<string, string>;
  fileExtension?: string;
}

function randomFileName(fileExtension?: string): string {
  const randomUuid = globalThis.crypto?.randomUUID;
  const identifier = typeof randomUuid === 'function'
    ? randomUuid.call(globalThis.crypto)
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}-${Math.random().toString(36).slice(2)}`;
  const extension = fileExtension
    ?.replace(/^\.+/, '')
    .replace(/[^a-zA-Z0-9]/g, '')
    .toLowerCase();
  return extension ? `${identifier}.${extension}` : identifier;
}

export async function withSensitiveDownload(
  options: SensitiveDownloadOptions,
  operation: (uri: string) => Promise<void>,
): Promise<void> {
  const FileSystem = await import('expo-file-system/legacy');
  if (!FileSystem.cacheDirectory) throw new Error('Temporary storage is unavailable.');

  const directory = `${FileSystem.cacheDirectory}${SENSITIVE_CACHE_DIRECTORY}`;
  const fileUri = `${directory}${randomFileName(options.fileExtension)}`;

  try {
    await FileSystem.makeDirectoryAsync(directory, { intermediates: true });
    const result = await FileSystem.downloadAsync(options.url, fileUri, {
      headers: options.headers,
    });
    if (result.status !== 200) {
      throw new Error(`Download failed (HTTP ${result.status})`);
    }
    await operation(result.uri);
  } finally {
    try {
      await FileSystem.deleteAsync(fileUri, { idempotent: true });
    } catch {
      // Startup and logout purge retry any file the platform could not remove now.
    }
  }
}

export async function purgeSensitiveFiles(): Promise<void> {
  try {
    const FileSystem = await import('expo-file-system/legacy');
    if (!FileSystem.cacheDirectory) return;
    await FileSystem.deleteAsync(
      `${FileSystem.cacheDirectory}${SENSITIVE_CACHE_DIRECTORY}`,
      { idempotent: true },
    );
  } catch {
    // Cache cleanup must not prevent session restoration or teardown.
  }
}
