using System.Text;
using SongLink.Protocol;

// 一次性工具：用 C# 端真实的 FrameCodec.Serialize 产出各帧类型的 JSON fixture，
// 作为 Android(kotlin.serialization) 端的跨语言解析契约测试输入。
// 输出目录由命令行第一个参数指定。

var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);

void Emit(string name, Frame f)
{
    var json = FrameCodec.Serialize(f);
    File.WriteAllText(Path.Combine(outDir, name + ".json"), json, new UTF8Encoding(false));
    Console.WriteLine($"{name}.json  {json}");
}

Emit("hello", new Hello
{
    V = 1, Seq = 1, Id = "h-001",
    Role = "remote", Device = "Pixel 7", Os = "Android 14",
    Caps = new List<string> { "cmd", "state", "lyric", "art" },
    Pin = "123456"
});

Emit("hello_reconnect", new Hello
{
    V = 1, Seq = 2, Id = "h-002",
    Role = "remote", Device = "Pixel 7", Os = "Android 14",
    Caps = new List<string> { "cmd", "state" },
    Token = "a1b2c3d4e5f6", LastSeq = 42
});

Emit("authResult_ok", new AuthResult
{
    V = 1, Seq = 3, Id = "h-001", Ref = "h-001", Ok = true,
    Token = "tok-abc-123", HostKey = "hostkey-xyz"
});

Emit("authResult_fail", new AuthResult
{
    V = 1, Seq = 4, Id = "h-003", Ref = "h-003", Ok = false, Code = "AUTH_FAILED"
});

Emit("cmd_pick", new Cmd
{
    V = 1, Seq = 5, Id = "c-001", Action = "pick",
    Args = System.Text.Json.Nodes.JsonNode.Parse("""{"title":"晴天","artist":"周杰伦"}""")!.AsObject()
});

Emit("cmd_next_noargs", new Cmd { V = 1, Seq = 6, Id = "c-002", Action = "next" });

Emit("ack_ok", new Ack { V = 1, Seq = 7, Id = "a-001", Ref = "c-001", Ok = true });

Emit("state", new State
{
    V = 1, Seq = 8,
    Now = new NowPlaying
    {
        Title = "晴天", Artist = "周杰伦", Album = "叶惠美",
        Pos = 61.5, Dur = 269.0, Status = "playing",
        ArtRef = "art-1", SourceMode = "PC_LOCAL"
    }
});

Emit("lyric", new Lyric
{
    V = 1, Seq = 9, TrackId = "track-77", OffsetMs = -120,
    Lines = new List<LyricLine>
    {
        new() { T = 0.0, X = "故事的小黄花" },
        new() { T = 3.5, X = "从出生那年就飘着" }
    }
});

Emit("event_queue", new EventFrame
{
    V = 1, Seq = 10, Name = "queue-updated",
    Queue = System.Text.Json.Nodes.JsonNode.Parse("""[{"title":"A","artist":"X"},{"title":"B","artist":"Y"}]""")!.AsArray()
});

Emit("art", new Art { V = 1, Seq = 11, Ref = "art-1", Mime = "image/png", Size = 20480 });

Emit("srcMeta", new SrcMeta
{
    V = 1, Seq = 12, Title = "流媒体曲目", Artist = "手机端",
    Pos = 12.0, Dur = 200.0, Status = "playing", ArtRef = "art-2"
});

Emit("audio_start", new AudioStart
{
    V = 1, Seq = 13, Codec = "opus", Rate = 48000, Ch = 2, FrameMs = 20, Port = 51000
});

Emit("audio_stop", new AudioStop { V = 1, Seq = 14 });

Emit("error", new ErrorFrame
{
    V = 1, Seq = 15, Ref = "c-009", Code = "APP_NOT_FOUND", Hint = "未找到该 App 进程"
});

Emit("ping", new Ping { V = 1, Seq = 16 });

Emit("searchResults", new SearchResults
{
    V = 1, Seq = 17, Ref = "c-003", Query = "晴天",
    Results = new List<SearchResultItem>
    {
        new() { Title = "晴天", Artist = "周杰伦", Album = "叶惠美", Dur = 269.0 },
        new() { Title = "晴天", Artist = " Revival", Album = null, Dur = 245.0 }
    }
});

// 带签名的帧：验证 kotlin 端 HMAC 能复算出同一 sig
var signed = new Cmd { V = 1, Seq = 18, Id = "c-004", Action = "toggle" };
FrameSigner.Sign("fixture-key-123", signed);
Emit("cmd_signed", signed);

// 坏帧：验证两端解析都要拒绝
File.WriteAllText(Path.Combine(outDir, "bad_version.json"), """{"v":2,"type":"ping","seq":1}""", new UTF8Encoding(false));
File.WriteAllText(Path.Combine(outDir, "bad_unknown_type.json"), """{"v":1,"type":"noSuchFrame","seq":1}""", new UTF8Encoding(false));
File.WriteAllText(Path.Combine(outDir, "bad_not_json.json"), "this is not json", new UTF8Encoding(false));
Console.WriteLine("bad_version.json / bad_unknown_type.json / bad_not_json.json");

Console.WriteLine();
Console.WriteLine("fixtures written to: " + Path.GetFullPath(outDir));