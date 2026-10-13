$port = 51001
$logPath = "C:\Users\Admin\udp-listener.log"
"" | Set-Content -Path $logPath
Add-Content -Path $logPath -Value "[start] on 127.0.0.1:$port"

try {
    $client = New-Object System.Net.Sockets.UdpClient([System.Net.Sockets.AddressFamily]::InterNetwork)
    $client.Client.Bind((New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Parse('127.0.0.1'), $port)))
    Add-Content -Path $logPath -Value "[bound] OK"
    for ($i = 0; $i -lt 200; $i++) {
        $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
        $buf = $client.Receive([ref]$remote)
        $hdr = [BitConverter]::ToString($buf, 0, [Math]::Min(16, $buf.Length))
        Add-Content -Path $logPath -Value "[recv $i] from $($remote.Address) len=$($buf.Length) hdr=$hdr"
        if ($i -ge 4) { break }
    }
    $client.Close()
} catch {
    Add-Content -Path $logPath -Value "[error] $($_.Exception.Message)"
}
