# ExtremeEditor Together — 空セグメント編集コンテキスト RED（2026-10-16）

## 基準と目的

合意した目標仕様: [together-time-first-design.md](together-time-first-design.md)。

- `0.0.577-prototype`: 空セグメントの生成・検証GREENがユーザーのWindowsテストで**pass**。
- 今回の `0.0.578-prototype` は、**空の時間区間に譜面を作り始めるためのCore RED**。
- REDが成立するまでGREEN実装しない。Gitバックエンドや公開用Exporterの仕様を緩めない。
- 作業ブランチ `feature/segment-model-poc` を維持。`main`、未完成のScaleRadius/PositionTrack回帰、Windows環境のファイルは変更しない。

## この段階の限界

**ここで配置するのは、ADOFAIの完成した床ではなく、曲中時刻に紐づく「タイルの配置候補（timing anchor）」である。**

理由: タイルの入射角、曲上の実到達時刻、セグメントの入口BPM・Twirl・惑星状態は相互依存しており、時間だけから再生可能な床・橋渡しタイルを生成できない。最初の段階では「空区間でも時刻を基準に創作を開始できる」ことと「他人の区間に書き込めない」ことを保証する。

## RED API

`src/ExtremeEditor.Core/SegmentTimeDraftEditor.cs`

- `SegmentDraftTileAnchor(TileId, AudioSample)`: **プロジェクト共通デコード音源の絶対サンプル**を持つ。床番号・角度・正式ヒット時刻ではない。
- `SegmentTimeDraft(SegmentId, OwnerId, SampleRate, StartSample, EndSampleExclusive, TileAnchors)`: 1担当区間の編集用不変スナップショット。ゼロ件でも合法。開くための `LevelDocument` 不要。
- `SegmentTimeDraftEditor.Open(timeline, entries, segmentId, ownerId)`: 既存の `SegmentTimePlanner.Validate` と `SegmentContentPlanner.Validate` の契約に従う、**自分の割当済み・Active・明示的Empty** な区間に限定。別人・保留・欠損・データ不整合・存在しないIDを拒否。
- `PlaceAnchor(draft, tileId, audioSample)`: **半開区間 `[StartSample, EndSampleExclusive)` 内**だけに追加。非空で一意なID、同一サンプル位置に複数アンカーは許可。結果は時刻昇順、同時刻は挿入順を維持。元のスナップショットは変更しない。
- `RemoveAnchor(draft, tileId)`: 指定したアンカーだけを削除。残りと元のスナップショットを保持。最後のアンカーを削除しても割当区間は消さない。

このAPIは一時的な**編集下書き**。既存 `SegmentContentEntry` の新たな `TimeDraft` 状態／Revisionの保存・Git送受信・共同編集UI連携は**未実装**で、次以降の契約。すでに存在する `FloorAligned` 制作データを勝手に「空」として開くことはしない。

## REDテストケース

`SegmentTimeDraftRegression`:

1. タイルゼロ枚の区間 `drop: [40s,80s)`（担当B）を開ける。開始サンプル、終了サンプル、サンプル周波数、IDを保持。
2. 区間始点40秒、内部60秒、終点直前79秒への配置を許可。終点80秒は不可。
3. 後から過去の時刻へ置いても、配列は時刻昇順。60秒へ同時刻アンカー2個は両方残り、入力順を保つ。
4. 追加・削除は新たな不変スナップショット。元の空状態に戻してもセグメント割当は残る。
5. 他人の区間、範囲外、重複ID、空ID、未知の削除IDは拒否。
6. 権限なしOpen、Parked、未登録Segment、欠損Entry、Revision不整合のOpenを拒否。
7. 内容辞書、音声割当の入力は一切書き換えない。
8. テストはプロセス内のみ。実際のGit、譜面ソース、書き出し、GUIには触れない。

## Windows RED実行

```powershell
git pull --ff-only
dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed — RED not established" }

$env:EXTREMEEDITOR_SEGMENT_DRAFT_ONLY = '1'
try {
    dotnet run --project .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release --no-build
}
finally {
    Remove-Item Env:\EXTREMEEDITOR_SEGMENT_DRAFT_ONLY -ErrorAction SilentlyContinue
}
```

**期待RED:** ビルドが通り、`SegmentTimeDraftEditor.Open` で以下の例外。

```text
System.NotImplementedException: RED: open an owned empty Segment as a tile-free audio-time draft.
```

コンパイルエラーはRED成立ではない。

RED成立をユーザーが報告した後に `0.0.579-prototype` のGREENで `Open`、`PlaceAnchor`、`RemoveAnchor` を実装し、古い空セグメントテストの退行も検証する。
