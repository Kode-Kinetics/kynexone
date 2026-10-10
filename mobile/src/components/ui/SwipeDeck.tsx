import React, { useCallback, useMemo, useRef, useState } from 'react';
import {
  NativeScrollEvent,
  NativeSyntheticEvent,
  ScrollView,
  StyleSheet,
  Text,
  View,
  useWindowDimensions,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useTheme } from '@/theme/ThemeProvider';
import { MotionPressable } from './MotionPressable';

interface Props {
  children: React.ReactNode[];
  horizontalInset?: number;
  gap?: number;
  minHeight?: number;
  labels?: string[];
  showControls?: boolean;
}

export function SwipeDeck({
  children,
  horizontalInset = 16,
  gap = 10,
  minHeight = 0,
  labels,
  showControls = false,
}: Props) {
  const { width } = useWindowDimensions();
  const { theme, reduceMotion } = useTheme();
  const [index, setIndex] = useState(0);
  const scrollRef = useRef<ScrollView>(null);

  const pages = useMemo(() => React.Children.toArray(children), [children]);
  const pageWidth = Math.max(280, width - horizontalInset * 2);
  const goToPage = useCallback((nextIndex: number) => {
    const boundedIndex = Math.max(0, Math.min(pages.length - 1, nextIndex));
    scrollRef.current?.scrollTo({
      x: boundedIndex * (pageWidth + gap),
      animated: !reduceMotion,
    });
    setIndex(boundedIndex);
  }, [gap, pageWidth, pages.length, reduceMotion]);

  const onMomentumScrollEnd = useCallback(
    (event: NativeSyntheticEvent<NativeScrollEvent>) => {
      const next = Math.round(event.nativeEvent.contentOffset.x / (pageWidth + gap));
      setIndex(Math.max(0, Math.min(pages.length - 1, next)));
    },
    [gap, pageWidth, pages.length],
  );

  return (
    <View>
      {labels?.length === pages.length ? (
        <View style={[styles.segmentedControl, { backgroundColor: theme.colors.surfaceSoft }]}>
          {labels.map((label, tabIndex) => {
            const selected = tabIndex === index;
            return (
              <MotionPressable
                key={label}
                onPress={() => goToPage(tabIndex)}
                haptic="selection"
                style={styles.segmentShell}
                contentStyle={[
                  styles.segment,
                  selected && { backgroundColor: theme.colors.surface, borderColor: theme.colors.border },
                ]}
                accessibilityRole="tab"
                accessibilityLabel={`${label}, ${tabIndex + 1} of ${pages.length}`}
                accessibilityState={{ selected }}
              >
                <Text
                  numberOfLines={1}
                  style={[
                    theme.typography.caption,
                    styles.segmentLabel,
                    { color: selected ? theme.colors.primary : theme.colors.textSecondary },
                  ]}
                >
                  {label}
                </Text>
              </MotionPressable>
            );
          })}
        </View>
      ) : null}
      <ScrollView
        ref={scrollRef}
        horizontal
        pagingEnabled={false}
        snapToInterval={pageWidth + gap}
        snapToAlignment="start"
        decelerationRate="fast"
        disableIntervalMomentum
        directionalLockEnabled
        showsHorizontalScrollIndicator={false}
        onMomentumScrollEnd={onMomentumScrollEnd}
        contentContainerStyle={[
          styles.rail,
          {
            paddingHorizontal: horizontalInset,
            gap,
          },
        ]}
      >
        {pages.map((page, pageIndex) => (
          <View
            key={pageIndex}
            style={[
              styles.page,
              {
                width: pageWidth,
                minHeight,
              },
            ]}
          >
            {page}
          </View>
        ))}
      </ScrollView>

      {showControls && pages.length > 1 ? (
        <View style={styles.controls}>
          {index > 0 ? (
            <MotionPressable
              onPress={() => goToPage(index - 1)}
              haptic="selection"
              contentStyle={[styles.controlButton, { backgroundColor: theme.colors.surfaceSoft }]}
              accessibilityRole="button"
              accessibilityLabel={`Back to ${labels?.[index - 1] ?? `step ${index}`}`}
            >
              <Ionicons name="arrow-back" size={18} color={theme.colors.textSecondary} />
              <Text style={[theme.typography.bodyStrong, { color: theme.colors.textSecondary }]}>Back</Text>
            </MotionPressable>
          ) : <View style={styles.controlSpacer} />}
          <Text accessibilityLiveRegion="polite" style={[theme.typography.caption, { color: theme.colors.textMuted }]}>
            {index + 1} of {pages.length}
          </Text>
          {index < pages.length - 1 ? (
            <MotionPressable
              onPress={() => goToPage(index + 1)}
              haptic="selection"
              contentStyle={[styles.controlButton, styles.nextButton, { backgroundColor: theme.colors.primary }]}
              accessibilityRole="button"
              accessibilityLabel={`Continue to ${labels?.[index + 1] ?? `step ${index + 2}`}`}
            >
              <Text style={[theme.typography.bodyStrong, { color: '#FFFFFF' }]}>Continue</Text>
              <Ionicons name="arrow-forward" size={18} color="#FFFFFF" />
            </MotionPressable>
          ) : <View style={styles.controlSpacer} />}
        </View>
      ) : pages.length > 1 ? (
        <View accessibilityLabel={`Page ${index + 1} of ${pages.length}`} style={styles.dots}>
          {pages.map((_, dotIndex) => (
            <MotionPressable
              key={dotIndex}
              onPress={() => goToPage(dotIndex)}
              contentStyle={styles.dotTarget}
              accessibilityRole="button"
              accessibilityLabel={`Show ${labels?.[dotIndex] ?? `page ${dotIndex + 1}`}`}
              accessibilityState={{ selected: dotIndex === index }}
            >
              <View style={[styles.dot, {
                backgroundColor: dotIndex === index ? theme.colors.primary : theme.colors.divider,
                width: dotIndex === index && !reduceMotion ? 18 : 7,
              }]} />
            </MotionPressable>
          ))}
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  rail: { alignItems: 'stretch' },
  page: { flexShrink: 0 },
  segmentedControl: { flexDirection: 'row', marginHorizontal: 16, marginBottom: 12, padding: 4, borderRadius: 16 },
  segmentShell: { flex: 1 },
  segment: { minHeight: 44, paddingHorizontal: 8, alignItems: 'center', justifyContent: 'center', borderRadius: 13, borderWidth: StyleSheet.hairlineWidth, borderColor: 'transparent' },
  segmentLabel: { fontWeight: '700', textAlign: 'center' },
  controls: { minHeight: 58, paddingHorizontal: 16, paddingTop: 8, flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 10 },
  controlButton: { minWidth: 104, minHeight: 48, paddingHorizontal: 14, borderRadius: 16, flexDirection: 'row', alignItems: 'center', justifyContent: 'center', gap: 7 },
  nextButton: { minWidth: 124 },
  controlSpacer: { width: 104 },
  dots: {
    minHeight: 24,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 6,
    paddingTop: 2,
  },
  dotTarget: { width: 44, height: 44, alignItems: 'center', justifyContent: 'center' },
  dot: {
    height: 7,
    borderRadius: 999,
  },
});
