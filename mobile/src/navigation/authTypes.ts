export type AuthStackParamList = {
  Login:
    | {
        tenantId?: string;
        email?: string;
        enrollmentComplete?: boolean;
      }
    | undefined;
  ForgotPassword: undefined;
  /**
   * First sign-in with HR's welcome code. Params are in-memory navigation state only: there is no
   * linking config, so they never become a URL, and navigation state is not persisted.
   */
  Welcome:
    | {
        email?: string;
        code?: string;
        workspace?: string;
        /** Sent here by the sign-in screen because the code was typed as a password. */
        fromLogin?: boolean;
      }
    | undefined;
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
