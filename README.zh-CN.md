# ExtremeEditor

[English](README.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md)

ExtremeEditor 是一款面向超大型 A Dance of Fire and Ice（`.adofai`）谱面的
Windows 独立编辑器。正式应用使用 WPF 与原生 C++ 视口，不依赖 Unity、UMM、
Harmony 或 Workbench 运行时。

当前版本为 `0.0.484-prototype`，默认开发分支为 `main`。
从 WinForms 到 WPF 的迁移以及旧 WinForms 宿主的移除已经完成。

## 发布

目前公开构建仍为 **Alpha 品质**。首个公开发布系列为
`v0.1.0-alpha.*`。

发布 ZIP 面向 Windows x64，并计划以 self-contained 形式提供，因此无需另外安装
.NET 运行时。ExtremeEditor 不会重新分发 ADOFAI 游戏资源；资源设置会从本机合法安装的
游戏副本中读取。

## 环境要求

- Windows
- .NET 8 SDK
- CMake
- 带 Windows SDK 和 x64 支持的 Visual Studio C++ 构建工具
- 提取游戏资源时，需要合法安装的 ADOFAI

## 运行

标准启动命令:

```powershell
.\run.ps1
```

`run.ps1` 会启动 `src/ExtremeEditor.Wpf/ExtremeEditor.Wpf.csproj`。
直接运行 Release 构建:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Wpf
```

可以在 `--` 后传入谱面路径:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Wpf -- "C:\path\to\level.adofai"
```

未指定文件时，编辑器会打开一个小型合成谱面。可使用 `Open` 命令载入
`.adofai` 文件。

常用操作:

- 鼠标滚轮: 以指针为中心缩放
- 中键或右键拖动: 平移
- 左键: 选择地板
- Alt + 左键拖动: 使用 PositionTrack 移动所选地板范围
- `F`: 适配整个谱面
- `O`: 打开谱面
- `[` / `]`: 将主选地板旋转 -15° / +15°
- `Ctrl+S`: 保存
- `Esc`: 清除选择

## 正式架构

```text
ExtremeEditor.Wpf (C#)
├─ MainWindow / toolbar / inspector
├─ LevelDocument / TimingMap
├─ editor commands / undo / redo
├─ save / load
├─ audio
├─ EditorSelectionState
└─ NativeLevelViewport : HwndHost
   │
   └─ P/Invoke
      ↓
ExtremeEditor.NativeRenderer.dll (C++)
├─ child HWND
├─ render thread
├─ D3D11 / DXGI / Direct2D
├─ camera / pan / zoom
├─ floor / icon / planet / selection drawing
├─ hit testing
└─ native viewport input
```

C# 负责编辑语义、关卡文档、计时、撤销/重做、保存/加载、音频和选择状态。
C++ 负责正式视口的渲染、裁剪、命中测试、摄像机交互以及原生视口输入。
不可变原生快照通过扁平 P/Invoke 边界在两者之间传递。

`src/ExtremeEditor.Wpf/LevelViewport*.cs` 被有意保留，用于旧 WPF temporal/raster
渲染路径的对比与回归测试。`WpfPlaybackPresenter`、`WpfFloorRenderer`、
`WpfIconRenderer`、`WpfLevelLoader`、raster-cache 类和 `SpatialGridIndex`
为这些测试和共享的非正式职责提供支持。它们不是第二个正式视口；
`MainWindow` 只承载 `NativeLevelViewport`。

已移除的 WinForms 宿主仅属于历史代码，不参与启动、构建或正式渲染。

## 资源设置

WPF 应用使用独立的 `ExtremeEditor.AssetExtractor.exe`。WPF 项目不会直接引用
extractor 库；构建时会生成并复制可执行文件，资源设置时再将其作为子进程启动。

资源设置流程:

1. 检查已保存的 ADOFAI 安装路径
2. 搜索 Steam 库与 ADOFAI app manifest
3. 自动检测失败时提供浏览选择
4. 保存有效的游戏路径
5. 将资源提取到 `%LocalAppData%\ExtremeEditor\AssetCache`

旧 AssetProbe / 手动导入流程已废弃，不再支持。

## 构建与测试

构建正式应用:

```powershell
dotnet build src/ExtremeEditor.Wpf/ExtremeEditor.Wpf.csproj -c Release --nologo
```

运行 managed 回归测试:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Wpf.Tests
dotnet run -c Release --project src/ExtremeEditor.Core.Tests
dotnet run -c Release --project src/ExtremeEditor.Audio.Tests
```

若已经生成 `build/native-renderer`，运行 native 测试:

```powershell
ctest --test-dir build/native-renderer -C Release --output-on-failure
```

WPF 回归测试包含用于防止旧 WinForms 宿主恢复，以及防止用于对比的 WPF 视口重新回到
正式 `MainWindow` 的契约测试。

## 基准测试与诊断

运行 headless Core 基准测试:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Bench -- "C:\path\to\level.adofai"
```

记录首次加载与音频诊断:

```powershell
$env:EXTREMEEDITOR_DIAGNOSTICS = "$PWD\first-load.log"
dotnet run -c Release --project src/ExtremeEditor.Wpf -- "C:\path\to\level.adofai"
```

使用 `EXTREMEEDITOR_DIAGNOSTICS=1` 会写入临时目录中的默认诊断日志。
诊断默认关闭。

## 兼容性与性能策略

- 正式视口目标至少 60 FPS
- 即使在高密度谱面中，近距离地板也必须保持 ADOFAI 风格外观
- 不允许以点状显示替换地板作为优化手段
- 保持音频同步精度
- 将 ADOFAI DLL / 反编译实现中观察到的行为作为兼容性基准
- 对大型谱面，尽可能让工作量与当前可见或时间相关的集合成比例

ExtremeEditor 目前还不是原版编辑器的 byte-for-byte 替代品。
兼容性工作应配套针对性的回归测试。

## 路线图

已完成:

- 仅原生的正式视口
- 移除旧 WinForms 宿主
- README 与迁移文档整理
- UI 整理与诊断信息精简

`docs/superpowers` 下的详细文档属于历史设计与实现快照。
使用前请查看 `docs/README.md`；其中的清单和命令不代表当前路线图。
