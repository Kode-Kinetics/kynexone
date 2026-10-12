import React, { useContext, useEffect } from 'react';
import { StyleSheet, Text, useWindowDimensions, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { NavigationContext } from '@react-navigation/native';
import { useTranslation } from 'react-i18next';
import Animated, {
  Easing,
  interpolate,
  useAnimatedStyle,
  useSharedValue,
  withTiming,
} from 'react-native-reanimated';
import { MotionPressable } from './MotionPressable';
import { DirectionalIcon } from './DirectionalIcon';
import { useTheme } from '@/theme/ThemeProvider';

export interface ScreenHeroProps {
  eyebrow?: string;
  title: string;
  subtitle?: string;
  /** Trailing controls (e.g. GlassIconButton). */
  actions?: React.ReactNode;
  children?: React.ReactNode;
  /**
   * Back handler. When omitted, a back control appears automatically if this screen was pushed
   * onto a stack (not on a tab's root screen). Pass `hideBack` to suppress it.
   */
  onBack?: () => void;
  backLabel?: string;
  hideBack?: boolean;
}

/** Whether the screen sits above another screen in its own stack (tab switches don't count). */
function usePushedOnStack(): (() => void) | null {
  const navigation = useContext(NavigationContext);
  if (!navigation) return null;
  try {
    const state = navigation.getState();
    if (state?.type === 'stack' && state.index > 0) return () => navigation.goBack();
  } catch {
    // Not inside a navigator.
  }
  return null;
}

/**
 * Screen header on the content layer: eyebrow, title, subtitle, trailing actions. Not a card and
 * not glass — floating controls inside it may be glass without stacking glass on glass.
 * Enters with a short fade (≤180ms); instant under Reduce Motion.
 */
export function ScreenHero({
  eyebrow,
  title,
  subtitle,
  actions,
  children,
  onBack,
  backLabel,
  hideBack = false,
}: ScreenHeroProps) {
  const { theme, reduceMotion } = useTheme();
  const { t } = useTranslation();
  const insets = useSafeAreaInsets();
  const { width, fontScale } = useWindowDimensions();
  const stackHeader = width < 360 || fontScale > 1.3;
  const autoBack = usePushedOnStack();
  const back = hideBack ? null : onBack ?? autoBack;
  const entrance = useSharedValue(reduceMotion ? 1 : 0);
  const enterOffset = theme.motion.enterOffset;

  useEffect(() => {
    entrance.value = reduceMotion
      ? 1
      : withTiming(1, { duration: 180, easing: Easing.out(Easing.cubic) });
  }, [entrance, reduceMotion]);

  const entranceMotion = useAnimatedStyle(() => ({
    opacity: entrance.value,
    transform: [{ translateY: interpolate(entrance.value, [0, 1], [enterOffset, 0]) }],
  }));

  return (
    <Animated.View style={[styles.wrapper, { paddingTop: insets.top + (back ? 2 : 12) }, entranceMotion]}>
      {back ? (
        <MotionPressable
          accessibilityRole="button"
          accessibilityLabel={backLabel ?? t('kit.back')}
          onPress={back}
          contentStyle={styles.backButton}
          style={styles.backShell}
          hitSlop={8}
        >
          <DirectionalIcon name="chevronBack" size={22} color={theme.colors.accent} />
          <Text
            maxFontSizeMultiplier={1.4}
            style={[theme.typography.callout, { color: theme.colors.accent }]}
          >
            {backLabel ?? t('kit.back')}
          </Text>
        </MotionPressable>
      ) : null}
      <View style={[styles.row, stackHeader && styles.rowStacked]}>
        <View style={styles.copy}>
          {eyebrow ? (
            <Text
              maxFontSizeMultiplier={1.4}
              style={[theme.typography.eyebrow, styles.eyebrow, { color: theme.colors.accent }]}
            >
              {eyebrow}
            </Text>
          ) : null}
          <Text accessibilityRole="header" style={[theme.typography.title1, { color: theme.colors.text }]}>
            {title}
          </Text>
          {subtitle ? (
            <Text style={[theme.typography.subheadline, styles.subtitle, { color: theme.colors.textSecondary }]}>
              {subtitle}
            </Text>
          ) : null}
        </View>
        {actions ? (
          <View style={[styles.actions, stackHeader && styles.actionsStacked]}>{actions}</View>
        ) : null}
      </View>
      {children}
    </Animated.View>
  );
}

const styles = StyleSheet.create({
  wrapper: { paddingHorizontal: 16, paddingBottom: 12 },
  backShell: { alignSelf: 'flex-start', marginStart: -6, marginBottom: 2 },
  backButton: { minHeight: 44, flexDirection: 'row', alignItems: 'center', gap: 2, paddingEnd: 10 },
  row: { flexDirection: 'row', alignItems: 'flex-end', gap: 12 },
  rowStacked: { flexDirection: 'column', alignItems: 'stretch' },
  copy: { flex: 1, minWidth: 0 },
  eyebrow: { marginBottom: 4 },
  subtitle: { marginTop: 4 },
  actions: { flexDirection: 'row', gap: 8 },
  actionsStacked: { alignSelf: 'flex-end' },
});
