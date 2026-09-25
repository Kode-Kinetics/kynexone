import React, { useEffect } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import type { BottomTabBarProps } from '@react-navigation/bottom-tabs';
import { Ionicons } from '@expo/vector-icons';
import Animated, {
  interpolate,
  useAnimatedStyle,
  useSharedValue,
  withSpring,
} from 'react-native-reanimated';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { GlassSurface, MotionPressable } from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';

const icons: Record<string, { active: any; inactive: any; label: string }> = {
  Home: { active: 'home', inactive: 'home-outline', label: 'Home' },
  Attendance: { active: 'location', inactive: 'location-outline', label: 'Attendance' },
  Leave: { active: 'calendar', inactive: 'calendar-outline', label: 'Leave' },
  Payslips: { active: 'wallet', inactive: 'wallet-outline', label: 'Payslips' },
  Team: { active: 'people', inactive: 'people-outline', label: 'Team' },
  Approvals: { active: 'checkmark-done', inactive: 'checkmark-done-outline', label: 'Approvals' },
  More: { active: 'grid', inactive: 'grid-outline', label: 'More' },
};

export function LiquidTabBar({ state, descriptors, navigation }: BottomTabBarProps) {
  const { theme } = useTheme();
  const insets = useSafeAreaInsets();

  return (
    <View
      style={[
        styles.safeArea,
        {
          paddingBottom: Math.max(insets.bottom, 8),
          backgroundColor: theme.colors.canvas,
        },
      ]}
    >
      <GlassSurface
        interactive
        radius={theme.radius.xxl}
        style={styles.bar}
        contentStyle={styles.row}
        tintColor={theme.isDark ? 'rgba(25,57,112,0.22)' : 'rgba(255,255,255,0.40)'}
      >
        {state.routes.map((route, index) => {
          const focused = state.index === index;
          const descriptor = descriptors[route.key];
          const meta = icons[route.name] ?? {
            active: 'ellipse',
            inactive: 'ellipse-outline',
            label: descriptor.options.title ?? route.name,
          };

          const onPress = () => {
            const event = navigation.emit({
              type: 'tabPress',
              target: route.key,
              canPreventDefault: true,
            });
            if (!focused && !event.defaultPrevented) navigation.navigate(route.name);
          };

          return (
            <MotionPressable
              key={route.key}
              accessibilityRole="tab"
              accessibilityState={focused ? { selected: true } : {}}
              accessibilityLabel={descriptor.options.tabBarAccessibilityLabel ?? meta.label}
              onPress={onPress}
              haptic="selection"
              style={styles.item}
              contentStyle={styles.itemPressable}
            >
              <AnimatedTabIcon focused={focused}>
                <Ionicons
                  name={focused ? meta.active : meta.inactive}
                  size={22}
                  color={focused ? theme.colors.primary : theme.colors.textMuted}
                />
              </AnimatedTabIcon>
              <Text
                numberOfLines={2}
                style={[
                  styles.label,
                  {
                    color: focused ? theme.colors.text : theme.colors.textMuted,
                    fontWeight: focused ? '700' : '500',
                  },
                ]}
              >
                {meta.label}
              </Text>
            </MotionPressable>
          );
        })}
      </GlassSurface>
    </View>
  );
}

function AnimatedTabIcon({ focused, children }: { focused: boolean; children: React.ReactNode }) {
  const { theme, reduceMotion } = useTheme();
  const selection = useSharedValue(focused ? 1 : 0);

  useEffect(() => {
    selection.value = reduceMotion
      ? focused ? 1 : 0
      : withSpring(focused ? 1 : 0, { damping: 15, stiffness: 240, mass: 0.68 });
  }, [focused, reduceMotion, selection]);

  const motion = useAnimatedStyle(() => ({
    transform: [
      { translateY: interpolate(selection.value, [0, 1], [0, -3]) },
      { scale: interpolate(selection.value, [0, 1], [1, 1.1]) },
    ],
    opacity: interpolate(selection.value, [0, 1], [0.82, 1]),
  }));

  return (
    <Animated.View
      style={[
        styles.iconWrap,
        focused && { backgroundColor: theme.colors.surfaceSoft },
        motion,
      ]}
    >
      {children}
    </Animated.View>
  );
}

const styles = StyleSheet.create({
  safeArea: { paddingHorizontal: 12, paddingTop: 8 },
  bar: { minHeight: 68 },
  row: { flexDirection: 'row', alignItems: 'center', paddingHorizontal: 6, paddingVertical: 7 },
  item: { flex: 1 },
  itemPressable: { alignItems: 'center', justifyContent: 'center', minHeight: 54, gap: 2 },
  iconWrap: { width: 38, height: 32, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  label: { fontSize: 11, lineHeight: 14, textAlign: 'center' },
});
