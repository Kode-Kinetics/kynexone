import React from 'react';
import { StyleSheet, Text, TextInput, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import { MotionPressable, Surface } from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import type { StatusTone } from '@/theme/tokens';

const NOTICE_ICONS: Record<StatusTone, React.ComponentProps<typeof Ionicons>['name']> = {
  success: 'checkmark-circle',
  warning: 'alert-circle',
  danger: 'alert-circle',
  info: 'information-circle',
  neutral: 'information-circle-outline',
};

/** An inline message on a tinted solid card. Colour is never the only signal: icon + words. */
export function Notice({
  tone,
  title,
  body,
  onDismiss,
  testID,
}: {
  tone: StatusTone;
  title?: string;
  body: string;
  onDismiss?: () => void;
  testID?: string;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const c = theme.colors.status[tone];
  return (
    <Surface level={1} tintColor={c.tint} testID={testID} contentStyle={styles.notice}>
      <Ionicons accessible={false} name={NOTICE_ICONS[tone]} size={20} color={c.fg} style={styles.noticeIcon} />
      <View style={styles.noticeCopy} accessible accessibilityLiveRegion="polite">
        {title ? <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{title}</Text> : null}
        <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>{body}</Text>
      </View>
      {onDismiss ? (
        <MotionPressable
          accessibilityRole="button"
          accessibilityLabel={t('kit.close')}
          onPress={onDismiss}
          hitSlop={8}
          contentStyle={styles.dismiss}
        >
          <Ionicons accessible={false} name="close" size={18} color={theme.colors.textMuted} />
        </MotionPressable>
      ) : null}
    </Surface>
  );
}

/** Persistent field label; "Optional" marks the exception rather than "Required" on everything. */
export function FieldLabel({ label, optional }: { label: string; optional?: boolean }) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  return (
    <Text style={[theme.typography.footnote, styles.label, { color: theme.colors.textSecondary }]}>
      {label}
      {optional ? <Text style={{ color: theme.colors.textMuted, fontWeight: '400' }}>{` · ${t('leavePay.common.optional')}`}</Text> : null}
    </Text>
  );
}

export function FieldError({ text }: { text?: string | null }) {
  const { theme } = useTheme();
  if (!text) return null;
  return <Text style={[theme.typography.footnote, { color: theme.colors.status.danger.fg }]}>{text}</Text>;
}

/** Multi-line text input with a persistent label and an inline error. */
export function TextArea({
  label,
  optional,
  value,
  onChangeText,
  placeholder,
  error,
  testID,
}: {
  label: string;
  optional?: boolean;
  value: string;
  onChangeText: (v: string) => void;
  placeholder: string;
  error?: string | null;
  testID?: string;
}) {
  const { theme } = useTheme();
  return (
    <View style={styles.group}>
      <FieldLabel label={label} optional={optional} />
      <TextInput
        testID={testID}
        accessibilityLabel={label}
        accessibilityHint={error ?? undefined}
        value={value}
        onChangeText={onChangeText}
        placeholder={placeholder}
        placeholderTextColor={theme.colors.textMuted}
        selectionColor={theme.colors.accent}
        multiline
        textAlignVertical="top"
        style={[
          theme.typography.callout,
          styles.textarea,
          {
            color: theme.colors.text,
            backgroundColor: theme.colors.surfaceInset,
            borderColor: error ? theme.colors.status.danger.fg : theme.colors.hairline,
          },
        ]}
      />
      <FieldError text={error} />
    </View>
  );
}

/** A label / value row for detail lists. Values never truncate silently. */
export function DetailRow({
  label,
  value,
  sublabel,
  strong,
  selectable,
}: {
  label: string;
  value: string;
  sublabel?: string;
  strong?: boolean;
  selectable?: boolean;
}) {
  const { theme } = useTheme();
  return (
    <View style={[styles.row, { borderBottomColor: theme.colors.hairline }]} accessible>
      <View style={styles.rowCopy}>
        <Text style={[strong ? theme.typography.headline : theme.typography.subheadline, { color: theme.colors.text }]}>
          {label}
        </Text>
        {sublabel ? (
          <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{sublabel}</Text>
        ) : null}
      </View>
      <Text
        selectable={selectable}
        style={[
          strong ? theme.typography.headline : theme.typography.subheadline,
          styles.rowValue,
          { color: theme.colors.text },
        ]}
      >
        {value}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  notice: { flexDirection: 'row', alignItems: 'flex-start', gap: 10, padding: 14 },
  noticeIcon: { marginTop: 1 },
  noticeCopy: { flex: 1, gap: 2 },
  dismiss: { width: 32, height: 32, alignItems: 'center', justifyContent: 'center' },
  group: { gap: 6 },
  label: { fontWeight: '600' },
  textarea: {
    minHeight: 96,
    borderRadius: 14,
    borderWidth: StyleSheet.hairlineWidth,
    paddingHorizontal: 14,
    paddingTop: 12,
    paddingBottom: 12,
    textAlign: 'auto',
  },
  row: {
    minHeight: 48,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    paddingVertical: 10,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  rowCopy: { flex: 1, minWidth: 0, gap: 2 },
  rowValue: { fontVariant: ['tabular-nums'], textAlign: 'auto', flexShrink: 0, maxWidth: '55%' },
});
