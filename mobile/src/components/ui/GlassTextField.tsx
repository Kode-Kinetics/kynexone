import React, { useEffect, useState } from 'react';
import {
  StyleProp,
  StyleSheet,
  Text,
  TextInput,
  TextInputProps,
  View,
  ViewStyle,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import Animated, {
  interpolate,
  useAnimatedStyle,
  useSharedValue,
  withSpring,
} from 'react-native-reanimated';
import { GlassSurface } from './GlassSurface';
import { useTheme } from '@/theme/ThemeProvider';

interface Props extends TextInputProps {
  label: string;
  icon: React.ComponentProps<typeof Ionicons>['name'];
  error?: string;
  trailing?: React.ReactNode;
  containerStyle?: StyleProp<ViewStyle>;
}

export function GlassTextField({
  label,
  icon,
  error,
  trailing,
  containerStyle,
  onFocus,
  onBlur,
  ...inputProps
}: Props) {
  const { theme, reduceMotion } = useTheme();
  const [focused, setFocused] = useState(false);
  const focusProgress = useSharedValue(0);
  const multiline = Boolean(inputProps.multiline);

  useEffect(() => {
    focusProgress.value = reduceMotion
      ? focused ? 1 : 0
      : withSpring(focused ? 1 : 0, { damping: 18, stiffness: 220, mass: 0.72 });
  }, [focusProgress, focused, reduceMotion]);

  const focusMotion = useAnimatedStyle(() => ({
    transform: [
      { perspective: 700 },
      { translateY: interpolate(focusProgress.value, [0, 1], [0, -2]) },
      { scale: interpolate(focusProgress.value, [0, 1], [1, 1.008]) },
    ],
    shadowOpacity: interpolate(focusProgress.value, [0, 1], [0, 0.18]),
  }));

  return (
    <View style={[styles.group, containerStyle]}>
      <Text style={[theme.typography.caption, styles.label, { color: theme.colors.textSecondary }]}>
        {label}
      </Text>
      <Animated.View
        style={[
          styles.focusRing,
          focusMotion,
          {
            borderColor: error
              ? theme.colors.danger
              : focused
                ? theme.colors.primary
                : 'transparent',
          },
        ]}
      >
        <GlassSurface
          elevated={false}
          recessed
          radius={16}
          contentStyle={[styles.inputRow, multiline && styles.inputRowMultiline]}
          style={[styles.surface, multiline && styles.surfaceMultiline]}
        >
          <Ionicons
            accessible={false}
            name={icon}
            size={19}
            color={focused ? theme.colors.primary : theme.colors.textMuted}
            style={multiline ? styles.iconMultiline : undefined}
          />
          <TextInput
            {...inputProps}
            accessibilityLabel={inputProps.accessibilityLabel ?? label}
            accessibilityHint={inputProps.accessibilityHint}
            accessibilityState={{
              ...inputProps.accessibilityState,
              disabled: Boolean(inputProps.editable === false),
            }}
            selectionColor={theme.colors.primary}
            placeholderTextColor={theme.colors.textMuted}
            onFocus={(event) => {
              setFocused(true);
              onFocus?.(event);
            }}
            onBlur={(event) => {
              setFocused(false);
              onBlur?.(event);
            }}
            style={[
              styles.input,
              theme.typography.body,
              { color: theme.colors.text },
              inputProps.style,
            ]}
          />
          {trailing}
        </GlassSurface>
      </Animated.View>
      {error ? (
        <Text
          accessibilityLiveRegion="polite"
          role="alert"
          style={[theme.typography.caption, styles.error, { color: theme.colors.danger }]}
        >
          {error}
        </Text>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  group: { marginBottom: 16 },
  label: { marginBottom: 7, fontWeight: '700' },
  focusRing: {
    borderWidth: 1.5,
    borderRadius: 18,
    padding: 1,
    shadowColor: '#4F75FF',
    shadowOffset: { width: 0, height: 6 },
    shadowRadius: 14,
  },
  surface: { minHeight: 56 },
  surfaceMultiline: { minHeight: 120 },
  inputRow: { flexDirection: 'row', alignItems: 'center', paddingHorizontal: 14, gap: 10 },
  inputRowMultiline: { alignItems: 'flex-start' },
  iconMultiline: { marginTop: 17 },
  input: { flex: 1, minHeight: 52, paddingVertical: 12 },
  error: { marginTop: 5, marginLeft: 4 },
});
