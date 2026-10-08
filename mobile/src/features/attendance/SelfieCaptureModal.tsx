import React, { useCallback, useEffect, useRef, useState } from 'react';
import { ActivityIndicator, Image, Linking, Modal, StyleSheet, Text, View } from 'react-native';
import { CameraView, useCameraPermissions } from 'expo-camera';
import * as FileSystem from 'expo-file-system/legacy';
import { Ionicons } from '@expo/vector-icons';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { useTranslation } from 'react-i18next';
import { GlassSurface, LiquidButton, MotionPressable } from '@/components/ui';
import type { PunchType } from '@/types';
import { SELFIE_JPEG_QUALITY, choosePictureSize } from './selfieAttendance';

interface Props {
  visible: boolean;
  punchType: PunchType;
  /** optional: "clock without a selfie" is offered. required: the tenant asks consenting employees for one. */
  mode: 'optional' | 'required';
  /** The parent is uploading the photo handed to onUse. */
  uploading: boolean;
  /** Hands the captured file to the parent, which uploads it and then deletes it. */
  onUse: (uri: string) => void;
  /** Punch without a selfie (optional mode). */
  onSkip?: () => void;
  /** Withdraw consent and punch without a selfie. Always offered with the selfie step. */
  onWithdraw?: () => void;
  onCancel: () => void;
}

/** Deletes a camera temp file. Best effort: the OS cache is the fallback, the photo is never kept on purpose. */
export async function deleteTempPhoto(uri: string | null | undefined): Promise<void> {
  if (!uri) return;
  try {
    await FileSystem.deleteAsync(uri, { idempotent: true });
  } catch (error) {
    console.warn('[Selfie] Could not delete the temporary photo:', error);
  }
}

/**
 * Front-camera capture for one punch: capture, review, then hand the file to the parent. No face
 * matching and no device biometric step: the server stores the selfie as evidence only, and the app
 * never claims any verification.
 */
export function SelfieCaptureModal({ visible, punchType, mode, uploading, onUse, onSkip, onWithdraw, onCancel }: Props) {
  const { t } = useTranslation();
  const insets = useSafeAreaInsets();
  const cameraRef = useRef<CameraView>(null);
  const [permission, requestPermission] = useCameraPermissions();
  const [cameraReady, setCameraReady] = useState(false);
  const [capturing, setCapturing] = useState(false);
  const [photoUri, setPhotoUri] = useState<string | null>(null);
  const [pictureSize, setPictureSize] = useState<string | undefined>(undefined);
  // A photo handed to the parent is the parent's to delete; any other leftover is deleted here.
  const handedOff = useRef<string | null>(null);

  const photoRef = useRef<string | null>(null);
  useEffect(() => { photoRef.current = photoUri; }, [photoUri]);

  useEffect(() => {
    if (visible) {
      handedOff.current = null;
      if (permission && !permission.granted && permission.canAskAgain) void requestPermission();
      return;
    }
    const leftover = photoRef.current;
    if (leftover && leftover !== handedOff.current) void deleteTempPhoto(leftover);
    setCameraReady(false);
    setCapturing(false);
    setPhotoUri(null);
  }, [permission, requestPermission, visible]);

  // Unmounting with a captured photo that was never handed off (screen left mid-capture): delete it.
  useEffect(() => () => {
    const leftover = photoRef.current;
    if (leftover && leftover !== handedOff.current) void deleteTempPhoto(leftover);
  }, []);

  const take = useCallback(async () => {
    if (!cameraReady || capturing || !cameraRef.current) return;
    setCapturing(true);
    try {
      // About a 1080 px long edge (pictureSize) at JPEG quality 0.8 keeps the upload far below the 8 MB cap.
      // exif: false — nothing extra is read on the device; the server re-encodes and strips metadata anyway.
      const photo = await cameraRef.current.takePictureAsync({ quality: SELFIE_JPEG_QUALITY, exif: false, shutterSound: false });
      if (photo?.uri) setPhotoUri(photo.uri);
    } catch (error) {
      console.warn('[Selfie] Capture failed:', error);
    } finally {
      setCapturing(false);
    }
  }, [cameraReady, capturing]);

  const onCameraReady = useCallback(() => {
    setCameraReady(true);
    if (pictureSize !== undefined) return;
    cameraRef.current?.getAvailablePictureSizesAsync()
      .then((sizes) => {
        const chosen = choosePictureSize(sizes);
        if (chosen) setPictureSize(chosen);
      })
      .catch((error) => console.warn('[Selfie] Picture sizes unavailable; using the camera default:', error));
  }, [pictureSize]);

  const retake = useCallback(() => {
    void deleteTempPhoto(photoUri);
    setPhotoUri(null);
    setCameraReady(false);
  }, [photoUri]);

  const use = useCallback(() => {
    if (!photoUri || uploading) return;
    handedOff.current = photoUri;
    onUse(photoUri);
  }, [onUse, photoUri, uploading]);

  const action = punchType === 'CLOCK_OUT' || punchType === 'BREAK_OUT' ? t('selfie.capture.clockOut') : t('selfie.capture.clockIn');
  const granted = permission?.granted === true;

  const secondaryActions = (
    <View style={styles.secondaryRow}>
      {mode === 'optional' && onSkip ? (
        <TextAction label={t('selfie.capture.skip')} onPress={onSkip} disabled={uploading} />
      ) : null}
      {onWithdraw ? (
        <TextAction label={t('selfie.capture.withdrawInstead')} onPress={onWithdraw} disabled={uploading} muted />
      ) : null}
    </View>
  );

  return (
    <Modal visible={visible} animationType="slide" presentationStyle="fullScreen" onRequestClose={uploading ? () => undefined : onCancel}>
      <View style={styles.root}>
        {granted && !photoUri ? (
          <CameraView
            ref={cameraRef}
            style={StyleSheet.absoluteFill}
            facing="front"
            pictureSize={pictureSize}
            onCameraReady={onCameraReady}
          />
        ) : null}
        {photoUri ? <Image source={{ uri: photoUri }} style={StyleSheet.absoluteFill} resizeMode="cover" /> : null}

        <View style={[styles.topBar, { paddingTop: Math.max(insets.top, 14) }]}>
          <MotionPressable
            onPress={onCancel}
            disabled={uploading}
            haptic="selection"
            accessibilityRole="button"
            accessibilityLabel={t('selfie.capture.close')}
            contentStyle={styles.iconButton}
          >
            <Ionicons name="close" size={24} color="#FFFFFF" />
          </MotionPressable>
          <GlassSurface elevated={false} radius={999} tintColor="rgba(8,15,32,0.52)" contentStyle={styles.titlePill}>
            <Ionicons name="camera-outline" size={17} color="#A5F3FC" />
            <Text style={styles.titleText} numberOfLines={1}>{t('selfie.capture.title', { action })}</Text>
          </GlassSurface>
          <View style={styles.iconSpacer} />
        </View>

        <View style={styles.flex} />

        <View style={[styles.bottom, { paddingBottom: Math.max(insets.bottom, 18) }]}>
          <GlassSurface radius={26} tintColor="rgba(8,15,32,0.72)" contentStyle={styles.panel}>
            {!permission ? (
              <View style={styles.centerRow}>
                <ActivityIndicator color="#FFFFFF" />
                <Text style={styles.body}>{t('selfie.capture.preparing')}</Text>
              </View>
            ) : !granted ? (
              <>
                <Text style={styles.heading}>{t('selfie.capture.cameraNeededTitle')}</Text>
                <Text style={styles.body}>
                  {mode === 'required' ? t('selfie.capture.cameraNeededRequiredBody') : t('selfie.capture.cameraNeededBody')}
                </Text>
                <LiquidButton
                  label={permission.canAskAgain ? t('selfie.capture.allowCamera') : t('selfie.capture.openSettings')}
                  icon="camera-outline"
                  onPress={() => (permission.canAskAgain ? void requestPermission() : void Linking.openSettings())}
                />
                {secondaryActions}
              </>
            ) : uploading ? (
              <View style={styles.centerRow} accessibilityLiveRegion="polite">
                <ActivityIndicator color="#FFFFFF" />
                <Text style={styles.body}>{t('selfie.capture.uploading')}</Text>
              </View>
            ) : photoUri ? (
              <>
                <Text style={styles.body}>{t('selfie.capture.privacy')}</Text>
                <View style={styles.buttonRow}>
                  <TextAction label={t('selfie.capture.retake')} onPress={retake} />
                  <LiquidButton label={t('selfie.capture.use')} icon="checkmark" onPress={use} variant="success" style={styles.flex} />
                </View>
                {secondaryActions}
              </>
            ) : (
              <>
                <Text style={styles.body}>{t('selfie.capture.hint')}</Text>
                <LiquidButton
                  label={t('selfie.capture.take')}
                  icon="camera"
                  onPress={() => void take()}
                  loading={capturing || !cameraReady}
                  disabled={capturing || !cameraReady}
                />
                {secondaryActions}
              </>
            )}
          </GlassSurface>
        </View>
      </View>
    </Modal>
  );
}

function TextAction({ label, onPress, disabled, muted }: { label: string; onPress: () => void; disabled?: boolean; muted?: boolean }) {
  return (
    <MotionPressable
      onPress={onPress}
      disabled={disabled}
      haptic="selection"
      accessibilityRole="button"
      accessibilityLabel={label}
      contentStyle={styles.textAction}
    >
      <Text style={[styles.textActionLabel, muted && styles.textActionMuted]}>{label}</Text>
    </MotionPressable>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: '#020617' },
  flex: { flex: 1 },
  topBar: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', paddingHorizontal: 16, gap: 10 },
  iconButton: {
    width: 44,
    height: 44,
    borderRadius: 22,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: 'rgba(8,15,32,0.52)',
  },
  iconSpacer: { width: 44 },
  titlePill: { flexDirection: 'row', alignItems: 'center', gap: 7, paddingHorizontal: 14, paddingVertical: 9, flexShrink: 1 },
  titleText: { color: '#FFFFFF', fontSize: 14, fontWeight: '700', flexShrink: 1 },
  bottom: { paddingHorizontal: 16 },
  panel: { padding: 18, gap: 14 },
  heading: { color: '#FFFFFF', fontSize: 17, fontWeight: '800' },
  body: { color: 'rgba(255,255,255,0.86)', fontSize: 14, lineHeight: 20 },
  centerRow: { flexDirection: 'row', alignItems: 'center', gap: 10, paddingVertical: 6 },
  buttonRow: { flexDirection: 'row', alignItems: 'center', gap: 12 },
  secondaryRow: { flexDirection: 'row', flexWrap: 'wrap', justifyContent: 'center', gap: 6 },
  textAction: { minHeight: 44, paddingHorizontal: 12, justifyContent: 'center', borderRadius: 14 },
  textActionLabel: { color: '#A5F3FC', fontSize: 14, fontWeight: '700', textAlign: 'center' },
  textActionMuted: { color: 'rgba(255,255,255,0.72)', fontWeight: '600' },
});
