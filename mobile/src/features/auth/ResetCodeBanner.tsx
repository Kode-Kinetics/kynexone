import React, { useMemo, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { useAuthStore } from '@/auth/authStore';
import { MotionPressable } from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import type { KynexTheme } from '@/theme/tokens';
import { useFormat } from '@/utils/format';

/** Codes whose notice was hidden in this app session (survives remounts, not a restart). */
const hiddenThisSession = new Set<string>();

/**
 * "HR gave you a new sign-in code on {date}. If you didn't ask for it, tell HR." (contract
 * Amendment 3, F1). HR reset this login and the code is still unused, so the old password still
 * works: the person signed in is the one who can say "that wasn't me". Read from the user that
 * /auth/me returned at sign-in or session restore. Dismissible for this session; no cancel action.
 */
export function ResetCodeBanner() {
  const { t } = useTranslation();
  const { theme } = useTheme();
  const f = useFormat();
  const styles = useMemo(() => makeStyles(theme), [theme]);
  const insets = useSafeAreaInsets();
  const issued = useAuthStore((s) => s.user?.pendingResetNotice?.date ?? '');
  const [, rerender] = useState(0);
  if (!issued || hiddenThisSession.has(issued)) return null;

  const formatted = f.date(issued, 'long');
  const date = formatted === '—' ? issued : formatted;

  return (
    <View style={[styles.wrap, { top: Math.max(insets.top, 0) + 8 }]} pointerEvents="box-none">
      <View style={styles.card} accessibilityRole="alert" testID="reset-code-banner">
        <Ionicons accessible={false} name="alert-circle-outline" size={22} color={theme.colors.status.warning.fg} />
        <Text style={styles.message}>{t('signin.resetNotice', { date })}</Text>
        <MotionPressable
          onPress={() => { hiddenThisSession.add(issued); rerender((n) => n + 1); }}
          accessibilityRole="button"
          accessibilityLabel={t('signin.hideMessage')}
          contentStyle={styles.close}
        >
          <Ionicons accessible={false} name="close" size={20} color={theme.colors.textSecondary} />
        </MotionPressable>
      </View>
    </View>
  );
}

function makeStyles(theme: KynexTheme) {
  const c = theme.colors;
  return StyleSheet.create({
    wrap: { position: 'absolute', start: 12, end: 12 },
    card: {
      flexDirection: 'row', alignItems: 'center', gap: 10,
      backgroundColor: c.cardRaised, borderRadius: theme.radius.sm, paddingVertical: 10, paddingStart: 12, paddingEnd: 4,
      borderWidth: 1, borderColor: c.status.warning.fg,
      ...theme.shadows.raised,
    },
    message: { ...theme.typography.subheadline, flex: 1, color: c.text },
    close: { width: 44, height: 44, alignItems: 'center', justifyContent: 'center' },
  });
}
