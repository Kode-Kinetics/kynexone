import { useCallback } from 'react';
import { useFocusEffect } from '@react-navigation/native';
import { allowScreenCaptureAsync, preventScreenCaptureAsync } from 'expo-screen-capture';
import { reportBreadcrumb } from '@/observability/reportError';

/**
 * Blocks screenshots and screen recording (Android FLAG_SECURE; iOS blanks recordings and the app
 * switcher snapshot) while this screen is focused. Tab screens stay mounted after the first visit,
 * so protection follows focus rather than mount, otherwise one payslip visit would lock every tab.
 * Used on payslips, profile, loans and letters, as MFA enrolment already does.
 */
export function useSensitiveScreen(key: string): void {
  useFocusEffect(
    useCallback(() => {
      let active = true;
      preventScreenCaptureAsync(key).catch(() => {
        if (active) reportBreadcrumb(`screen capture block unavailable: ${key}`);
      });
      return () => {
        active = false;
        allowScreenCaptureAsync(key).catch(() => undefined);
      };
    }, [key]),
  );
}
