# リポジトリ整理記録（2026-11-02）

この文書は、2026-11-02時点のブランチ、pull request、検証状況を記録します。HEADやCI結果は更新されるため、統合またはブランチ削除の直前に再取得してください。

## Open pull requests

| PR | Head → Base | 状況 | 変更範囲 |
| --- | --- | --- | --- |
| #120 | `feature/segment-model-poc` → `main` | Draft。HEAD `6b417111`、148 commits、74 files。最新CIは成功。 | タブ、保存、音声、読込、Native描画・キャッシュ、動画出力、回帰テスト。 |
| #121 | `docs/readme-sync-with-code` → `main` | 通常PR。HEAD `85921e9d`、2 commits、6 files。CI成功。 | README 5言語と `docs/README.md`。 |
| #122 | `codex/docs-humanizer-skill` → `main` | 通常PR。HEAD `f1dc7d85`、1 commit、1 file。push CI成功。 | READMEとdocsの文体規則を定める `AGENTS.md`。 |

#121と#122に変更ファイルの重複はありません。#120も両文書PRとファイル重複はありません。ただし、#120にはsample-accurate hit-sound、統合音声グラフ、pitch、PlaySound関連の変更が含まれるため、#120を先に統合すると#121のRoadmapが古くなります。

推奨順序は #122、#121、#120 です。#121は#120より前に統合します。#120はDraftのまま実機確認と最終差分監査を終えてから判断します。

## PR #120の変更範囲

`main` との差分は3691 additions、334 deletionsです。ファイル数の内訳はAudio 9、Core 8、Native 7、WPF 24、WPF tests 25、repository metadata 1です。

主な分野と関連Issue:

- タブ、保存、dirty状態、終了処理、非同期状態管理: #62、#115、#116、#118
- 音声グラフ、WAV、pitch、PlaySound、ヒット音: #65、#77、#87、#90、#116、#118
- 読込と編集性能: #55、#67、#80、#83、#89、#101、#117
- Native描画、背景、DPI、キャッシュ、選択: #59、#62、#63、#116、#117、#118
- 動画出力とヘッドレス描画: #74、#117、#118
- レイアウトと操作性: #71、#115

最新のpull request CIでは、Core・Audio・WPF Full、Native CTest、WPF Release build、portable publishと成果物検査が成功しています。テスト追加はAudio、Core、WPF、Nativeの各領域にあります。明確な未登録回帰やCI失敗は、今回の静的監査では見つかりませんでした。

統合前にWindows実機で確認する項目:

- WAV、pitch、PlaySound、ヒット音の聴感と同期
- 未保存タブを含む終了、保存、タブ切替
- PositionTrackとScaleRadiusを含む惑星半径
- Native描画、背景画像、DPI、重なった床の選択
- Native故障時の復旧とキャッシュ再利用
- 動画の音声同期、非同期失敗時の停止、1080p出力性能
- 巨大譜面の読込、編集、再生、メモリ使用量

## ブランチ分類

| ブランチ | mainとの差 | 分類 | 判断 |
| --- | ---: | --- | --- |
| `main` | 基準 | 現在使用中 | 統合先。直接pushしない。 |
| `feature/segment-model-poc` | ahead 148 / behind 1 | 現在使用中 | PR #120と定期監査が更新中。 |
| `docs/readme-sync-with-code` | ahead 2 / behind 0 | 現在使用中 | Claude Code担当、PR #121。README所有を保護する。 |
| `codex/docs-humanizer-skill` | ahead 1 / behind 0 | 統合候補 | PR #122。`AGENTS.md`のみ。 |
| `feature/video-export-foundation` | ahead 0 | 統合済み・削除候補 | tipは`main`の祖先。open PRなし。 |
| `feature/wpf-migration` | ahead 0 | 統合済み・削除候補 | tipは`main`の祖先。open PRなし。 |
| `fix/10-dense-audio-stall` | ahead 0 | 統合済み・削除候補 | tipは`main`の祖先。open PRなし。 |
| `fix/11-icon-track-animation` | ahead 0 | 統合済み・削除候補 | tipは`main`の祖先。open PRなし。 |
| `fix/12-14-render-startup` | ahead 0 | 統合済み・削除候補 | tipは`main`の祖先。open PRなし。 |
| `fix/ci-asset-extractor-restore` | ahead 0 | 統合済み・削除候補 | tipは`main`の祖先。open PRなし。 |
| `fix/ci-scaleradius-playback-test` | ahead 0 | 統合済み・削除候補 | tipは`main`の祖先。open PRなし。 |
| `work/issue-fix-batch` | ahead 0 | 統合済み・削除候補 | PR #112はmerged。tipは`main`の祖先。 |

削除候補の8ブランチに独自コミットは残っていません。2026-11-02時点ではどのworktreeもこれらのブランチをcheckoutしていません。削除はこの監査では実行せず、承認後に最新HEADとworktreeを再確認します。

## 開発運用

- Issueまたは密接な小規模バッチごとにPRを作り、複数分野の変更を長期間蓄積しない。
- 他のエージェントが担当するファイルとopen PRを作業前に確認する。担当中のファイルはレビューにとどめる。
- mergedまたはclosed PRのheadへ新しい変更を追加しない。継続作業は新しいPRへ移す。
- 長期Draftは検証可能な区切りで世代交代し、旧Draftに最終HEADと後継PRを記録する。
- 自動監査は専用の時間帯または担当範囲で実行し、手動開発中のbranchへ同時pushしない。
- リリースまたは大規模統合後に、ahead 0、open PRなし、worktree未使用のブランチを一覧化して削除承認を取る。
- merge、branch削除、履歴書換えの直前には、PR HEAD、CI、差分、worktree、未コミット変更を再確認する。
