/** Shared cancellable seam for user-initiated shell navigation. */
export function confirmAppNavigation(path: string): boolean {
  if (new URL(path, window.location.href).href === window.location.href) return true;
  return window.dispatchEvent(new CustomEvent('kynexone:before-navigation', { cancelable: true, detail: { path } }));
}
