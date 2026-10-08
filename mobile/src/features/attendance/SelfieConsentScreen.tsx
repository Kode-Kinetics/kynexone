import React, { useCallback, useState } from 'react';
import { ActivityIndicator, Alert, ScrollView, StyleSheet, Text, View } from 'react-native';
import { useFocusEffect, useNavigation } from '@react-navigation/native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import { selfieAttendanceApi } from '@/api/services';
import { useTheme } from '@/theme/ThemeProvider';
import { GlassSurface, LiquidBackdrop, LiquidButton, MotionPressable, ScreenHero } from '@/components/ui';
import { demoNoticeText, mapPunchRefusal, withdrawalNoticeKey, type AttendanceVerification } from './selfieAttendance';

type IconName = React.ComponentProps<typeof Ionicons>['name'];

/**
 * Selfie attendance consent (EN/AR): what is captured, why, where it is kept, how long, and that
 * saying no changes nothing. Agree records consent for the server's current policy version;
 * withdraw is always available, including when the feature is off. Declining never blocks a punch.
 */
export default function SelfieConsentScreen() {
  const navigation = useNavigation<any>();
  const { t, i18n } = useTranslation();
  const tx = t as unknown as (key: string, options?: Record<string, unknown>) => string;
  const { theme } = useTheme();
  const [verification, setVerification] = useState<AttendanceVerification | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState<'agree' | 'withdraw' | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const load = useCallback(async (force: boolean) => {
    setLoadError(false);
    try {
      setVerification(await selfieAttendanceApi.getVerification({ force }));
    } catch (error) {
      console.warn('[SelfieConsent] Load failed:', error);
      setLoadError(true);
    } finally {
      setLoading(false);
    }
  }, []);

  useFocusEffect(useCallback(() => { void load(true); }, [load]));

  const showError = useCallback((error: unknown) => {
    const refusal = mapPunchRefusal(error, i18n.language);
    Alert.alert(tx(refusal.titleKey), refusal.serverMessage ?? tx(refusal.messageKey));
  }, [i18n.language, tx]);

  const agree = useCallback(async () => {
    if (!verification || saving) return;
    setSaving('agree');
    setNotice(null);
    try {
      setVerification(await selfieAttendanceApi.giveConsent(verification.selfie.currentPolicyVersion));
      setNotice(tx('selfie.consent.agreedToast'));
    } catch (error: any) {
      const data = error?.response?.data;
      if (data?.code === 'consent_version_mismatch') {
        // The text changed under us: re-read, and ask the employee to read it again before agreeing.
        selfieAttendanceApi.clearVerificationCache();
        await load(true);
        setNotice(tx('selfie.consent.versionChanged'));
      } else {
        showError(error);
      }
    } finally {
      setSaving(null);
    }
  }, [load, saving, showError, tx, verification]);

  const withdraw = useCallback(() => {
    Alert.alert(tx('selfie.consent.withdrawConfirmTitle'), tx('selfie.consent.withdrawConfirmBody'), [
      { text: tx('common.cancel'), style: 'cancel' },
      {
        text: tx('selfie.consent.withdrawConfirm'),
        style: 'destructive',
        onPress: async () => {
          setSaving('withdraw');
          setNotice(null);
          try {
            const { verification: next, withdrawal } = await selfieAttendanceApi.withdrawConsent();
            setVerification(next);
            // "Deleted" only when storage confirmed it; otherwise "within about 15 minutes" (the server's purge).
            setNotice(tx(withdrawalNoticeKey(withdrawal)));
          } catch (error) {
            showError(error);
          } finally {
            setSaving(null);
          }
        },
      },
    ]);
  }, [showError, tx]);

  const selfie = verification?.selfie;
  const consent = selfie?.consent ?? null;
  const enabled = selfie?.enabled === true;
  const givenDate = consent?.givenAtUtc ? formatDate(consent.givenAtUtc, i18n.language) : null;
  // Only under the owner's time-boxed demo exception: where the photos are and when they go, before anything else.
  const demoNotice = demoNoticeText(verification, i18n.language, tx('selfie.demo.notice'));

  const sections: { icon: IconName; title: string; body: string }[] = [
    { icon: 'camera-outline', title: tx('selfie.consent.whatTitle'), body: tx('selfie.consent.whatBody') },
    { icon: 'help-circle-outline', title: tx('selfie.consent.whyTitle'), body: tx('selfie.consent.whyBody') },
    { icon: 'lock-closed-outline', title: tx('selfie.consent.whoTitle'), body: tx('selfie.consent.whoBody') },
    { icon: 'time-outline', title: tx('selfie.consent.retentionTitle'), body: tx('selfie.consent.retentionBody') },
    { icon: 'hand-left-outline', title: tx('selfie.consent.choiceTitle'), body: tx('selfie.consent.choiceBody') },
  ];

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView contentContainerStyle={styles.content} showsVerticalScrollIndicator={false}>
        <ScreenHero
          eyebrow={tx('selfie.consent.eyebrow')}
          title={tx('selfie.consent.title')}
          subtitle={tx('selfie.consent.subtitle')}
          onBack={() => navigation.goBack()}
          backLabel={tx('common.back')}
        />

        {loading && !verification ? (
          <View style={styles.center}>
            <ActivityIndicator color={theme.colors.primary} />
          </View>
        ) : loadError && !verification ? (
          <View style={styles.section}>
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.card}>
              <Text style={[theme.typography.body, { color: theme.colors.textSecondary }]}>{tx('selfie.consent.unavailable')}</Text>
              <LiquidButton label={tx('common.retry')} onPress={() => void load(true)} />
            </GlassSurface>
          </View>
        ) : (
          <>
            {demoNotice ? (
              <View style={styles.section}>
                <View
                  style={[styles.demoCard, { borderColor: theme.colors.warning, backgroundColor: `${theme.colors.warning}1F` }]}
                  accessibilityRole="alert"
                  accessibilityLabel={demoNotice}
                >
                  <Ionicons name="warning-outline" size={22} color={theme.colors.warning} />
                  <View style={styles.flex}>
                    <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>{tx('selfie.demo.label')}</Text>
                    <Text style={[theme.typography.body, styles.body, { color: theme.colors.text }]}>{demoNotice}</Text>
                  </View>
                </View>
              </View>
            ) : null}

            <View style={styles.section}>
              <GlassSurface radius={theme.radius.xl} contentStyle={styles.statusCard}>
                <Ionicons
                  name={consent ? 'checkmark-circle' : 'ellipse-outline'}
                  size={22}
                  color={consent ? theme.colors.success : theme.colors.textMuted}
                />
                <View style={styles.flex} accessibilityLiveRegion="polite">
                  <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>
                    {!enabled && !consent
                      ? tx('selfie.consent.featureOff')
                      : consent ? tx('selfie.consent.givenStatus') : tx('selfie.consent.notGivenStatus')}
                  </Text>
                  {consent && givenDate ? (
                    <Text style={[theme.typography.caption, { color: theme.colors.textMuted, marginTop: 2 }]}>
                      {tx('selfie.consent.givenOn', { date: givenDate })}
                    </Text>
                  ) : null}
                  {notice ? (
                    <Text style={[theme.typography.caption, { color: theme.colors.primary, marginTop: 6 }]}>{notice}</Text>
                  ) : null}
                </View>
              </GlassSurface>
            </View>

            {selfie?.requiredForConsented && enabled ? (
              <View style={styles.section}>
                <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>{tx('selfie.consent.requiredNote')}</Text>
              </View>
            ) : null}

            <View style={styles.section}>
              <GlassSurface radius={theme.radius.xl} contentStyle={styles.card}>
                {sections.map((section) => (
                  <View key={section.title} style={styles.row}>
                    <View style={[styles.icon, { backgroundColor: `${theme.colors.primary}17` }]}>
                      <Ionicons name={section.icon} size={19} color={theme.colors.primary} />
                    </View>
                    <View style={styles.flex}>
                      <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>{section.title}</Text>
                      <Text style={[theme.typography.caption, styles.body, { color: theme.colors.textSecondary }]}>{section.body}</Text>
                    </View>
                  </View>
                ))}
                {selfie?.currentPolicyVersion ? (
                  <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>
                    {tx('selfie.consent.policyVersion', { version: selfie.currentPolicyVersion })}
                  </Text>
                ) : null}
              </GlassSurface>
            </View>

            <View style={[styles.section, styles.actions]}>
              {enabled && !consent ? (
                <>
                  <LiquidButton
                    label={tx('selfie.consent.agree')}
                    icon="checkmark"
                    onPress={() => void agree()}
                    loading={saving === 'agree'}
                    disabled={saving != null || !selfie?.currentPolicyVersion}
                  />
                  <MotionPressable
                    onPress={() => navigation.goBack()}
                    haptic="selection"
                    accessibilityRole="button"
                    accessibilityLabel={tx('selfie.consent.notNow')}
                    contentStyle={styles.textButton}
                  >
                    <Text style={[theme.typography.bodyStrong, { color: theme.colors.primary }]}>{tx('selfie.consent.notNow')}</Text>
                  </MotionPressable>
                </>
              ) : null}
              {consent ? (
                <>
                  <LiquidButton
                    label={tx('selfie.consent.withdraw')}
                    icon="close-circle-outline"
                    variant="danger"
                    onPress={withdraw}
                    loading={saving === 'withdraw'}
                    disabled={saving != null}
                  />
                  <Text style={[theme.typography.caption, styles.centerText, { color: theme.colors.textMuted }]}>
                    {tx('selfie.consent.withdrawDeletesUnused')}
                  </Text>
                </>
              ) : null}
            </View>
          </>
        )}
      </ScrollView>
    </View>
  );
}

function formatDate(iso: string, language: string): string | null {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return null;
  try {
    return date.toLocaleDateString(language === 'ar' ? 'ar-SA' : 'en-GB', { year: 'numeric', month: 'long', day: 'numeric' });
  } catch {
    return date.toISOString().slice(0, 10);
  }
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 40 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  center: { paddingVertical: 40, alignItems: 'center' },
  card: { padding: 16, gap: 16 },
  statusCard: { padding: 16, flexDirection: 'row', alignItems: 'flex-start', gap: 12 },
  demoCard: { padding: 16, flexDirection: 'row', alignItems: 'flex-start', gap: 12, borderWidth: 1.5, borderRadius: 20 },
  row: { flexDirection: 'row', alignItems: 'flex-start', gap: 12 },
  icon: { width: 38, height: 38, borderRadius: 13, alignItems: 'center', justifyContent: 'center' },
  flex: { flex: 1, minWidth: 0 },
  body: { marginTop: 3, lineHeight: 19 },
  actions: { gap: 10 },
  textButton: { minHeight: 44, alignItems: 'center', justifyContent: 'center', borderRadius: 14 },
  centerText: { textAlign: 'center' },
});
