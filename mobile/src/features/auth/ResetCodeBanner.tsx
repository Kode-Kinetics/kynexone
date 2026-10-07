import React, { useState } from 'react';
import { StyleSheet, Text, TouchableOpacity, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { useAuthStore } from '@/auth/authStore';
import { COLORS } from '@/config';

/** Codes whose notice was hidden in this app session (survives remounts, not a restart). */
const hiddenThisSession = new Set<string>();

/**
 * "HR gave you a new sign-in code on {date}. If you didn't ask for it, tell HR." (contract
 * Amendment 3, F1). HR reset this login and the code is still unused, so the old password still
 * works: the person signed in is the one who can say "that wasn't me". Read from the user that
 * /auth/me returned at sign-in or session restore. Dismissible for this session; no cancel action.
 */
export function ResetCodeBanner() {
  const { t, i18n } = useTranslation();
  const insets = useSafeAreaInsets();
  const issued = useAuthStore((s) => s.user?.pendingResetNotice?.date ?? '');
  const [, rerender] = useState(0);
  if (!issued || hiddenThisSession.has(issued)) return null;

  const when = new Date(issued);
  const date = Number.isNaN(when.getTime())
    ? issued
    : new Intl.DateTimeFormat(i18n.language === 'ar' ? 'ar-SA-u-ca-gregory-nu-latn' : 'en-GB',
      { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'Asia/Riyadh' }).format(when);

  return (
    <View style={[styles.wrap, { top: Math.max(insets.top, 0) + 8 }]} pointerEvents="box-none">
      <View style={styles.card} accessibilityRole="alert" testID="reset-code-banner">
        <Ionicons name="alert-circle-outline" size={22} color={COLORS.warning} />
        <Text style={styles.message}>{t('signin.resetNotice', { date })}</Text>
        <TouchableOpacity
          onPress={() => { hiddenThisSession.add(issued); rerender((n) => n + 1); }}
          accessibilityRole="button"
          accessibilityLabel={t('signin.hideMessage')}
          style={styles.close}
        >
          <Ionicons name="close" size={20} color="#fff" />
        </TouchableOpacity>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: { position: 'absolute', start: 12, end: 12 },
  card: {
    flexDirection: 'row', alignItems: 'center', gap: 10,
    backgroundColor: COLORS.navy, borderRadius: 14, paddingVertical: 10, paddingStart: 12, paddingEnd: 4,
    borderWidth: 1, borderColor: COLORS.warning,
  },
  message: { flex: 1, color: '#fff', fontSize: 14, lineHeight: 20 },
  close: { width: 44, height: 44, alignItems: 'center', justifyContent: 'center' },
});
