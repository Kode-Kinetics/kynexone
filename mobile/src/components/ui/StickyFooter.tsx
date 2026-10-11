import React, { useContext } from 'react';
import { StyleProp, StyleSheet, View, ViewStyle } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { BottomTabBarHeightContext } from '@react-navigation/bottom-tabs';
import { useTheme } from '@/theme/ThemeProvider';

export interface StickyFooterProps {
  children: React.ReactNode;
  style?: StyleProp<ViewStyle>;
  testID?: string;
}

/**
 * Bottom action bar for a screen's primary action. Place it AFTER the ScrollView (in-flow), so the
 * list ends above it and nothing hides behind it. Inside the tab shell the scene already stops
 * above the floating tab bar; outside it, the footer pads for the home indicator itself.
 */
export function StickyFooter({ children, style, testID }: StickyFooterProps) {
  const { theme } = useTheme();
  const insets = useSafeAreaInsets();
  const inTabs = useContext(BottomTabBarHeightContext) !== undefined;
  const bottom = inTabs ? 10 : Math.max(insets.bottom, 12);

  return (
    <View
      testID={testID}
      style={[
        styles.bar,
        {
          paddingBottom: bottom,
          backgroundColor: theme.colors.chromeSolid,
          borderTopColor: theme.colors.hairline,
        },
        style,
      ]}
    >
      {children}
    </View>
  );
}

const styles = StyleSheet.create({
  bar: {
    paddingHorizontal: 16,
    paddingTop: 10,
    gap: 8,
    borderTopWidth: StyleSheet.hairlineWidth,
  },
});
