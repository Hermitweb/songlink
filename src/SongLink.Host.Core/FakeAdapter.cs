using SongLink.Protocol;

namespace SongLink.Host;

/// <summary>
/// 测试用假适配器：不碰任何真实 App，模拟一个可推进的播放会话。
/// 让 Host.Core 在无 WinRT/无声卡的机器上也能端到端跑通（本机验证用）。
/// </summary>
public sealed class FakeAdapter : IPlayerAdapter
{
    private readonly object _lock = new();
    private int _index = 0;
    private bool _playing = true;
    private DateTime _startedUtc = DateTime.UtcNow;

    private static readonly (string Title, string Artist, double Dur)[] Tracks =
    {
        ("晴天", "周杰伦", 269),
        ("稻香", "周杰伦", 223),
        ("Yellow", "Coldplay", 267),
    };

    public string AppId => "fake";
    public AdapterCapability Caps => AdapterCapability.ReadMeta | AdapterCapability.Transport | AdapterCapability.Pick | AdapterCapability.Search;

    public Task<IReadOnlyList<SearchResultItem>> Search(string kw, CancellationToken ct = default)
    {
        kw = (kw ?? "").Trim();
        IReadOnlyList<SearchResultItem> hits = string.IsNullOrEmpty(kw)
            ? Array.Empty<SearchResultItem>()
            : Tracks.Where(t => t.Title.Contains(kw, StringComparison.OrdinalIgnoreCase)
                             || t.Artist.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    .Select(t => new SearchResultItem { Title = t.Title, Artist = t.Artist, Album = "demo", Dur = t.Dur })
                    .ToList();
        return Task.FromResult(hits);
    }

    public Task<NowPlaying?> ReadNowPlaying(CancellationToken ct = default)
    {
        lock (_lock)
        {
            var t = Tracks[_index];
            double pos = 0;
            if (_playing)
            {
                pos = (DateTime.UtcNow - _startedUtc).TotalSeconds;
                if (pos >= t.Dur) { _index = (_index + 1) % Tracks.Length; _startedUtc = DateTime.UtcNow; t = Tracks[_index]; pos = 0; }
            }
            NowPlaying np = new()
            {
                Title = t.Title, Artist = t.Artist, Album = "demo", Dur = t.Dur,
                Pos = Math.Round(pos, 2), Status = _playing ? "playing" : "paused",
                ArtRef = $"demo-{_index}", SourceMode = "PC_LOCAL"
            };
            return Task.FromResult<NowPlaying?>(np);
        }
    }

    public Task<bool> Play(CancellationToken ct = default) { lock (_lock) { _playing = true; _startedUtc = DateTime.UtcNow; } return Task.FromResult(true); }
    public Task<bool> Pause(CancellationToken ct = default) { lock (_lock) { _playing = false; } return Task.FromResult(true); }
    public Task<bool> Stop(CancellationToken ct = default) { lock (_lock) { _playing = false; } return Task.FromResult(true); }

    public Task<bool> Next(CancellationToken ct = default) { lock (_lock) { _index = (_index + 1) % Tracks.Length; _startedUtc = DateTime.UtcNow; _playing = true; } return Task.FromResult(true); }
    public Task<bool> Prev(CancellationToken ct = default) { lock (_lock) { _index = (_index + Tracks.Length - 1) % Tracks.Length; _startedUtc = DateTime.UtcNow; _playing = true; } return Task.FromResult(true); }

    public Task<bool> Pick(string title, string artist, CancellationToken ct = default)
    {
        lock (_lock)
        {
            for (int i = 0; i < Tracks.Length; i++)
                if (Tracks[i].Title == title) { _index = i; _playing = true; _startedUtc = DateTime.UtcNow; return Task.FromResult(true); }
        }
        return Task.FromResult(false); // 假曲库里没有
    }
}
