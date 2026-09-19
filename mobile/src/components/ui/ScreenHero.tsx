import React from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { GlassSurface } from './GlassSurface';
import { useTheme } from '@/theme/ThemeProvider';

interface Props {
  eyebrow?: string;
  title: string;
  subtitle?: string;
  actions?: React.ReactNode;
  children?: React.ReactNode;
}

export function ScreenHero({ eyebrow, title, subtitle, actions, children }: Props) {
  const { theme } = useTheme();
  const insets = useSafeAreaInsets();

  return (
    <View style={[styles.wrapper, { paddingTop: insets.top + 10 }]}>
      <GlassSurface
        radius={theme.radius.xxl}
        tintColor={theme.isDark ? 'rgba(47,107,255,0.16)' : 'rgba(255,255,255,0.42)'}
        contentStyle={styles.content}
      >
        <View style={styles.row}>
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
          {actions ? <View style={styles.actions}>{actions}</View> : null}
        </View>
        {children}
      </GlassSurface>
    </View>
  );
}

const styles = StyleSheet.create({
  wrapper: { paddingHorizontal: 16, paddingBottom: 4 },
  content: { paddingHorizontal: 18, paddingVertical: 16 },
  row: { flexDirection: 'row', alignItems: 'flex-start', gap: 12 },
  copy: { flex: 1 },
  eyebrow: { fontSize: 10, lineHeight: 14, fontWeight: '800', letterSpacing: 1.2, textTransform: 'uppercase' },
  subtitle: { marginTop: 5 },
  actions: { flexDirection: 'row', gap: 8 },
});
