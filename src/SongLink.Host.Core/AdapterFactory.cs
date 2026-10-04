using System.Reflection;
using System.Text.Json;

namespace SongLink.Host;

/// <summary>
/// 适配器工厂（组合根核心）：按配置选择 IPlayerAdapter。
/// 内置 "fake"（本机可跑）；"smtc" 反射按配置加载外部程序集；"uia" 反射装配 UIA 点歌包装器
/// （内层适配器 + AppDescriptor）。Host.Core 不硬依赖任何适配器工程，本机可编译/测试。
/// </summary>
public static class AdapterFactory
{
    public static IPlayerAdapter Create(HostConfig cfg)
    {
        switch (cfg.Adapter.Trim().ToLowerInvariant())
        {
            case "fake":
            case "demo":
                return new FakeAdapter();

            case "uia":
                return CreateUia(cfg);

            default:
                if (string.IsNullOrWhiteSpace(cfg.AdapterAssembly))
                    throw new InvalidOperationException(
                        $"未知适配器 '{cfg.Adapter}'。内置仅 'fake'；外部适配器（如 smtc）需在配置提供 adapterAssembly（+可选 adapterType）。");

                var asm = Assembly.LoadFrom(Resolve(cfg.AdapterAssembly!));
                var type = !string.IsNullOrWhiteSpace(cfg.AdapterType)
                    ? asm.GetType(cfg.AdapterType!)
                    : asm.GetTypes().FirstOrDefault(t =>
                        typeof(IPlayerAdapter).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null);

                if (type is null)
                    throw new InvalidOperationException(
                        $"在 '{cfg.AdapterAssembly}' 中找不到实现 IPlayerAdapter 的类型{(cfg.AdapterType is null ? "" : $" '{cfg.AdapterType}'")}。");

                var instance = Activator.CreateInstance(type) as IPlayerAdapter
                    ?? throw new InvalidOperationException($"类型 '{type.FullName}' 无法实例化为 IPlayerAdapter。");
                Console.WriteLine($"[factory] 反射加载适配器：{type.FullName}");
                return instance;
        }
    }

    // UIA 点歌包装器：反射装配（避免 Host.Core ↔ UiaPick 循环引用）
    private static IPlayerAdapter CreateUia(HostConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Descriptor))
            throw new InvalidOperationException("uia 适配器需要配置 descriptor（AppDescriptor JSON 路径）。");

        var asm = Assembly.LoadFrom(Resolve(cfg.AdapterAssembly ?? "SongLink.Host.Adapters.UiaPick.dll"));
        var descType = asm.GetType("SongLink.Host.Adapters.UiaPick.AppDescriptor")
            ?? throw new InvalidOperationException("找不到 AppDescriptor 类型。");
        var adapterType = asm.GetType("SongLink.Host.Adapters.UiaPick.UiaPickAdapter")
            ?? throw new InvalidOperationException("找不到 UiaPickAdapter 类型。");

        var descriptor = JsonSerializer.Deserialize(File.ReadAllText(Resolve(cfg.Descriptor!)), descType, HostConfig.JsonOpts)
            ?? throw new InvalidOperationException($"无法解析 descriptor：{cfg.Descriptor}");
        var inner = Create(new HostConfig { Adapter = cfg.InnerAdapter });

        object? engine = null;
        if (!string.IsNullOrWhiteSpace(cfg.OcrAssembly))
        {
            var ocrAsm = Assembly.LoadFrom(Resolve(cfg.OcrAssembly!));
            var engineType = ocrAsm.GetType(cfg.OcrType ?? "SongLink.Host.Adapters.Ocr.MediaOcrEngine")
                ?? throw new InvalidOperationException($"找不到 OCR 引擎类型：{cfg.OcrType}");
            engine = Activator.CreateInstance(engineType, cfg.OcrLanguageTag);
        }

        var wrapped = (engine is not null
            ? Activator.CreateInstance(adapterType, new object[] { inner, descriptor, engine })
            : Activator.CreateInstance(adapterType, inner, descriptor)) as IPlayerAdapter
            ?? throw new InvalidOperationException("UiaPickAdapter 实例化失败。");
        Console.WriteLine($"[factory] UIA 点歌适配器：inner={cfg.InnerAdapter} descriptor={cfg.Descriptor} ocr={(engine is null ? "off" : cfg.OcrAssembly)}");
        return wrapped;
    }

    private static string Resolve(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
}
