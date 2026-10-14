# UIA AppDescriptor 模板

每个音乐 App 配一个 JSON,描述「打开 App → 找搜索框 → 输入歌名 → 点第一项」的具体控件 ID/Name。

## 用法

### 1. 用 FlaUInspect 校准控件 ID

下载:https://github.com/FlaUI/FlaUInspect/releases
- 启动目标音乐 App(QQ 音乐 / 网易云 / 酷狗 / 汽水)
- 启动 FlaUInspect,鼠标拖到「搜索框」上 → 记下 `AutomationId`(优先)或 `Name`(回退)
- 同样拖到「搜索结果列表第一项」上 → 记下 `AutomationId` 或 `Name Regex`

### 2. 填到 JSON

打开 `qq-music.json` 等模板,替换:
- `searchBoxAutomationId`: 真实 ID
- `searchBoxName`: 真实 Name(若没 ID 用 Name)
- `resultItemAutomationId`: 真实 ID
- `resultItemNameRegex`: 第一项的 Name 模式,如 `".*"`(任意) 或 `"^.{0,30}$"`

### 3. 配置 Host 启用

`appsettings.json`:
```json
{
  "adapter": "uia",
  "adapterAssembly": "SongLink.Host.Adapters.UiaPick.dll",
  "innerAdapter": "smtc",
  "descriptor": "descriptors/qq-music.json",
  "ocrAssembly": "SongLink.Host.Adapters.Ocr.dll",
  "ocrType": "SongLink.Host.Adapters.Ocr.MediaOcrEngine"
}
```

### 4. 启动测试

```bash
dotnet run -c Release -- --adapter uia
```

日志看到 `[factory] UIA 点歌适配器：inner=smtc descriptor=descriptors/qq-music.json` 即成功装载。
点歌失败时看 stderr 日志 — UIA 找不到控件会回退到 OCR(需配 `ocrAssembly`)。

## 已知局限性

- **控件 ID 随版本变化** — App 一升级 descriptor 就可能失效,需重抓
- **CEF/Electron 套壳 App** 没暴露 UIA 节点,只能走 OCR
- **OCR 兜底** 依赖中文字库 + 屏幕坐标,分辨率变了要改 `searchBoxPoint` / `resultRegion`
- **汽水音乐(字节系)** 部分版本走 WebView2 内核,UIA 读不到全部控件

## OCR 模式(可选)

UIA 找不到控件时,自动改用屏幕截图 + OCR:
1. 点 `searchBoxPoint` 坐标 → 焦点跳到搜索框
2. 输入歌名 → Enter
3. 截 `resultRegion` 区域 → OCR 找歌名 → 点中点
4. 失败回 false,Host 走 `App-Not-Found` 错误路径

需要 MediaOcrEngine(在 `SongLink.Host.Adapters.Ocr` 工程,目前是占位,需要后续接 Windows.Media.Ocr 或 PaddleOCR)。
