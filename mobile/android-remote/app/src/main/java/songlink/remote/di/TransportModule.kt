package songlink.remote.di

import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.components.SingletonComponent
import songlink.remote.data.transport.WsTransport
import javax.inject.Singleton

/**
 * 传输层依赖装配。
 *
 * [WsTransport] 自己持有 OkHttpClient 与重连协程作用域，构造参数都有生产默认值，
 * 因此这里只提供「生产单例」这一种绑定；单元测试直接 new，不需要 Hilt。
 */
@Module
@InstallIn(SingletonComponent::class)
object TransportModule {

    @Provides
    @Singleton
    fun provideWsTransport(): WsTransport = WsTransport()
}