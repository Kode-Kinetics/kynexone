import React, { useCallback, useEffect, useState } from 'react';
import { ActivityIndicator, Alert, RefreshControl, ScrollView, Text, TouchableOpacity, View } from 'react-native';
import * as Location from 'expo-location';
import { attendanceApi } from '@/api/services';
import { useAuthStore } from '@/auth/authStore';
import { getDeviceInfo } from '@/utils/device';
import { formatRiyadhBusinessDate } from '@/utils/businessDate';
import { formatTime } from '@/utils/date';
import { COLORS } from '@/config';
import type { GeoLocation, TodayAttendance } from '@/types';
import { kioskPunchLabel, nextKioskPunch } from './kioskPolicy';

export default function KioskAttendanceScreen() {
  const { user } = useAuthStore();
  const [attendance, setAttendance] = useState<TodayAttendance | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const load = useCallback(async () => {
    try {
      setAttendance(await attendanceApi.getKioskTodayAttendance());
    } catch (error: any) {
      Alert.alert('Attendance unavailable', error?.response?.data?.message ?? 'Could not load your punch status.');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  useEffect(() => { void load(); }, [load]);

  const punch = async () => {
    if (submitting) return;
    setSubmitting(true);
    try {
      const permission = await Location.requestForegroundPermissionsAsync();
      let location: GeoLocation | undefined;
      if (permission.status === 'granted') {
        const current = await Location.getCurrentPositionAsync({ accuracy: Location.Accuracy.High });
        location = {
          latitude: current.coords.latitude,
          longitude: current.coords.longitude,
          accuracy: current.coords.accuracy ?? undefined,
          timestamp: current.timestamp,
        };
      }
      const punchType = nextKioskPunch(attendance);
      await attendanceApi.punchKiosk({
        punchType,
        timestamp: new Date().toISOString(),
        location,
        deviceInfo: await getDeviceInfo(),
      });
      await load();
      Alert.alert('Attendance recorded', punchType === 'CLOCK_IN' ? 'You are clocked in.' : 'You are clocked out.');
    } catch (error: any) {
      Alert.alert('Punch failed', error?.response?.data?.message ?? 'Could not record attendance. Please try again.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <ScrollView
      style={{ flex: 1, backgroundColor: COLORS.background }}
      contentContainerStyle={{ flexGrow: 1, padding: 20, justifyContent: 'center' }}
      refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => { setRefreshing(true); void load(); }} />}
    >
      <View style={{ backgroundColor: COLORS.navy, borderRadius: 24, padding: 24, alignItems: 'center' }}>
        <Text style={{ color: '#fff', fontSize: 13, opacity: 0.72 }}>KYNEXONE ATTENDANCE</Text>
        <Text style={{ color: '#fff', fontSize: 24, fontWeight: '800', marginTop: 10 }}>{user?.fullName}</Text>
        <Text style={{ color: '#fff', opacity: 0.7, marginTop: 4 }}>{formatRiyadhBusinessDate()}</Text>
        {loading ? (
          <ActivityIndicator color="#fff" size="large" style={{ marginVertical: 48 }} />
        ) : (
          <>
            <View style={{ flexDirection: 'row', width: '100%', marginVertical: 32 }}>
              <View style={{ flex: 1, alignItems: 'center' }}>
                <Text style={{ color: '#fff', opacity: 0.62, fontSize: 12 }}>CLOCK IN</Text>
                <Text style={{ color: '#fff', fontWeight: '700', fontSize: 20, marginTop: 6 }}>{formatTime(attendance?.clockIn)}</Text>
              </View>
              <View style={{ width: 1, backgroundColor: 'rgba(255,255,255,0.2)' }} />
              <View style={{ flex: 1, alignItems: 'center' }}>
                <Text style={{ color: '#fff', opacity: 0.62, fontSize: 12 }}>CLOCK OUT</Text>
                <Text style={{ color: '#fff', fontWeight: '700', fontSize: 20, marginTop: 6 }}>{formatTime(attendance?.clockOut)}</Text>
              </View>
            </View>
            <TouchableOpacity
              accessibilityRole="button"
              accessibilityLabel={kioskPunchLabel(attendance)}
              onPress={punch}
              disabled={submitting}
              style={{
                width: 190,
                height: 190,
                borderRadius: 95,
                alignItems: 'center',
                justifyContent: 'center',
                backgroundColor: attendance?.currentlyActive ? '#DC2626' : COLORS.emerald,
                borderWidth: 8,
                borderColor: 'rgba(255,255,255,0.16)',
                opacity: submitting ? 0.7 : 1,
              }}
            >
              {submitting ? <ActivityIndicator color="#fff" size="large" /> : (
                <>
                  <Text style={{ fontSize: 40 }}>{attendance?.currentlyActive ? '↗' : '↘'}</Text>
                  <Text style={{ color: '#fff', fontWeight: '800', fontSize: 20, marginTop: 8 }}>
                    {kioskPunchLabel(attendance)}
                  </Text>
                </>
              )}
            </TouchableOpacity>
          </>
        )}
      </View>
    </ScrollView>
  );
}
