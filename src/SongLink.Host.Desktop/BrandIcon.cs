using System;
using System.Drawing;
using System.Reflection;

namespace SongLink.Host.Desktop;

/// <summary>
/// 品牌图标加载器。
///
/// app.ico 有两份用途：
///   1. 被编译进 exe 的 PE 资源 —— 任务栏、资源管理器、快捷方式、卸载列表都用这一份；
///   2. 同时作为嵌入资源保留 —— 单文件发布（PublishSingleFile）后磁盘上并没有独立的
///      .ico 文件，托盘图标只能从程序集里读流。
/// 所以这里统一从嵌入资源取，取不到就回落到系统默认图标，绝不让窗体/托盘没有图标。
/// </summary>
internal static class BrandIcon
{
    /// <summary>与 csproj 里 EmbeddedResource 的 LogicalName 对应。</summary>
    private const string ResourceName = "SongLink.Host.Desktop.app.ico";

    /// <summary>加载品牌图标（多尺寸 .ico，Windows 会按需挑 16/32/48…）。</summary>
    public static Icon Load()
    {
        try
        {
            using var stream = typeof(BrandIcon).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is not null)
                return new Icon(stream);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[icon] 加载品牌图标失败，回落系统默认图标: {ex.Message}");
        }
        return SystemIcons.Application;
    }
}
