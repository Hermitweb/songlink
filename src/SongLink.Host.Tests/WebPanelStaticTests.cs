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
        Assert.Contains("SongLink 点歌台", html);
        Assert.Contains("/static/style.css", html);
        Assert.Contains("/static/app.js", html);
    }

    [Fact]
    public async Task Static_css_returns_200_with_text_css_mime()
    {
        var resp = await _http.GetAsync("static/style.css");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/css", resp.Content.Headers.ContentType?.MediaType);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(".controls button.primary", body);
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

    [Fact]
    public async Task Unknown_path_returns_404()
    {
        var resp = await _http.GetAsync("no/such/file");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
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
