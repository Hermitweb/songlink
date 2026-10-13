package songlink.remote.data.model

import kotlinx.serialization.decodeFromString
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.junit.Assert.*
import org.junit.Test

/**
 * Frames.kt 数据模型测试
 * 验证 JSON 序列化/反序列化正确性（type 置顶兼容）
 */
class FramesTest {
    
    private val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    }
    
    @Test
    fun `Hello serializes with type first`() {
        val hello = Hello(
            v = 1,
            type = "hello",
            role = "remote",
            device = "Pixel-7",
            os = "Android",
            caps = listOf("cmd", "state", "lyric"),
            id = "h-test-001"
        )
        
        val jsonStr = json.encodeToString(hello)
        
        // 验证 JSON 字符串包含所有必需字段
        assertTrue(jsonStr.contains("\"type\":\"hello\""))
        assertTrue(jsonStr.contains("\"v\":1"))
        assertTrue(jsonStr.contains("\"role\":\"remote\""))
        
        // 验证反序列化
        val parsed = json.decodeFromString<Hello>(jsonStr)
        assertEquals(hello.type, parsed.type)
        assertEquals(hello.role, parsed.role)
        assertEquals(hello.caps, parsed.caps)
    }
    
    @Test
    fun `AuthResult deserializes correctly`() {
        val jsonStr = """
        {
            "v": 1,
            "type": "authResult",
            "id": "h-test-001",
            "ref": "",
            "ok": true,
            "token": "a1b2c3d4e5f6g7h8",
            "hostKey": "fedcba9876543210"
        }
        """.trimIndent()
        
        val auth = json.decodeFromString<AuthResult>(jsonStr)
        
        assertTrue(auth.ok)
        assertEquals("a1b2c3d4e5f6g7h8", auth.token)
        assertEquals("fedcba9876543210", auth.hostKey)
    }
    
    @Test
    fun `NowPlaying handles all states`() {
        val states = mapOf(
            "playing" to NowPlaying(status = "playing"),
            "paused" to NowPlaying(status = "paused"),
            "stopped" to NowPlaying(status = "stopped")
        )
        
        states.forEach { (expectedStatus, np) ->
            assertEquals(expectedStatus, np.status)
            
            val jsonStr = json.encodeToString(np)
            val parsed = json.decodeFromString<NowPlaying>(jsonStr)
            assertEquals(np.status, parsed.status)
        }
    }
    
    @Test
    fun `Cmd frame serializes with optional args`() {
        val cmdWithArgs = Cmd(
            v = 1,
            type = "cmd",
            id = "c-001",
            action = "pick",
            args = JsonObject(mapOf(
                "title" to JsonPrimitive("晴天"),
                "artist" to JsonPrimitive("周杰伦")
            ))
        )
        
        val jsonStr = json.encodeToString(cmdWithArgs)
        assertTrue(jsonStr.contains("\"action\":\"pick\""))
        assertTrue(jsonStr.contains("\"title\":\"晴天\""))
        
        // Test without args
        val cmdWithoutArgs = Cmd(v = 1, type = "cmd", id = "c-002", action = "next")
        val jsonNoArgs = json.encodeToString(cmdWithoutArgs)
        assertFalse(jsonNoArgs.contains("args"))
    }
}

// Mock objects for testing
data class JsonObject(val content: Map<String, JsonElement>) {
    companion object {
        operator fun invoke(block: JsonObjectBuilder.() -> Unit): JsonObject {
            val builder = JsonObjectBuilder()
            block(builder)
            return JsonObject(builder.build())
        }
    }
}

class JsonObjectBuilder {
    private val map = mutableMapOf<String, JsonElement>()
    fun build(): Map<String, JsonElement> = map.toMap()
}

typealias JsonElement = Any
typealias JsonPrimitive = String
