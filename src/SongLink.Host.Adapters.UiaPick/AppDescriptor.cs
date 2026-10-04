using System.Text.Json;

namespace SongLink.Host.Adapters.UiaPick;

/// <summary>
/// 每个音乐 App 的 UIA 点歌描述符（数据驱动，架构 §6.1/§3.3）。
/// 把"找窗口→定位搜索框→输入→回车→点结果"的差异抽成配置，减少 per-app 代码。
/// ⚠ 具体 AutomationId/Name 需用 FlaUInspect/Inspect.exe 在真机对目标 App 版本校准。
/// </summary>
public sealed class AppDescriptor
{
    public string AppId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>目标进程名（不含 .exe），用于找到已运行窗口。</summary>
    public string[] ExeNames { get; set; } = Array.Empty<string>();
    /// <summary>可选：主窗口标题正则（进一步筛选窗口）。</summary>
    public string? WindowTitleRegex { get; set; }
    /// <summary>未运行时启动的可执行路径（可选）。</summary>
    public string? LaunchPath { get; set; }

    // 搜索框定位（AutomationId 优先，回退 Name）
    public string? SearchBoxAutomationId { get; set; }
    public string? SearchBoxName { get; set; }
    /// <summary>输入后提交的按键（默认 Enter）。</summary>
    public string SubmitKey { get; set; } = "Enter";

    // 结果项定位
    public string? ResultItemAutomationId { get; set; }
    public string? ResultItemNameRegex { get; set; }
    /// <summary>双击结果以播放（true）还是单击。</summary>
    public bool DoubleClickResult { get; set; } = true;
    /// <summary>搜索/结果等待超时（毫秒）。</summary>
    public int PickTimeoutMs { get; set; } = 6000;

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppDescriptor LoadFile(string path) =>
        JsonSerializer.Deserialize<AppDescriptor>(File.ReadAllText(path), JsonOpts)
        ?? throw new InvalidOperationException($"无法解析描述符：{path}");
}
