import React from 'react';
import type { NativeStackScreenProps } from '@react-navigation/native-stack';
import type { AuthStackParamList } from '@/navigation/authTypes';
import { MfaEnrollmentView } from './MfaEnrollmentView';

type Props = NativeStackScreenProps<AuthStackParamList, 'MfaEnrollment'>;

/**
 * Sign-in path: the password was right but two-step sign-in is mandatory now
 * and not set up. No session exists; enrolling returns the user to sign in.
 */
export default function MfaEnrollmentScreen({ navigation, route }: Props) {
  const { enrollmentToken, tenantId, email, expiresInSeconds } = route.params;
  return (
    <MfaEnrollmentView
      enrollmentToken={enrollmentToken}
      expiresInSeconds={expiresInSeconds}
      tenantId={tenantId}
      email={email}
      onCancel={() => navigation.goBack()}
      onEnrolled={() => navigation.navigate('Login', { tenantId, email, enrollmentComplete: true })}
    />
  );
}
