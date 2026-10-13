using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SongLink.Protocol;

/// <summary>
/// SongLink 通信帧（与《协议契约.md》/ songlink-frame.schema.json 一一对应）。
/// 用 System.Text.Json 多态，以 "type" 作判别符。纯托管、无平台依赖。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(Cmd), "cmd")]
[JsonDerivedType(typeof(Ack), "ack")]
[JsonDerivedType(typeof(State), "state")]
[JsonDerivedType(typeof(Lyric), "lyric")]
[JsonDerivedType(typeof(EventFrame), "event")]
[JsonDerivedType(typeof(Art), "art")]
[JsonDerivedType(typeof(SrcMeta), "srcMeta")]
[JsonDerivedType(typeof(AudioStart), "audio:start")]
[JsonDerivedType(typeof(AudioStop), "audio:stop")]
[JsonDerivedType(typeof(ErrorFrame), "error")]
[JsonDerivedType(typeof(Ping), "ping")]
[JsonDerivedType(typeof(AuthResult), "authResult")]
[JsonDerivedType(typeof(SearchResults), "searchResults")]
public abstract class Frame
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("ts")] public double? Ts { get; set; }
    [JsonPropertyName("sig")] public string? Sig { get; set; }   // 契约 §6：HMAC-SHA256(token, canonical(frame 去 sig)) 十六进制
}

public sealed class Hello : Frame
{
    [JsonPropertyName("role")] public string Role { get; set; } = "remote"; // remote|host
    [JsonPropertyName("device")] public string? Device { get; set; }
    [JsonPropertyName("os")] public string? Os { get; set; }
    [JsonPropertyName("caps")] public List<string> Caps { get; set; } = new();
    [JsonPropertyName("pin")] public string? Pin { get; set; }      // 首次配对：Host 显示的 PIN
    [JsonPropertyName("token")] public string? Token { get; set; }  // 重连：携带上次下发的令牌
    [JsonPropertyName("lastSeq")] public long? LastSeq { get; set; } // 重连补发：客户端已见的最大 seq（契约 §7）
}

public sealed class AuthResult : Frame
{
    [JsonPropertyName("ref")] public string Ref { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("hostKey")] public string? HostKey { get; set; }   // 开启签名模式时下发，供客户端验证 Host 出站帧
    [JsonPropertyName("code")] public string? Code { get; set; } // 失败时如 AUTH_FAILED
}

public sealed class SearchResultItem
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("artist")] public string Artist { get; set; } = "";
    [JsonPropertyName("album")] public string? Album { get; set; }
    [JsonPropertyName("dur")] public double? Dur { get; set; }
}

public sealed class SearchResults : Frame
{
    [JsonPropertyName("ref")] public string Ref { get; set; } = "";
    [JsonPropertyName("query")] public string Query { get; set; } = "";
    [JsonPropertyName("results")] public List<SearchResultItem> Results { get; set; } = new();
}

public sealed class Cmd : Frame
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("args")] public JsonObject? Args { get; set; }
}

public sealed class Ack : Frame
{
    [JsonPropertyName("ref")] public string Ref { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
}

public sealed class NowPlaying
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("artist")] public string Artist { get; set; } = "";
    [JsonPropertyName("album")] public string? Album { get; set; }
    [JsonPropertyName("pos")] public double Pos { get; set; }
    [JsonPropertyName("dur")] public double Dur { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "stopped";
    [JsonPropertyName("artRef")] public string? ArtRef { get; set; }
    [JsonPropertyName("sourceMode")] public string SourceMode { get; set; } = "PC_LOCAL";

    /// <summary>音量 0.0~1.0（Web 面板音量条用）。</summary>
    [JsonPropertyName("volume")] public double Volume { get; set; } = 1.0;

    /// <summary>浅拷贝（字段皆值类型/不可变字符串），供广播前克隆、不改共享实例（#8）。</summary>
    public NowPlaying Clone() => new()
    {
        Title = Title, Artist = Artist, Album = Album, Pos = Pos, Dur = Dur,
        Status = Status, ArtRef = ArtRef, SourceMode = SourceMode, Volume = Volume
    };
}

public sealed class State : Frame
{
    [JsonPropertyName("now")] public NowPlaying Now { get; set; } = new();
}

public sealed class LyricLine
{
    [JsonPropertyName("t")] public double T { get; set; }
    [JsonPropertyName("x")] public string X { get; set; } = "";
}

public sealed class Lyric : Frame
{
    [JsonPropertyName("trackId")] public string TrackId { get; set; } = "";
    [JsonPropertyName("offsetMs")] public int OffsetMs { get; set; }
    [JsonPropertyName("lines")] public List<LyricLine> Lines { get; set; } = new();
}

public sealed class EventFrame : Frame
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("queue")] public JsonArray? Queue { get; set; }
}

public sealed class Art : Frame
{
    [JsonPropertyName("ref")] public string Ref { get; set; } = "";
    [JsonPropertyName("mime")] public string? Mime { get; set; }
    [JsonPropertyName("size")] public int Size { get; set; }
}

public sealed class SrcMeta : Frame
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("artist")] public string Artist { get; set; } = "";
    [JsonPropertyName("pos")] public double Pos { get; set; }
    [JsonPropertyName("dur")] public double Dur { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "playing";
    [JsonPropertyName("artRef")] public string? ArtRef { get; set; }
}

public sealed class AudioStart : Frame
{
    [JsonPropertyName("codec")] public string Codec { get; set; } = "opus";
    [JsonPropertyName("rate")] public int Rate { get; set; } = 48000;
    [JsonPropertyName("ch")] public int Ch { get; set; } = 2;
    [JsonPropertyName("frameMs")] public int FrameMs { get; set; } = 20;
    [JsonPropertyName("port")] public int Port { get; set; } = 51000;
}

public sealed class AudioStop : Frame { }

public sealed class ErrorFrame : Frame
{
    [JsonPropertyName("ref")] public string? Ref { get; set; }
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("hint")] public string? Hint { get; set; }
}

public sealed class Ping : Frame { }

/// <summary>帧编解码 + 版本校验。</summary>
public static class FrameCodec
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Serialize(Frame f) => JsonSerializer.Serialize(f, Options);

    /// <summary>
    /// 解析一帧；非法 JSON、显式 v!=1、未知 type、字段异常一律返回 null（不抛）。
    /// 注：System.Text.Json 的多态反序列化要求判别符 "type" 出现在对象首个属性；
    /// 第三方客户端（Python/JS）常按 {"v":1,"type":"hello"} 顺序发帧，直接 Deserialize&lt;Frame&gt;
    /// 会抛 NotSupportedException。故这里先取 type 判别符，再按具体派生类型反序列化——对属性顺序不敏感。
    /// </summary>
    public static Frame? Parse(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            var vNode = node?["v"];
            if (vNode is not null && vNode.GetValue<int>() != 1) return null;

            var type = node?["type"]?.GetValue<string>();
            var clr = type switch
            {
                "hello" => typeof(Hello),
                "cmd" => typeof(Cmd),
                "ack" => typeof(Ack),
                "state" => typeof(State),
                "lyric" => typeof(Lyric),
                "event" => typeof(EventFrame),
                "art" => typeof(Art),
                "srcMeta" => typeof(SrcMeta),
                "audio:start" => typeof(AudioStart),
                "audio:stop" => typeof(AudioStop),
                "error" => typeof(ErrorFrame),
                "ping" => typeof(Ping),
                "authResult" => typeof(AuthResult),
                "searchResults" => typeof(SearchResults),
                _ => null
            };
            if (clr is null) return null;
            return JsonSerializer.Deserialize(json, clr, Options) as Frame;
        }
        catch { return null; }   // 任何字段/类型异常都视为坏帧，交由上层回 BAD_PROTO（不吞连接）
    }
}
