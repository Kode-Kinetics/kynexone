import React, { useEffect } from 'react';
import {
  ActivityIndicator,
  StyleProp,
  StyleSheet,
  Text,
  View,
  ViewStyle,
} from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import { Ionicons } from '@expo/vector-icons';
import Animated, {
  cancelAnimation,
  Easing,
  interpolate,
  useAnimatedStyle,
  useSharedValue,
  withRepeat,
  withSequence,
  withTiming,
} from 'react-native-reanimated';
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
  const { theme, reduceMotion } = useTheme();
  const activity = useSharedValue(0);
  const colors = variant === 'success'
    ? theme.gradients.success
    : variant === 'danger'
      ? theme.gradients.danger
      : theme.gradients.primary;

  useEffect(() => {
    cancelAnimation(activity);
    activity.value = 0;
    if (loading && !reduceMotion) {
      activity.value = withRepeat(
        withSequence(
          withTiming(1, { duration: 650, easing: Easing.inOut(Easing.quad) }),
          withTiming(0, { duration: 650, easing: Easing.inOut(Easing.quad) }),
        ),
        -1,
        false,
      );
    }
  }, [activity, loading, reduceMotion]);

  const loadingMotion = useAnimatedStyle(() => ({
    transform: [
      { translateY: interpolate(activity.value, [0, 1], [0, -1.5]) },
      { scale: interpolate(activity.value, [0, 1], [1, 1.008]) },
    ],
  }));

  return (
    <Animated.View style={[style, loadingMotion]}>
    <MotionPressable
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityState={{ disabled: Boolean(disabled || loading), busy: Boolean(loading) }}
      testID={testID}
      onPress={onPress}
      disabled={disabled || loading}
      haptic="medium"
      dimensional
      style={[styles.shell, theme.shadows.soft]}
      contentStyle={styles.pressable}
    >
      <LinearGradient
        colors={colors}
        start={{ x: 0, y: 0 }}
        end={{ x: 1, y: 1 }}
        style={styles.gradient}
      >
        <View pointerEvents="none" style={styles.innerHighlight} />
        <View pointerEvents="none" style={styles.innerShade} />
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
    </Animated.View>
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
  innerHighlight: {
    position: 'absolute',
    top: 1,
    right: 16,
    left: 16,
    height: 1,
    borderRadius: 1,
    backgroundColor: 'rgba(255,255,255,0.68)',
  },
  innerShade: {
    position: 'absolute',
    right: 14,
    bottom: 1,
    left: 14,
    height: 1,
    borderRadius: 1,
    backgroundColor: 'rgba(13,33,112,0.22)',
  },
  label: {
    color: '#FFFFFF',
    fontSize: 16,
    fontWeight: '700',
    letterSpacing: 0.1,
  },
});
