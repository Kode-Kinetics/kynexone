import React, { useCallback } from 'react';
import {
  Pressable,
  PressableProps,
  StyleProp,
  ViewStyle,
} from 'react-native';
import * as Haptics from 'expo-haptics';
import Animated, {
  interpolate,
  useAnimatedStyle,
  useSharedValue,
  withSpring,
} from 'react-native-reanimated';
import { useTheme } from '@/theme/ThemeProvider';

type Props = Omit<PressableProps, 'style' | 'onPress'> & {
  style?: StyleProp<ViewStyle>;
  contentStyle?: StyleProp<ViewStyle>;
  onPress?: PressableProps['onPress'];
  haptic?: 'none' | 'light' | 'medium' | 'selection';
  dimensional?: boolean;
};

export function MotionPressable({
  children,
  style,
  contentStyle,
  onPress,
  haptic = 'light',
  dimensional = false,
  disabled,
  ...props
}: Props) {
  const { reduceMotion } = useTheme();
  const scale = useSharedValue(1);
  const depth = useSharedValue(0);

  const animatedStyle = useAnimatedStyle(() => {
    if (!dimensional) {
      return { transform: [{ scale: scale.value }] };
    }
    const tilt = interpolate(depth.value, [0, 1], [0, 1.25]);
    return {
      transform: [
        { perspective: 900 },
        { translateY: interpolate(depth.value, [0, 1], [0, 1.5]) },
        { rotateX: `${tilt}deg` },
        { scale: scale.value },
      ],
    };
  });
  const runHaptic = useCallback(async () => {
    if (haptic === 'none') return;
    if (haptic === 'selection') {
      await Haptics.selectionAsync();
      return;
    }
    const style = haptic === 'medium'
      ? Haptics.ImpactFeedbackStyle.Medium
      : Haptics.ImpactFeedbackStyle.Light;
    await Haptics.impactAsync(style);
  }, [haptic]);

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
          if (!reduceMotion) {
            scale.set(withSpring(0.972, { damping: 18, stiffness: 340 }));
            if (dimensional) depth.set(withSpring(1, { damping: 20, stiffness: 380 }));
          }
          props.onPressIn?.(event);
        }}
        onPressOut={(event) => {
          if (!reduceMotion) {
            scale.set(withSpring(1, { damping: 16, stiffness: 260 }));
            if (dimensional) depth.set(withSpring(0, { damping: 17, stiffness: 280 }));
          }
          props.onPressOut?.(event);
        }}
        onPress={(event) => {
          void runHaptic();
          onPress?.(event);
        }}
        style={contentStyle}
      >
        {children}
      </Pressable>
    </Animated.View>
  );
}
