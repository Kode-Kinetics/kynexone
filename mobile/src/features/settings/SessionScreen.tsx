import React, { useState } from 'react';
import { ActivityIndicator, Alert, Text, TouchableOpacity, View } from 'react-native';
import { useAuthStore } from '@/auth/authStore';
import { COLORS } from '@/config';

export default function SessionScreen() {
  const { user, logout } = useAuthStore();
  const [loggingOut, setLoggingOut] = useState(false);

  const signOut = () => {
    Alert.alert('Sign Out', 'Are you sure you want to sign out?', [
      { text: 'Cancel', style: 'cancel' },
      {
        text: 'Sign Out',
        style: 'destructive',
        onPress: async () => {
          setLoggingOut(true);
          await logout();
          setLoggingOut(false);
        },
      },
    ]);
  };

  return (
    <View style={{ flex: 1, backgroundColor: COLORS.background }}>
      <View style={{ backgroundColor: COLORS.navy, paddingTop: 64, paddingBottom: 28, paddingHorizontal: 20 }}>
        <Text style={{ color: '#fff', fontSize: 24, fontWeight: '800' }}>Account</Text>
        <Text style={{ color: 'rgba(255,255,255,0.65)', marginTop: 4 }}>Session and access details</Text>
      </View>
      <View style={{ margin: 20, backgroundColor: '#fff', borderRadius: 18, padding: 20 }}>
        <View style={{ width: 60, height: 60, borderRadius: 30, backgroundColor: COLORS.blue, alignItems: 'center', justifyContent: 'center' }}>
          <Text style={{ color: '#fff', fontSize: 24, fontWeight: '800' }}>
            {(user?.fullName ?? 'U').charAt(0).toUpperCase()}
          </Text>
        </View>
        <Text style={{ fontSize: 19, fontWeight: '800', color: COLORS.text, marginTop: 14 }}>{user?.fullName}</Text>
        <Text style={{ color: COLORS.muted, marginTop: 3 }}>{user?.email}</Text>
        <Text style={{ color: COLORS.muted, marginTop: 12 }}>Role: {user?.role}</Text>
        <Text style={{ color: COLORS.muted, marginTop: 4 }}>Access: {user?.accessMode}</Text>
        <TouchableOpacity
          accessibilityRole="button"
          accessibilityLabel="Sign out"
          onPress={signOut}
          disabled={loggingOut}
          style={{ marginTop: 28, backgroundColor: '#FEF2F2', padding: 16, borderRadius: 12, alignItems: 'center' }}
        >
          {loggingOut
            ? <ActivityIndicator color="#DC2626" />
            : <Text style={{ color: '#DC2626', fontSize: 16, fontWeight: '700' }}>Sign Out</Text>}
        </TouchableOpacity>
      </View>
    </View>
  );
}
