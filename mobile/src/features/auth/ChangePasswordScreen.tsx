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
import { useTranslation } from 'react-i18next';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { zodResolver } from '@hookform/resolvers/zod';
import { authApi } from '@/api/adapters';
import { DEFAULT_MIN_PASSWORD_LENGTH } from '@/auth/welcomeCode';
import { haptics } from '@/utils/haptics';
import {
  GlassTextField,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
  Surface,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';

// Messages are translation keys (account.password.errors.*), resolved where they are shown.
const schema = z.object({
  currentPassword: z.string().min(1, 'currentRequired'),
  newPassword: z.string().min(DEFAULT_MIN_PASSWORD_LENGTH, 'tooShort'),
  confirmPassword: z.string().min(1, 'confirmRequired'),
}).refine((data) => data.newPassword === data.confirmPassword, {
  message: 'mismatch',
  path: ['confirmPassword'],
});
type ErrorKey = 'currentRequired' | 'tooShort' | 'confirmRequired' | 'mismatch';
type FormData = z.infer<typeof schema>;

export default function ChangePasswordScreen() {
  const navigation = useNavigation();
  const { t } = useTranslation();
  const { theme } = useTheme();
  const [submitting, setSubmitting] = useState(false);
  const [failed, setFailed] = useState(false);
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
    setFailed(false);
    try {
      await authApi.changePassword({
        currentPassword: data.currentPassword,
        newPassword: data.newPassword,
      });
      reset();
      haptics.success();
      Alert.alert(
        t('account.password.doneTitle'),
        t('account.password.doneBody'),
        [{ text: t('common.done'), onPress: () => navigation.goBack() }],
      );
    } catch {
      haptics.error();
      setFailed(true);
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
      label: t('account.password.current'),
      icon: 'lock-closed-outline',
      autoComplete: 'current-password',
    },
    {
      name: 'newPassword',
      label: t('account.password.new'),
      icon: 'key-outline',
      autoComplete: 'new-password',
    },
    {
      name: 'confirmPassword',
      label: t('account.password.confirm'),
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
        {/* The back control comes from ScreenHero automatically (this screen is pushed on More). */}
        <ScreenHero
          eyebrow={t('account.password.eyebrow')}
          title={t('account.password.title')}
          subtitle={t('account.password.subtitle')}
        />

        <View style={styles.content}>
          <Surface level={0} contentStyle={styles.requirements}>
            <View style={[styles.requirementsIcon, { backgroundColor: theme.colors.card }]}>
              <Ionicons accessible={false} name="shield-checkmark-outline" size={21} color={theme.colors.accent} />
            </View>
            <View style={styles.requirementsCopy}>
              <Text style={[theme.typography.headline, { color: theme.colors.text }]}>
                {t('account.password.rulesTitle')}
              </Text>
              <Text style={[theme.typography.footnote, styles.requirementLine, { color: theme.colors.textSecondary }]}>
                {t('account.password.rulesBody', { n: DEFAULT_MIN_PASSWORD_LENGTH })}
              </Text>
            </View>
          </Surface>

          <Surface contentStyle={styles.form}>
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
                    error={errors[field.name]?.message
                      ? t(`account.password.errors.${errors[field.name]!.message as ErrorKey}`, { n: DEFAULT_MIN_PASSWORD_LENGTH })
                      : undefined}
                    trailing={
                      <MotionPressable
                        accessibilityRole="button"
                        accessibilityLabel={visible[field.name]
                          ? t('account.password.hide', { field: field.label })
                          : t('account.password.show', { field: field.label })}
                        onPress={() => setVisible((current) => ({
                          ...current,
                          [field.name]: !current[field.name],
                        }))}
                        haptic="selection"
                        contentStyle={styles.visibilityButton}
                      >
                        <Ionicons
                          accessible={false}
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

            {failed ? (
              <Text accessibilityRole="alert" style={[theme.typography.footnote, styles.failed, { color: theme.colors.status.danger.fg }]}>
                {t('account.password.failed')}
              </Text>
            ) : null}
            <LiquidButton
              label={t('account.password.submit')}
              icon="shield-checkmark-outline"
              onPress={handleSubmit(onSubmit)}
              loading={submitting}
              disabled={submitting}
            />
          </Surface>
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
  failed: { marginBottom: 12 },
});
