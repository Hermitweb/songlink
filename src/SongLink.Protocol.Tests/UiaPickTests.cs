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

    [Fact]
    public void AdapterFactory_uia_with_ocr_engine_enables_fallback()
    {
        var path = WriteDescriptor();
        try
        {
            var a = AdapterFactory.Create(new HostConfig
            {
                Adapter = "uia",
                Descriptor = path,
                InnerAdapter = "fake",
                OcrAssembly = typeof(SongLink.Host.Adapters.UiaPick.Ocr.StaticOcrEngine).Assembly.Location,
                OcrType = "SongLink.Host.Adapters.UiaPick.Ocr.StaticOcrEngine",
            });
            Assert.True(((UiaPickAdapter)a).HasOcrFallback);   // OCR 兜底已注入生效
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AdapterFactory_uia_without_ocr_engine_has_no_fallback()
    {
        var path = WriteDescriptor();
        try
        {
            var a = AdapterFactory.Create(new HostConfig { Adapter = "uia", Descriptor = path });
            Assert.False(((UiaPickAdapter)a).HasOcrFallback);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Pick 失败场景：UIA+OCR 双路均不可用时返回 false。</summary>
    [Fact]
    public async Task UiaPickAdapter_pick_fails_gracefully_on_missing_app_and_no_ocr()
    {
        var desc = new AppDescriptor 
        { 
            AppId = "ghost-app", 
            ExeNames = new[] { "__does_not_exist__" },
            LaunchPath = null,  // 未提供启动路径
            SearchBoxPoint = null,  // 无 OCR 坐标
            ResultRegion = null
        };
        
        using var adapter = new UiaPickAdapter(new FakeAdapter(), desc);
        
        // UIA 尝试 → 找不到进程 → 抛异常被捕获 → 进入 OCR 兜底
        // OCR 未配置 → HasOcrFallback=false → 最终返回 false
        var ok = await adapter.Pick("Test Song", "Test Artist");
        
        Assert.False(ok, "App 不存在且无 OCR 时 Pick 应返回 false");
        Assert.False(adapter.HasOcrFallback);
    }

    /// <summary>Mock 场景：StaticOcrEngine 提供固定结果模拟 OCR 匹配。</summary>
    [Fact]
    public void UiaPickAdapter_pick_mocked_by_static_ocr_engine()
    {
        // 构造一个带 OCR 坐标的描述符
        var desc = new AppDescriptor 
        { 
            AppId = "mock-ocr-app", 
            ExeNames = Array.Empty<string>(),  // 无进程
            SearchBoxPoint = new[] { 1920, 100 },
            ResultRegion = new[] { 100, 200, 800, 600 },
            PickTimeoutMs = 500
        };
        
        var ocrEngine = new SongLink.Host.Adapters.UiaPick.Ocr.StaticOcrEngine("zh-Hans");
        using var adapter = new UiaPickAdapter(new FakeAdapter(), desc, ocrEngine);
        
        // 此时 Pick 会尝试 OCR（因为 UIA 读不到控件）
        // 注意：由于 StaticOcrEngine.Recognize 只返回固定结果而不会真正截图，
        // 这里实际走的是 OCR 框架流程，但不一定能成功点击（需要真实屏幕坐标）
        // 这个测试主要验证：静态 OCR 能注入 + HasOcrFallback=true
        Assert.True(adapter.HasOcrFallback);
        
        // 验证 Mock OCR 引擎已成功接入
        var actualOcr = typeof(UiaPickAdapter).GetField("_ocr", 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.NotNull(actualOcr.GetValue(adapter));
    }

    /// <summary>能力探测：Caps 组合正确性验证。</summary>
    [Fact]
    public void UiaPickAdapter_caps_combined_correctly()
    {
        var inner = new FakeAdapter();
        var desc = new AppDescriptor { AppId = "cap-test" };
        
        using var adapter = new UiaPickAdapter(inner, desc);
        
        // Caps 应为 inner.Caps | AdapterCapability.Pick
        Assert.True(adapter.Caps.HasFlag(AdapterCapability.ReadMeta));
        Assert.True(adapter.Caps.HasFlag(AdapterCapability.Transport));
        Assert.True(adapter.Caps.HasFlag(AdapterCapability.Pick));
        Assert.True(adapter.Caps.HasFlag(AdapterCapability.Search));
        
        // 确保其他能力继承
        Assert.Equal(inner.Caps | AdapterCapability.Pick, adapter.Caps);
    }
}
