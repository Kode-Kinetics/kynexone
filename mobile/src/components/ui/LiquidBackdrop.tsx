import React, { useEffect } from 'react';
import { StyleSheet, useWindowDimensions, View } from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import Animated, {
  Easing,
  interpolate,
  useAnimatedStyle,
  useSharedValue,
  withRepeat,
  withTiming,
} from 'react-native-reanimated';
import { useTheme } from '@/theme/ThemeProvider';

export function LiquidBackdrop({ subtle = false }: { subtle?: boolean }) {
  const { theme, reduceMotion } = useTheme();
  const { width, height } = useWindowDimensions();
  const drift = useSharedValue(0);

  useEffect(() => {
    if (reduceMotion) {
      drift.value = 0;
      return;
    }
    drift.value = withRepeat(
      withTiming(1, { duration: 12000, easing: Easing.inOut(Easing.cubic) }),
      -1,
      true,
    );
  }, [drift, reduceMotion]);

  const firstOrb = useAnimatedStyle(() => ({
    transform: [
      { translateX: interpolate(drift.value, [0, 1], [-20, 24]) },
      { translateY: interpolate(drift.value, [0, 1], [0, 34]) },
      { scale: interpolate(drift.value, [0, 1], [0.94, 1.08]) },
    ],
  }));
  const secondOrb = useAnimatedStyle(() => ({
    transform: [
      { translateX: interpolate(drift.value, [0, 1], [22, -28]) },
      { translateY: interpolate(drift.value, [0, 1], [20, -18]) },
      { scale: interpolate(drift.value, [0, 1], [1.06, 0.92]) },
    ],
  }));

  const orbSize = Math.max(width * 0.88, 320);
  const accentOpacity = subtle ? 0.30 : 0.52;

  return (
    <View pointerEvents="none" style={StyleSheet.absoluteFill}>
      <LinearGradient
        colors={theme.gradients.app}
        start={{ x: 0.05, y: 0 }}
        end={{ x: 0.95, y: 1 }}
        style={StyleSheet.absoluteFill}
      />

      <Animated.View
        style={[
          styles.orb,
          firstOrb,
          {
            width: orbSize,
            height: orbSize,
            borderRadius: orbSize / 2,
            top: -orbSize * 0.48,
            right: -orbSize * 0.40,
            opacity: accentOpacity,
          },
        ]}
      >
        <LinearGradient
          colors={
            theme.isDark
              ? ['rgba(72,221,246,0.28)', 'rgba(79,117,255,0.24)', 'transparent']
              : ['rgba(72,221,246,0.38)', 'rgba(79,117,255,0.24)', 'transparent']
          }
          style={StyleSheet.absoluteFill}
        />
      </Animated.View>

      <Animated.View
        style={[
          styles.orb,
          secondOrb,
          {
            width: orbSize * 0.82,
            height: orbSize * 0.82,
            borderRadius: orbSize,
            bottom: Math.min(-120, -height * 0.08),
            left: -orbSize * 0.42,
            opacity: subtle ? 0.24 : 0.42,
          },
        ]}
      >
        <LinearGradient
          colors={
            theme.isDark
              ? ['rgba(144,103,249,0.27)', 'rgba(79,117,255,0.18)', 'transparent']
              : ['rgba(144,103,249,0.24)', 'rgba(79,117,255,0.18)', 'transparent']
          }
          style={StyleSheet.absoluteFill}
        />
      </Animated.View>
      <View
        style={[
          StyleSheet.absoluteFill,
          {
            backgroundColor: theme.isDark
              ? 'rgba(2,5,12,0.08)'
              : 'rgba(255,255,255,0.06)',
          },
        ]}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  orb: {
    position: 'absolute',
    overflow: 'hidden',
  },
});
