import React, { useCallback, useMemo, useRef, useState } from 'react';
import {
  AccessibilityActionEvent,
  NativeScrollEvent,
  NativeSyntheticEvent,
  ScrollView,
  StyleSheet,
  View,
  useWindowDimensions,
} from 'react-native';
import { useTranslation } from 'react-i18next';
import { useTheme } from '@/theme/ThemeProvider';

interface Props {
  children: React.ReactNode[];
  horizontalInset?: number;
  gap?: number;
  minHeight?: number;
}

export function SwipeDeck({
  children,
  horizontalInset = 16,
  gap = 10,
  minHeight = 0,
}: Props) {
  const { width } = useWindowDimensions();
  const { theme, reduceMotion } = useTheme();
  const { t } = useTranslation();
  const [index, setIndex] = useState(0);
  const scrollRef = useRef<ScrollView>(null);

  const pages = useMemo(() => React.Children.toArray(children), [children]);
  const pageWidth = Math.max(280, width - horizontalInset * 2);
  const onMomentumScrollEnd = useCallback(
    (event: NativeSyntheticEvent<NativeScrollEvent>) => {
      const next = Math.round(event.nativeEvent.contentOffset.x / (pageWidth + gap));
      setIndex(Math.max(0, Math.min(pages.length - 1, next)));
    },
    [gap, pageWidth, pages.length],
  );

  // VoiceOver / TalkBack: swipe up/down on the page indicator moves between pages.
  const goTo = useCallback(
    (target: number) => {
      const next = Math.max(0, Math.min(pages.length - 1, target));
      setIndex(next);
      scrollRef.current?.scrollTo({ x: next * (pageWidth + gap), animated: !reduceMotion });
    },
    [gap, pageWidth, pages.length, reduceMotion],
  );
  const onAccessibilityAction = useCallback(
    (event: AccessibilityActionEvent) => {
      if (event.nativeEvent.actionName === 'increment') goTo(index + 1);
      if (event.nativeEvent.actionName === 'decrement') goTo(index - 1);
    },
    [goTo, index],
  );

  return (
    <View>
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

      {pages.length > 1 ? (
        <View
          accessible
          accessibilityRole="adjustable"
          accessibilityLabel={t('kit.pageOf', { current: index + 1, total: pages.length })}
          accessibilityValue={{ min: 1, max: pages.length, now: index + 1 }}
          accessibilityHint={t('kit.pageHint')}
          accessibilityActions={[{ name: 'increment' }, { name: 'decrement' }]}
          onAccessibilityAction={onAccessibilityAction}
          style={styles.dots}
        >
          {pages.map((_, dotIndex) => (
            <View
              key={dotIndex}
              style={[
                styles.dot,
                {
                  backgroundColor:
                    dotIndex === index
                      ? theme.colors.accent
                      : theme.colors.divider,
                  width: dotIndex === index && !reduceMotion ? 18 : 7,
                },
              ]}
            />
          ))}
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  rail: { alignItems: 'stretch' },
  page: { flexShrink: 0 },
  dots: {
    minHeight: 24,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 6,
    paddingTop: 8,
  },
  dot: {
    height: 7,
    borderRadius: 999,
  },
});
