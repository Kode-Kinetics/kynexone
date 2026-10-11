import React from 'react';
import { StyleProp, StyleSheet, View, ViewStyle } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import { GlassSurface } from './GlassSurface';
import { MotionPressable } from './MotionPressable';
import { useTheme } from '@/theme/ThemeProvider';

export interface GlassIconButtonProps {
  icon: React.ComponentProps<typeof Ionicons>['name'];
  onPress: () => void;
  /** Required: spoken by VoiceOver/TalkBack (an icon alone has no name). */
  label: string;
  /** Unread count; shows a dot and is announced. */
  badge?: number;
  style?: StyleProp<ViewStyle>;
  /** Tint the icon with the brand accent. */
  accent?: boolean;
  accessibilityHint?: string;
  testID?: string;
}

const SIZE = 44;

/** Floating control-layer button: real glass, 44pt target, never placed on another glass. */
export function GlassIconButton({ icon, onPress, label, badge, style, accent, accessibilityHint, testID }: GlassIconButtonProps) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const hasBadge = typeof badge === 'number' && badge > 0;

  return (
    <MotionPressable
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityHint={accessibilityHint}
      accessibilityValue={hasBadge ? { text: t('kit.unread', { count: badge }) } : undefined}
      onPress={onPress}
      testID={testID}
      style={style}
      contentStyle={styles.pressable}
      hitSlop={4}
    >
      <GlassSurface interactive radius={SIZE / 2} style={styles.surface} contentStyle={styles.content}>
        <Ionicons
          accessible={false}
          name={icon}
          size={20}
          color={accent ? theme.colors.accent : theme.colors.text}
        />
      </GlassSurface>
      {hasBadge ? (
        <View
          pointerEvents="none"
          style={[styles.badge, { backgroundColor: theme.colors.status.danger.fill, borderColor: theme.colors.card }]}
        />
      ) : null}
    </MotionPressable>
  );
}

const styles = StyleSheet.create({
  pressable: { borderRadius: SIZE / 2 },
  surface: { width: SIZE, height: SIZE },
  content: { alignItems: 'center', justifyContent: 'center' },
  badge: {
    position: 'absolute',
    top: 6,
    end: 6,
    width: 10,
    height: 10,
    borderRadius: 5,
    borderWidth: 1.5,
  },
});
