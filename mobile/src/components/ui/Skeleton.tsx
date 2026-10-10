import React, { useEffect } from 'react';
import { DimensionValue, StyleProp, StyleSheet, View, ViewStyle } from 'react-native';
import { useTranslation } from 'react-i18next';
import Animated, {
  cancelAnimation,
  Easing,
  useAnimatedStyle,
  useSharedValue,
  withRepeat,
  withTiming,
} from 'react-native-reanimated';
import { Surface } from './Surface';
import { useTheme } from '@/theme/ThemeProvider';

export interface SkeletonProps {
  width?: DimensionValue;
  height?: number;
  radius?: number;
  style?: StyleProp<ViewStyle>;
}

/** A placeholder block. Gently pulses while loading; static under Reduce Motion. */
export function Skeleton({ width = '100%', height = 14, radius = 6, style }: SkeletonProps) {
  const { theme, reduceMotion } = useTheme();
  const pulse = useSharedValue(1);

  useEffect(() => {
    if (reduceMotion) {
      cancelAnimation(pulse);
      pulse.value = 1;
      return;
    }
    pulse.value = withRepeat(withTiming(0.55, { duration: 900, easing: Easing.inOut(Easing.quad) }), -1, true);
    return () => cancelAnimation(pulse);
  }, [pulse, reduceMotion]);

  const animated = useAnimatedStyle(() => ({ opacity: pulse.value }));

  return (
    <Animated.View
      accessible={false}
      importantForAccessibility="no-hide-descendants"
      style={[{ width, height, borderRadius: radius, backgroundColor: theme.colors.skeleton }, animated, style]}
    />
  );
}

/** A list-row placeholder: leading circle and two lines. */
export function SkeletonRow({ style }: { style?: StyleProp<ViewStyle> }) {
  const { t } = useTranslation();
  return (
    <View
      accessible
      accessibilityLabel={t('kit.loading')}
      accessibilityState={{ busy: true }}
      style={[styles.row, style]}
    >
      <Skeleton width={40} height={40} radius={20} />
      <View style={styles.lines}>
        <Skeleton width="62%" height={14} />
        <Skeleton width="38%" height={12} />
      </View>
    </View>
  );
}

/** A card placeholder: title line and `lines` body lines. */
export function SkeletonCard({ lines = 3, style }: { lines?: number; style?: StyleProp<ViewStyle> }) {
  const { t } = useTranslation();
  return (
    <Surface
      style={style}
      contentStyle={styles.card}
      accessible
      accessibilityLabel={t('kit.loading')}
      accessibilityState={{ busy: true }}
    >
      <Skeleton width="45%" height={16} />
      {Array.from({ length: lines }, (_, i) => (
        <Skeleton key={i} width={i === lines - 1 ? '70%' : '100%'} height={12} />
      ))}
    </Surface>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row', alignItems: 'center', gap: 12, paddingVertical: 10 },
  lines: { flex: 1, gap: 8 },
  card: { padding: 16, gap: 10 },
});
