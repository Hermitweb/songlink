using System.Text.Json;

namespace SongLink.Host;

/// <summary>
/// Host 组合根配置（评审“组合根配置化”）：端口、适配器选择、PIN 等。
/// 来源优先级：命令行 &gt; appsettings.json &gt; 默认值。纯托管、可本机测。
/// </summary>
public sealed class HostConfig
{
    public int Port { get; set; } = 51999;
    public string Adapter { get; set; } = "fake";       // fake | smtc | uia | ...
    public string? AdapterAssembly { get; set; }         // 外部适配器 DLL（反射加载，Host.Core 不硬依赖）
    public string? AdapterType { get; set; }             // 适配器类型全名（可空→取程序集内首个 IPlayerAdapter）
    public string InnerAdapter { get; set; } = "fake";   // uia 包装时的内层（读/传输）
    public string? Descriptor { get; set; }              // uia 的 AppDescriptor JSON 路径
    public string? OcrAssembly { get; set; }             // OCR 引擎 DLL（反射加载，接入 UIA 的 OCR 兜底）
    public string? OcrType { get; set; }                 // OCR 引擎类型全名（默认 MediaOcrEngine）
    public string OcrLanguageTag { get; set; } = "zh-Hans";
    public bool RequireFrameSig { get; set; } = false;   // 契约 §6：入站命令帧强制 HMAC 验签（默认关）
    public string? Pin { get; set; }                     // null → 运行时自动生成 6 位

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>从 JSON 文件加载（不存在则返回默认）。</summary>
    public static HostConfig LoadFile(string path)
    {
        if (!File.Exists(path)) return new HostConfig();
        try { return JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(path), JsonOpts) ?? new HostConfig(); }
        catch { return new HostConfig(); }
    }

    /// <summary>命令行覆盖：--port --adapter --assembly --type --inner --descriptor --ocr-assembly --ocr-type --ocr-lang --pin</summary>
    public HostConfig ApplyArgs(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--port" when int.TryParse(args[i + 1], out var p): Port = p; break;
                case "--adapter": Adapter = args[i + 1]; break;
                case "--assembly": case "--adapter-assembly": AdapterAssembly = args[i + 1]; break;
                case "--type": case "--adapter-type": AdapterType = args[i + 1]; break;
                case "--inner": InnerAdapter = args[i + 1]; break;
                case "--descriptor": Descriptor = args[i + 1]; break;
                case "--ocr-assembly": OcrAssembly = args[i + 1]; break;
                case "--ocr-type": OcrType = args[i + 1]; break;
                case "--ocr-lang": OcrLanguageTag = args[i + 1]; break;
                case "--require-sig": RequireFrameSig = bool.TryParse(args[i + 1], out var rs) && rs; break;
                case "--pin": Pin = args[i + 1]; break;
            }
        }
        return this;
    }
}
