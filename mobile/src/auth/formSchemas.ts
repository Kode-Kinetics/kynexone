// The two sign-in-area form schemas. Kept free of React Native so tests/formSchemas.test.ts
// can pin their exact messages under node --test. Messages are translation keys, resolved
// where they are shown (LoginScreen, ChangePasswordScreen). Import-free apart from zod, so the
// password minimum (DEFAULT_MIN_PASSWORD_LENGTH) is passed in by the screen.

import { z } from 'zod';

/* The company ID is asked for only when needed (a link that names it, or the server answering
   `workspace_required`), so it is validated in onSubmit rather than by the schema. */
export const loginSchema = z.object({
  tenantId: z.string(),
  username: z.string().refine((value) => value.trim().length > 0, 'signin.emailRequired'),
  password: z.string().min(1, 'signin.passwordRequired'),
});
export type LoginFormData = z.infer<typeof loginSchema>;

export const makeChangePasswordSchema = (minLength: number) => z.object({
  currentPassword: z.string().min(1, 'currentRequired'),
  newPassword: z.string().min(minLength, 'tooShort'),
  confirmPassword: z.string().min(1, 'confirmRequired'),
}).refine((data) => data.newPassword === data.confirmPassword, {
  message: 'mismatch',
  path: ['confirmPassword'],
});
export type ChangePasswordFormData = z.infer<ReturnType<typeof makeChangePasswordSchema>>;
