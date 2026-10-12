import React from 'react';
import { StyleProp, StyleSheet, Text, View, ViewStyle } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useTheme } from '@/theme/ThemeProvider';
import type { StatusTone } from '@/theme/tokens';

export interface StatusPillProps {
  tone: StatusTone;
  /** Plain-language status — the colour is never the only signal. */
  label: string;
  icon?: React.ComponentProps<typeof Ionicons>['name'];
  /** 'tint' (default): tinted background, coloured text. 'solid': solid fill, white text. */
  emphasis?: 'tint' | 'solid';
  style?: StyleProp<ViewStyle>;
  testID?: string;
}

export function StatusPill({ tone, label, icon, emphasis = 'tint', style, testID }: StatusPillProps) {
  const { theme } = useTheme();
  const c = theme.colors.status[tone];
  const solid = emphasis === 'solid';
  const fg = solid ? theme.colors.onFill : c.fg;

  return (
    <View
      testID={testID}
      accessible
      accessibilityRole="text"
      accessibilityLabel={label}
      style={[styles.pill, { backgroundColor: solid ? c.fill : c.tint }, style]}
    >
      {icon ? <Ionicons accessible={false} name={icon} size={12} color={fg} /> : null}
      <Text
        numberOfLines={1}
        maxFontSizeMultiplier={1.4}
        style={[theme.typography.footnote, styles.label, { color: fg }]}
      >
        {label}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  pill: {
    flexDirection: 'row',
    alignItems: 'center',
    alignSelf: 'flex-start',
    gap: 4,
    minHeight: 24,
    paddingHorizontal: 10,
    paddingVertical: 3,
    borderRadius: 999,
  },
  label: { fontWeight: '600' },
});
