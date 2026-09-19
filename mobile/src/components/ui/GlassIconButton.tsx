import React from 'react';
import { StyleProp, StyleSheet, View, ViewStyle } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { GlassSurface } from './GlassSurface';
import { MotionPressable } from './MotionPressable';
import { useTheme } from '@/theme/ThemeProvider';

interface Props {
  icon: React.ComponentProps<typeof Ionicons>['name'];
  onPress: () => void;
  label: string;
  badge?: number;
  style?: StyleProp<ViewStyle>;
  accent?: boolean;
}

export function GlassIconButton({ icon, onPress, label, badge, style, accent }: Props) {
  const { theme } = useTheme();

  return (
    <MotionPressable
      accessibilityRole="button"
      accessibilityLabel={label}
      onPress={onPress}
      style={style}
      contentStyle={styles.pressable}
      haptic="selection"
    >
      <GlassSurface
        elevated={false}
        radius={15}
        interactive
        contentStyle={styles.content}
        style={styles.surface}
      >
        <Ionicons
          name={icon}
          size={21}
          color={accent ? theme.colors.cyan : theme.colors.text}
        />
        {!!badge && badge > 0 ? (
          <View style={[styles.badge, { backgroundColor: theme.colors.danger }]}>
            <Ionicons name="ellipse" size={5} color="#FFFFFF" />
          </View>
        ) : null}
      </GlassSurface>
    </MotionPressable>
  );
}

const styles = StyleSheet.create({
  pressable: { borderRadius: 15 },
  surface: { width: 44, height: 44 },
  content: { alignItems: 'center', justifyContent: 'center' },
  badge: {
    position: 'absolute',
    top: 8,
    right: 8,
    width: 10,
    height: 10,
    borderRadius: 5,
    alignItems: 'center',
    justifyContent: 'center',
  },
});
