using System;
using System.Linq;

namespace SongLink.Host.AudioStream;

/// <summary>
/// M2.5: OPUS 音频编解码器封装（纯托管占位实现）
/// 待：安装 Concentus NuGet 包后替换为真实编码逻辑
/// </summary>
public sealed class OpusCodec : IDisposable
{
    public readonly int SampleRate;
    public readonly int Channels;
    private readonly int BitrateBps;
    
    public OpusCodec(int sampleRate = 48000, int channels = 2, int bitrateKbps = 128)
    {
        this.SampleRate = sampleRate;
        this.Channels = channels;
        this.BitrateBps = bitrateKbps * 1000;
    }
    
    /// <summary>PCM (short[]) → OPUS 编码为 byte[]（占位实现）</summary>
    public byte[] Encode(byte[] pcmBytes, int frameSizeSamples)
    {
        // TODO: 安装 Concentus NuGet 包后替换为：
        // var encoder = new Concentus.Enc.OpusEncoder(sampleRate, channels);
        // return encoder.Encode(pcmBuffer, frameSizeSamples).ToArray();
        
        // 当前占位：直接返回输入数据
        var opusData = new byte[pcmBytes.Length];
        Array.Copy(pcmBytes, opusData, pcmBytes.Length);
        
        Console.WriteLine($"[opus] 占位编码：{pcmBytes.Length} bytes → {opusData.Length} bytes");
        return opusData;
    }
    
    /// <summary>OPUS byte[] → PCM (short[])(占位实现)</summary>
    public short[] Decode(byte[] opusData, int outputFrameSize)
    {
        // TODO: 安装 Concentus NuGet 包后替换为：
        // var decoder = new Concentus.Dec.OpusDecoder(sampleRate, channels);
        // return decoder.Decode(opusData, 0, opusData.Length, outputBuffer, 0, outputFrameSize * channels);
        
        // 当前占位：创建空缓冲区
        var pcmBuffer = new short[outputFrameSize];
        
        Console.WriteLine($"[opus] 占位解码：{opusData.Length} bytes → {pcmBuffer.Length * 2} bytes");
        return pcmBuffer;
    }
    
    /// <summary>UDP 包解析：[长度头 2 字节][OPUS 帧]</summary>
    public static (int FrameLength, byte[] OpusData) ParseUdpPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 2)
            throw new InvalidOperationException("UDP 包太短，缺少长度头");
            
        var lengthHeader = BitConverter.ToUInt16(packet.Slice(0, 2));
        var expectedTotal = 2 + lengthHeader;
        
        if (packet.Length < expectedTotal)
            throw new InvalidOperationException($"UDP 包不完整：期望 {expectedTotal}, 实际 {packet.Length}");
            
        var opusData = packet.Slice(2, lengthHeader).ToArray();
        return ((int)lengthHeader, opusData);
    }
    
    /// <summary>组装 UDP 包</summary>
    public static byte[] AssembleUdpPacket(byte[] opusData)
    {
        var packet = new byte[2 + opusData.Length];
        var lengthHeader = BitConverter.GetBytes((ushort)opusData.Length);
        
        packet[0] = lengthHeader[0]; // Big-endian
        packet[1] = lengthHeader[1];
        Array.Copy(opusData, 0, packet, 2, opusData.Length);
        
        return packet;
    }
    
    public void Dispose() 
    {
        // Placeholder: no resources to release
    }
}