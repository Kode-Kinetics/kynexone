import React from 'react';
import {
  Pressable,
  PressableProps,
  StyleProp,
  ViewStyle,
} from 'react-native';
import Animated, {
  interpolate,
  useAnimatedStyle,
  useSharedValue,
  withSpring,
  withTiming,
} from 'react-native-reanimated';
import { useTheme } from '@/theme/ThemeProvider';
import { motion } from '@/theme/motion';
import { haptics } from '@/utils/haptics';

export type PressHaptic = 'none' | 'selection' | 'commit' | 'light' | 'medium';

export type MotionPressableProps = Omit<PressableProps, 'style' | 'onPress'> & {
  style?: StyleProp<ViewStyle>;
  contentStyle?: StyleProp<ViewStyle>;
  onPress?: PressableProps['onPress'];
  /**
   * Default 'none': ordinary taps are silent. 'selection' for pickers/tabs, 'commit' for
   * deliberate actions ('light' / 'medium' are legacy aliases of 'commit').
   */
  haptic?: PressHaptic;
  /** @deprecated No effect — the 3D press tilt was removed. */
  dimensional?: boolean;
};

/**
 * Press feedback: scale to 0.97 on a snappy spring. Under Reduce Motion the feedback becomes an
 * opacity dip instead of disappearing.
 */
export function MotionPressable({
  children,
  style,
  contentStyle,
  onPress,
  haptic = 'none',
  dimensional: _dimensional,
  disabled,
  ...props
}: MotionPressableProps) {
  const { reduceMotion } = useTheme();
  const pressed = useSharedValue(0);

  const animatedStyle = useAnimatedStyle(() => {
    if (reduceMotion) {
      return { opacity: interpolate(pressed.value, [0, 1], [1, motion.press.reducedOpacity]) };
    }
    return { transform: [{ scale: interpolate(pressed.value, [0, 1], [1, motion.press.scale]) }] };
  });

  const press = (to: 0 | 1) => {
    pressed.set(reduceMotion
      ? withTiming(to, { duration: to ? 60 : motion.durations.fast })
      : withSpring(to, motion.springs.snappy));
  };

  return (
    <Animated.View style={[style, animatedStyle, disabled && { opacity: 0.55 }]}>
      <Pressable
        {...props}
        accessibilityRole={props.accessibilityRole ?? (onPress ? 'button' : undefined)}
        accessibilityState={{
          ...props.accessibilityState,
          disabled: Boolean(disabled || props.accessibilityState?.disabled),
        }}
        disabled={disabled}
        onPressIn={(event) => {
          press(1);
          props.onPressIn?.(event);
        }}
        onPressOut={(event) => {
          press(0);
          props.onPressOut?.(event);
        }}
        onPress={(event) => {
          if (haptic === 'selection') haptics.select();
          else if (haptic !== 'none') haptics.commit();
          onPress?.(event);
        }}
        style={contentStyle}
      >
        {children}
      </Pressable>
    </Animated.View>
  );
}
