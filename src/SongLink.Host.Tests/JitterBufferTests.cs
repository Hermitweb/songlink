using SongLink.Host.AudioStream;
using Xunit;

namespace SongLink.Host.Tests;

/// <summary>
/// 抖动缓冲的确定性单测：乱序重排、丢包补静音、迟到旧帧丢弃、字节 seq 回绕。
/// 不依赖网络/时序，直接按 seq 驱动。
/// </summary>
public class JitterBufferTests
{
    private static short[] Frame(byte v, int len = 4)
    {
        var a = new short[len];
        for (int i = 0; i < len; i++) a[i] = (short)v;
        return a;
    }

    [Fact]
    public void InOrderFrames_AreDeliveredInOrder()
    {
        var jb = new JitterBuffer(maxReorderFrames: 4);
        jb.Offer(0, Frame(0), 0);
        jb.Offer(1, Frame(1), 10);
        jb.Offer(2, Frame(2), 20);

        var f0 = jb.TryTake(0);
        Assert.True(f0.HasFrame);
        Assert.False(f0.WasSilence);
        Assert.Equal((short)0, f0.Pcm[0]);

        var f1 = jb.TryTake(10);
        Assert.True(f1.HasFrame);
        Assert.Equal((short)1, f1.Pcm[0]);
    }

    [Fact]
    public void OutOfOrderFrame_WaitsThenReorders()
    {
        // 先到 0，之后 2 抢在 1 前到（窗口内乱序）：取帧仍应按 0,1,2 顺序
        var jb = new JitterBuffer(maxReorderFrames: 8);
        jb.Offer(0, Frame(0), 0);
        jb.Offer(2, Frame(2), 20);   // 抢跑
        jb.Offer(1, Frame(1), 21);

        var f = jb.TryTake(21);
        Assert.True(f.HasFrame);
        Assert.Equal(0, (int)f.Pcm[0]); // 先吐锚定的 0

        var f1 = jb.TryTake(22);
        Assert.True(f1.HasFrame);
        Assert.False(f1.WasSilence);
        Assert.Equal(1, (int)f1.Pcm[0]); // 1 虽后到仍按序吐出

        var f2 = jb.TryTake(23);
        Assert.Equal(2, (int)f2.Pcm[0]);
    }

    [Fact]
    public void GapAfterTimeout_IsFilledWithSilence()
    {
        // 收到 0,1,3（丢了 2）：消费完 0,1 后，缺口 2 在超时后应补静音推进
        var jb = new JitterBuffer(maxReorderFrames: 8, gapTimeoutMs: 10, maxSilencePerGap: 4);
        jb.Offer(0, Frame(0), 0);
        jb.Offer(1, Frame(1), 10);
        jb.Offer(3, Frame(3), 11);

        var f0 = jb.TryTake(0);
        var f1 = jb.TryTake(10);
        Assert.True(f0.HasFrame && f1.HasFrame);

        // 期望 seq=2，窗口里最早是 3（gap=1）。现在时刻远超 GapTimeout → 补静音
        var gap = jb.TryTake(1000);
        Assert.True(gap.HasFrame);
        Assert.True(gap.WasSilence, "丢包后应补静音而非空转");
        Assert.Equal(0, (int)gap.Pcm[0]); // 静音帧应为 0
        Assert.Equal(1L, jb.SilenceFrames);
    }

    [Fact]
    public void StaleOldFrame_IsDropped()
    {
        // 已消费到 5 之后，再来一个"旧的" seq（回绕距离>128 视为旧）应被丢弃
        var jb = new JitterBuffer(maxReorderFrames: 4);
        for (byte i = 0; i < 5; i++)
        {
            jb.Offer(i, Frame(i), i * 10);
            jb.TryTake(i * 10);
        }
        // seq=3 相对 _expected=5 的前向距离是 -2 → 254（>128）→ 旧帧
        bool accepted = jb.Offer(3, Frame(99), 100);
        Assert.False(accepted, "迟到的旧帧应被丢弃");
    }

    [Fact]
    public void ByteSeq_WrapsAt256()
    {
        var jb = new JitterBuffer(maxReorderFrames: 4);
        // 驱动到 255 附近，验证 0/255 的回绕新度判断
        for (byte s = 250; s < 255; s++)
        {
            jb.Offer(s, Frame(s), s * 20);
            jb.TryTake(s * 20);
        }
        // _expected 现在 = 255。255 与 0 应无缝回绕
        Assert.Equal(1, JitterBuffer.ForwardDistance((byte)0, (byte)255));
        Assert.True(jb.Offer(0, Frame(100), 99999), "回绕到 0 应被接受");
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var jb = new JitterBuffer();
        jb.Offer(7, Frame(7), 0);
        jb.Reset();
        var f = jb.TryTake(0);
        Assert.False(f.HasFrame, "Reset 后空窗应无帧");
        Assert.Equal(0L, jb.SilenceFrames);
    }
}
