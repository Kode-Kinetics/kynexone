import React from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { MotionPressable } from './MotionPressable';
import { useTheme } from '@/theme/ThemeProvider';

interface Props {
  title: string;
  subtitle?: string;
  actionLabel?: string;
  onAction?: () => void;
}

export function SectionHeader({ title, subtitle, actionLabel, onAction }: Props) {
  const { theme } = useTheme();
  return (
    <View style={styles.row}>
      <View style={styles.copy}>
        <Text style={[theme.typography.h3, { color: theme.colors.text }]}>{title}</Text>
        {subtitle ? (
          <Text style={[theme.typography.caption, { color: theme.colors.textMuted, marginTop: 2 }]}>
            {subtitle}
          </Text>
        ) : null}
      </View>
      {actionLabel && onAction ? (
        <MotionPressable onPress={onAction} haptic="selection" contentStyle={styles.action}>
          <Text style={[theme.typography.caption, { color: theme.colors.primary, fontWeight: '700' }]}>
            {actionLabel}
          </Text>
        </MotionPressable>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  row: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 12, marginBottom: 10 },
  copy: { flex: 1 },
  action: {
    minHeight: 44,
    paddingHorizontal: 8,
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: 10,
  },
});
