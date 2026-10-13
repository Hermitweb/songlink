package songlink.remote.di

import android.content.Context
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import songlink.remote.data.audio.PhoneAudioCapturer
import songlink.remote.data.transport.AudioTransportManager
import songlink.remote.data.transport.WsTransport
import javax.inject.Singleton

/**
 * 传输层依赖装配。
 *
 * [WsTransport] 自己持有 OkHttpClient 与重连协程作用域，构造参数都有生产默认值，
 * 因此这里只提供「生产单例」这一种绑定；单元测试直接 new，不需要 Hilt。
 *
 * [PhoneAudioCapturer] 与 [AudioTransportManager] 的工厂方法在这里绑定给 Hilt 容器，
 * 让 [songlink.remote.data.audio.ProjectionService] 等 @AndroidEntryPoint 字段注入拿到。
 */
@Module
@InstallIn(SingletonComponent::class)
object TransportModule {

    @Provides
    @Singleton
    fun provideWsTransport(): WsTransport = WsTransport()

    @Provides
    @Singleton
    fun providePhoneAudioCapturer(@ApplicationContext ctx: Context): PhoneAudioCapturer =
        PhoneAudioCapturer(ctx)

    @Provides
    @Singleton
    fun provideAudioTransportManager(@ApplicationContext ctx: Context): AudioTransportManager =
        AudioTransportManager(ctx)
}