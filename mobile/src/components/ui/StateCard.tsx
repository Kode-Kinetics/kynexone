import React from 'react';
import { StyleProp, StyleSheet, Text, View, ViewStyle } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import { Surface } from './Surface';
import { LiquidButton } from './LiquidButton';
import { useTheme } from '@/theme/ThemeProvider';

export type StateCardVariant = 'error' | 'empty' | 'offline' | 'permission';

export interface StateCardProps {
  variant: StateCardVariant;
  /** Defaults to a plain-language title for the variant. */
  title?: string;
  body?: string;
  /** Shows a retry button (error / offline). */
  onRetry?: () => void;
  retryLabel?: string;
  /** A different single next action (e.g. "Apply for leave" on an empty list). */
  action?: { label: string; onPress: () => void };
  style?: StyleProp<ViewStyle>;
  testID?: string;
}

const ICONS: Record<StateCardVariant, React.ComponentProps<typeof Ionicons>['name']> = {
  error: 'alert-circle-outline',
  empty: 'file-tray-outline',
  offline: 'cloud-offline-outline',
  permission: 'lock-closed-outline',
};

/** One component for error / empty / offline / no-access states, each with one clear next action. */
export function StateCard({ variant, title, body, onRetry, retryLabel, action, style, testID }: StateCardProps) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const tone = variant === 'error'
    ? theme.colors.status.danger
    : variant === 'offline'
      ? theme.colors.status.warning
      : theme.colors.status.neutral;

  const defaults = {
    error: [t('kit.state.errorTitle'), t('kit.state.errorBody')],
    empty: [t('kit.state.emptyTitle'), t('kit.state.emptyBody')],
    offline: [t('kit.state.offlineTitle'), t('kit.state.offlineBody')],
    permission: [t('kit.state.permissionTitle'), t('kit.state.permissionBody')],
  }[variant];

  const next = action ?? (onRetry ? { label: retryLabel ?? t('kit.retry'), onPress: onRetry } : null);

  return (
    <Surface style={style} contentStyle={styles.content} testID={testID}>
      <View
        accessible
        accessibilityLiveRegion={variant === 'error' || variant === 'offline' ? 'polite' : 'none'}
        style={styles.copyBlock}
      >
        <View style={[styles.icon, { backgroundColor: tone.tint }]}>
          <Ionicons accessible={false} name={ICONS[variant]} size={22} color={tone.fg} />
        </View>
        <Text style={[theme.typography.headline, styles.center, { color: theme.colors.text }]}>
          {title ?? defaults[0]}
        </Text>
        <Text style={[theme.typography.subheadline, styles.center, { color: theme.colors.textSecondary }]}>
          {body ?? defaults[1]}
        </Text>
      </View>
      {next ? (
        <LiquidButton
          label={next.label}
          onPress={next.onPress}
          variant="secondary"
          size="compact"
          style={styles.button}
        />
      ) : null}
    </Surface>
  );
}

const styles = StyleSheet.create({
  content: { padding: 20, alignItems: 'center', gap: 14 },
  copyBlock: { alignItems: 'center', gap: 6 },
  icon: { width: 44, height: 44, borderRadius: 22, alignItems: 'center', justifyContent: 'center', marginBottom: 4 },
  center: { textAlign: 'center' },
  button: { alignSelf: 'center', minWidth: 160 },
});
