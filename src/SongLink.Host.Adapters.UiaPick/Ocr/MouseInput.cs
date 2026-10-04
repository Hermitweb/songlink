using System.Runtime.InteropServices;

namespace SongLink.Host.Adapters.UiaPick.Ocr;

/// <summary>
/// 鼠标/键盘输入（user32 SendInput，纯 P/Invoke）。OCR 兜底路径用它点坐标、输入歌名。
/// 真机使用；本文件可编译，调用需桌面会话。
/// </summary>
public static class MouseInput
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, LBUTTONDOWN = 0x0002, LBUTTONUP = 0x0004;
    private const uint KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_KEYUP = 0x0002;

    public static void Click(int x, int y, bool doubleClick = false)
    {
        SetCursorPos(x, y);
        Send(new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = LBUTTONDOWN } } });
        Send(new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = LBUTTONUP } } });
        if (doubleClick)
        {
            Send(new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = LBUTTONDOWN } } });
            Send(new INPUT { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = LBUTTONUP } } });
        }
    }

    /// <summary>以 Unicode 注入输入文本（支持中文）。</summary>
    public static void TypeText(string text)
    {
        foreach (var ch in text)
        {
            Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = (ushort)ch, dwFlags = KEYEVENTF_UNICODE } } });
            Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = (ushort)ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }
    }

    public static void PressEnter()
    {
        const ushort VK_RETURN = 0x0D;
        Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_RETURN } } });
        Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_RETURN, dwFlags = KEYEVENTF_KEYUP } } });
    }

    private static void Send(INPUT input) => SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
}
