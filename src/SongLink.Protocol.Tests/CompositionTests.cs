using SongLink.Host;
using Xunit;

namespace SongLink.Protocol.Tests;

/// <summary>组合根配置化测试：HostConfig 加载/覆盖 + AdapterFactory 选择与错误路径。</summary>
public class CompositionTests
{
    [Fact]
    public void HostConfig_defaults()
    {
        var c = new HostConfig();
        Assert.Equal(51999, c.Port);
        Assert.Equal("fake", c.Adapter);
        Assert.Null(c.Pin);
    }

    [Fact]
    public void HostConfig_loads_json_and_applies_cli_override()
    {
        var path = Path.Combine(Path.GetTempPath(), "sl-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ \"port\": 6000, \"adapter\": \"demo\", \"pin\": \"123456\" }");
            var c = HostConfig.LoadFile(path).ApplyArgs(new[] { "run", "--port", "7000" });
            Assert.Equal(7000, c.Port);          // CLI 覆盖 JSON
            Assert.Equal("demo", c.Adapter);      // 来自 JSON
            Assert.Equal("123456", c.Pin);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HostConfig_missing_file_returns_defaults()
        => Assert.Equal(51999, HostConfig.LoadFile(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".json")).Port);

    /// <summary>
    /// 回归：旧实现用 Substring(1) 剥横杠，"--port" 会变成 "-port" 而匹配不到 switch，
    /// 导致所有命令行覆盖静默失效（start-*.bat 传的参数全部无效）。
    /// </summary>
    [Theory]
    [InlineData(new[] { "run", "--port=7000" }, 7000)]
    [InlineData(new[] { "run", "--port", "7000" }, 7000)]
    [InlineData(new[] { "run", "-port=7000" }, 7000)]
    [InlineData(new[] { "run", "-port", "7000" }, 7000)]
    [InlineData(new[] { "run", "--PORT", "7000" }, 7000)] // 大小写不敏感
    public void HostConfig_ApplyArgs_accepts_both_dash_forms(string[] args, int expected)
        => Assert.Equal(expected, new HostConfig { Port = 51999 }.ApplyArgs(args).Port);

    [Fact]
    public void HostConfig_ApplyArgs_equals_form_sets_all_keys()
    {
        var c = new HostConfig().ApplyArgs(new[]
        {
            "--adapter=uia", "--descriptor=d.json", "--ocr-assembly=o.dll",
            "--ocr-type=T", "--pin=123456", "--require-sig=true"
        });

        Assert.Equal("uia", c.Adapter);
        Assert.Equal("d.json", c.Descriptor);
        Assert.Equal("o.dll", c.OcrAssembly);
        Assert.Equal("T", c.OcrType);
        Assert.Equal("123456", c.Pin);
        Assert.True(c.RequireFrameSig);
    }

    [Fact]
    public void HostConfig_ApplyArgs_space_form_sets_all_keys()
    {
        var c = new HostConfig().ApplyArgs(new[]
        {
            "run", "--adapter", "uia", "--descriptor", "d.json", "--pin", "654321"
        });

        Assert.Equal("uia", c.Adapter);
        Assert.Equal("d.json", c.Descriptor);
        Assert.Equal("654321", c.Pin);
    }

    [Fact]
    public void HostConfig_ApplyArgs_bare_flag_without_value_is_ignored()
    {
        var c = new HostConfig { Adapter = "fake" }.ApplyArgs(new[] { "run", "--adapter" });
        Assert.Equal("fake", c.Adapter);
    }

    [Fact]
    public void AdapterFactory_builtin_fake()
    {
        var a = AdapterFactory.Create(new HostConfig { Adapter = "fake" });
        Assert.IsType<FakeAdapter>(a);
        Assert.Equal(AdapterCapability.ReadMeta | AdapterCapability.Transport | AdapterCapability.Pick | AdapterCapability.Search, a.Caps);
    }

    [Fact]
    public void AdapterFactory_unknown_without_assembly_throws()
        => Assert.Throws<InvalidOperationException>(() => AdapterFactory.Create(new HostConfig { Adapter = "smtc" }));

    [Fact]
    public void AdapterFactory_missing_assembly_throws()
        => Assert.ThrowsAny<Exception>(() => AdapterFactory.Create(
            new HostConfig { Adapter = "smtc", AdapterAssembly = "definitely-not-here.dll" }));
}
