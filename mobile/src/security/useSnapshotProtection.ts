import { useLayoutEffect } from 'react';
import { Platform } from 'react-native';
import * as ScreenCapture from 'expo-screen-capture';
import { createScreenProtection } from './snapshotProtection';

const screenProtection = createScreenProtection(ScreenCapture, Platform.OS);

export function useSnapshotProtection(active = true): void {
  useLayoutEffect(() => active ? screenProtection.acquireSnapshot() : undefined, [active]);
}

export function useSensitiveScreenProtection(): void {
  useLayoutEffect(() => screenProtection.acquireSensitive(), []);
}
