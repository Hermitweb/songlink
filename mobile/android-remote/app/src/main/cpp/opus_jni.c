/* SongLink Remote — Opus 编码器 JNI 薄壳。
 * 编出 libsonglink_opus.so；Kotlin 侧 OpusJni 加载失败自动回退裸 PCM 直通。
 * 编码 API 走 Xiph opus.h（third_party/opus，见 CMakeLists.txt 的 add_subdirectory）。 */
#include <jni.h>
#include <stdint.h>
#include <opus.h>

JNIEXPORT jlong JNICALL
Java_songlink_remote_data_audio_OpusJni_nativeCreateEncoder
        (JNIEnv *, jclass, jint sampleRate, jint channels, jint bitrateBps, jint frameMs) {
    if (channels != 1 && channels != 2) return 0;
    if (frameMs != 10 && frameMs != 20 && frameMs != 40 && frameMs != 60) frameMs = 20;
    int err = 0;
    OpusEncoder *enc = opus_encoder_create(sampleRate, channels, OPUS_APPLICATION_AUDIO, &err);
    if (enc == NULL) return 0;
    opus_encoder_ctl(enc, OPUS_SET_BITRATE(bitrateBps));
    opus_encoder_ctl(enc, OPUS_SET_VBR(1));
    opus_encoder_ctl(enc, OPUS_SET_DTX(0));
    opus_encoder_ctl(enc, OPUS_SET_FORCE_CHANNELS(channels));
    opus_encoder_ctl(enc, OPUS_SET_SIGNAL(OPUS_SIGNAL_VOICE));
    return (jlong)(intptr_t)enc;
}

JNIEXPORT void JNICALL
Java_songlink_remote_data_audio_OpusJni_nativeDestroyEncoder(JNIEnv *, jclass, jlong handle) {
    if (handle != 0) opus_encoder_destroy((OpusEncoder *)(intptr_t)handle);
}

/* 返回成功包字节数（>0）或负值（OPUS_BAD_ARG 等错误码）。 */
JNIEXPORT jint JNICALL
Java_songlink_remote_data_audio_OpusJni_nativeEncode
        (JNIEnv *env, jclass, jlong handle, jshortArray pcm, jint frameSize,
         jbyteArray out, jint outCap) {
    if (handle == 0 || pcm == NULL || out == NULL) return OPUS_BAD_ARG;
    OpusEncoder *enc = (OpusEncoder *)(intptr_t)handle;
    jshort *pcmBuf = (*env)->GetShortArrayElements(env, pcm, 0);
    jbyte *outBuf = (*env)->GetByteArrayElements(env, out, 0);
    if (pcmBuf == NULL || outBuf == NULL) {
        if (pcmBuf) (*env)->ReleaseShortArrayElements(env, pcm, pcmBuf, JNI_ABORT);
        if (outBuf) (*env)->ReleaseByteArrayElements(env, out, outBuf, JNI_ABORT);
        return OPUS_INTERNAL_ERROR;
    }
    int n = opus_encode(enc, pcmBuf, frameSize, outBuf, outCap);
    (*env)->ReleaseShortArrayElements(env, pcm, pcmBuf, JNI_ABORT);
    (*env)->ReleaseByteArrayElements(env, out, outBuf, 0);
    return n;
}
