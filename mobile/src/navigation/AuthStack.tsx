import React from 'react';
import { createNativeStackNavigator } from '@react-navigation/native-stack';
import LoginScreen from '@/features/auth/LoginScreen';
import ForgotPasswordScreen from '@/features/auth/ForgotPasswordScreen';
import MfaChallengeScreen from '@/features/auth/MfaChallengeScreen';
import MfaEnrollmentScreen from '@/features/auth/MfaEnrollmentScreen';
import type { AuthStackParamList } from './authTypes';

const Stack = createNativeStackNavigator<AuthStackParamList>();

export function AuthStack() {
  return (
    <Stack.Navigator screenOptions={{ headerShown: false }}>
      <Stack.Screen name="Login" component={LoginScreen} />
      <Stack.Screen name="ForgotPassword" component={ForgotPasswordScreen} />
      <Stack.Screen name="MfaChallenge" component={MfaChallengeScreen} />
      <Stack.Screen name="MfaEnrollment" component={MfaEnrollmentScreen} />
    </Stack.Navigator>
  );
}
