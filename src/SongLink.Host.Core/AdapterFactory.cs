using System.Reflection;

namespace SongLink.Host;

/// <summary>
/// 适配器工厂（组合根核心）：按配置选择 IPlayerAdapter。
/// 内置 "fake"（本机可跑）；其余（如 "smtc"）通过反射按配置加载外部程序集——
/// 这样 Host.Core 不硬依赖 WinRT 适配器工程，本机可编译/测试，开发机换真 SMTC 只需改配置。
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

    private static string Resolve(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
}
