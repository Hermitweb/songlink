using SongLink.Host;

namespace SongLink.Host.Tests;

/// <summary>
/// 验证 AdapterFactory.LoadExternalAdapter 的多候选消歧义策略。
/// 测试夹具: SongLink.Host.Adapters.LocalSource.dll 含两个 IPlayerAdapter 实现
///          (LocalSourceAdapter, WmPlayerSourceAdapter),正好覆盖"多候选歧义"路径。
/// </summary>
public class AdapterFactoryTests
{
    private static string LocalSourceDll =>
        Path.Combine(
            AppContext.BaseDirectory,
            "SongLink.Host.Adapters.LocalSource.dll");

    [Fact]
    public void FakeAdapter_works_for_special_case()
    {
        var cfg = new HostConfig { Adapter = "fake" };
        var adapter = AdapterFactory.Create(cfg);
        Assert.Equal("fake", adapter.AppId);
    }

    [Fact]
    public void Explicit_AdapterType_wins_over_name_and_ambiguity()
    {
        var dll = LocalSourceDll;
        Assert.True(File.Exists(dll), $"测试夹具 dll 缺失: {dll}");

        var cfg = new HostConfig
        {
            Adapter = "anything",
            AdapterAssembly = dll,
            AdapterType = "SongLink.Host.Adapters.LocalSource.LocalSourceAdapter"
        };
        var adapter = AdapterFactory.Create(cfg);
        Assert.Equal("localsource", adapter.AppId);
    }

    [Fact]
    public void Adapter_name_matches_LocalSourceAdapter()
    {
        var cfg = new HostConfig
        {
            Adapter = "localSource",
            AdapterAssembly = LocalSourceDll
        };
        var adapter = AdapterFactory.Create(cfg);
        // 名称以 "localsource" 开头会同时匹配 LocalSourceAdapter 和 WmPlayerSourceAdapter
        // (后者以 "wmplayer" 开头不匹配)，所以单匹配
        Assert.Equal("localsource", adapter.AppId);
    }

    [Fact]
    public void Adapter_name_with_Adapter_suffix_strip()
    {
        var cfg = new HostConfig
        {
            Adapter = "LocalSource",       // 严格匹配：LocalSource + "Adapter"
            AdapterAssembly = LocalSourceDll
        };
        var adapter = AdapterFactory.Create(cfg);
        Assert.Equal("localsource", adapter.AppId);
    }

    [Fact]
    public void WmPlayer_loaded_via_explicit_AdapterType_when_two_candidates_in_same_dll()
    {
        var cfg = new HostConfig
        {
            Adapter = "wmPlayer",
            AdapterAssembly = LocalSourceDll,
            AdapterType = "SongLink.Host.Adapters.LocalSource.WmPlayerSourceAdapter"
        };
        var adapter = AdapterFactory.Create(cfg);
        // WMPlayer 与 NAudio 两个适配器的 AppId 都写 "localsource"（同一逻辑抽象的不同实现），
        // 所以用实际 runtime type 区分：成功的关键是给 AdapterType 显式参数时不要被 name 命中错
        Assert.IsType<SongLink.Host.Adapters.LocalSource.WmPlayerSourceAdapter>(adapter);
        Assert.NotNull(adapter);
    }

    [Fact]
    public void LocalSource_loaded_via_explicit_AdapterType_when_two_candidates_in_same_dll()
    {
        var cfg = new HostConfig
        {
            Adapter = "localSource",
            AdapterAssembly = LocalSourceDll,
            AdapterType = "SongLink.Host.Adapters.LocalSource.LocalSourceAdapter"
        };
        var adapter = AdapterFactory.Create(cfg);
        Assert.IsType<SongLink.Host.Adapters.LocalSource.LocalSourceAdapter>(adapter);
    }

    /// <summary>
    /// 关键回归：之前多用 FirstOrDefault，遇到多候选会无声地挑第一个。
    /// 现在 adapter="bogus" 且 dll 含 2+ IPlayerAdapter、既未给 AdapterType、又不是名字匹配，
    /// 应该明确报错列出全部候选，而不是 FirstOrDefault 偷偷选。
    /// </summary>
    [Fact]
    public void Multi_candidate_with_no_disambiguator_lists_all_candidates()
    {
        var cfg = new HostConfig
        {
            Adapter = "nonexistent",
            AdapterAssembly = LocalSourceDll
        };
        var ex = Assert.Throws<InvalidOperationException>(() => AdapterFactory.Create(cfg));
        // 应包含两个候选的全限定名
        Assert.Contains("SongLink.Host.Adapters.LocalSource.LocalSourceAdapter", ex.Message);
        Assert.Contains("SongLink.Host.Adapters.LocalSource.WmPlayerSourceAdapter", ex.Message);
        // 应包含提示让调用方用 AdapterType 显式选
        Assert.Contains("AdapterType", ex.Message);
    }

    [Fact]
    public void Empty_Assembly_throws_with_helpful_message()
    {
        var cfg = new HostConfig { Adapter = "smtc" };   // 没有 AdapterAssembly
        var ex = Assert.Throws<InvalidOperationException>(() => AdapterFactory.Create(cfg));
        Assert.Contains("smtc", ex.Message);
        Assert.Contains("adapterAssembly", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bad_AdapterType_throws_with_qualified_name_in_message()
    {
        var cfg = new HostConfig
        {
            Adapter = "fakeOrOther",
            AdapterAssembly = LocalSourceDll,
            AdapterType = "NonExistent.Type"
        };
        var ex = Assert.Throws<InvalidOperationException>(() => AdapterFactory.Create(cfg));
        Assert.Contains("NonExistent.Type", ex.Message);
    }
}
