import React from 'react';
import { StyleProp, StyleSheet, Text, View, ViewStyle } from 'react-native';
import { useTranslation } from 'react-i18next';
import { Surface } from './Surface';
import { MotionPressable } from './MotionPressable';
import { DirectionalIcon } from './DirectionalIcon';
import { useTheme } from '@/theme/ThemeProvider';
import type { StatusTone } from '@/theme/tokens';

export interface KpiTileProps {
  /** What is counted, in plain words ("Leave balance"). */
  label: string;
  /** Pre-formatted value. null/undefined shows "—" (unknown is never shown as 0). */
  value: string | number | null | undefined;
  /** The period or as-of the value covers ("This month", "As of 10 Oct"). Required: no unexplained numbers. */
  period: string;
  /** Opens the records behind the number. */
  onPress?: () => void;
  /** Colours the value only when it carries a status meaning. */
  tone?: StatusTone;
  /** Optional one-line definition / evidence under the period. */
  caption?: string;
  /** Overrides the composed spoken label. */
  accessibilityLabel?: string;
  accessibilityHint?: string;
  style?: StyleProp<ViewStyle>;
  testID?: string;
}

export function KpiTile({
  label,
  value,
  period,
  onPress,
  tone,
  caption,
  accessibilityLabel,
  accessibilityHint,
  style,
  testID,
}: KpiTileProps) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const shown = value === null || value === undefined || value === '' ? '—' : String(value);
  const spoken = accessibilityLabel ?? [label, shown, period, caption].filter(Boolean).join(', ');
  const valueColor = tone && tone !== 'neutral' ? theme.colors.status[tone].fg : theme.colors.text;

  const body = (
    <Surface style={styles.fill} contentStyle={styles.content}>
      <View style={styles.top}>
        <Text
          numberOfLines={2}
          maxFontSizeMultiplier={1.5}
          style={[theme.typography.footnote, styles.flex, { color: theme.colors.textSecondary }]}
        >
          {label}
        </Text>
        {onPress ? <DirectionalIcon name="chevron" size={16} color={theme.colors.textMuted} /> : null}
      </View>
      <Text
        numberOfLines={1}
        adjustsFontSizeToFit
        minimumFontScale={0.7}
        style={[theme.typography.title2, { color: valueColor, fontVariant: ['tabular-nums'] }]}
      >
        {shown}
      </Text>
      <Text numberOfLines={1} maxFontSizeMultiplier={1.5} style={[theme.typography.caption, { color: theme.colors.textMuted }]}>
        {period}
      </Text>
      {caption ? (
        <Text numberOfLines={2} maxFontSizeMultiplier={1.5} style={[theme.typography.caption, { color: theme.colors.textMuted }]}>
          {caption}
        </Text>
      ) : null}
    </Surface>
  );

  if (!onPress) {
    return (
      <View testID={testID} accessible accessibilityLabel={spoken} style={style}>
        {body}
      </View>
    );
  }

  return (
    <MotionPressable
      testID={testID}
      accessibilityRole="button"
      accessibilityLabel={spoken}
      accessibilityHint={accessibilityHint ?? t('kit.opensRecords')}
      onPress={onPress}
      style={style}
      contentStyle={styles.fill}
    >
      {body}
    </MotionPressable>
  );
}

const styles = StyleSheet.create({
  fill: { flexGrow: 1 },
  content: { padding: 14, gap: 4, minHeight: 104 },
  top: { flexDirection: 'row', alignItems: 'flex-start', gap: 6 },
  flex: { flex: 1 },
});
