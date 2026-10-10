import React from 'react';
import { StyleSheet, useWindowDimensions, View } from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import { useTheme } from '@/theme/ThemeProvider';

/**
 * The app's single, static backdrop: a quiet canvas gradient with two soft colour fields.
 * No animation — it sits under blurred chrome, where a looping animation costs GPU every frame.
 *
 * Laid out against the WINDOW (not its parent), so a screen's backdrop and the tab shell's
 * backdrop line up pixel-for-pixel under the floating tab bar. `subtle` is kept for compatibility.
 */
export function LiquidBackdrop({ subtle = false }: { subtle?: boolean }) {
  const { theme } = useTheme();
  const { width, height } = useWindowDimensions();
  const orbSize = Math.max(width * 0.88, 320);
  const strength = subtle ? 0.8 : 1;

  return (
    <View pointerEvents="none" style={[styles.frame, { height }]}>
      <LinearGradient
        colors={theme.gradients.app}
        start={{ x: 0.05, y: 0 }}
        end={{ x: 0.95, y: 1 }}
        style={StyleSheet.absoluteFill}
      />
      <View
        style={[
          styles.orb,
          {
            width: orbSize,
            height: orbSize,
            borderRadius: orbSize / 2,
            top: -orbSize * 0.5,
            end: -orbSize * 0.38,
            opacity: (theme.isDark ? 0.42 : 0.36) * strength,
          },
        ]}
      >
        <LinearGradient
          colors={['rgba(79,117,255,0.30)', 'rgba(79,117,255,0.10)', 'transparent']}
          style={StyleSheet.absoluteFill}
        />
      </View>
      <View
        style={[
          styles.orb,
          {
            width: orbSize * 0.8,
            height: orbSize * 0.8,
            borderRadius: orbSize,
            top: height - orbSize * 0.42,
            start: -orbSize * 0.42,
            opacity: (theme.isDark ? 0.32 : 0.26) * strength,
          },
        ]}
      >
        <LinearGradient
          colors={['rgba(144,103,249,0.22)', 'rgba(79,117,255,0.12)', 'transparent']}
          style={StyleSheet.absoluteFill}
        />
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  frame: { position: 'absolute', top: 0, start: 0, end: 0, overflow: 'hidden' },
  orb: { position: 'absolute', overflow: 'hidden' },
});
