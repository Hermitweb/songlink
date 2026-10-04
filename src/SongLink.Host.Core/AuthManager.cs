using System.Security.Cryptography;

namespace SongLink.Host;

/// <summary>
/// 配对与鉴权（架构 §6.6 / 评审 #6）。PIN 首次配对，成功后签发长期 token 供重连。
/// 纯逻辑、可单测。M1-β 先做"连接级鉴权"（握手过 PIN/token 后该连接可信）；
/// 每帧 HMAC 留后续。
/// </summary>
public sealed class AuthManager
{
    private readonly Dictionary<string, string> _tokens = new(); // token -> device
    private readonly object _lock = new();

    public string Pin { get; }

    public AuthManager(string? pin = null) => Pin = pin ?? GenPin();

    private static string GenPin() => Random.Shared.Next(100000, 1000000).ToString("D6");

    public bool ValidatePin(string? pin) =>
        !string.IsNullOrEmpty(pin) && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(pin), System.Text.Encoding.UTF8.GetBytes(Pin));

    public string IssueToken(string device)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_lock) _tokens[token] = device;
        return token;
    }

    public bool ValidateToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        lock (_lock) return _tokens.ContainsKey(token);
    }
}
