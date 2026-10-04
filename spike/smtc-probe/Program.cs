using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SongLink.Spikes.Smtc;

/// <summary>
/// M0 Spike · SMTC 探针（跨 App，无需任何音乐 App 的私有 API）
/// 目的：验证点歌台系统骨架的"当前播放真值来源"是否成立——
///   读元数据 / 存封面 / 采样进度 / 传输控制（上一首·下一首·播放·暂停·停止）。
///
/// 用法（先放一首歌再跑）：
///   dotnet run -- status                 读取当前正在播放的曲目信息 + 保存封面
///   dotnet run -- watch                  采样进度，验证 PlaybackPosition 平滑可驱动歌词
///   dotnet run -- list                   列出所有活跃 SMTC 会话
///   dotnet run -- next|prev|play|pause|toggle|stop   向当前会话下发传输控制
/// 无参数默认 status。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var cmd = (args.Length > 0 ? args[0] : "status").ToLowerInvariant();
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var sessions = mgr.GetSessions();
        Console.WriteLine($"活跃 SMTC 会话数：{sessions.Count}");

        if (sessions.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("→ 当前没有任何 App 通过 SMTC 注册播放会话。");
            Console.WriteLine("→ 请先在 QQ音乐 / 网易云 / 酷狗 / Spotify / Apple Music 里播放一首歌，再重跑本命令。");
            return 0;
        }

        var current = mgr.GetCurrentSession() ?? sessions[0];

        // —— 用闭包局部函数，避免在方法签名里出现 WinRT 投影类型名 ——
        string FormatPos(TimeSpan t) => $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

        async Task<string?> SaveArt()
        {
            try
            {
                var artRef = current.ThumbnailProperties;
                if (artRef is null) return null;
                var outDir = Path.Combine(AppContext.BaseDirectory, "art");
                Directory.CreateDirectory(outDir);
                using var ras = await artRef.OpenReadAsync();
                if (ras.Size == 0) return null;
                var ext = artRef.ContentType switch
                {
                    "image/jpeg" or "image/jpg" => ".jpg",
                    "image/png" => ".png",
                    _ => ".img"
                };
                var path = Path.Combine(outDir, $"current-art{ext}");
                using var fs = File.Create(path);
                await RandomAccessStream.CopyAsync(ras, fs.AsOutputStream());
                return $"{path}  ({ras.Size} bytes)";
            }
            catch (Exception ex) { return $"读取失败：{ex.Message}"; }
        }

        int DoList()
        {
            Console.WriteLine();
            foreach (var s in sessions)
            {
                var mp = s.MediaProperties; var pi = s.PlaybackInfo;
                Console.WriteLine($"  · [{s.Source}] {mp.Title} — {mp.Artist}  (status={pi.PlaybackStatus})");
            }
            return 0;
        }

        async Task<int> DoStatus()
        {
            var mp = current.MediaProperties; var pi = current.PlaybackInfo;
            Console.WriteLine();
            Console.WriteLine("=== 当前播放（SMTC 真值）===");
            Console.WriteLine($"  来源 App (AppUserModelId)：{current.Source}");
            Console.WriteLine($"  曲名    ：{mp.Title}");
            Console.WriteLine($"  艺人    ：{mp.Artist}");
            Console.WriteLine($"  专辑    ：{mp.Album}");
            Console.WriteLine($"  播放状态：{pi.PlaybackStatus}  (Playing={pi.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing})");
            Console.WriteLine($"  进度    ：{FormatPos(pi.PlaybackPosition)}");
            Console.WriteLine($"  封面    ：{await SaveArt() ?? "无（该 App 未提供 Thumbnail）"}");
            Console.WriteLine();
            Console.WriteLine("[✓] 若能读到以上字段，说明 §3.1 SMTC 只读链路成立，副屏体验的底座可复用。");
            return 0;
        }

        async Task<int> DoWatch()
        {
            Console.WriteLine();
            Console.WriteLine("=== 进度采样（验证歌词同步可用性）· Ctrl+C 退出 ===");
            Console.WriteLine($"  {current.MediaProperties.Title} — {current.MediaProperties.Artist}");
            var last = TimeSpan.Zero;
            while (true)
            {
                var pi = current.PlaybackInfo;
                var pos = pi.PlaybackPosition;
                var playing = pi.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var delta = (pos - last).TotalSeconds;
                Console.WriteLine($"  [{DateTime.Now:HH:mm:ss.fff}] {pi.PlaybackStatus,-8} pos={FormatPos(pos)}  Δ={delta:+0.00;-0.00;0.00}s{(playing ? "" : "  (未播放)")}");
                last = pos;
                await Task.Delay(700);
            }
        }

        async Task<int> DoControl()
        {
            Console.WriteLine($"目标会话：[{current.Source}] {current.MediaProperties.Title} — {current.MediaProperties.Artist}");
            var op = cmd switch
            {
                "next" => current.TrySkipNextAheadAsync(),
                "prev" or "previous" => current.TrySkipPreviousAsync(),
                "play" => current.TryPlayAsync(),
                "pause" => current.TryPauseAsync(),
                "toggle" => current.TryTogglePlayPauseAsync(),
                "stop" => current.TryStopAsync(),
                _ => null
            };
            if (op is null) { Console.WriteLine($"[x] 未知控制指令：{cmd}"); return 1; }
            var ok = await op.AsTask();
            Console.WriteLine($"[{(ok ? "✓" : "x")}] {cmd} → 被控 App {(ok ? "已接受" : "未响应")}（返回 {ok}）");
            if (!ok) Console.WriteLine("    说明该 App 的 SMTC 未实现此按钮；点歌台会据此在能力探针里把该操作标记为不可用（文档 §6.1 Caps）。");
            return ok ? 0 : 3;
        }

        async Task<int> DoDump()
        {
            var list = new List<object>();
            foreach (var s in sessions)
            {
                var mp = s.MediaProperties; var pi = s.PlaybackInfo;
                var p1 = pi.PlaybackPosition;
                await Task.Delay(300);
                var p2 = pi.PlaybackPosition;
                list.Add(new
                {
                    source = s.Source,
                    title = mp.Title,
                    artist = mp.Artist,
                    album = mp.Album,
                    hasThumbnail = s.ThumbnailProperties is not null,
                    status = pi.PlaybackStatus.ToString(),
                    positionSec = Math.Round(pi.PlaybackPosition.TotalSeconds, 2),
                    positionAdvancing = (p2 - p1).TotalSeconds > 0.05
                });
            }
            var dump = new
            {
                capturedAt = DateTimeOffset.Now.ToString("o"),
                os = Environment.OSVersion.VersionString,
                sessions = list
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(dump,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        return cmd switch
        {
            "list" => DoList(),
            "watch" => await DoWatch(),
            "dump" => await DoDump(),
            "next" or "prev" or "previous" or "play" or "pause" or "toggle" or "stop" => await DoControl(),
            _ => await DoStatus(),
        };
    }
}
