import React from 'react';
import {
  ActivityIndicator,
  StyleProp,
  StyleSheet,
  Text,
  ViewStyle,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { MotionPressable } from './MotionPressable';
import { DirectionalIcon, type DirectionalIconName } from './DirectionalIcon';
import { useTheme } from '@/theme/ThemeProvider';

type IoniconName = React.ComponentProps<typeof Ionicons>['name'];
const DIRECTIONAL: readonly string[] = ['forward', 'back', 'chevron', 'chevronBack', 'forwardCircle'];

export interface LiquidButtonProps {
  label: string;
  onPress: () => void;
  /** Leading icon. */
  icon?: IoniconName;
  /**
   * Opt-in trailing icon. Use 'forward' (mirrored in Arabic) only when the button moves the user
   * to a next step — never on Approve / Reject / Save.
   */
  trailingIcon?: DirectionalIconName | IoniconName;
  loading?: boolean;
  disabled?: boolean;
  style?: StyleProp<ViewStyle>;
  /** primary / success / danger: solid fills with white text. secondary: quiet outlined button. */
  variant?: 'primary' | 'success' | 'danger' | 'secondary';
  size?: 'regular' | 'compact';
  accessibilityHint?: string;
  testID?: string;
}

export function LiquidButton({
  label,
  onPress,
  icon,
  trailingIcon,
  loading,
  disabled,
  style,
  variant = 'primary',
  size = 'regular',
  accessibilityHint,
  testID,
}: LiquidButtonProps) {
  const { theme } = useTheme();
  const secondary = variant === 'secondary';
  const fill = variant === 'success'
    ? theme.colors.status.success.fill
    : variant === 'danger'
      ? theme.colors.status.danger.fill
      : secondary
        ? theme.colors.card
        : theme.colors.primaryFill;
  const fg = secondary ? theme.colors.accent : theme.colors.onFill;
  const radius = theme.radius.md;

  return (
    <MotionPressable
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityHint={accessibilityHint}
      accessibilityState={{ disabled: Boolean(disabled || loading), busy: Boolean(loading) }}
      testID={testID}
      onPress={onPress}
      disabled={disabled || loading}
      haptic="commit"
      style={style}
      contentStyle={[
        styles.body,
        size === 'compact' && styles.compact,
        {
          borderRadius: radius,
          backgroundColor: fill,
          borderColor: secondary ? theme.colors.hairline : fill,
        },
      ]}
    >
      {loading ? (
        <ActivityIndicator color={fg} size="small" />
      ) : (
        <>
          {icon ? <Ionicons accessible={false} name={icon} size={19} color={fg} /> : null}
          <Text
            numberOfLines={1}
            maxFontSizeMultiplier={1.6}
            style={[theme.typography.headline, styles.label, { color: fg }]}
          >
            {label}
          </Text>
          {trailingIcon ? (
            DIRECTIONAL.includes(trailingIcon) ? (
              <DirectionalIcon name={trailingIcon as DirectionalIconName} size={18} color={fg} />
            ) : (
              <Ionicons accessible={false} name={trailingIcon as IoniconName} size={18} color={fg} />
            )
          ) : null}
        </>
      )}
    </MotionPressable>
  );
}

const styles = StyleSheet.create({
  body: {
    minHeight: 52,
    paddingHorizontal: 20,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 8,
    borderWidth: StyleSheet.hairlineWidth,
  },
  compact: { minHeight: 44, paddingHorizontal: 14 },
  label: { flexShrink: 1, textAlign: 'center' },
});
