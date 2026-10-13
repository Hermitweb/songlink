#!/usr/bin/env python3
"""
SongLink M2.5 - 实时性能指标收集器
在真机测试期间自动收集 CPU/Memory 等关键指标
"""

import psutil
import socket
import time
from datetime import datetime
from dataclasses import dataclass, asdict
from typing import List
import json
import csv

@dataclass
class SystemMetrics:
    timestamp: str
    cpu_percent: float
    memory_percent: float
    network_rx_mb: float
    network_tx_mb: float
    port_51999_connections: int

def get_network_io():
    """获取网络 IO 统计"""
    io = psutil.net_io_counters()
    return io.bytes_recv / (1024*1024), io.bytes_sent / (1024*1024)

def count_port_connections(port):
    """统计指定端口的连接数"""
    try:
        connections = psutil.net_connections(kind='inet')
        return len([c for c in connections if c.laddr.port == port])
    except:
        return 0

def main():
    print("=" * 70)
    print("SongLink M2.5 - 实时性能指标收集器")
    print("=" * 70)
    print(f"开始时间：{datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    print("\n按 Ctrl+C 停止收集\n")
    
    metrics_list: List[SystemMetrics] = []
    
    try:
        while True:
            now = datetime.now()
            
            # 采集系统指标
            cpu = psutil.cpu_percent(interval=0.5)
            mem = psutil.virtual_memory().percent
            
            rx, tx = get_network_io()
            
            # 统计 WebSocket 连接
            conns = count_port_connections(51999)
            
            # 记录指标
            metric = SystemMetrics(
                timestamp=now.strftime("%Y-%m-%d %H:%M:%S"),
                cpu_percent=cpu,
                memory_percent=mem,
                network_rx_mb=rx,
                network_tx_mb=tx,
                port_51999_connections=conns
            )
            
            metrics_list.append(metric)
            
            # 显示当前数据（每 10 秒显示一次）
            if len(metrics_list) % 20 == 0 or len(metrics_list) == 1:
                print(f"\n[{now.strftime('%H:%M:%S')}] "
                      f"CPU:{cpu:.1f}% | Memory:{mem:.1f}% | "
                      f"Rx:{rx:.2f}MB | Tx:{tx:.2f}MB | "
                      f"Connections:{conns}")
            
            time.sleep(1)
            
    except KeyboardInterrupt:
        pass
    
    # 保存结果
    output_file = f"metrics_{now.strftime('%Y%m%d_%H%M%S')}.csv"
    
    with open(output_file, 'w', newline='', encoding='utf-8-sig') as f:
        writer = csv.DictWriter(f, fieldnames=asdict(metrics_list[0]).keys())
        writer.writeheader()
        writer.writerows([asdict(m) for m in metrics_list])
    
    # 也保存 JSON 格式
    json_file = output_file.replace('.csv', '.json')
    with open(json_file, 'w', encoding='utf-8') as f:
        json.dump({
            'start_time': metrics_list[0].timestamp,
            'end_time': metrics_list[-1].timestamp,
            'metrics': [asdict(m) for m in metrics_list]
        }, f, ensure_ascii=False, indent=2)
    
    print(f"\n{'=' * 70}")
    print(f"数据已保存到:")
    print(f"  CSV: {output_file}")
    print(f"  JSON: {json_file}")
    print(f"{'=' * 70}\n")

if __name__ == "__main__":
    main()
