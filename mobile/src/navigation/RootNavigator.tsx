import React, { useEffect } from 'react';
import { View, ActivityIndicator } from 'react-native';
import { NavigationContainer } from '@react-navigation/native';
import { useAuthStore } from '@/auth/authStore';
import { AuthStack } from './AuthStack';
import { MainTabs } from './MainTabs';
import { COLORS } from '@/config';
import { navigationRef } from './routes';
import { MfaSetupBanner } from '@/features/auth/MfaSetupBanner';

export function RootNavigator() {
  const { isAuthenticated, isInitialized, initialize } = useAuthStore();

  useEffect(() => {
    void initialize();
  }, [initialize]);

  // Only the cold-start session restore may replace the navigator. Sign-in,
  // MFA and sign-out also toggle isLoading; unmounting the tree for those
  // threw away the screen that was about to navigate to the MFA step.
  if (!isInitialized) {
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
      {isAuthenticated ? <MfaSetupBanner /> : null}
    </NavigationContainer>
  );
}
