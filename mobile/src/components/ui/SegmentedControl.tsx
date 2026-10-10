import React, { useEffect, useState } from 'react';
import { I18nManager, LayoutChangeEvent, StyleProp, StyleSheet, Text, View, ViewStyle } from 'react-native';
import Animated, { useAnimatedStyle, useSharedValue, withSpring } from 'react-native-reanimated';
import { MotionPressable } from './MotionPressable';
import { useTheme } from '@/theme/ThemeProvider';
import { motion } from '@/theme/motion';

export interface Segment<K extends string = string> {
  key: K;
  label: string;
  accessibilityLabel?: string;
}

export interface SegmentedControlProps<K extends string = string> {
  segments: readonly Segment<K>[];
  value: K;
  onChange: (key: K) => void;
  style?: StyleProp<ViewStyle>;
  testID?: string;
}

const PAD = 3;

/** iOS-style segmented control with one sliding selection. Equal-width segments, RTL-aware. */
export function SegmentedControl<K extends string = string>({
  segments,
  value,
  onChange,
  style,
  testID,
}: SegmentedControlProps<K>) {
  const { theme, reduceMotion } = useTheme();
  const [width, setWidth] = useState(0);
  const index = Math.max(0, segments.findIndex((s) => s.key === value));
  const segmentWidth = segments.length ? (width - PAD * 2) / segments.length : 0;
  const offset = useSharedValue(0);
  const direction = I18nManager.isRTL ? -1 : 1;

  useEffect(() => {
    const target = direction * index * segmentWidth;
    offset.value = reduceMotion || !segmentWidth ? target : withSpring(target, motion.springs.snappy);
  }, [direction, index, offset, reduceMotion, segmentWidth]);

  const lens = useAnimatedStyle(() => ({ transform: [{ translateX: offset.value }] }));

  return (
    <View
      testID={testID}
      accessibilityRole="tablist"
      onLayout={(e: LayoutChangeEvent) => setWidth(e.nativeEvent.layout.width)}
      style={[styles.track, { backgroundColor: theme.colors.surfaceInset }, style]}
    >
      {segmentWidth > 0 ? (
        <Animated.View
          pointerEvents="none"
          style={[
            styles.lens,
            theme.shadows.raised,
            { width: segmentWidth, backgroundColor: theme.colors.cardRaised },
            lens,
          ]}
        />
      ) : null}
      {segments.map((segment) => {
        const selected = segment.key === value;
        return (
          <MotionPressable
            key={segment.key}
            accessibilityRole="tab"
            accessibilityLabel={segment.accessibilityLabel ?? segment.label}
            accessibilityState={{ selected }}
            haptic={selected ? 'none' : 'selection'}
            onPress={() => {
              if (!selected) onChange(segment.key);
            }}
            style={styles.item}
            contentStyle={styles.itemBody}
          >
            <Text
              numberOfLines={1}
              maxFontSizeMultiplier={1.3}
              style={[
                theme.typography.footnote,
                styles.label,
                { color: selected ? theme.colors.text : theme.colors.textSecondary },
              ]}
            >
              {segment.label}
            </Text>
          </MotionPressable>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  track: { flexDirection: 'row', padding: PAD, borderRadius: 12, minHeight: 40 },
  lens: { position: 'absolute', top: PAD, bottom: PAD, start: PAD, borderRadius: 9 },
  item: { flex: 1 },
  itemBody: { minHeight: 34, alignItems: 'center', justifyContent: 'center', paddingHorizontal: 8 },
  // Constant weight: selection never changes the text width.
  label: { fontWeight: '600', textAlign: 'center' },
});
