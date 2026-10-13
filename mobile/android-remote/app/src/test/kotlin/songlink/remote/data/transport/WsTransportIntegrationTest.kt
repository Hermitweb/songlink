package songlink.remote.data.transport

import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import okhttp3.OkHttpClient
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import songlink.remote.data.util.FrameCodec
import java.net.InetAddress
import java.net.ServerSocket
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * WsTransport 集成测试
 * 使用本地 ServerMock + 真实客户端验证完整流程
 */
class WsTransportIntegrationTest {
    
    private lateinit var server: MockWebSocketServer
    private lateinit var client: WsTransport
    private val clock = System.currentTimeMillis()
    
    @Before
    fun setup() = runBlocking {
        // Find free port
        server = ServerSocket(0).use { socket ->
            socket.localPort
        }
        
        val httpClient = OkHttpClient.Builder().build()
        
        client = object : WsTransport(httpClient, FrameCodec()) {}
    }
    
    @Test
    fun `connectHelloAuthSequence_worksWithRealClient()`() = runTest(timeout = 5000) {
        val url = "ws://localhost:$server"
        val pin = "123456"
        
        val connectLatch = CountDownLatch(1)
        val authLatch = CountDownLatch(1)
        
        server.start(object : MockWebSocketListener {
            override fun onMessage(webSocket: WebSocket, text: String) {
                when {
                    text.contains("\"type\":\"hello\"") -> {
                        // Send authResult back
                        val authResponse = """
                        {
                            "v": 1,
                            "type": "authResult",
                            "id": "h-test",
                            "ref": "",
                            "ok": true,
                            "token": "test-token-123"
                        }
                        """.trimIndent()
                        webSocket.send(authResponse)
                        authLatch.countDown()
                    }
                }
            }
            
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                super.onClosed(webSocket, code, reason)
                connectLatch.countDown()
            }
        })
        
        // Connect client
        val result = client.connect(url, pin)
        
        // Wait for auth response
        assertTrue("Auth should complete within timeout", 
            authLatch.await(3, TimeUnit.SECONDS))
        
        // Verify connection is active
        assertTrue(client.isConnected)
    }
    
    @Test
    fun `reconnectWithExponentialBackoff_worksAfterDisconnect()`() = runTest(timeout = 10000) {
        val url = "ws://localhost:$server"
        val pin = "123456"
        
        val reconnectLatch = CountDownLatch(2)
        
        server.start(object : MockWebSocketListener {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                // Close immediately to trigger reconnect
                webSocket.close(1000, "bye")
                reconnectLatch.countDown()
            }
        })
        
        client.connect(url, pin)
        
        // Should have reconnected twice (max retry count)
        assertTrue("Should reconnect at least twice", 
            reconnectLatch.await(8, TimeUnit.SECONDS))
    }
}

// Mock WebSocket Server for testing
class MockWebSocketServer(private val port: Int) {
    
    private var listener: MockWebSocketListener? = null
    private var serverThread: Thread? = null
    
    interface MockWebSocketListener {
        fun onMessage(ws: WebSocket, text: String)
        fun onOpen(ws: WebSocket, response: Response)
        fun onClosed(ws: WebSocket, code: Int, reason: String)
    }
    
    fun start(listener: MockWebSocketListener) {
        this.listener = listener
        
        serverThread = Thread {
            try {
                ServerSocket(port).use { server ->
                    while (!Thread.currentThread().isInterrupted) {
                        val client = server.accept()
                        handleConnection(client)
                    }
                }
            } catch (e: InterruptedException) {
                Thread.currentThread().interrupt()
            }
        }.apply { start() }
    }
    
    private fun handleConnection(client: java.net.Socket) {
        try {
            val input = client.inputStream.bufferedReader()
            val output = client.outputStream.bufferedWriter()
            
            // Read HTTP Upgrade request
            val requestLine = input.readLine()
            if ("Upgrade: websocket" in requestLine) {
                // Echo back upgrade response
                output.write("HTTP/1.1 101 Switching Protocols\r\n")
                output.write("Upgrade: websocket\r\n")
                output.write("Connection: Upgrade\r\n")
                output.write("Sec-WebSocket-Accept: mock-response\r\n")
                output.write("\r\n")
                output.flush()
                
                // Now WebSocket communication
                listener?.onOpen(MockWebSocketProxy(), Response.Builder()
                    .code(101)
                    .message("Switching Protocols")
                    .request(okhttp3.Request.Builder().url("ws://localhost").build())
                    .build())
                
                // Simple echo loop (simplified for test)
                while (client.isConnected) {
                    val line = input.readLine() ?: break
                    println("[Server] Received: $line")
                    
                    listener?.onMessage(MockWebSocketProxy(), line)
                }
            }
        } catch (e: Exception) {
            e.printStackTrace()
        } finally {
            client.close()
        }
    }
    
    fun stop() {
        serverThread?.interrupt()
    }
}

// Simplified Mock WebSocket
class MockWebSocketProxy : WebSocket {
    override fun send(message: String): Boolean = false
    override fun close(code: Int, reason: String?): Boolean = false
    override fun closeNow(closeStatus: okhttp3.WebSocketCloseStatus): Boolean = false
    override val protocol: okhttp3.WebSocketProtocol? = null
    override val request: okhttp3.Request = okhttp3.Request.Builder().url("ws://test").build()
    override val listener: WebSocketListener? = null
    
    // Delegate all other methods to a no-op implementation
    override fun toString(): String = "MockWebSocket"
    override fun equals(other: Any?): Boolean = other is MockWebSocketProxy
    override fun hashCode(): Int = 123456
    override fun cancel() = false
}
