# ExtremeEditor Together — 日本語優先UI再設計（RED）

## 目的

現在の \`ExtremeEditor Together\` パネルはCoreのGit操作を露出するデバッグ用UIで、一般の譜面制作者向けの操作導線ではない。**初期表示・ラベル・説明・通常のエラー表示を日本語**にし、既存のWPF譜面編集を主役にする。開発用項目は開いた場合だけ見える詳細設定へ。

- Branch: \`feature/segment-model-poc\`、main変更禁止
- 前回の実機表示: \`0.0.573-prototype\`、画面表示までは確認済み
- 今回: \`0.0.574-prototype\` はRED。**RED成立をユーザーが確認してから**GREEN実装
- ネットワーク利用の招待リンク、リアルタイム同期、クラウド招待を装った文言は使わない

## 最初の画面（通常モード）

ウィンドウ名: **ExtremeEditor 共同編集**

1. **現在の譜面を共同編集する**ことを説明する日本語案内。未読込なら \`.adofai\` を開くように案内。
2. 大きな二つの入口:
   - **共同編集を作成**（現在の譜面を新しいローカルTogether Workspaceに分割）
   - **共同編集に参加**（共有されたGit Workspaceのローカルフォルダーを開く）
3. 作成・参加時に必要な入力は作業手順に応じて案内し、項目名とエラーは日本語。Sample rateは音声データから確実に取得できない場合、自動計測と表示せず**詳細設定で周波数を確認・指定**させる。
4. Workspaceを開いたら「担当区間」一覧と選択中の区間、所有者、進行状況を日本語で表示。ユーザーが触ってはいけない区間は一覧で判別できるようにする。
5. 主操作は **変更を共有** と **完成譜面を書き出す**。共有ボタンは既存のStage→Commit→Pushの意味を隠し過ぎない。「変更を共有」は明示操作であり、通信先未接続・未保存の他人の変更などで失敗したら日本語の原因と回避方法を表示。操作失敗時は編集内容を保持。
6. 状態表示: 作業中/未共有の変更/共有完了/競合が発生（内容を捨てない）など日本語。履歴のCommit SHA等は補足表示のみ。

## 詳細設定（初期状態では閉じる）

\`AdvancedControlsExpander\` = 「詳細設定」（\`IsExpanded=False\`）に以下をまとめる。

- 共同編集WorkspaceとローカルBare Gitパス
- コミットメッセージとStage/Commit/Fetch/Pull/Push（開発・トラブルシュート用）
- 分割床番号、担当者ID、音声サンプル周波数（初期値48 kHzは**デフォルト値**であり、音源から解析された値と誤表示しない）
- 開発者向けサンプル範囲などの詳細表示。床番号と音声サンプルは異なる座標系であることを維持。

## 機能上の注意

- Core側のSegment/Git/Exportの検証ロジックを重複実装しない。
- 閲覧・選択・編集中に自動Commit・自動Pull・自動Force Pushしない。
- ユーザー操作で消える未保存編集は事前に確認する。
- 通常のCtrl+Sで原本の \`.adofai\` を上書きしない。
- ネットワークGitサーバーや招待URLは**未対応**。作成と参加の仕組みはローカルBare GitのPoC範囲。
- 多言語化機構そのものは後続。今回のデフォルトUIは日本語にする（単に設計資料だけを日本語化して終わらない）。

## REDテスト

新規 \`src/ExtremeEditor.Wpf.Tests/Editor/TogetherJapaneseUiRegression.cs\`、専用環境変数 \`EXTREMEEDITOR_TOGETHER_JA_UI_ONLY=1\`。

- ソースWPF XAMLをXMLとして読み取り、ウィンドウ名・通常の入口・担当区間・共有・出力・初期案内が日本語であること
- Git/サンプル周波数の詳細入力が折り畳まれていること
- 通常UIに従来の英語ラベルが残っていないこと
- MainWindowメニューも「共同編集」であること
- 「変更を共有」がダミーボタンではなく処理に接続されていること
- WPF GUI自動クリックや起動は行わない。既存のCore E2E/WPF E2Eの検証は維持する

### Windows RED検証

\`\`\`powershell
git pull --ff-only
dotnet build .\src\ExtremeEditor.Wpf.Tests\ExtremeEditor.Wpf.Tests.csproj -c Release -p:BuildProjectReferences=false
if ($LASTEXITCODE -ne 0) { throw "Build failed: RED cannot be accepted" }

$env:EXTREMEEDITOR_TOGETHER_JA_UI_ONLY = '1'
try {
    dotnet run --project .\src\ExtremeEditor.Wpf.Tests\ExtremeEditor.Wpf.Tests.csproj -c Release --no-build
}
finally {
    Remove-Item Env:\EXTREMEEDITOR_TOGETHER_JA_UI_ONLY -ErrorAction SilentlyContinue
}
\`\`\`

**期待RED:** テストプロジェクトがビルド成功し、\`TogetherJapaneseUiRegression\` に入って「RED: Togetherウィンドウのタイトルを日本語にする」から始まる \`InvalidOperationException\` で失敗する。ビルド失敗ではRED不成立。

**GREEN予定:** \`0.0.575-prototype\`（別途テスト実行・画面目視が必要）。
