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
import { LinearGradient } from 'expo-linear-gradient';
import {
  GlassView,
  isGlassEffectAPIAvailable,
  isLiquidGlassAvailable,
} from 'expo-glass-effect';
import { useTheme } from '@/theme/ThemeProvider';

export interface GlassSurfaceProps extends ViewProps {
  children?: React.ReactNode;
  style?: StyleProp<ViewStyle>;
  contentStyle?: StyleProp<ViewStyle>;
  radius?: number;
  intensity?: number;
  interactive?: boolean;
  variant?: 'regular' | 'clear';
  tintColor?: string;
  elevated?: boolean;
  recessed?: boolean;
}

function canUseNativeLiquidGlass() {
  if (Platform.OS !== 'ios') return false;
  try {
    return isLiquidGlassAvailable() && isGlassEffectAPIAvailable();
  } catch {
    return false;
  }
}
export function GlassSurface({
  children,
  style,
  contentStyle,
  radius,
  intensity = 42,
  interactive = false,
  variant = 'regular',
  tintColor,
  elevated = true,
  recessed = false,
  ...viewProps
}: GlassSurfaceProps) {
  const { theme } = useTheme();
  const nativeGlass = useMemo(() => canUseNativeLiquidGlass(), []);
  const resolvedRadius = radius ?? theme.radius.xl;

  return (
    <View
      style={[
        elevated ? theme.shadows.soft : undefined,
        style,
      ]}
      {...viewProps}
    >
      <View
        style={[
          styles.clip,
          {
            borderRadius: resolvedRadius,
            borderColor: theme.colors.glassBorder,
            backgroundColor: theme.colors.surface,
          },
        ]}
      >
        {!theme.reduceTransparency && nativeGlass ? (
          <GlassView
            pointerEvents="none"
            style={StyleSheet.absoluteFill}
            glassEffectStyle={variant}
            colorScheme={theme.mode}
            tintColor={tintColor ?? theme.colors.glassTint}
            isInteractive={interactive}
          />
        ) : !theme.reduceTransparency ? (
          <BlurView
            pointerEvents="none"
            style={StyleSheet.absoluteFill}
            intensity={intensity}
            tint={theme.isDark ? 'dark' : 'light'}
          />
        ) : null}

        <LinearGradient
          pointerEvents="none"
          colors={
            theme.isDark
              ? ['rgba(255,255,255,0.12)', 'rgba(255,255,255,0.02)', 'transparent']
              : ['rgba(255,255,255,0.78)', 'rgba(255,255,255,0.20)', 'transparent']
          }
          locations={[0, 0.42, 1]}
          start={{ x: 0, y: 0 }}
          end={{ x: 1, y: 1 }}
          style={StyleSheet.absoluteFill}
        />

        {elevated || interactive ? (
          <LinearGradient
            pointerEvents="none"
            colors={
              theme.isDark
                ? ['rgba(255,255,255,0.26)', 'rgba(255,255,255,0.04)', 'transparent']
                : ['rgba(255,255,255,0.96)', 'rgba(255,255,255,0.20)', 'transparent']
            }
            locations={[0, 0.32, 1]}
            start={{ x: 0.08, y: 0 }}
            end={{ x: 0.92, y: 0 }}
            style={styles.specularRim}
          />
        ) : null}

        {elevated || interactive ? (
          <LinearGradient
            pointerEvents="none"
            colors={
              theme.isDark
                ? ['transparent', 'rgba(0,0,0,0.04)', 'rgba(0,2,12,0.24)']
                : ['transparent', 'rgba(112,142,184,0.035)', 'rgba(63,91,132,0.13)']
            }
            locations={[0, 0.58, 1]}
            start={{ x: 0.05, y: 0.05 }}
            end={{ x: 1, y: 1 }}
            style={styles.neumorphicDepth}
          />
        ) : null}

        {recessed ? (
          <LinearGradient
            pointerEvents="none"
            colors={
              theme.isDark
                ? ['rgba(0,0,0,0.26)', 'transparent', 'rgba(255,255,255,0.05)']
                : ['rgba(75,103,145,0.13)', 'transparent', 'rgba(255,255,255,0.58)']
            }
            locations={[0, 0.42, 1]}
            start={{ x: 0, y: 0 }}
            end={{ x: 1, y: 1 }}
            style={StyleSheet.absoluteFill}
          />
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
  specularRim: {
    position: 'absolute',
    top: 0,
    right: 12,
    left: 12,
    height: StyleSheet.hairlineWidth,
  },
  neumorphicDepth: {
    position: 'absolute',
    right: 0,
    bottom: 0,
    left: 0,
    height: '58%',
  },
  content: {
    flex: 1,
  },
});
