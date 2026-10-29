import { AppState } from 'react-native';
import * as Sharing from 'expo-sharing';
import { useAuthStore } from '@/auth/authStore';
import { currentSessionGeneration, isCurrentSession } from '@/auth/sessionGeneration';
import { useAppLockStore } from '@/safety/appLockStore';

/**
 * Fail closed when a sensitive PDF download completes after a lock,
 * background, sign-out, account switch, or screen navigation.
 *
 * NOTE: Expo Sharing has NO API to dismiss an already-open system share sheet.
 * This guards the moment before opening it; active native sheets require a
 * separate platform-level cancellation solution before App Lock is certified.
 */
export function currentShareGeneration(): number {
  return currentSessionGeneration();
}

export function canShareProtectedDocument(generation: number, onScreen: () => boolean): boolean {
  return onScreen()
    && isCurrentSession(generation)
    && useAuthStore.getState().isAuthenticated
    && !useAppLockStore.getState().locked
    && AppState.currentState === 'active';
}

export async function shareProtectedDocument(
  uri: string,
  options: Parameters<typeof Sharing.shareAsync>[1],
  generation: number,
  onScreen: () => boolean,
): Promise<boolean> {
  // No await between the final auth/lock/app-state check and native presentation.
  if (!canShareProtectedDocument(generation, onScreen)) return false;
  await Sharing.shareAsync(uri, options);
  return true;
}
