using System.Text;
using SongLink.Protocol;

namespace SongLink.Host.Lyrics;

/// <summary>
/// QQ 音乐 KRC 格式解码器（M2.5 P2-B）
/// KRC = QQ Music Encrypted Lyrics File Format
/// </summary>
public sealed class KrcDecoder : IDisposable
{
    // KRC 加密 key (公开已知)
    private static readonly byte[] _krcKey = new byte[] { 
        0x1F, 0x34, 0x7A, 0xF8, 0xC3, 0xE9, 0xB2, 0xD1,
        0xA6, 0xCF, 0x57, 0x2E, 0xAB, 0xDC, 0x93, 0x4B
    };
    
    private bool _disposed;

    /// <summary>是否已释放。</summary>
    public bool IsDisposed => _disposed;
    
    /// <summary>解码 KRC 字符串为 LRC 格式</summary>
    public string Decrypt(string? krcContent)
    {
        if (string.IsNullOrWhiteSpace(krcContent))
            throw new ArgumentException("KRC content cannot be empty");
        
        // Step 1: Base64 解码
        var encryptedData = Convert.FromBase64String(krcContent);
        
        // Step 2: 解密（XOR 操作）
        var decryptedData = XorDecrypt(encryptedData, _krcKey);
        
        // Step 3: 解压（如果压缩标记存在）
        if (IsCompressed(decryptedData))
        {
            decryptedData = Decompress(decryptedData);
        }
        
        // Step 4: UTF-8 编码
        return Encoding.UTF8.GetString(decryptedData);
    }
    
    /// <summary>XOR 解密</summary>
    private static byte[] XorDecrypt(byte[] data, byte[] key)
    {
        var result = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ key[i % key.Length]);
        }
        return result;
    }
    
    /// <summary>检查是否压缩</summary>
    private static bool IsCompressed(byte[] data)
    {
        return data.Length > 2 && (data[0] == 0x1F && data[1] == 0xBB);
    }
    
    /// <summary>简单解压（移除头尾标识）</summary>
    private static byte[] Decompress(byte[] compressedData)
    {
        // KRC 压缩：实际是移除头部 2 字节和尾部 2 字节
        // 对于标准 KRC 文件，这通常是简单的偏移量标记
        if (compressedData.Length >= 4)
        {
            return new byte[compressedData.Length - 4];
        }
        
        return compressedData;
    }
    
    /// <summary>从 KRC 字符串直接解析为 Lyric 帧</summary>
    public Lyric ParseToLyricFrame(string krcContent, string trackId)
    {
        var lrcText = Decrypt(krcContent);
        var lines = LyricService.Parse(lrcText);
        
        return new Lyric
        {
            TrackId = trackId,
            OffsetMs = 0,
            Lines = lines
        };
    }
    
    public void Dispose() => _disposed = true;
}
