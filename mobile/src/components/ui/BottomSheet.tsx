import React, { useEffect, useState } from 'react';
import {
  Keyboard,
  KeyboardAvoidingView,
  Modal,
  Platform,
  Pressable,
  ScrollView,
  StyleProp,
  StyleSheet,
  Text,
  useWindowDimensions,
  View,
  ViewStyle,
} from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import Animated, {
  Easing,
  interpolate,
  runOnJS,
  useAnimatedStyle,
  useSharedValue,
  withTiming,
} from 'react-native-reanimated';
import { MotionPressable } from './MotionPressable';
import { useTheme } from '@/theme/ThemeProvider';

export interface BottomSheetProps {
  visible: boolean;
  /** Called on backdrop tap (after the keyboard is down), close button, or Android back. */
  onClose: () => void;
  title?: string;
  /** Short line under the title. */
  subtitle?: string;
  children: React.ReactNode;
  /** Sticky action area (e.g. one LiquidButton). Stays above the keyboard; the body scrolls. */
  actions?: React.ReactNode;
  /** Fraction of the window the sheet may use. Default 0.9. */
  maxHeightRatio?: number;
  /** Hide the close (x) button — e.g. when an action is mandatory. Backdrop still closes. */
  hideClose?: boolean;
  contentStyle?: StyleProp<ViewStyle>;
  testID?: string;
}

/**
 * Modal bottom sheet: solid content surface, scrollable body, sticky actions, keyboard-aware.
 * Tapping the backdrop first dismisses the keyboard; a second tap closes the sheet.
 */
export function BottomSheet({
  visible,
  onClose,
  title,
  subtitle,
  children,
  actions,
  maxHeightRatio = 0.9,
  hideClose = false,
  contentStyle,
  testID,
}: BottomSheetProps) {
  const { theme, reduceMotion } = useTheme();
  const { t } = useTranslation();
  const insets = useSafeAreaInsets();
  const { height } = useWindowDimensions();
  const [mounted, setMounted] = useState(visible);
  const progress = useSharedValue(0);

  useEffect(() => {
    if (visible) {
      setMounted(true);
      progress.value = withTiming(1, {
        duration: reduceMotion ? 120 : theme.motion.durations.base + 60,
        easing: Easing.out(Easing.cubic),
      });
    } else {
      Keyboard.dismiss();
      progress.value = withTiming(0, { duration: theme.motion.durations.fast + 40, easing: Easing.in(Easing.cubic) }, (done) => {
        if (done) runOnJS(setMounted)(false);
      });
    }
  }, [progress, reduceMotion, theme.motion.durations.base, theme.motion.durations.fast, visible]);

  const slide = reduceMotion ? 0 : 1;
  const backdropStyle = useAnimatedStyle(() => ({ opacity: progress.value }));
  const sheetStyle = useAnimatedStyle(() => ({
    opacity: slide ? 1 : progress.value,
    transform: [{ translateY: interpolate(progress.value, [0, 1], [slide ? 420 : 0, 0]) }],
  }));

  const onBackdrop = () => {
    if (Keyboard.isVisible()) {
      Keyboard.dismiss();
      return;
    }
    onClose();
  };

  return (
    <Modal
      visible={mounted}
      transparent
      animationType="none"
      statusBarTranslucent
      onRequestClose={onClose}
      testID={testID}
    >
      <KeyboardAvoidingView
        style={styles.flex}
        behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
      >
        <Animated.View style={[StyleSheet.absoluteFill, { backgroundColor: theme.colors.scrim }, backdropStyle]}>
          <Pressable
            style={StyleSheet.absoluteFill}
            onPress={onBackdrop}
            accessibilityRole="button"
            accessibilityLabel={t('kit.close')}
          />
        </Animated.View>
        <View style={styles.anchor} pointerEvents="box-none">
          <Animated.View
            accessibilityViewIsModal
            style={[
              styles.sheet,
              {
                maxHeight: height * maxHeightRatio,
                backgroundColor: theme.colors.card,
                borderColor: theme.colors.hairline,
              },
              sheetStyle,
            ]}
          >
            <View style={[styles.grabber, { backgroundColor: theme.colors.border }]} />
            {title || !hideClose ? (
              <View style={styles.header}>
                <View style={styles.flex}>
                  {title ? (
                    <Text accessibilityRole="header" style={[theme.typography.headline, { color: theme.colors.text }]}>
                      {title}
                    </Text>
                  ) : null}
                  {subtitle ? (
                    <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary, marginTop: 2 }]}>
                      {subtitle}
                    </Text>
                  ) : null}
                </View>
                {!hideClose ? (
                  <MotionPressable
                    accessibilityRole="button"
                    accessibilityLabel={t('kit.close')}
                    onPress={onClose}
                    hitSlop={8}
                    contentStyle={[styles.close, { backgroundColor: theme.colors.surfaceInset }]}
                  >
                    <Ionicons accessible={false} name="close" size={18} color={theme.colors.textSecondary} />
                  </MotionPressable>
                ) : null}
              </View>
            ) : null}
            <ScrollView
              style={styles.body}
              contentContainerStyle={[
                styles.bodyContent,
                !actions && { paddingBottom: Math.max(insets.bottom, 16) },
                contentStyle,
              ]}
              keyboardShouldPersistTaps="handled"
              keyboardDismissMode="interactive"
              showsVerticalScrollIndicator={false}
            >
              {children}
            </ScrollView>
            {actions ? (
              <View
                style={[
                  styles.actions,
                  {
                    paddingBottom: Math.max(insets.bottom, 14),
                    borderTopColor: theme.colors.hairline,
                    backgroundColor: theme.colors.card,
                  },
                ]}
              >
                {actions}
              </View>
            ) : null}
          </Animated.View>
        </View>
      </KeyboardAvoidingView>
    </Modal>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  anchor: { flex: 1, justifyContent: 'flex-end' },
  sheet: {
    borderTopStartRadius: 28,
    borderTopEndRadius: 28,
    borderWidth: StyleSheet.hairlineWidth,
    borderBottomWidth: 0,
    overflow: 'hidden',
  },
  grabber: { alignSelf: 'center', width: 36, height: 5, borderRadius: 3, marginTop: 8, marginBottom: 4 },
  header: { flexDirection: 'row', alignItems: 'center', gap: 12, paddingHorizontal: 20, paddingTop: 8, paddingBottom: 8 },
  close: { width: 32, height: 32, borderRadius: 16, alignItems: 'center', justifyContent: 'center' },
  body: { flexGrow: 0, flexShrink: 1 },
  bodyContent: { paddingHorizontal: 20, paddingTop: 4, paddingBottom: 16, gap: 12 },
  actions: { paddingHorizontal: 20, paddingTop: 12, gap: 8, borderTopWidth: StyleSheet.hairlineWidth },
});
