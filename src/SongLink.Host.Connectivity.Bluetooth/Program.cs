namespace SongLink.Host.Connectivity.Bluetooth;

class Program
{
    static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        
        Console.WriteLine("=== SongLink Bluetooth Tool ===");
        Console.WriteLine("RFCOMM 配对服务器（用于 Android 端连接）");
        Console.WriteLine();
        
        var server = new RfcommServer();
        
        if (server.listener == null)
        {
            Console.WriteLine("[错误] 蓝牙适配器未就绪，请检查设备是否安装蓝牙硬件");
            Console.WriteLine("按任意键退出...");
            Console.ReadKey();
            return;
        }
        
        Console.WriteLine("正在监听...");
        Console.WriteLine("PIN 码：123456");
        Console.WriteLine("在手机上搜索蓝牙设备并配对输入 PIN 码");
        Console.WriteLine();
        Console.WriteLine("按 Ctrl+C 停止服务");
        
        try
        {
            while (!Console.KeyAvailable)
            {
                await Task.Delay(1000);
                
                // 每 10 秒显示状态
                if (DateTime.Now.Second == 0)
                {
                    Console.Write("\r[Broadcasting...] 等待客户端连接...");
                }
            }
        }
        finally
        {
            server.Dispose();
            Console.WriteLine("\n\n[Service Stopped] 已停止服务");
        }
    }
}