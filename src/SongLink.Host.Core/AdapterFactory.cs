using System.Reflection;
using System.Text.Json;

namespace SongLink.Host;

/// <summary>
/// 适配器工厂（组合根核心）：按配置选择 IPlayerAdapter。
/// 内置 "fake"（本机可跑）；"smtc" 反射按配置加载外部程序集；"uia" 反射装配 UIA 点歌包装器
/// （内层适配器 + AppDescriptor）。Host.Core 不硬依赖任何适配器工程，本机可编译/测试。
///
/// 多候选选择策略（解决「同程序集多 IPlayerAdapter 不确定哪一个」）：
///   1) cfg.AdapterType 显式指定 → 按全限定名 GetType
///   2) cfg.Adapter 名（不区分大小写）→ 在候选里挑类型名后缀匹配的（如 smtc → *SmtcAdapter）
///   3) 候选唯一 → 用它
///   4) 多候选且无 2) 命中 → 抛错列出全部候选，让调用方补 cfg.AdapterType
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

                return LoadExternalAdapter(cfg);
        }
    }

    /// <summary>
    /// 外部程序集加载 + 多候选消歧义。public（internal→public 调整）：
    /// 给单元测试用，可直接验证消歧义逻辑。
    /// </summary>
    public static IPlayerAdapter LoadExternalAdapter(HostConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.AdapterAssembly))
            throw new InvalidOperationException("LoadExternalAdapter 要求 cfg.AdapterAssembly 非空。");

        var asm = Assembly.LoadFrom(Resolve(cfg.AdapterAssembly!));

        // (1) 显式 AdapterType 优先
        if (!string.IsNullOrWhiteSpace(cfg.AdapterType))
        {
            var explicitType = asm.GetType(cfg.AdapterType!)
                ?? throw new InvalidOperationException($"在 '{cfg.AdapterAssembly}' 中找不到类型 '{cfg.AdapterType}'。");
            return Instantiate(explicitType);
        }

        // (2)+(3)+(4) 候选筛选 + 消歧义
        // 接受无参 ctor 或全-optional 参数 ctor（DI 注入场景下常用）
        var candidates = asm.GetTypes()
            .Where(t => typeof(IPlayerAdapter).IsAssignableFrom(t)
                        && !t.IsInterface
                        && !t.IsAbstract
                        && HasAutoInstantiableCtor(t))
            .ToArray();

        if (candidates.Length == 0)
            throw new InvalidOperationException(
                $"程序集 '{cfg.AdapterAssembly}' 中找不到任何带无参构造的 IPlayerAdapter 实现类型。");

        // 名称匹配：SmtcAdapter、SmtcXxxAdapter 都接受"smtc"；LocalSourceAdapter 接受"local" / "localsource" / "localSource"
        var adapterKey = cfg.Adapter.Trim();
        Type? byName = candidates.FirstOrDefault(t => TypeNameMatches(t, adapterKey));

        if (byName is not null)
        {
            Console.WriteLine($"[factory] 按名称 'cfg.Adapter={adapterKey}' 命中候选：{byName.FullName}");
            return Instantiate(byName);
        }

        if (candidates.Length == 1)
        {
            Console.WriteLine($"[factory] 唯一候选：{candidates[0].FullName}");
            return Instantiate(candidates[0]);
        }

        // (4) 多候选歧义：列清单，让用户用 cfg.AdapterType 显式选
        var list = string.Join(", ", candidates.Select(c => c.FullName));
        throw new InvalidOperationException(
            $"程序集 '{cfg.AdapterAssembly}' 中有 {candidates.Length} 个 IPlayerAdapter 候选：[{list}]。" +
            $"既未指定 cfg.AdapterType，又不能用 cfg.Adapter='{adapterKey}' 名称匹配。" +
            $"请显式设 cfg.AdapterType 为其中之一。");
    }

    private static bool TypeNameMatches(Type t, string adapterKey)
    {
        // 接受 'smtc' -> 'SmtcAdapter' / 'Smtc...' /  'SmtcXxxAdapter' (后缀 Adapter 紧接或带 Xxx)
        var name = t.Name;
        var lowerKey = adapterKey.ToLowerInvariant();
        var lowerName = name.ToLowerInvariant();
        // 直接以小写 key 开头: smtcadapter, smtcfooadapter
        if (lowerName.StartsWith(lowerKey, StringComparison.Ordinal)) return true;
        // 移除 "Adapter" 后再比对
        if (name.EndsWith("Adapter", StringComparison.OrdinalIgnoreCase))
        {
            var head = name[..^7];   // 去 "Adapter"
            return head.Equals(adapterKey, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static IPlayerAdapter Instantiate(Type t)
    {
        var ctors = t.GetConstructors();
        // 优先用无参 ctor；否则挑 ctor 中参数最少且全部有 default 的，按默认值调用
        var ctor = ctors.FirstOrDefault(c => c.GetParameters().Length == 0);
        object?[] args;
        if (ctor is not null)
        {
            args = Array.Empty<object?>();
        }
        else
        {
            ctor = ctors.OrderBy(c => c.GetParameters().Length).First();
            if (!ctor.GetParameters().All(p => p.HasDefaultValue))
                throw new InvalidOperationException(
                    $"类型 '{t.FullName}' 的 ctor 不是无参且不全是 optional，无法自动实例化。");
            args = ctor.GetParameters().Select(p => p.DefaultValue).ToArray();
        }

        var instance = ctor.Invoke(args) as IPlayerAdapter
            ?? throw new InvalidOperationException($"类型 '{t.FullName}' 无法实例化为 IPlayerAdapter。");
        Console.WriteLine($"[factory] 实例化适配器：{t.FullName}");
        return instance;
    }

    private static bool HasAutoInstantiableCtor(Type t)
    {
        var ctors = t.GetConstructors();
        if (ctors.Length == 0) return false;
        if (ctors.Any(c => c.GetParameters().Length == 0)) return true;
        // 全-optional 也视为可自动实例化（按默认值调用）
        return ctors.Any(c => c.GetParameters().All(p => p.HasDefaultValue));
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
