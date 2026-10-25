using System.Net;
using System.Net.Http.Headers;
using SongLink.Host;
using Xunit;

namespace SongLink.Host.Tests;

/// <summary>
/// POST /art?name={ref} 封面上传测试 —— "手机正在播放的封面 → 主机 → 面板 GET /art/{ref}" 这条
/// 链路此前完全没有上行入口(Host 只有 GET /art/{ref})。这里验证上传端点的契约与防护:
/// 200/"ok"、400(参数/Content-Type)、413(>2MB),以及上传后 GET 能原样取回(端到端闭环)。
/// 用真实 HttpListener + 真实 HostServer,不走 mock(同 WebPanelStaticTests 的写法)。
/// </summary>
public class ArtUploadTests : IDisposable
{
    private readonly HostServer _server;
    private readonly int _port;
    private readonly HttpClient _http;
    private readonly List<string> _created = new();

    // 端口段 53400-53699:与 WebPanelStaticTests 的 53100+Random(300) 写法一致但**不重叠**,
    // 避免 xUnit 并行跑两个类时两台 HttpListener 抢同一个端口(偶发 flaky 的经典来源)。
    private static readonly string ArtDir = Path.Combine(AppContext.BaseDirectory, "art");

    public ArtUploadTests()
    {
        _port = 53400 + Random.Shared.Next(300);
        _server = new HostServer(new FakeAdapter(), _port, pin: "999888");
        _server.Start(_port);
        // 给 HttpListener 50ms 起(同 WebPanelStaticTests)
        Thread.Sleep(100);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/") };
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        // 清理:HostServer 内部 ArtStore 的默认目录就是 AppContext.BaseDirectory/art(bin 下,已 gitignore)。
        foreach (var r in _created)
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp" })
            {
                try { File.Delete(Path.Combine(ArtDir, r + ext)); } catch { /* 清理失败不影响断言 */ }
            }
    }

    /// <summary>最小合法 PNG(1x1 透明,67 字节)。服务端不解析图片内容,但用真 PNG 才算闭环。</summary>
    private static readonly byte[] OnePixelPng =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    };

    private string NewRef(string ext)
    {
        var r = $"t{Guid.NewGuid():N}{ext}";
        _created.Add(r);
        return r;
    }

    private async Task<HttpResponseMessage> UploadAsync(string query, byte[] body, string contentType)
    {
        var content = new ByteArrayContent(body);
        if (contentType.Length > 0) content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return await _http.PostAsync($"art{query}", content);
    }

    // ------------------------------------------------------------------ 正常路径(端到端闭环)

    [Fact]
    public async Task Upload_png_returns_ok_and_get_returns_the_same_bytes()
    {
        var refId = NewRef(".png");

        var up = await UploadAsync($"?name={refId}", OnePixelPng, "image/png");
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);
        Assert.Equal("ok", (await up.Content.ReadAsStringAsync()).Trim());

        // 闭环:同一个 ref,GET /art/{ref} 应拿回逐字节相同的图(安卓 ArtImage.kt 就是这么拼 URL 的)
        var get = await _http.GetAsync($"art/{refId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("image/png", get.Content.Headers.ContentType?.MediaType);
        Assert.Equal(OnePixelPng, await get.Content.ReadAsByteArrayAsync());
    }

    /// <summary>回归:GET /art/{ref} 历史上还支持"ref 不带扩展名"(ArtStore.Get 逐个后缀试)。
    /// 新上传的 abc.png 必须让这种老写法也照样命中,否则改了 Get 就把既有调用方打挂了。</summary>
    [Fact]
    public async Task Uploaded_ref_is_still_reachable_by_legacy_ref_without_extension()
    {
        var refId = NewRef(".png");
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync($"?name={refId}", OnePixelPng, "image/png")).StatusCode);

        var get = await _http.GetAsync($"art/{refId[..^4]}");   // 去掉 ".png"
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(OnePixelPng, await get.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".webp", "image/webp")]
    public async Task Upload_accepts_every_whitelisted_extension(string ext, string expectedMime)
    {
        var refId = NewRef(ext);
        var up = await UploadAsync($"?name={refId}", OnePixelPng, expectedMime);
        Assert.Equal(HttpStatusCode.OK, up.StatusCode);

        var get = await _http.GetAsync($"art/{refId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(expectedMime, get.Content.Headers.ContentType?.MediaType);
    }

    // ------------------------------------------------------------------ 参数 / 防护

    /// <summary>路径穿越:ref 里带 ".." 一律 400,绝不能写到 art 目录外面去(IsSafeRef 第一道防线)。</summary>
    [Fact]
    public async Task Ref_with_parent_traversal_returns_400()
    {
        var evil = Uri.EscapeDataString("../../evil.png");
        var resp = await UploadAsync($"?name={evil}", OnePixelPng, "image/png");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // 顺带确认没在 art 目录外留下文件
        Assert.False(File.Exists(Path.Combine(ArtDir, "..", "evil.png")));
    }

    [Fact]
    public async Task Empty_ref_returns_400()
    {
        var resp = await UploadAsync("?name=", OnePixelPng, "image/png");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Missing_ref_parameter_returns_400()
    {
        var resp = await UploadAsync("", OnePixelPng, "image/png");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData("evil.exe")]
    [InlineData("evil.html")]
    [InlineData("noext")]
    public async Task Non_whitelisted_extension_returns_400(string name)
    {
        var resp = await UploadAsync($"?name={Uri.EscapeDataString(name)}", OnePixelPng, "image/png");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        // 白名单之外的 ref 连盘都不该碰
        Assert.False(File.Exists(Path.Combine(ArtDir, name)));
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    [InlineData("image/gif")]
    public async Task Unsupported_content_type_returns_400(string contentType)
    {
        var refId = NewRef(".png");
        var resp = await UploadAsync($"?name={refId}", OnePixelPng, contentType);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        // Content-Type 没过就不该写盘
        Assert.False(File.Exists(Path.Combine(ArtDir, refId)));
    }

    // ------------------------------------------------------------------ 限流

    [Fact]
    public async Task Body_over_2mb_returns_413()
    {
        var refId = NewRef(".png");
        var tooBig = new byte[2 * 1024 * 1024 + 1];
        Array.Copy(OnePixelPng, tooBig, OnePixelPng.Length);   // 假装它是张 PNG

        var resp = await UploadAsync($"?name={refId}", tooBig, "image/png");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        Assert.False(File.Exists(Path.Combine(ArtDir, refId)));
    }

    [Fact]
    public async Task Body_just_under_2mb_is_accepted()
    {
        // 边界:正好 2MB 必须放行(上限是 >2MB 才拒,别把 2MB 卡掉)
        var refId = NewRef(".png");
        var atLimit = new byte[2 * 1024 * 1024];
        Array.Copy(OnePixelPng, atLimit, OnePixelPng.Length);

        var resp = await UploadAsync($"?name={refId}", atLimit, "image/png");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var get = await _http.GetAsync($"art/{refId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(atLimit.Length, (await get.Content.ReadAsByteArrayAsync()).Length);
    }

    // ------------------------------------------------------------------ 分支隔离

    /// <summary>新分支必须精确匹配 path == "/art",不能吃掉 GET /art/{ref},也不能被它吃掉:
    /// GET /art(无斜杠)没东西可取 → 404,而不是被 POST 分支当成上传。</summary>
    [Fact]
    public async Task Get_art_without_ref_still_404s()
    {
        var resp = await _http.GetAsync("art");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_unknown_art_ref_still_404s()
    {
        var resp = await _http.GetAsync($"art/{NewRef(".png")}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
