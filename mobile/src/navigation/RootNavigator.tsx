import React, { useEffect } from 'react';
import { View, ActivityIndicator } from 'react-native';
import { NavigationContainer } from '@react-navigation/native';
import { useAuthStore } from '@/auth/authStore';
import { AuthStack } from './AuthStack';
import { MainTabs } from './MainTabs';
import { COLORS } from '@/config';
import { navigationRef } from './routes';

export function RootNavigator() {
  const { isAuthenticated, isLoading, initialize } = useAuthStore();

  useEffect(() => {
    void initialize();
  }, [initialize]);

  if (isLoading) {
    return (
      <View style={{ flex: 1, backgroundColor: COLORS.navy, alignItems: 'center', justifyContent: 'center' }}>
        <View style={{
          width: 64, height: 64, borderRadius: 16, backgroundColor: COLORS.blue,
          alignItems: 'center', justifyContent: 'center', marginBottom: 20,
        }}>
          <ActivityIndicator color="#fff" size="large" />
        </View>
      </View>
    );
  }

  return (
    <NavigationContainer ref={navigationRef}>
      {isAuthenticated ? <MainTabs /> : <AuthStack />}
    </NavigationContainer>
  );
}
