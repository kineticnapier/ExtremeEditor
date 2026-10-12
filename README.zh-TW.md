# ExtremeEditor

[English](README.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md)

ExtremeEditor 是一款面向超大型 A Dance of Fire and Ice（`.adofai`）譜面的
Windows 獨立編輯器。正式應用程式使用 WPF 與原生 C++ 視埠，不依賴 Unity、UMM、
Harmony 或 Workbench 執行階段。

目前版本為 `0.0.484-prototype`，預設開發分支為 `main`。
從 WinForms 到 WPF 的遷移以及舊 WinForms 宿主的移除已經完成。

## 發布

目前公開建置仍為 **Alpha 品質**。首個公開發布系列為
`v0.1.0-alpha.*`。

發布 ZIP 面向 Windows x64，並計畫以 self-contained 形式提供，因此無需另外安裝
.NET 執行階段。ExtremeEditor 不會重新散布 ADOFAI 遊戲資源；資源設定會從本機合法安裝的
遊戲副本中讀取。

## 環境需求

- Windows
- .NET 8 SDK
- CMake
- 含 Windows SDK 與 x64 支援的 Visual Studio C++ 建置工具
- 擷取遊戲資源時，需要合法安裝的 ADOFAI

## 執行

標準啟動命令:

```powershell
.\run.ps1
```

`run.ps1` 會啟動 `src/ExtremeEditor.Wpf/ExtremeEditor.Wpf.csproj`。
直接執行 Release 建置:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Wpf
```

可以在 `--` 後傳入譜面路徑:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Wpf -- "C:\path\to\level.adofai"
```

未指定檔案時，編輯器會開啟一個小型合成譜面。可使用 `Open` 命令載入
`.adofai` 檔案。

常用操作:

- 滑鼠滾輪: 以指標為中心縮放
- 中鍵或右鍵拖曳: 平移
- 左鍵: 選擇地板
- Alt + 左鍵拖曳: 使用 PositionTrack 移動所選地板範圍
- `F`: 適配整個譜面
- `O`: 開啟譜面
- `[` / `]`: 將主選地板旋轉 -15° / +15°
- `Ctrl+S`: 儲存
- `Esc`: 清除選取

## 正式架構

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

C# 負責編輯語意、關卡文件、計時、復原/重做、儲存/載入、音訊與選取狀態。
C++ 負責正式視埠的渲染、裁切、命中測試、攝影機互動與原生視埠輸入。
不可變的原生快照透過扁平 P/Invoke 邊界在兩者之間傳遞。

`src/ExtremeEditor.Wpf/LevelViewport*.cs` 被刻意保留，用於舊 WPF temporal/raster
渲染路徑的比較與回歸測試。`WpfPlaybackPresenter`、`WpfFloorRenderer`、
`WpfIconRenderer`、`WpfLevelLoader`、raster-cache 類別與 `SpatialGridIndex`
支援這些測試與共用的非正式職責。它們不是第二個正式視埠；
`MainWindow` 只承載 `NativeLevelViewport`。

已移除的 WinForms 宿主僅屬歷史程式碼，不參與啟動、建置或正式渲染。

## 資源設定

WPF 應用程式使用獨立的 `ExtremeEditor.AssetExtractor.exe`。WPF 專案不會直接參考
extractor 程式庫；建置時會產生並複製可執行檔，資源設定時再以子行程啟動。

資源設定流程:

1. 檢查已儲存的 ADOFAI 安裝路徑
2. 搜尋 Steam 程式庫與 ADOFAI app manifest
3. 自動偵測失敗時提供瀏覽選擇
4. 儲存有效的遊戲路徑
5. 將資源擷取到 `%LocalAppData%\ExtremeEditor\AssetCache`

舊 AssetProbe / 手動匯入流程已淘汰，不再支援。

## 建置與測試

建置正式應用程式:

```powershell
dotnet build src/ExtremeEditor.Wpf/ExtremeEditor.Wpf.csproj -c Release --nologo
```

執行 managed 回歸測試:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Wpf.Tests
dotnet run -c Release --project src/ExtremeEditor.Core.Tests
dotnet run -c Release --project src/ExtremeEditor.Audio.Tests
```

若已產生 `build/native-renderer`，執行 native 測試:

```powershell
ctest --test-dir build/native-renderer -C Release --output-on-failure
```

WPF 回歸測試包含防止舊 WinForms 宿主恢復，以及防止比較用 WPF 視埠重新回到
正式 `MainWindow` 的契約測試。

## 基準測試與診斷

執行 headless Core 基準測試:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Bench -- "C:\path\to\level.adofai"
```

記錄首次載入與音訊診斷:

```powershell
$env:EXTREMEEDITOR_DIAGNOSTICS = "$PWD\first-load.log"
dotnet run -c Release --project src/ExtremeEditor.Wpf -- "C:\path\to\level.adofai"
```

使用 `EXTREMEEDITOR_DIAGNOSTICS=1` 會寫入暫存目錄中的預設診斷日誌。
診斷預設關閉。

## 相容性與效能策略

- 正式視埠目標至少 60 FPS
- 即使在高密度譜面中，近距離地板也必須維持 ADOFAI 風格外觀
- 不允許以點狀顯示取代地板作為最佳化方式
- 維持音訊同步精度
- 將 ADOFAI DLL / 反編譯實作中觀察到的行為作為相容性基準
- 對大型譜面，盡可能讓工作量與目前可見或時間相關的集合成比例

ExtremeEditor 目前還不是原版編輯器的 byte-for-byte 替代品。
相容性工作應搭配針對性的回歸測試。

## 路線圖

已完成:

- 僅原生的正式視埠
- 移除舊 WinForms 宿主
- README 與遷移文件整理
- UI 整理與診斷資訊精簡

`docs/superpowers` 下的詳細文件屬於歷史設計與實作快照。
使用前請查看 `docs/README.md`；其中的清單與命令不代表目前路線圖。
