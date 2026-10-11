import React, { useMemo } from 'react';
import {
  Platform,
  StyleProp,
  StyleSheet,
  View,
  ViewProps,
  ViewStyle,
} from 'react-native';
import { BlurView } from 'expo-blur';
import {
  GlassView,
  isGlassEffectAPIAvailable,
  isLiquidGlassAvailable,
} from 'expo-glass-effect';
import { useTheme } from '@/theme/ThemeProvider';
import { Surface } from './Surface';

export interface GlassSurfaceProps extends ViewProps {
  children?: React.ReactNode;
  style?: StyleProp<ViewStyle>;
  contentStyle?: StyleProp<ViewStyle>;
  radius?: number;
  /** Real glass. Only for chrome: tab bar, floating/back buttons, sheet toolbars, sticky footers. */
  glass?: boolean;
  /** Real, touch-reactive glass (implies `glass`). For floating controls. */
  interactive?: boolean;
  intensity?: number;
  variant?: 'regular' | 'clear';
  tintColor?: string;
  /** Glass: soft shadow on the solid fallback only. Content: ignored (cards are flat). */
  elevated?: boolean;
  /** Content mode: render as an inset (level 0) surface. */
  recessed?: boolean;
  /** Content mode: surface level (see Surface). */
  level?: 0 | 1 | 2;
}

let nativeGlassCache: boolean | null = null;
function canUseNativeLiquidGlass() {
  if (nativeGlassCache !== null) return nativeGlassCache;
  if (Platform.OS !== 'ios') return (nativeGlassCache = false);
  try {
    nativeGlassCache = isLiquidGlassAvailable() && isGlassEffectAPIAvailable();
  } catch {
    nativeGlassCache = false;
  }
  return nativeGlassCache;
}

/**
 * Compatibility wrapper. Without `glass` / `interactive` it renders a solid content Surface — the
 * 100+ existing call sites (cards, list rows) stop painting glass in the content layer.
 *
 * With `glass`: iOS 26+ native Liquid Glass (transparent parent, the radius handed to the glass
 * itself, no fake highlights); older iOS: system chrome blur; Android and Reduce Transparency:
 * a solid chrome fill.
 */
export function GlassSurface({
  children,
  style,
  contentStyle,
  radius,
  glass = false,
  interactive = false,
  intensity = 60,
  variant = 'regular',
  tintColor,
  elevated,
  recessed = false,
  level,
  ...viewProps
}: GlassSurfaceProps) {
  const { theme } = useTheme();
  const nativeGlass = useMemo(() => canUseNativeLiquidGlass(), []);
  const resolvedRadius = radius ?? theme.radius.lg;

  if (!glass && !interactive) {
    return (
      <Surface
        level={level ?? (recessed ? 0 : 1)}
        radius={resolvedRadius}
        tintColor={tintColor}
        style={style}
        contentStyle={contentStyle}
        {...viewProps}
      >
        {children}
      </Surface>
    );
  }

  const solid = theme.reduceTransparency || Platform.OS !== 'ios';

  if (!solid && nativeGlass) {
    return (
      <GlassView
        style={[style, { borderRadius: resolvedRadius }]}
        glassEffectStyle={variant}
        colorScheme={theme.mode}
        tintColor={tintColor}
        isInteractive={interactive}
        {...viewProps}
      >
        <View style={[styles.content, contentStyle]}>{children}</View>
      </GlassView>
    );
  }

  return (
    <View
      style={[
        styles.clip,
        solid && elevated ? theme.shadows.raised : undefined,
        {
          borderRadius: resolvedRadius,
          borderColor: theme.colors.hairline,
          backgroundColor: solid ? theme.colors.chromeSolid : 'transparent',
        },
        style,
      ]}
      {...viewProps}
    >
      {!solid ? (
        <BlurView
          pointerEvents="none"
          style={StyleSheet.absoluteFill}
          intensity={intensity}
          tint={theme.isDark ? 'systemChromeMaterialDark' : 'systemChromeMaterialLight'}
        />
      ) : null}
      {tintColor && !solid ? (
        <View pointerEvents="none" style={[StyleSheet.absoluteFill, { backgroundColor: tintColor }]} />
      ) : null}
      <View style={[styles.content, contentStyle]}>{children}</View>
    </View>
  );
}

const styles = StyleSheet.create({
  clip: {
    overflow: 'hidden',
    borderWidth: StyleSheet.hairlineWidth,
  },
  // flexGrow, not flex: 1. `flex: 1` sets flexBasis 0, which collapses the content to nothing inside an
  // auto-height parent (the selfie capture panel lost its controls). flexGrow keeps the content's natural
  // height there, yet still fills a fixed-height surface so callers that centre content (GlassIconButton's
  // 44×44 circle, LiquidTabBar's row) stay vertically centred.
  content: { flexGrow: 1 },
});
