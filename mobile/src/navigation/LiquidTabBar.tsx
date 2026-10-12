import React, { useContext, useEffect, useState } from 'react';
import { I18nManager, Keyboard, LayoutChangeEvent, Platform, StyleSheet, Text, View } from 'react-native';
import { BottomTabBarHeightCallbackContext, type BottomTabBarProps } from '@react-navigation/bottom-tabs';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import Animated, { useAnimatedStyle, useSharedValue, withSpring } from 'react-native-reanimated';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { GlassSurface, MotionPressable } from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import { motion } from '@/theme/motion';
import { TAB_BAR_HEIGHT, tabBarBottomOffset, useTabBarInset } from './tabBarInset';

export { TAB_BAR_HEIGHT, useTabBarInset } from './tabBarInset';

type IconName = React.ComponentProps<typeof Ionicons>['name'];
type NavKey = 'home' | 'attendance' | 'leave' | 'payslips' | 'team' | 'approvals' | 'more' | 'punch' | 'account';

const TABS: Record<string, { active: IconName; inactive: IconName; labelKey: NavKey }> = {
  Home: { active: 'home', inactive: 'home-outline', labelKey: 'home' },
  Attendance: { active: 'location', inactive: 'location-outline', labelKey: 'attendance' },
  Leave: { active: 'calendar', inactive: 'calendar-outline', labelKey: 'leave' },
  Payslips: { active: 'wallet', inactive: 'wallet-outline', labelKey: 'payslips' },
  Team: { active: 'people', inactive: 'people-outline', labelKey: 'team' },
  Approvals: { active: 'checkmark-done', inactive: 'checkmark-done-outline', labelKey: 'approvals' },
  More: { active: 'grid', inactive: 'grid-outline', labelKey: 'more' },
  Punch: { active: 'finger-print', inactive: 'finger-print-outline', labelKey: 'punch' },
  Account: { active: 'person-circle', inactive: 'person-circle-outline', labelKey: 'account' },
};

const INNER_PAD = 6;

function useKeyboardShown(): boolean {
  const [shown, setShown] = useState(false);
  useEffect(() => {
    // iOS: the keyboard covers the bar. Android (adjustResize) would lift it above the keyboard.
    if (Platform.OS !== 'android') return;
    const a = Keyboard.addListener('keyboardDidShow', () => setShown(true));
    const b = Keyboard.addListener('keyboardDidHide', () => setShown(false));
    return () => {
      a.remove();
      b.remove();
    };
  }, []);
  return shown;
}

/**
 * Floating capsule tab bar on real glass, with one selection lens that slides between tabs.
 * The wrapper is transparent and absolutely positioned; MainTabs reserves the space it covers.
 */
export function LiquidTabBar({ state, descriptors, navigation }: BottomTabBarProps) {
  const { theme, reduceMotion } = useTheme();
  const { t } = useTranslation();
  const insets = useSafeAreaInsets();
  const inset = useTabBarInset();
  const reportHeight = useContext(BottomTabBarHeightCallbackContext);
  const keyboardShown = useKeyboardShown();
  const [rowWidth, setRowWidth] = useState(0);
  const count = state.routes.length;
  const itemWidth = count ? (rowWidth - INNER_PAD * 2) / count : 0;
  const lensX = useSharedValue(0);
  const direction = I18nManager.isRTL ? -1 : 1;

  // useBottomTabBarHeight() keeps working for any screen that asks.
  useEffect(() => {
    reportHeight?.(inset);
  }, [inset, reportHeight]);

  useEffect(() => {
    const target = direction * state.index * itemWidth;
    lensX.value = reduceMotion || !itemWidth ? target : withSpring(target, motion.springs.snappy);
  }, [direction, itemWidth, lensX, reduceMotion, state.index]);

  const lensStyle = useAnimatedStyle(() => ({ transform: [{ translateX: lensX.value }] }));

  if (keyboardShown) return null;

  return (
    <View
      pointerEvents="box-none"
      style={[styles.wrapper, { bottom: tabBarBottomOffset(insets.bottom) }]}
    >
      <GlassSurface
        glass
        radius={TAB_BAR_HEIGHT / 2}
        elevated
        style={styles.bar}
        contentStyle={styles.row}
      >
        <View
          accessibilityRole="tablist"
          accessibilityLabel={t('nav.tabsLabel')}
          style={styles.items}
          onLayout={(e: LayoutChangeEvent) => setRowWidth(e.nativeEvent.layout.width)}
        >
          {itemWidth > 0 ? (
            <Animated.View
              pointerEvents="none"
              style={[
                styles.lens,
                { width: itemWidth, backgroundColor: theme.isDark ? 'rgba(124,155,255,0.18)' : 'rgba(49,87,232,0.10)' },
                lensStyle,
              ]}
            />
          ) : null}
          {state.routes.map((route, index) => {
            const focused = state.index === index;
            const options = descriptors[route.key].options;
            const meta = TABS[route.name];
            const label = meta
              ? t(`nav.${meta.labelKey}`)
              : typeof options.tabBarLabel === 'string'
                ? options.tabBarLabel
                : options.title ?? route.name;

            const onPress = () => {
              const event = navigation.emit({ type: 'tabPress', target: route.key, canPreventDefault: true });
              if (!focused && !event.defaultPrevented) navigation.navigate(route.name);
            };
            const onLongPress = () => navigation.emit({ type: 'tabLongPress', target: route.key });

            return (
              <MotionPressable
                key={route.key}
                accessibilityRole="tab"
                accessibilityState={{ selected: focused }}
                accessibilityLabel={options.tabBarAccessibilityLabel ?? label}
                testID={options.tabBarTestID}
                onPress={onPress}
                onLongPress={onLongPress}
                haptic={focused ? 'none' : 'selection'}
                style={styles.item}
                contentStyle={styles.itemBody}
              >
                <Ionicons
                  accessible={false}
                  name={meta ? (focused ? meta.active : meta.inactive) : focused ? 'ellipse' : 'ellipse-outline'}
                  size={22}
                  color={focused ? theme.colors.accent : theme.colors.textSecondary}
                />
                <Text
                  numberOfLines={1}
                  adjustsFontSizeToFit
                  minimumFontScale={0.85}
                  maxFontSizeMultiplier={1.3}
                  style={[
                    styles.label,
                    { color: focused ? theme.colors.accent : theme.colors.textSecondary },
                  ]}
                >
                  {label}
                </Text>
              </MotionPressable>
            );
          })}
        </View>
      </GlassSurface>
    </View>
  );
}

const styles = StyleSheet.create({
  wrapper: { position: 'absolute', start: 0, end: 0, paddingHorizontal: 16, backgroundColor: 'transparent' },
  bar: { height: TAB_BAR_HEIGHT },
  row: { justifyContent: 'center' },
  items: { flex: 1, flexDirection: 'row', alignItems: 'stretch', padding: INNER_PAD },
  lens: {
    position: 'absolute',
    top: INNER_PAD,
    bottom: INNER_PAD,
    start: INNER_PAD,
    borderRadius: (TAB_BAR_HEIGHT - INNER_PAD * 2) / 2,
  },
  item: { flex: 1 },
  itemBody: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: 1, paddingHorizontal: 2 },
  // Constant weight: the selected tab never changes width.
  label: { fontSize: 11, lineHeight: 13, fontWeight: '600', textAlign: 'center' },
});
