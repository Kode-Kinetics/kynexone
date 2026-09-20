import React from 'react';
import { StyleSheet, Text, useWindowDimensions, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { Ionicons } from '@expo/vector-icons';
import { GlassSurface } from './GlassSurface';
import { MotionPressable } from './MotionPressable';
import { useTheme } from '@/theme/ThemeProvider';

interface Props {
  eyebrow?: string;
  title: string;
  subtitle?: string;
  actions?: React.ReactNode;
  children?: React.ReactNode;
  onBack?: () => void;
  backLabel?: string;
}

export function ScreenHero({
  eyebrow,
  title,
  subtitle,
  actions,
  children,
  onBack,
  backLabel = 'Back',
}: Props) {
  const { theme } = useTheme();
  const insets = useSafeAreaInsets();
  const { width, fontScale } = useWindowDimensions();
  const stackHeader = width < 390 || fontScale > 1.15;

  return (
    <View style={[styles.wrapper, { paddingTop: insets.top + 10 }]}>
      {onBack ? (
        <MotionPressable
          accessibilityRole="button"
          accessibilityLabel={backLabel}
          onPress={onBack}
          haptic="selection"
          contentStyle={styles.backButton}
          style={styles.backShell}
        >
          <Ionicons name="chevron-back" size={20} color={theme.colors.primary} />
          <Text style={[theme.typography.caption, styles.backLabel, { color: theme.colors.textSecondary }]}>
            {backLabel}
          </Text>
        </MotionPressable>
      ) : null}
      <GlassSurface
        radius={theme.radius.xxl}
        tintColor={theme.isDark ? 'rgba(47,107,255,0.16)' : 'rgba(255,255,255,0.42)'}
        contentStyle={styles.content}
      >
        <View style={[styles.row, stackHeader && styles.rowStacked]}>
          <View style={styles.copy}>
            {eyebrow ? (
              <Text style={[styles.eyebrow, { color: theme.colors.cyan }]}>{eyebrow}</Text>
            ) : null}
            <Text style={[theme.typography.h1, { color: theme.colors.text }]}>{title}</Text>
            {subtitle ? (
              <Text style={[theme.typography.caption, styles.subtitle, { color: theme.colors.textSecondary }]}>
                {subtitle}
              </Text>
            ) : null}
          </View>
          {actions ? (
            <View style={[styles.actions, stackHeader && styles.actionsStacked]}>{actions}</View>
          ) : null}
        </View>
        {children}
      </GlassSurface>
    </View>
  );
}

const styles = StyleSheet.create({
  wrapper: { paddingHorizontal: 16, paddingBottom: 4 },
  backShell: { alignSelf: 'flex-start', marginBottom: 8 },
  backButton: { minHeight: 44, flexDirection: 'row', alignItems: 'center', gap: 3, paddingRight: 10 },
  backLabel: { fontWeight: '700' },
  content: { paddingHorizontal: 18, paddingVertical: 16 },
  row: { flexDirection: 'row', alignItems: 'flex-start', gap: 12 },
  rowStacked: { flexDirection: 'column' },
  copy: { flex: 1, minWidth: 0 },
  eyebrow: { fontSize: 11, lineHeight: 15, fontWeight: '800', letterSpacing: 1.1, textTransform: 'uppercase' },
  subtitle: { marginTop: 5 },
  actions: { flexDirection: 'row', gap: 8 },
  actionsStacked: { alignSelf: 'flex-end' },
});
