import React from 'react';
import { StyleProp, StyleSheet, View, ViewProps, ViewStyle } from 'react-native';
import { useTheme } from '@/theme/ThemeProvider';

export interface SurfaceProps extends ViewProps {
  children?: React.ReactNode;
  /** 0 = inset/grouped, 1 = card (default), 2 = raised card with a soft shadow. */
  level?: 0 | 1 | 2;
  radius?: number;
  /** Optional wash over the solid fill (e.g. a status tint). */
  tintColor?: string;
  style?: StyleProp<ViewStyle>;
  contentStyle?: StyleProp<ViewStyle>;
  /** Hide the hairline border. */
  borderless?: boolean;
}

/**
 * Solid content surface — the default for cards and list rows. Never glass: glass is for chrome
 * (tab bar, floating buttons, sheet toolbars), and stays out of the content layer.
 */
export function Surface({
  children,
  level = 1,
  radius,
  tintColor,
  style,
  contentStyle,
  borderless = false,
  ...viewProps
}: SurfaceProps) {
  const { theme } = useTheme();
  const r = radius ?? theme.radius.lg;
  const fill = level === 0
    ? theme.colors.surfaceInset
    : level === 2
      ? theme.colors.cardRaised
      : theme.colors.card;

  return (
    <View style={[level === 2 ? theme.shadows.raised : undefined, style]} {...viewProps}>
      <View
        style={[
          styles.clip,
          {
            borderRadius: r,
            backgroundColor: fill,
            borderColor: borderless || level === 0 ? 'transparent' : theme.colors.hairline,
          },
        ]}
      >
        {tintColor ? (
          <View pointerEvents="none" style={[StyleSheet.absoluteFill, { backgroundColor: tintColor }]} />
        ) : null}
        <View style={[styles.content, contentStyle]}>{children}</View>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  clip: {
    flex: 1,
    overflow: 'hidden',
    borderWidth: StyleSheet.hairlineWidth,
  },
  content: { flex: 1 },
});
