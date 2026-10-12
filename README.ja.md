# ExtremeEditor

[English](README.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md)

ExtremeEditor は、非常に大規模な A Dance of Fire and Ice
（`.adofai`）譜面向けの、Windows 用スタンドアロンエディタです。
製品版アプリケーションは WPF とネイティブ C++ ビューポートで構成されており、
Unity、UMM、Harmony、Workbench への実行時依存はありません。

現在のバージョンは `0.0.484-prototype` です。既定の開発ブランチは `main` です。
WinForms から WPF への移行と、旧 WinForms ホストの削除は完了しています。

## リリース

公開ビルドは現在 **alpha 品質** です。最初の公開リリース系列は
`v0.1.0-alpha.*` です。

リリース ZIP は Windows x64 向けの自己完結型ビルドとして公開する想定で、
.NET ランタイムを別途インストールする必要はありません。
ExtremeEditor は ADOFAI のゲームアセットを再配布しません。アセットセットアップでは、
合法的にインストールされたローカルの ADOFAI から読み込みます。

## 必要環境

- Windows
- .NET 8 SDK
- CMake
- Windows SDK と x64 対応を含む Visual Studio C++ ビルドツール
- ゲームアセットを抽出する場合、合法的にインストールされた ADOFAI

## 実行

通常の起動コマンド:

```powershell
.\run.ps1
```

`run.ps1` は
`src/ExtremeEditor.Wpf/ExtremeEditor.Wpf.csproj` を起動します。
Release ビルドを直接実行する場合:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Wpf
```

`--` の後に譜面パスを指定できます:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Wpf -- "C:\path\to\level.adofai"
```

ファイルを指定しない場合は小さな合成レベルを開きます。
`Open` コマンドから `.adofai` を読み込めます。

主な操作:

- マウスホイール: ポインタ位置を中心にズーム
- 中クリックまたは右ドラッグ: パン
- 左クリック: タイル選択
- Alt + 左ドラッグ: 選択範囲を PositionTrack で移動
- `F`: 譜面全体をフレーム
- `O`: 譜面を開く
- `[` / `]`: 主選択タイルを -15° / +15° 回転
- `Ctrl+S`: 保存
- `Esc`: 選択解除

## 製品版アーキテクチャ

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

編集セマンティクス、レベルドキュメント、タイミング、Undo/Redo、保存/読込、
オーディオ、選択状態は C# が管理します。C++ は製品版ビューポートの描画、
カリング、ヒットテスト、カメラ操作、ネイティブ入力を担当します。
両者の間では不変なネイティブスナップショットをフラットな P/Invoke 境界で渡します。

`src/ExtremeEditor.Wpf/LevelViewport*.cs` は、旧 WPF の temporal/raster 描画経路との
比較および回帰テスト用に意図的に残されています。
`WpfPlaybackPresenter`、`WpfFloorRenderer`、`WpfIconRenderer`、
`WpfLevelLoader`、raster cache 関連クラス、`SpatialGridIndex` は、
これらのテストや共通処理を支えるものです。製品版の第2ビューポートではありません。
`MainWindow` がホストするのは `NativeLevelViewport` のみです。

削除済みの WinForms ホストは履歴上の存在であり、起動・ビルド・製品描画には使われません。

## アセットセットアップ

WPF アプリは独立した `ExtremeEditor.AssetExtractor.exe` を使用します。
WPF プロジェクトは extractor ライブラリを直接参照せず、ビルド時に実行ファイルを生成・コピーし、
アセットセットアップ時にサブプロセスとして起動します。

アセットセットアップの流れ:

1. 保存済みの ADOFAI インストールパスを確認
2. Steam ライブラリと ADOFAI app manifest を検索
3. 自動検出できない場合はフォルダ選択を提供
4. 有効なゲームパスを保存
5. `%LocalAppData%\ExtremeEditor\AssetCache` にアセットを抽出

旧 AssetProbe / 手動インポート方式は廃止され、サポート対象ではありません。

## ビルドとテスト

製品版アプリをビルド:

```powershell
dotnet build src/ExtremeEditor.Wpf/ExtremeEditor.Wpf.csproj -c Release --nologo
```

managed 回帰テスト:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Wpf.Tests
dotnet run -c Release --project src/ExtremeEditor.Core.Tests
dotnet run -c Release --project src/ExtremeEditor.Audio.Tests
```

`build/native-renderer` が生成済みなら native テスト:

```powershell
ctest --test-dir build/native-renderer -C Release --output-on-failure
```

WPF 回帰テストには、旧 WinForms ホストの復活や、比較用 WPF ビューポートが
製品版 `MainWindow` に戻ることを防ぐ契約テストが含まれています。

## ベンチマークと診断

Headless Core ベンチマーク:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Bench -- "C:\path\to\level.adofai"
```

初回読み込みとオーディオ診断を記録:

```powershell
$env:EXTREMEEDITOR_DIAGNOSTICS = "$PWD\first-load.log"
dotnet run -c Release --project src/ExtremeEditor.Wpf -- "C:\path\to\level.adofai"
```

`EXTREMEEDITOR_DIAGNOSTICS=1` を使うと、一時ディレクトリ内の既定ログへ出力します。
診断機能は既定では無効です。

## 互換性・性能ポリシー

- 製品版ビューポートで最低 60 FPS を目標とする
- 高密度譜面でも近距離タイルは ADOFAI 風の見た目を維持する
- ドット表示への置き換えは最適化として認めない
- オーディオ同期精度を維持する
- ADOFAI DLL / 逆コンパイル実装で観測される挙動を互換性の基準とする
- 大規模譜面の処理量は可能な限り、表示中または時間的に関連する集合に比例させる

ExtremeEditor はまだ本家エディタの完全な byte-for-byte 代替ではありません。
互換性対応には、焦点を絞った回帰テストを付ける方針です。

## ロードマップ

完了:

- Native-only 製品版ビューポート
- 旧 WinForms ホストの削除
- README と移行ドキュメントの整理
- UI 整理と診断表示の簡素化

詳細な設計・実装資料は `docs/superpowers` 以下にありますが、これらは過去時点のスナップショットです。
利用前に `docs/README.md` を参照してください。そこにあるチェックリストやコマンドは
現在のロードマップそのものではありません。
