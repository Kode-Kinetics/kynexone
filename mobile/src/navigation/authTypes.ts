export type AuthStackParamList = {
  Login:
    | {
        tenantId?: string;
        email?: string;
        enrollmentComplete?: boolean;
      }
    | undefined;
  ForgotPassword: undefined;
  MfaChallenge: {
    challengeToken: string;
    tenantId: string;
    email: string;
    expiresInSeconds: number;
    /** First sign-in after enrolling: a rejected first code is most likely the enrolment code reused. */
    justEnrolled?: boolean;
  };
  MfaEnrollment: {
    enrollmentToken: string;
    tenantId: string;
    email: string;
    expiresInSeconds: number;
  };
};
