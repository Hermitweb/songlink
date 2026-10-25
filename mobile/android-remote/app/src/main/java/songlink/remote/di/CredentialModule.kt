package songlink.remote.di

import dagger.Binds
import dagger.Module
import dagger.hilt.InstallIn
import dagger.hilt.components.SingletonComponent
import songlink.remote.data.repo.CredentialStore
import songlink.remote.data.repo.PrefsCredentialStore
import javax.inject.Singleton

/**
 * 凭据存储的绑定：接口 → SharedPreferences 实现。
 *
 * 单元测试不走 Hilt，直接 new `InMemoryCredentialStore`，所以这里只提供生产实现。
 */
@Module
@InstallIn(SingletonComponent::class)
abstract class CredentialModule {

    @Binds
    @Singleton
    abstract fun bindCredentialStore(impl: PrefsCredentialStore): CredentialStore
}
