import React, { useCallback, useEffect, useState } from 'react';
import { ActivityIndicator, Alert, Linking, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Location from 'expo-location';
import { useTranslation } from 'react-i18next';
import { attendanceApi } from '@/api/services';
import { useAuthStore } from '@/auth/authStore';
import { getDeviceInfo } from '@/utils/device';
import { useFormat } from '@/utils/format';
import { haptics } from '@/utils/haptics';
import { useTheme } from '@/theme/ThemeProvider';
import { LiquidBackdrop, MotionPressable, StateCard, Surface } from '@/components/ui';
import type { GeoLocation, TodayAttendance } from '@/types';
import { nextKioskPunch } from './kioskPolicy';
import { mapPunchRefusal, refusalText } from './selfieAttendance';

export default function KioskAttendanceScreen() {
  const { user } = useAuthStore();
  const { theme } = useTheme();
  const { t, i18n } = useTranslation();
  const f = useFormat();
  const tx = t as unknown as (key: string) => string;
  const [attendance, setAttendance] = useState<TodayAttendance | null>(null);
  const [loading, setLoading] = useState(true);
  const [failed, setFailed] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const load = useCallback(async () => {
    setFailed(false);
    try {
      setAttendance(await attendanceApi.getKioskTodayAttendance());
    } catch (error) {
      console.warn('[Kiosk] Load failed:', error);
      setFailed(true);
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  const punchType = nextKioskPunch(attendance);
  const actionLabel = punchType === 'CLOCK_OUT' ? t('home.kiosk.clockOut') : t('home.kiosk.clockIn');

  const punch = async () => {
    if (submitting) return;
    setSubmitting(true);
    haptics.commit();
    try {
      const permission = await Location.requestForegroundPermissionsAsync();
      let location: GeoLocation | undefined;
      if (permission.status === 'granted') {
        const current = await Location.getCurrentPositionAsync({ accuracy: Location.Accuracy.High });
        location = {
          latitude: current.coords.latitude,
          longitude: current.coords.longitude,
          accuracy: current.coords.accuracy ?? undefined,
          mocked: typeof current.mocked === 'boolean' ? current.mocked : undefined,
          timestamp: current.timestamp,
        };
      }
      await attendanceApi.punchKiosk({
        punchType,
        timestamp: new Date().toISOString(),
        location,
        deviceInfo: await getDeviceInfo(),
      });
      await load();
      haptics.success();
      Alert.alert(t('home.kiosk.recordedTitle'), punchType === 'CLOCK_IN' ? t('home.kiosk.clockedIn') : t('home.kiosk.clockedOut'));
    } catch (error) {
      haptics.error();
      // The kiosk route runs the same server-side geofence: show its reason and the next step.
      const refusal = mapPunchRefusal(error, i18n.language);
      const message = refusalText(refusal, tx);
      Alert.alert(tx(refusal.titleKey), message, refusal.action === 'open_settings'
        ? [{ text: tx('selfie.punch.openSettings'), onPress: () => void Linking.openSettings() }, { text: tx('selfie.punch.ok'), style: 'cancel' }]
        : [{ text: tx('selfie.punch.ok') }]);
    } finally {
      setSubmitting(false);
    }
  };

  const today = f.businessDate();
  const fill = punchType === 'CLOCK_OUT' ? theme.colors.primaryFill : theme.colors.status.success.fill;

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => { setRefreshing(true); void load(); }}
            tintColor={theme.colors.accent}
            colors={[theme.colors.accent]}
          />
        }
      >
        <Surface level={2} radius={theme.radius.xl} contentStyle={styles.card}>
          <Text style={[theme.typography.eyebrow, { color: theme.colors.accent }]}>{t('home.kiosk.eyebrow')}</Text>
          <Text accessibilityRole="header" style={[theme.typography.title1, styles.center, { color: theme.colors.text }]}>
            {user?.fullName}
          </Text>
          {today ? (
            <Text style={[theme.typography.subheadline, styles.center, { color: theme.colors.textSecondary }]}>
              {f.date(today, 'full')}
            </Text>
          ) : null}

          {loading ? (
            <ActivityIndicator color={theme.colors.accent} size="large" style={styles.loading} />
          ) : failed ? (
            <StateCard
              style={styles.state}
              variant="error"
              title={t('home.kiosk.loadErrorTitle')}
              body={t('home.kiosk.loadErrorBody')}
              onRetry={() => void load()}
            />
          ) : (
            <>
              <View style={[styles.times, { backgroundColor: theme.colors.surfaceInset }]}>
                <View style={styles.time} accessible accessibilityLabel={`${t('home.kiosk.clockIn')}: ${f.time(attendance?.clockIn)}`}>
                  <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{t('home.kiosk.clockIn')}</Text>
                  <Text style={[theme.typography.title2, { color: theme.colors.text, fontVariant: ['tabular-nums'] }]}>
                    {f.time(attendance?.clockIn)}
                  </Text>
                </View>
                <View style={[styles.divider, { backgroundColor: theme.colors.hairline }]} />
                <View style={styles.time} accessible accessibilityLabel={`${t('home.kiosk.clockOut')}: ${f.time(attendance?.clockOut)}`}>
                  <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{t('home.kiosk.clockOut')}</Text>
                  <Text style={[theme.typography.title2, { color: theme.colors.text, fontVariant: ['tabular-nums'] }]}>
                    {f.time(attendance?.clockOut)}
                  </Text>
                </View>
              </View>

              <MotionPressable
                accessibilityRole="button"
                accessibilityLabel={actionLabel}
                accessibilityState={{ busy: submitting }}
                onPress={() => void punch()}
                disabled={submitting}
                contentStyle={[styles.punch, { backgroundColor: fill, borderColor: theme.colors.hairline }]}
              >
                {submitting ? (
                  <ActivityIndicator color={theme.colors.onFill} size="large" />
                ) : (
                  <>
                    <Ionicons
                      accessible={false}
                      name={punchType === 'CLOCK_OUT' ? 'log-out-outline' : 'log-in-outline'}
                      size={40}
                      color={theme.colors.onFill}
                    />
                    <Text style={[theme.typography.title3, { color: theme.colors.onFill }]}>{actionLabel}</Text>
                  </>
                )}
              </MotionPressable>
            </>
          )}
        </Surface>
      </ScrollView>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { flexGrow: 1, padding: 16, justifyContent: 'center' },
  card: { padding: 24, alignItems: 'center', gap: 8 },
  center: { textAlign: 'center' },
  loading: { marginVertical: 48 },
  state: { alignSelf: 'stretch', marginTop: 16 },
  times: { flexDirection: 'row', alignSelf: 'stretch', borderRadius: 16, paddingVertical: 16, marginVertical: 24 },
  time: { flex: 1, alignItems: 'center', gap: 4 },
  divider: { width: StyleSheet.hairlineWidth },
  punch: {
    width: 190,
    height: 190,
    borderRadius: 95,
    alignItems: 'center',
    justifyContent: 'center',
    gap: 8,
    borderWidth: 8,
  },
});
