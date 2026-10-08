import React from 'react';
import { Alert, StyleSheet, Text, View } from 'react-native';
import { reloadAppAsync } from 'expo';
import { useTranslation } from 'react-i18next';
import { setLanguage } from '@/config/i18n';
import { MotionPressable } from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';

/**
 * "English / العربية" on the sign-in and first-sign-in screens. Each option is written in its own
 * language so either reader can find theirs. It is the same switch as Settings: the text changes now,
 * and when the layout direction changes too the app offers a restart (React Native fixes direction
 * per process).
 */
export function SignInLanguageToggle() {
  const { t, i18n } = useTranslation();
  const { theme } = useTheme();
  const arabic = i18n.language === 'ar';

  const choose = async (lang: 'en' | 'ar') => {
    if (i18n.language === lang) return;
    const { restartRequired } = await setLanguage(lang);
    if (!restartRequired) return;
    Alert.alert(t('settings.restartTitle'), t('settings.restartBody'), [
      { text: t('settings.later'), style: 'cancel' },
      {
        text: t('settings.restartNow'),
        onPress: () => {
          reloadAppAsync('Language direction changed').catch((error) =>
            console.warn('[SignIn] Reload failed; the direction applies on next launch:', error));
        },
      },
    ]);
  };

  const option = (lang: 'en' | 'ar', label: string, selected: boolean) => (
    <MotionPressable
      accessibilityRole="button"
      accessibilityState={{ selected }}
      accessibilityLanguage={lang}
      onPress={() => void choose(lang)}
      haptic="selection"
      contentStyle={styles.option}
      testID={`signin-lang-${lang}`}
    >
      <Text style={[theme.typography.caption, { color: selected ? theme.colors.text : theme.colors.textMuted, fontWeight: selected ? '700' : '500' }]}>
        {label}
      </Text>
    </MotionPressable>
  );

  return (
    <View style={styles.bar} accessibilityRole="radiogroup" accessibilityLabel={t('signin.language')}>
      {option('en', 'English', !arabic)}
      <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]} accessible={false}>/</Text>
      {option('ar', 'العربية', arabic)}
    </View>
  );
}

const styles = StyleSheet.create({
  bar: { flexDirection: 'row', alignItems: 'center', justifyContent: 'center', gap: 4, minHeight: 48 },
  option: { minHeight: 44, minWidth: 44, paddingHorizontal: 10, alignItems: 'center', justifyContent: 'center' },
});
