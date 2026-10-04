using SongLink.Host;
using SongLink.Host.Adapters.UiaPick;
using Xunit;

namespace SongLink.Protocol.Tests;

/// <summary>
/// M2 点歌适配器框架测试（本机可跑部分）：AppDescriptor 解析、UiaPickAdapter 构造/委托/优雅失败、
/// 组合根 "uia" 反射装配。真正的 UIA 点歌需目标 App 在真机验证（此处不触碰真实窗口）。
/// </summary>
public class UiaPickTests
{
    private static string WriteDescriptor()
    {
        var path = Path.Combine(Path.GetTempPath(), "desc-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path,
            "{ \"appId\":\"qqmusic\",\"displayName\":\"QQ音乐\",\"exeNames\":[\"__no_such_proc__\"],\"searchBoxName\":\"搜索\",\"pickTimeoutMs\":1000 }");
        return path;
    }

    [Fact]
    public void AppDescriptor_parses_json()
    {
        var path = WriteDescriptor();
        try
        {
            var d = AppDescriptor.LoadFile(path);
            Assert.Equal("qqmusic", d.AppId);
            Assert.Contains("__no_such_proc__", d.ExeNames);
            Assert.Equal("搜索", d.SearchBoxName);
            Assert.True(d.DoubleClickResult);   // 默认 true
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task UiaPickAdapter_delegates_read_and_exposes_pick_cap()
    {
        var desc = new AppDescriptor { AppId = "test", ExeNames = new[] { "__no_such_proc__" } };
        using var adapter = new UiaPickAdapter(new FakeAdapter(), desc);
        Assert.True(adapter.Caps.HasFlag(AdapterCapability.Pick));
        Assert.Equal("test", adapter.AppId);
        var np = await adapter.ReadNowPlaying();       // 委托给 inner(FakeAdapter)
        Assert.NotNull(np);
    }

    [Fact]
    public async Task UiaPickAdapter_pick_returns_false_when_app_absent()
    {
        var desc = new AppDescriptor { AppId = "test", ExeNames = new[] { "__no_such_proc__" }, PickTimeoutMs = 200 };
        using var adapter = new UiaPickAdapter(new FakeAdapter(), desc);
        var ok = await adapter.Pick("某歌", "某歌手");   // 无进程/无启动路径 → 优雅返回 false，不抛
        Assert.False(ok);
    }

    [Fact]
    public void AdapterFactory_uia_wraps_inner_via_reflection()
    {
        var path = WriteDescriptor();
        try
        {
            var a = AdapterFactory.Create(new HostConfig { Adapter = "uia", Descriptor = path, InnerAdapter = "fake" });
            Assert.True(a.Caps.HasFlag(AdapterCapability.Pick));
            Assert.IsType<UiaPickAdapter>(a);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AdapterFactory_uia_without_descriptor_throws()
        => Assert.Throws<InvalidOperationException>(() => AdapterFactory.Create(new HostConfig { Adapter = "uia" }));
}
