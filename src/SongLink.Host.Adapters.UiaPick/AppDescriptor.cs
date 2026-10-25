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

    // —— 坐标驱动点歌（CEF/Electron 套壳的客户端 UIA 读不到控件，只能走坐标）——
    /// <summary>
    /// 搜索框在**窗口内**的归一化坐标 [x,y]（0..1）。
    ///
    /// <para>为什么用归一化而不是屏幕绝对坐标：绝对坐标换分辨率/换缩放/换窗口大小就废了。
    /// 归一化坐标跟着窗口走，窗口在哪、多大都能对上。</para>
    ///
    /// <para>标定方法：截客户端窗口的图，看搜索框在图里的位置，换算成比例。
    /// 参考 QQ音乐 1226x800 的窗口：搜索框在 (480,38) → 比例约 (0.39, 0.047)。</para>
    /// </summary>
    public double[]? SearchBoxRatio { get; set; }

    /// <summary>
    /// 搜索结果**第一条**在窗口内的归一化坐标 [x,y]（0..1）。
    /// 参考 QQ音乐 1226x800：结果行标题在 (295,366) → 比例约 (0.24, 0.457)。
    /// </summary>
    public double[]? ResultItemRatio { get; set; }

    /// <summary>
    /// 标定基准的窗口宽度/高度（像素）。
    ///
    /// <para>为什么要有：坐标是**按某个窗口尺寸标定**的，客户端窗口被最大化后布局就变了，
    /// 同一个比例会点到别的地方。点歌前先把窗口调回标定尺寸，标定才恒定成立。</para>
    /// </summary>
    public int? CalibrationWidth { get; set; }

    /// <summary>标定基准的窗口高度（像素），见 <see cref="CalibrationWidth"/>。</summary>
    public int? CalibrationHeight { get; set; }

    /// <summary>结果行间距（归一化，相对窗口高度）。逐行试点时用。</summary>
    public double? ResultRowHeightRatio { get; set; }

    /// <summary>最多试几行（逐行试点时用）。</summary>
    public int MaxResultRows { get; set; } = 6;

    /// <summary>目标窗口的类名（同一个进程里挑对窗口用）。</summary>
    public string? WindowClassName { get; set; }

    /// <summary>该客户端在系统媒体会话里的 AppId（如 QQMusic.exe / kugou），用于播放校验。</summary>
    public string? MediaSessionAppId { get; set; }

    /// <summary>
    /// 该客户端在"铺满屏幕"时是否要拒绝点歌。
    ///
    /// <para>为什么要分 App：酷狗的"歌手模式"是全屏歌词页，里面**没有搜索框**，
    /// 必须让用户切回主界面；而 QQ音乐 只是被最大化了而已，界面照常可用。
    /// 所以这条限制只对"全屏时确实没有搜索框"的客户端打开。</para>
    /// </summary>
    public bool BlockWhenScreenFilling { get; set; }

    // 结果项定位
    public string? ResultItemAutomationId { get; set; }
    public string? ResultItemNameRegex { get; set; }
    /// <summary>双击结果以播放（true）还是单击。</summary>
    public bool DoubleClickResult { get; set; } = true;
    /// <summary>搜索/结果等待超时（毫秒）。</summary>
    public int PickTimeoutMs { get; set; } = 6000;

    // —— OCR 兜底（UIA 读不到控件时用，架构 R2）——
    /// <summary>搜索框坐标 [x,y]：UIA 找不到搜索框时，点它聚焦再输入歌名。</summary>
    public int[]? SearchBoxPoint { get; set; }
    /// <summary>结果区域 [x,y,w,h]：OCR 只在该区域内找歌名并点击。</summary>
    public int[]? ResultRegion { get; set; }
    /// <summary>OCR 语言标签（如 zh-Hans）。</summary>
    public string? OcrLanguageTag { get; set; }

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
