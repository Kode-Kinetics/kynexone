import React, { useCallback, useMemo, useState } from 'react';
import {
  NativeScrollEvent,
  NativeSyntheticEvent,
  ScrollView,
  StyleSheet,
  View,
  useWindowDimensions,
} from 'react-native';
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
  const [index, setIndex] = useState(0);

  const pages = useMemo(() => React.Children.toArray(children), [children]);
  const pageWidth = Math.max(280, width - horizontalInset * 2);  const onMomentumScrollEnd = useCallback(
    (event: NativeSyntheticEvent<NativeScrollEvent>) => {
      const next = Math.round(event.nativeEvent.contentOffset.x / (pageWidth + gap));
      setIndex(Math.max(0, Math.min(pages.length - 1, next)));
    },
    [gap, pageWidth, pages.length],
  );

  return (
    <View>
      <ScrollView
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
      >        {pages.map((page, pageIndex) => (
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
          accessibilityRole="adjustable"
          accessibilityLabel={"Page " + (index + 1) + " of " + pages.length}
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
                      ? theme.colors.primary
                      : theme.colors.divider,
                  width: dotIndex === index && !reduceMotion ? 18 : 7,
                },
              ]}
            />
          ))}
        </View>      ) : null}
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
