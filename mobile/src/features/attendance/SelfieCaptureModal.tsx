import React, { useCallback, useEffect, useRef, useState } from 'react';
import { ActivityIndicator, AppState, Image, Linking, Modal, StyleSheet, Text, View } from 'react-native';
import { CameraView, useCameraPermissions } from 'expo-camera';
import { Ionicons } from '@expo/vector-icons';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { useTranslation } from 'react-i18next';
import { GlassSurface, LiquidButton, MotionPressable } from '@/components/ui';
import type { PunchType } from '@/types';
import { SELFIE_IMAGE_TYPE, SELFIE_JPEG_QUALITY, choosePictureSize } from './selfieAttendance';
import { CaptureGuard, previewActionFor } from './selfiePhotoPolicy';
import { adoptCapturedPhoto, deleteTempPhoto } from './selfieFiles';

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
  onCancel: () => void;
}

/**
 * Front-camera capture for one punch: capture, review, then hand the file to the parent. No face
 * matching and no device biometric step: the server stores the selfie as evidence only, and the app
 * never claims any verification.
 *
 * Withdrawing consent is deliberately NOT offered here: it is an ordinary choice on the consent screen
 * (Settings, or the attendance card's link), never presented as the way past a required selfie.
 *
 * The photo never outlives its use: it is deleted on retake, on close, when the app goes to the
 * background, and if it arrives after the capture stopped being wanted (CaptureGuard). While the app is
 * inactive (the iOS app switcher snapshots the screen), the preview is covered (previewActionFor).
 */
export function SelfieCaptureModal({ visible, punchType, mode, uploading, onUse, onSkip, onCancel }: Props) {
  const { t } = useTranslation();
  const insets = useSafeAreaInsets();
  const cameraRef = useRef<CameraView>(null);
  const [permission, requestPermission, getPermission] = useCameraPermissions();
  const [cameraReady, setCameraReady] = useState(false);
  const [capturing, setCapturing] = useState(false);
  const [photoUri, setPhotoUri] = useState<string | null>(null);
  const [pictureSize, setPictureSize] = useState<string | undefined>(undefined);
  const [cameraProblem, setCameraProblem] = useState(false);
  const [cameraKey, setCameraKey] = useState(0);
  // Review 3, 12-note: while the app is not active the preview (camera or photo) is covered by an opaque view.
  const [obscured, setObscured] = useState(false);
  // A photo handed to the parent is the parent's to delete; any other leftover is deleted here.
  const handedOff = useRef<string | null>(null);
  const guard = useRef(new CaptureGuard());

  const photoRef = useRef<string | null>(null);
  useEffect(() => { photoRef.current = photoUri; }, [photoUri]);

  /** Deletes the photo on screen unless the parent owns it, and forgets it. */
  const discardPhoto = useCallback(() => {
    const leftover = photoRef.current;
    if (leftover && leftover !== handedOff.current) void deleteTempPhoto(leftover);
    photoRef.current = null;
    setPhotoUri(null);
  }, []);

  useEffect(() => {
    if (visible) {
      guard.current.open();
      handedOff.current = null;
      setObscured(previewActionFor(AppState.currentState) !== 'show');
      if (permission && !permission.granted && permission.canAskAgain) void requestPermission();
      return;
    }
    guard.current.close();
    discardPhoto();
    setCameraReady(false);
    setCapturing(false);
    setCameraProblem(false);
  }, [discardPhoto, permission, requestPermission, visible]);

  // Unmounting (screen left mid-capture): a pending capture is unwanted and a shown photo is deleted.
  useEffect(() => () => {
    guard.current.close();
    const leftover = photoRef.current;
    if (leftover && leftover !== handedOff.current) void deleteTempPhoto(leftover);
  }, []);

  // Background: delete the photo on screen and drop any capture in flight. Back to the foreground:
  // re-read the camera permission (the employee may have allowed it in Settings) so they can retry.
  const visibleRef = useRef(visible);
  useEffect(() => { visibleRef.current = visible; }, [visible]);
  useEffect(() => {
    const subscription = AppState.addEventListener('change', (next) => {
      if (!visibleRef.current) return;
      const action = previewActionFor(next);
      // inactive (the iOS app switcher snapshots the screen now) covers the preview; background also deletes it.
      setObscured(action !== 'show');
      if (action === 'delete') {
        guard.current.cancel();
        discardPhoto();
        setCapturing(false);
        setCameraReady(false);
        setCameraKey((key) => key + 1);
      } else if (action === 'show') {
        void getPermission().catch(() => undefined);
      }
    });
    return () => subscription.remove();
  }, [discardPhoto, getPermission]);

  const take = useCallback(async () => {
    if (!cameraReady || capturing || !cameraRef.current) return;
    const ticket = guard.current.begin();
    setCapturing(true);
    setCameraProblem(false);
    try {
      // About a 1080 px long edge (pictureSize) at JPEG quality 0.8 keeps the upload far below the 8 MB cap.
      // JPEG explicitly: the server accepts JPEG only. exif: false: nothing extra is read on the device.
      const photo = await cameraRef.current.takePictureAsync({
        quality: SELFIE_JPEG_QUALITY,
        imageType: SELFIE_IMAGE_TYPE,
        exif: false,
        shutterSound: false,
      });
      if (!photo?.uri) return;
      if (!guard.current.isCurrent(ticket)) {
        // The modal closed or the app went to the background while the camera was saving.
        void deleteTempPhoto(photo.uri);
        return;
      }
      const kept = await adoptCapturedPhoto(photo.uri);
      if (!guard.current.isCurrent(ticket)) {
        void deleteTempPhoto(kept);
        return;
      }
      setPhotoUri(kept);
    } catch (error) {
      console.warn('[Selfie] Capture failed:', error);
      if (guard.current.isCurrent(ticket)) setCameraProblem(true);
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

  const onMountError = useCallback((event: { message?: string }) => {
    console.warn('[Selfie] Camera could not start:', event?.message);
    setCameraReady(false);
    setCameraProblem(true);
  }, []);

  /** Restart the camera after a problem (remounts the CameraView). */
  const retryCamera = useCallback(() => {
    setCameraProblem(false);
    setCameraReady(false);
    setCameraKey((key) => key + 1);
  }, []);

  const retake = useCallback(() => {
    guard.current.cancel();
    discardPhoto();
    setCameraReady(false);
  }, [discardPhoto]);

  const use = useCallback(() => {
    if (!photoUri || uploading) return;
    handedOff.current = photoUri;
    onUse(photoUri);
  }, [onUse, photoUri, uploading]);

  const action = punchType === 'CLOCK_OUT' || punchType === 'BREAK_OUT' ? t('selfie.capture.clockOut') : t('selfie.capture.clockIn');
  const granted = permission?.granted === true;

  const secondaryActions = mode === 'optional' && onSkip ? (
    <View style={styles.secondaryRow}>
      <TextAction label={t('selfie.capture.skip')} onPress={onSkip} disabled={uploading} />
    </View>
  ) : null;

  return (
    <Modal visible={visible} animationType="slide" presentationStyle="fullScreen" onRequestClose={uploading ? () => undefined : onCancel}>
      <View style={styles.root}>
        {granted && !photoUri && !cameraProblem && !uploading ? (
          <CameraView
            key={cameraKey}
            ref={cameraRef}
            style={StyleSheet.absoluteFill}
            facing="front"
            pictureSize={pictureSize}
            onCameraReady={onCameraReady}
            onMountError={onMountError}
          />
        ) : null}
        {photoUri ? <Image source={{ uri: photoUri }} style={StyleSheet.absoluteFill} resizeMode="cover" /> : null}
        {obscured ? <View style={styles.cover} accessibilityElementsHidden importantForAccessibility="no-hide-descendants" /> : null}

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
            ) : cameraProblem && !photoUri ? (
              <>
                <Text style={styles.body}>
                  {mode === 'required' ? t('selfie.capture.cameraProblemRequired') : t('selfie.capture.cameraProblem')}
                </Text>
                <LiquidButton label={t('selfie.capture.tryAgain')} icon="refresh" onPress={retryCamera} />
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

function TextAction({ label, onPress, disabled }: { label: string; onPress: () => void; disabled?: boolean }) {
  return (
    <MotionPressable
      onPress={onPress}
      disabled={disabled}
      haptic="selection"
      accessibilityRole="button"
      accessibilityLabel={label}
      contentStyle={styles.textAction}
    >
      <Text style={styles.textActionLabel}>{label}</Text>
    </MotionPressable>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: '#020617' },
  // Opaque: nothing of the camera or the photo shows through in the app-switcher snapshot.
  cover: { position: 'absolute', top: 0, right: 0, bottom: 0, left: 0, backgroundColor: '#020617' },
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
});
