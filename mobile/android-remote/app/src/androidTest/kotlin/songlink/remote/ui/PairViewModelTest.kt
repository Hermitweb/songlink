package songlink.remote.ui

import androidx.lifecycle.SavedStateHandle
import androidx.test.ext.junit.runners.AndroidJUnit4
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import songlink.remote.data.transport.WsTransport
import songlink.remote.data.repo.NowPlayingRepo
import songlink.remote.ui.viewmodel.PairViewModel
import songlink.remote.util.Constants
import dagger.hilt.android.testing.HiltAndroidRule
import dagger.hilt.android.testing.HiltAndroidTest
import javax.inject.Provider

@HiltAndroidTest
@RunWith(AndroidJUnit4::class)
class PairViewModelTest {
    
    @get:Rule
    val hiltRule = HiltAndroidRule(this)
    
    private lateinit var viewModel: PairViewModel
    
    @Test
    fun `initialState has empty URL and PIN`() = runTest {
        viewModel = PairViewModel(
            savedStateHandle = SavedStateHandle(),
            wsTransport = object : WsTransportProvider() {},
            nowPlayingRepo = object : NowPlayingRepoProvider() {}
        )
        
        assertEquals("", viewModel.serverUrl.value)
        assertEquals("", viewModel.pin.value)
        assertEquals(PairViewModel.ConnectionState.Discovered, viewModel.connectionState.value)
    }
    
    @Test
    fun `invalidURL_throwsValidationError`() = runTest {
        viewModel = createMockViewModel()
        
        val invalidUrls = listOf(
            "not-a-url",
            "http://localhost:51999", // Should be ws://
            "",
            null
        )
        
        invalidUrls.forEach { url ->
            viewModel.serverUrl = url ?: ""
            assertTrue(viewModel.hasInvalidUrl)
        }
    }
    
    @Test
    fun `validURL_acceptsWsProtocol`() = runTest {
        viewModel = createMockViewModel()
        
        val validUrls = listOf(
            "ws://localhost:51999",
            "wss://192.168.1.100:51999",
            "ws://songlink.local"
        )
        
        validUrls.forEach { url ->
            viewModel.serverUrl = url
            assertFalse(viewModel.hasInvalidUrl)
        }
    }
    
    @Test
    fun `PIN_validation_rejectsShortAndLong`() = runTest {
        viewModel = createMockViewModel()
        
        // Too short
        viewModel.pin = "12345"
        assertTrue(viewModel.hasPinError)
        
        // Correct length
        viewModel.pin = "123456"
        assertFalse(viewModel.hasPinError)
        
        // Too long
        viewModel.pin = "1234567"
        assertTrue(viewModel.hasPinError)
        
        // Non-numeric
        viewModel.pin = "abcDEF"
        assertTrue(viewModel.hasPinError)
    }
    
    @Test
    fun `connectionState_changes_correctly_onConnect()`() = runTest {
        viewModel = createMockViewModel(mockSuccess = true)
        
        val stateChanges = mutableListOf<PairViewModel.ConnectionState>()
        viewModel.connectionState.collect { state -> stateChanges.add(state) }
        
        viewModel.connect()
        
        // Should transition through states
        assertTrue(stateChanges.any { it is PairViewModel.ConnectionState.Connecting })
        assertTrue(stateChanges.any { it is PairViewModel.ConnectionState.Connected })
    }
    
    @Test
    fun `savedCredentials_loadsFromDataStore`() = runTest {
        // Mock DataStore with saved values
        val savedUrl = "ws://192.168.1.100:51999"
        val savedPin = "123456"
        
        viewModel = createMockViewModel(savedServerUrl = savedUrl, savedPin = savedPin)
        
        assertEquals(savedUrl, viewModel.serverUrl.value)
        assertEquals(savedPin, viewModel.pin.value)
    }
    
    private fun createMockViewModel(
        mockSuccess: Boolean = true,
        savedServerUrl: String? = null,
        savedPin: String? = null
    ): PairViewModel {
        return PairViewModel(
            savedStateHandle = SavedStateHandle().apply {
                if (savedServerUrl != null) set(Constants.KEY_SERVER_URL, savedServerUrl)
                if (savedPin != null) set(Constants.KEY_PIN, savedPin)
            },
            wsTransport = object : WsTransportProvider() {
                override suspend fun connect(url: String, pin: String): Result<Unit> {
                    return if (mockSuccess) Result.success(Unit) 
                          else Result.failure(Exception("Connection failed"))
                }
            },
            nowPlayingRepo = object : NowPlayingRepoProvider() {}
        )
    }
}

// Mock providers for DI injection
abstract class WsTransportProvider {
    abstract suspend fun connect(url: String, pin: String): Result<Unit>
}

abstract class NowPlayingRepoProvider {
    // No methods needed for this test
}
