import React, { useCallback, useRef, useState } from 'react';
import { KeyboardAvoidingView, Platform, ScrollView, StyleSheet, Text, View } from 'react-native';
import { CameraView, useCameraPermissions } from 'expo-camera';
import { useNavigation } from '@react-navigation/native';
import { useTranslation } from 'react-i18next';
import { GlassSurface, GlassTextField, LiquidBackdrop, LiquidButton, ScreenHero } from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import {
  allowInsecureServer,
  checkServer,
  getServer,
  saveServer,
  ServerCheckError,
} from '@/config/server';
import { normalizeServerAddress, parseServerQr, type ServerAddressResult } from '@/config/serverAddress';

/**
 * Which KynexOne installation this app talks to. Shown before sign-in when no server is chosen, and from the
 * sign-in screen's "Change server". The address is typed, or scanned from the QR code Tenant Admin shows
 * (kynexone://server?url=…); either way it must be HTTPS and must answer GET /api/public/installation as a set-up
 * KynexOne server before it is saved.
 */
export default function ServerSetupScreen() {
  const navigation = useNavigation<any>();
  const { t } = useTranslation();
  const { theme } = useTheme();
  const [address, setAddress] = useState(() => getServer()?.origin ?? '');
  const [message, setMessage] = useState('');
  const [checking, setChecking] = useState(false);
  const [scanning, setScanning] = useState(false);
  const [permission, requestPermission] = useCameraPermissions();
  const scanned = useRef(false);

  const connect = useCallback(async (result: ServerAddressResult) => {
    if (!result.ok) {
      setMessage(t(`server.errors.${result.error}`));
      return;
    }
    setMessage('');
    setChecking(true);
    try {
      const info = await checkServer(result.origin);
      await saveServer(result.origin, info);
      setAddress(result.origin);
      navigation.reset({ index: 0, routes: [{ name: 'Login' }] });
    } catch (error) {
      setMessage(t(`server.errors.${error instanceof ServerCheckError ? error.reason : 'unreachable'}`));
    } finally {
      setChecking(false);
    }
  }, [navigation, t]);

  const startScan = useCallback(async () => {
    setMessage('');
    const granted = permission?.granted || (await requestPermission()).granted;
    if (!granted) {
      setMessage(t('server.cameraDenied'));
      return;
    }
    scanned.current = false;
    setScanning(true);
  }, [permission?.granted, requestPermission, t]);

  const onScanned = useCallback(({ data }: { data: string }) => {
    // The camera reports the same code many times a second: act on the first.
    if (scanned.current) return;
    scanned.current = true;
    setScanning(false);
    void connect(parseServerQr(data, { allowInsecure: allowInsecureServer }));
  }, [connect]);

  return (
    <KeyboardAvoidingView
      style={[styles.root, { backgroundColor: theme.colors.canvas }]}
      behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
    >
      <LiquidBackdrop />
      <ScrollView
        contentContainerStyle={styles.scroll}
        keyboardShouldPersistTaps="handled"
        keyboardDismissMode={Platform.OS === 'ios' ? 'interactive' : 'on-drag'}
        automaticallyAdjustKeyboardInsets={Platform.OS === 'ios'}
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow={t('server.eyebrow')}
          title={t('server.title')}
          subtitle={t('server.subtitle')}
          hideBack={!navigation.canGoBack()}
          backLabel={t('mfa.backToSignIn')}
        />

        <View style={styles.content}>
          {scanning ? (
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.form}>
              <View style={styles.camera}>
                <CameraView
                  style={StyleSheet.absoluteFill}
                  facing="back"
                  barcodeScannerSettings={{ barcodeTypes: ['qr'] }}
                  onBarcodeScanned={onScanned}
                />
              </View>
              <LiquidButton label={t('server.stopScanning')} icon="close-outline" onPress={() => setScanning(false)} />
            </GlassSurface>
          ) : (
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.form}>
              <GlassTextField
                label={t('server.addressLabel')}
                icon="server-outline"
                value={address}
                onChangeText={setAddress}
                placeholder={t('server.addressPlaceholder')}
                autoCapitalize="none"
                autoCorrect={false}
                keyboardType="url"
                textContentType="URL"
                returnKeyType="go"
                onSubmitEditing={() => void connect(normalizeServerAddress(address, { allowInsecure: allowInsecureServer }))}
                style={{ textAlign: 'left' }}
              />
              {message ? (
                <Text accessibilityRole="alert" style={[theme.typography.caption, styles.message, { color: theme.colors.text }]}>
                  {message}
                </Text>
              ) : null}
              <LiquidButton
                label={checking ? t('server.checking') : t('server.connect')}
                icon="link-outline"
                onPress={() => void connect(normalizeServerAddress(address, { allowInsecure: allowInsecureServer }))}
                loading={checking}
                disabled={checking}
              />
              <View style={styles.gap} />
              <LiquidButton
                label={t('server.scan')}
                icon="qr-code-outline"
                variant="secondary"
                onPress={() => void startScan()}
                disabled={checking}
              />
            </GlassSurface>
          )}
        </View>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  scroll: { flexGrow: 1, paddingBottom: 34 },
  content: { paddingHorizontal: 16, paddingTop: 12, gap: 12 },
  form: { padding: 18 },
  message: { marginTop: 4, marginBottom: 8, lineHeight: 18 },
  camera: { height: 280, borderRadius: 16, overflow: 'hidden', marginBottom: 12 },
  gap: { height: 10 },
});
