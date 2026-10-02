import React, { useEffect, useMemo, useState } from 'react';
import {
  Image,
  ImageSourcePropType,
  StyleProp,
  StyleSheet,
  Text,
  View,
  ViewStyle,
} from 'react-native';
import { profileApi } from '@/api/services';
import { useTheme } from '@/theme/ThemeProvider';

interface Props {
  name: string;
  photoUrl?: string | null;
  size?: number;
  style?: StyleProp<ViewStyle>;
  ring?: boolean;
}

export function EmployeeAvatar({
  name,
  photoUrl,
  size = 44,
  style,
  ring = false,
}: Props) {  const { theme } = useTheme();
  const [source, setSource] = useState<ImageSourcePropType | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let active = true;
    setFailed(false);
    setSource(null);

    if (!photoUrl) {
      return () => {
        active = false;
      };
    }

    void profileApi.photoSource(photoUrl).then((resolved) => {
      if (active && resolved) setSource(resolved);
    });

    return () => {
      active = false;
    };
  }, [photoUrl]);

  const initials = useMemo(
    () =>
      name
        .trim()
        .split(/\s+/)
        .filter(Boolean)        .slice(0, 2)
        .map((part) => part[0]?.toUpperCase())
        .join('') || '?',
    [name],
  );

  return (
    <View
      accessibilityRole="image"
      accessibilityLabel={name + ' profile photo'}
      style={[
        styles.shell,
        {
          width: size,
          height: size,
          borderRadius: size * 0.34,
          borderColor: ring ? theme.colors.glassBorder : 'transparent',
          backgroundColor: theme.colors.primary + '1A',
        },
        style,
      ]}
    >
      {source && !failed ? (
        <Image
          source={source}
          resizeMode="cover"          style={StyleSheet.absoluteFill}
          onError={() => setFailed(true)}
        />
      ) : (
        <Text
          allowFontScaling={false}
          style={[
            styles.initials,
            {
              color: theme.colors.primary,
              fontSize: Math.max(12, size * 0.31),
            },
          ]}
        >
          {initials}
        </Text>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  shell: {
    overflow: 'hidden',
    borderWidth: 1,
    alignItems: 'center',
    justifyContent: 'center',
  },
  initials: { fontWeight: '800', letterSpacing: 0.2 },
});
