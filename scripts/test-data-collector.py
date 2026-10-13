#!/usr/bin/env python3
"""
SongLink M2.5 - P0/P1测试数据自动采集器
实时收集并分析测试结果，生成可视化报告
"""

import json
from datetime import datetime
from typing import Dict, List
from dataclasses import dataclass, asdict, as_dataclass
import csv

@dataclass
class TestResult:
    test_id: str
    name: str
    priority: str
    expected_value: str
    actual_value: str
    status: str
    timestamp: str = None
    
    def __post_init__(self):
        if self.timestamp is None:
            self.timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")

class TestDataCollector:
    def __init__(self):
        self.results: List[TestResult] = []
        self.start_time = datetime.now()
        
    def add_result(self, result: TestResult):
        self.results.append(result)
        
    def get_p0_results(self) -> List[TestResult]:
        return [r for r in self.results if r.priority == "P0"]
    
    def get_p1_results(self) -> List[TestResult]:
        return [r for r in self.results if r.priority == "P1"]
    
    def calculate_pass_rate(self, tests: List[TestResult]) -> float:
        if not tests:
            return 0.0
        passed = len([t for t in tests if t.status == "Pass"])
        return (passed / len(tests)) * 100
    
    def generate_report(self, output_file="test_report.html"):
        p0_tests = self.get_p0_results()
        p1_tests = self.get_p1_results()
        
        p0_pass_rate = self.calculate_pass_rate(p0_tests)
        p1_pass_rate = self.calculate_pass_rate(p1_tests)
        
        all_tests = p0_tests + p1_tests
        total_pass_rate = self.calculate_pass_rate(all_tests)
        
        report = f"""<!DOCTYPE html>
<html lang="zh-CN">
<head>
    <meta charset="UTF-8">
    <title>SongLink M2.5 测试报告</title>
    <style>
        body {{ font-family: Arial, sans-serif; padding: 20px; background: #f5f5f5; }}
        .container {{ max-width: 1200px; margin: 0 auto; background: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }}
        h1 {{ color: #333; border-bottom: 3px solid #667eea; padding-bottom: 10px; }}
        .summary {{ display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 20px; margin: 30px 0; }}
        .card {{ background: linear-gradient(135deg, #667eea 0%, #764ba2 100%); color: white; padding: 20px; border-radius: 8px; text-align: center; }}
        .card h3 {{ margin: 0 0 10px 0; font-size: 14px; opacity: 0.9; }}
        .card .value {{ font-size: 32px; font-weight: bold; margin-bottom: 5px; }}
        .card .label {{ font-size: 12px; opacity: 0.8; }}
        table {{ width: 100%; border-collapse: collapse; margin-top: 20px; }}
        th, td {{ padding: 12px; text-align: left; border-bottom: 1px solid #ddd; }}
        th {{ background: #f8f9fa; font-weight: 600; }}
        .pass {{ color: #10b981; font-weight: bold; }}
        .fail {{ color: #ef4444; font-weight: bold; }}
        .status-badge {{ display: inline-block; padding: 4px 12px; border-radius: 20px; font-size: 12px; font-weight: 600; }}
        .badge-p0 {{ background: #fee2e2; color: #dc2626; }}
        .badge-p1 {{ background: #fef3c7; color: #d97706; }}
        .decision-box {{ background: #eff6ff; border-left: 4px solid #3b82f6; padding: 20px; margin: 20px 0; border-radius: 4px; }}
        footer {{ margin-top: 40px; padding-top: 20px; border-top: 1px solid #e5e7eb; color: #6b7280; font-size: 14px; }}
    </style>
</head>
<body>
    <div class="container">
        <h1>🎵 SongLink M2.5 真机测试报告</h1>
        
        <p><strong>测试时间:</strong> {self.start_time.strftime('%Y-%m-%d %H:%M:%S')}</p>
        <p><strong>总测试项:</strong> {len(all_tests)} | <strong>总通过率:</strong> {total_pass_rate:.1f}%</p>
        
        <div class="summary">
            <div class="card">
                <h3>P0 关键功能</h3>
                <div class="value">{p0_pass_rate:.0f}%</div>
                <div class="label">必须全部通过</div>
            </div>
            <div class="card">
                <h3>P1 重要功能</h3>
                <div class="value">{p1_pass_rate:.0f}%</div>
                <div class="label">建议≥80%</div>
            </div>
            <div class="card">
                <h3>P0 通过率</h3>
                <div class="value">{len(p0_tests)} / {len(all_tests)}</div>
                <div class="label">必须 100%</div>
            </div>
            <div class="card">
                <h3>P1 通过率</h3>
                <div class="value">{len(p1_tests)} / {len(all_tests)}</div>
                <div class="label">建议≥80%</div>
            </div>
        </div>
        
        <h2>🔴 P0 关键功能测试（必须全部通过）</h2>
        <table>
            <tr>
                <th>测试项</th>
                <th>预期结果</th>
                <th>实测结果</th>
                <th>状态</th>
            </tr>"""
        
        for test in p0_tests:
            status_class = "pass" if test.status == "Pass" else "fail"
            status_text = "✓ Pass" if test.status == "Pass" else "✗ Fail"
            report += f"""
            <tr>
                <td><span class="status-badge badge-p0">P0</span> {test.name}</td>
                <td>{test.expected_value}</td>
                <td>{test.actual_value}</td>
                <td><span class="{status_class}">{status_text}</span></td>
            </tr>"""
        
        report += """
        </table>
        
        <h2>🟡 P1 重要功能测试（建议通过）</h2>
        <table>
            <tr>
                <th>测试项</th>
                <th>预期结果</th>
                <th>实测结果</th>
                <th>状态</th>
            </tr>"""
        
        for test in p1_tests:
            status_class = "pass" if test.status == "Pass" else "fail"
            status_text = "✓ Pass" if test.status == "Pass" else "✗ Fail"
            report += f"""
            <tr>
                <td><span class="status-badge badge-p1">P1</span> {test.name}</td>
                <td>{test.expected_value}</td>
                <td>{test.actual_value}</td>
                <td><span class="{status_class}">{status_text}</span></td>
            </tr>"""
        
        # 决策建议
        p0_required = len(p0_tests) > 0 and p0_pass_rate == 100.0
        p1_recommendation = len(p1_tests) > 0 and p1_pass_rate >= 80.0
        
        if p0_required:
            decision = "✅ 通过发布！所有 P0 功能已通过"
            decision_color = "#10b981"
        elif p0_pass_rate < 100:
            decision = "❌ 不通过，需要修复 P0 阻塞问题"
            decision_color = "#ef4444"
        elif not p1_recommendation:
            decision = "⚠️ 需进一步优化，部分 P1 未达标"
            decision_color = "#f59e0b"
        else:
            decision = "✅ 可以发布（P1≥80%）"
            decision_color = "#10b981"
        
        report += f"""
        
        <h2>🎯 发布决策建议</h2>
        <div class="decision-box" style="border-left-color: {decision_color};">
            <h3 style="color: {decision_color}; margin-top: 0;">{decision}</h3>
            <ul>
                {'<li>☑ P0 测试全部通过' if p0_required else '<li>✗ P0 测试有未通过的项'}
                {'<li>☑ P1 测试达到推荐标准' if p1_recommendation else '<li>✗ P1 测试未达到推荐标准'}
            </ul>
        </div>
        
        <footer>
            <p>报告自动生成于 {datetime.now().strftime('%Y-%m-%d %H:%M:%S')} | SongLink M2.5 测试团队</p>
        </footer>
    </div>
</body>
</html>"""
        
        with open(output_file, 'w', encoding='utf-8-sig') as f:
            f.write(report)
        
        print(f"✓ HTML 报告已生成：{output_file}")
        
        return {
            'p0_pass_rate': p0_pass_rate,
            'p1_pass_rate': p1_pass_rate,
            'total_tests': len(all_tests),
            'passed_tests': len(all_tests) - len([t for t in all_tests if t.status != "Pass"])
        }
    
    def export_to_csv(self, output_file="test_results.csv"):
        with open(output_file, 'w', newline='', encoding='utf-8-sig') as f:
            writer = csv.DictWriter(f, fieldnames=['test_id', 'name', 'priority', 'expected_value', 'actual_value', 'status', 'timestamp'])
            writer.writeheader()
            writer.writerows([asdict(r) for r in self.results])
        print(f"✓ CSV 数据已导出：{output_file}")


# 示例用法
if __name__ == "__main__":
    collector = TestDataCollector()
    
    # 添加测试数据
    collector.add_result(TestResult("T001", "AudioPlaybackCapture", "P0", "+5~15%", "+12%", "Pass"))
    collector.add_result(TestResult("T002", "Bluetooth Pairing", "P0", "<=10s", "8.5s", "Pass"))
    collector.add_result(TestResult("T003", "QR Code Scanning", "P0", "100%", "100%", "Pass"))
    collector.add_result(TestResult("T004", "Realtime Spectrum", "P0", ">=60 FPS", "58 FPS", "Fail"))
    
    collector.add_result(TestResult("T005", "Dark Mode Auto-switch", "P1", "Correct", "Yes", "Pass"))
    collector.add_result(TestResult("T006", "I18n Chinese/English", "P1", "Complete", "Yes", "Pass"))
    collector.add_result(TestResult("T007", "Excel Export", "P1", "Readable", "Yes", "Pass"))
    collector.add_result(TestResult("T008", "Web Admin Spectrum", "P1", ">=60 FPS", "62 FPS", "Pass"))
    
    results = collector.generate_report()
    collector.export_to_csv()
    
    print("\n=== 测试结果汇总 ===")
    print(f"P0 通过率：{results['p0_pass_rate']:.1f}%")
    print(f"P1 通过率：{results['p1_pass_rate']:.1f}%")
    print(f"总测试项：{results['total_tests']} | 通过数：{results['passed_tests']}")
