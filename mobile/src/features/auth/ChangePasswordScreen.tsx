import React, { useState } from 'react';
import {
  Alert,
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useNavigation } from '@react-navigation/native';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { zodResolver } from '@hookform/resolvers/zod';
import { authApi } from '@/api/adapters';
import {
  GlassSurface,
  GlassTextField,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';

const schema = z.object({
  currentPassword: z.string().min(1, 'Current password is required'),
  newPassword: z.string().min(10, 'Use at least 10 characters'),
  confirmPassword: z.string().min(1, 'Confirm your new password'),
}).refine((data) => data.newPassword === data.confirmPassword, {
  message: 'Passwords do not match',
  path: ['confirmPassword'],
});
type FormData = z.infer<typeof schema>;

export default function ChangePasswordScreen() {
  const navigation = useNavigation();
  const { theme } = useTheme();
  const [submitting, setSubmitting] = useState(false);
  const [visible, setVisible] = useState<Record<keyof FormData, boolean>>({
    currentPassword: false,
    newPassword: false,
    confirmPassword: false,
  });

  const { control, handleSubmit, reset, formState: { errors } } = useForm<FormData>({
    resolver: zodResolver(schema),
    defaultValues: { currentPassword: '', newPassword: '', confirmPassword: '' },
  });

  const onSubmit = async (data: FormData) => {
    setSubmitting(true);
    try {
      await authApi.changePassword({
        currentPassword: data.currentPassword,
        newPassword: data.newPassword,
      });
      reset();
      Alert.alert(
        'Password updated',
        'Your new password is active on this account.',
        [{ text: 'Done', onPress: () => navigation.goBack() }],
      );
    } catch (error: any) {
      Alert.alert('Could not update password', error?.message || 'Please try again.');
    } finally {
      setSubmitting(false);
    }
  };

  const fields: {
    name: keyof FormData;
    label: string;
    icon: React.ComponentProps<typeof Ionicons>['name'];
    autoComplete: 'current-password' | 'new-password';
  }[] = [
    {
      name: 'currentPassword',
      label: 'Current password',
      icon: 'lock-closed-outline',
      autoComplete: 'current-password',
    },
    {
      name: 'newPassword',
      label: 'New password',
      icon: 'key-outline',
      autoComplete: 'new-password',
    },
    {
      name: 'confirmPassword',
      label: 'Confirm new password',
      icon: 'checkmark-circle-outline',
      autoComplete: 'new-password',
    },
  ];

  return (
    <KeyboardAvoidingView
      style={[styles.root, { backgroundColor: theme.colors.canvas }]}
      behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
    >
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.scroll}
        keyboardShouldPersistTaps="handled"
        keyboardDismissMode={Platform.OS === 'ios' ? 'interactive' : 'on-drag'}
        automaticallyAdjustKeyboardInsets={Platform.OS === 'ios'}
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow="Security"
          title="Change password"
          subtitle="Update your sign-in password without interrupting your workspace."
          onBack={() => navigation.goBack()}
        />

        <View style={styles.content}>
          <GlassSurface
            elevated={false}
            radius={theme.radius.xl}
            contentStyle={styles.requirements}
          >
            <View style={[styles.requirementsIcon, { backgroundColor: theme.colors.primary + '16' }]}>
              <Ionicons name="shield-checkmark-outline" size={21} color={theme.colors.primary} />
            </View>
            <View style={styles.requirementsCopy}>
              <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>
                Password requirements
              </Text>
              <Text style={[theme.typography.caption, styles.requirementLine, { color: theme.colors.textSecondary }]}>
                At least 10 characters. A unique mix of letters, numbers and symbols is recommended.
              </Text>
            </View>
          </GlassSurface>

          <GlassSurface radius={theme.radius.xl} contentStyle={styles.form}>
            {fields.map((field) => (
              <Controller
                key={field.name}
                control={control}
                name={field.name}
                render={({ field: { onChange, value, onBlur } }) => (
                  <GlassTextField
                    label={field.label}
                    icon={field.icon}
                    value={value}
                    onChangeText={onChange}
                    onBlur={onBlur}
                    secureTextEntry={!visible[field.name]}
                    autoCapitalize="none"
                    autoCorrect={false}
                    autoComplete={field.autoComplete}
                    textContentType={field.autoComplete === 'current-password' ? 'password' : 'newPassword'}
                    error={errors[field.name]?.message}
                    trailing={
                      <MotionPressable
                        accessibilityRole="button"
                        accessibilityLabel={(visible[field.name] ? 'Hide ' : 'Show ') + field.label}
                        onPress={() => setVisible((current) => ({
                          ...current,
                          [field.name]: !current[field.name],
                        }))}
                        haptic="selection"
                        contentStyle={styles.visibilityButton}
                      >
                        <Ionicons
                          name={visible[field.name] ? 'eye-off-outline' : 'eye-outline'}
                          size={20}
                          color={theme.colors.textMuted}
                        />
                      </MotionPressable>
                    }
                  />
                )}
              />
            ))}

            <LiquidButton
              label="Update password"
              icon="shield-checkmark-outline"
              onPress={handleSubmit(onSubmit)}
              loading={submitting}
              disabled={submitting}
            />
          </GlassSurface>

          <Text style={[theme.typography.micro, styles.footer, { color: theme.colors.textMuted }]}>
            KynexOne never displays your existing password after submission.
          </Text>
        </View>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  scroll: { flexGrow: 1, paddingBottom: 34 },
  content: { paddingHorizontal: 16, paddingTop: 12, gap: 12 },
  requirements: { flexDirection: 'row', alignItems: 'flex-start', gap: 11, padding: 14 },
  requirementsIcon: {
    width: 40,
    height: 40,
    borderRadius: 14,
    alignItems: 'center',
    justifyContent: 'center',
  },
  requirementsCopy: { flex: 1, minWidth: 0 },
  requirementLine: { marginTop: 4, lineHeight: 18 },
  form: { padding: 18 },
  visibilityButton: {
    width: 44,
    height: 44,
    borderRadius: 14,
    alignItems: 'center',
    justifyContent: 'center',
  },
  footer: { textAlign: 'center', paddingHorizontal: 18 },
});
