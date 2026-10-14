# 開発進捗・統合時点の未完事項（2026-10-14）

この文書は `feature/video-export-foundation` から `main` への統合時点のスナップショットです。**mainに統合したことは機能完成や全テスト通過を意味しません。**

## 開発基準
- 統合対象: `feature/video-export-foundation`、EditorVersion `0.0.544-prototype`
- WPF / Native renderer、音声同期、動画出力の基盤開発を継続
- 本家ADOFAI相当の近距離タイル表示品質を維持（密集を理由にdots化しない）
- 描画性能目標: 60 FPS以上。音声同期精度を損なわないこと
- Windowsビルド・動作確認は開発者のローカル環境で実施。CIに依存しない
- GUI自動操作禁止。未コミット変更の破棄・勝手なリセット禁止
- 今後のコード変更ではEditorVersionを更新する。TDD: RED → ローカル実行 → GREEN

## 直近の実装
- WPF移植、Native描画パイプラインの整備
- 動画出力の基盤、ヘッドレス描画・関連テスト
- Flash / Bloom / ScreenTile / SetFilter のイベント状態・時間軸・Native連携
  - **SetFilter**: 状態連携テストPASS。ただし41種類すべてのピクセル効果の描画完成を意味しない
- CustomBackground:
  - rootの背景設定、CustomBackground・RepeatEventsの発生時刻／優先順位処理
  - Managed/Nativeの構造体整合、時間軸転送、画像ID・アセット登録・Direct2D描画経路
  - 既存CustomBackground回帰テストと構造的な描画統合テストはユーザー環境でPASS（最終確認: `0.0.543-prototype`）
  - **未完**: 実画像のピクセル検証、Stockとの一致、Tiled、parallax、imageColor、lockRot、loopBGなどの正確な描画仕様、シーク実表示の十分な検証
  - 画像が正常に見えることを構造的テストだけでは保証しない

## 未完・REDのままの仕様
### PositionTrack × 惑星半径
- `0.0.544-prototype` で専用REDを追加済み。
- ユーザー実行結果:
  - C# `TimingMap.GetPose()`: PositionTrackのタイル距離9から公転半径9を算出（期待値1.5）
  - C++ `CalculatePlaybackVisual()`: 隣接タイル座標差から半径を算出
- **想定仕様**: PositionTrackによるタイルの移動だけでは公転半径を変更せず、次のタイルへ瞬間移動。ScaleRadiusによる変更は別軸として適用。
- **保留**: 本家ADOFAIでのPositionTrack・ScaleRadius併用、適用タイミング、半径の持続範囲、瞬間移動の厳密な挙動を確認後にGREEN実装。
- 既存ScaleRadius回帰テストを壊さないこと。
- 該当RED: `src/ExtremeEditor.Wpf.Tests/Native/PlanetRadiusPositionTrackRegression.cs`
- 実行: `EXTREMEEDITOR_PLANET_RADIUS_POSITION_TRACK_ONLY=1`（Wpf.Testsを起動）

### その他
- CustomBackgroundのStock描画互換性は未完。描画関連の機能追加時は実譜面比較を推奨。
- Native/WPFの既存機能群について、統合を機に網羅的なfresh build / 全回帰テストを実施したという記録はない。
- 巨大譜面やパフォーマンスを含め、実環境での確認を引き続き行う。

## 将来の計画: ExtremeEditor Together
- 本家ADOFAI用 `kineticnapier/Editor-Together` の `prototype/v0.0.1-full-snapshot` を調査済み。
- WebSocket、部屋管理、revision / operation batch、再同期、SHA-256アセット転送は再利用候補。
- `scnEditor` / Harmony Patchに依存する編集適用・Undo/RedoはExtremeEditor用に作り直す。
- **安全要件**: ExtremeEditor同士のみ接続。ADOFAI版とプロトコル・サーバーを分離し、巨大な操作・スナップショット・アセットをサーバー側で検証／制限する。
- 初期目標: EE間2クライアントでのタイル追加・削除の同期。現時点では設計調査のみで未着手。

## ブランチ統合・運用
- この文書を追加した `feature/video-export-foundation` を fast-forward で `main` に反映する。
- `feature/wpf-migration` と既存の `fix/*` ブランチは2026-10-14時点でmainに対してahead=0の旧ブランチ。**この統合作業では削除しない。**
- 次の大規模機能の実装前に未完項目を再確認すること。
