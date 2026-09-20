import React from 'react';
import {
  ActivityIndicator,
  StyleProp,
  StyleSheet,
  Text,
  ViewStyle,
} from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import { Ionicons } from '@expo/vector-icons';
import { MotionPressable } from './MotionPressable';
import { useTheme } from '@/theme/ThemeProvider';

interface Props {
  label: string;
  onPress: () => void;
  icon?: React.ComponentProps<typeof Ionicons>['name'];
  loading?: boolean;
  disabled?: boolean;
  style?: StyleProp<ViewStyle>;
  variant?: 'primary' | 'success' | 'danger';
  testID?: string;
}

export function LiquidButton({
  label,
  onPress,
  icon,
  loading,
  disabled,
  style,
  variant = 'primary',
  testID,
}: Props) {
  const { theme } = useTheme();
  const colors = variant === 'success'
    ? theme.gradients.success
    : variant === 'danger'
      ? theme.gradients.danger
      : theme.gradients.primary;
  return (
    <MotionPressable
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityState={{ disabled: Boolean(disabled || loading), busy: Boolean(loading) }}
      testID={testID}
      onPress={onPress}
      disabled={disabled || loading}
      haptic="medium"
      dimensional
      style={[styles.shell, theme.shadows.soft, style]}
      contentStyle={styles.pressable}
    >
      <LinearGradient
        colors={colors}
        start={{ x: 0, y: 0 }}
        end={{ x: 1, y: 1 }}
        style={styles.gradient}
      >
        {loading ? (
          <ActivityIndicator color="#FFFFFF" size="small" />
        ) : (
          <>
            {icon ? <Ionicons accessible={false} name={icon} size={19} color="#FFFFFF" /> : null}
            <Text style={styles.label}>{label}</Text>
            <Ionicons accessible={false} name="arrow-forward" size={18} color="rgba(255,255,255,0.92)" />
          </>
        )}
      </LinearGradient>
    </MotionPressable>
  );
}

const styles = StyleSheet.create({
  shell: {
    borderRadius: 18,
  },
  pressable: {
    borderRadius: 18,
    overflow: 'hidden',
  },
  gradient: {
    minHeight: 56,
    paddingHorizontal: 20,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 10,
  },
  label: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
    letterSpacing: 0.1,
  },
});
