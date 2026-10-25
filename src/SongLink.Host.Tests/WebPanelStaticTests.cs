using System.Net;
using SongLink.Host;
using Xunit;

namespace SongLink.Host.Tests;

/// <summary>
/// Host 桌面 Web 面板静态服务测试 — 验证 / 和 /static/* 走 ServeStaticFile 路径。
/// 用真实 HttpListener + 真实 Server,不走 mock。
/// </summary>
public class WebPanelStaticTests : IDisposable
{
    private readonly HostServer _server;
    private readonly int _port;
    private readonly HttpClient _http;

    public WebPanelStaticTests()
    {
        _port = 53100 + Random.Shared.Next(300);
        _server = new HostServer(new FakeAdapter(), _port, pin: "999888");
        _server.Start(_port);
        // 给 HttpListener 50ms 起
        Thread.Sleep(100);
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}/") };
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Root_serves_index_html()
    {
        var resp = await _http.GetAsync("");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        // 注意:界面在 db5a0d1 重构过,断言要跟当前面板契约对齐,
        // 不要再断言重构前才存在的 "SongLink 点歌台" 标题串。
        Assert.Contains("/static/style.css", html);
        Assert.Contains("/static/app.js", html);
        Assert.Contains("<title>点歌台</title>", html);
    }

    /// <summary>
    /// 回归测试:index.html 里每个静态 &lt;use href="#i-xxx"&gt; 都必须有对应的
    /// &lt;symbol id="i-xxx"&gt;。界面是内联 SVG sprite(没有外部 sprite 文件兜底),
    /// 一旦引用了未定义的 symbol,浏览器会静默渲染成空白图标 —— 不会报错,只会在
    /// 首次引导的按钮上出现看不见的箭头。曾真实发生过(#i-chevron-right)。
    /// </summary>
    [Fact]
    public async Task Every_static_icon_reference_resolves_to_a_symbol()
    {
        var resp = await _http.GetAsync("");
        var html = await resp.Content.ReadAsStringAsync();

        var used = System.Text.RegularExpressions.Regex
            .Matches(html, "<use\\s+href=\"#(i-[a-z0-9-]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var defined = System.Text.RegularExpressions.Regex
            .Matches(html, "<symbol\\s+id=\"(i-[a-z0-9-]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(used);

        var missing = used.Except(defined).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.True(
            missing.Length == 0,
            $"这些图标被引用了但没有定义,会渲染成空白: {string.Join(", ", missing)}");
    }

    [Fact]
    public async Task Static_css_returns_200_with_text_css_mime()
    {
        var resp = await _http.GetAsync("static/style.css");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/css", resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadAsStringAsync();
        // 同样对齐重构后的样式:适老化样式块是当前面板的真实契约。
        Assert.Contains("body.elder-mode", body);
    }

    [Fact]
    public async Task Static_js_returns_200_with_js_mime()
    {
        var resp = await _http.GetAsync("static/app.js");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("application/javascript", resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("function handle", body);
        Assert.Contains("authResult", body);
    }

    /// <summary>
    /// 回归测试：面板必须记住配对凭据（token），否则用户每次打开/刷新都要重敲 6 位 PIN。
    ///
    /// 背景：Host 端 <c>ValidatePin(h.Pin) || ValidateToken(h.Token)</c> 本来就同时接受两者，
    /// 但面板曾经只发 PIN、从不保存 authResult.token，导致这个能力完全闲置。
    /// 点歌台面向的是家里长辈，"每次都要重新输 PIN" 是不可接受的体验缺陷。
    /// </summary>
    [Fact]
    public async Task Web_panel_persists_token_so_pin_is_not_required_every_load()
    {
        var resp = await _http.GetAsync("static/app.js");
        var body = await resp.Content.ReadAsStringAsync();

        // 读：从 localStorage 取回 token
        Assert.Contains("localStorage.getItem(TOKEN_KEY)", body);
        // 写：鉴权成功后落盘 token
        Assert.Contains("localStorage.setItem(TOKEN_KEY", body);
        // 用：握手时优先带 token
        Assert.Contains("hello.token = authToken", body);
        // 降级：token 失效（Host 重启）时丢弃并回到手输 PIN，而不是拿废 token 无限重试
        Assert.Contains("localStorage.removeItem(TOKEN_KEY)", body);
    }

    [Fact]
    public async Task Unknown_path_returns_404()
    {
        var resp = await _http.GetAsync("no/such/file");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// <summary>
    /// 回归测试：面板必须能把"在线平台能力"显示出来。
    ///
    /// <para>背景：Host 端 <c>IAdapterDiagnostics.PlatformStatuses</c> 早就实现了，
    /// 但一直没有东西把它送到面板上 —— 于是用户搜到歌就点，点了没响，
    /// 却不知道是没版权、要会员，还是这台机器压根没装那个客户端。
    /// 现在 Host 会推 <c>name="platforms"</c> 的事件，面板要接住并渲染。</para>
    /// </summary>
    [Fact]
    public async Task Web_panel_renders_the_online_platform_capabilities()
    {
        var html = await (await _http.GetAsync("")).Content.ReadAsStringAsync();
        var js = await (await _http.GetAsync("static/app.js")).Content.ReadAsStringAsync();

        // 页面里有承载平台标签的容器
        Assert.Contains("id=\"platforms\"", html);

        // 面板接住 platforms 事件并渲染
        Assert.Contains("f.name === 'platforms'", js);
        Assert.Contains("renderPlatforms", js);
        // 三档能力都有对应的说法（能点歌 / 只能搜 / 不可用）
        Assert.Contains("SearchAndPlay", js);
        Assert.Contains("SearchOnly", js);
    }

    [Fact]
    public async Task Static_path_traversal_blocked()
    {
        // ../ 走到 wwwroot 外 — 400(我们的代码) 或 404(HttpListener 已规范化掉) 都算成功拦截。
        var resp = await _http.GetAsync("static/../appsettings.json");
        Assert.True(
            resp.StatusCode == HttpStatusCode.BadRequest || resp.StatusCode == HttpStatusCode.NotFound,
            $"应拦截路径穿越,实际 {resp.StatusCode}");
    }

    [Fact]
    public async Task Static_unknown_file_returns_404()
    {
        var resp = await _http.GetAsync("static/not-here.txt");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
