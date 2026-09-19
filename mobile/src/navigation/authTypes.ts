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
  };
  MfaEnrollment: {
    enrollmentToken: string;
    tenantId: string;
    email: string;
    expiresInSeconds: number;
    message?: string;
  };
};
