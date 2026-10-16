# ExtremeEditor Together — Segment境界を守る時間検索 Core RED（2026-10-16）

## 背景

- [時間区間先行の設計決定](together-time-first-design.md) が基準。
- `0.0.577-prototype` 空区間のContentEntry GREEN: ユーザー確認済み。
- `0.0.579-prototype` 空区間に曲中サンプル位置のTileAnchorを追加・削除するGREEN: ユーザーから `pass` を確認。
- 今回 `0.0.580-prototype`: **時間検索のCore REDだけを実装**。WindowsでRED成立後、別バージョンでGREENを行う。

## 最重要条件

ユーザーの指摘: **区切りを含まない、単調性が保証された検索範囲でのみ二分探索を使う。**

- 音楽上のセグメント割当は共通デコード音源のサンプル単位の半開区間 `[StartSample, EndSampleExclusive)`。タイルが一枚もないことは合法。
- まず時刻から所属するActive Segmentを探す（ソート済み割当の二分探索）。
- 対象Segment内だけで、時刻順にソートされた配置アンカーを `lower_bound` / `upper_bound` で探す。
- 検索対象の空Segmentと、どの割当にも属さないGapは区別する（前者はSegmentIdあり・ヒット0、後者はSegmentIdなし）。
- 範囲検索は**1つのSegmentに完全に収まる半開区間**だけ許す。境界・未割当領域をまたぐ場合は自動統合せず拒否する。全区間検索が必要なら呼出側が境界で明示分割する。
- ちょうど境界 `b` のタイルは左 `[a,b)` には含めず、右 `[b,c)` に所属する。
- 同一時刻の複数のアンカーをすべて返し、挿入順を維持する。
- 入力に時刻の逆転、重複TileId、古い割当範囲・担当者、未提出のActive Draftなどがあれば**探索を開始する前に拒否**する。勝手に並べ替えて矛盾を消さない。
- 索引をBuild時に固定し、後から元の編集者のDraftが更新されても検索結果は変わらない。公開する検索結果は外部から変更できない。

**これは `SegmentDraftTileAnchor` の索引**であり、`LevelDocument` にある実際のADOFAI床、BPM変更、Pause、Twirlなどの時刻の正しさを証明しない。後続の実床検索は `TimingMap` / `PlaybackClock` からタイミングを再計算し、区切りを跨ぐ非単調部分を分離する別テストが必要。

## RED API

`src/ExtremeEditor.Core/SegmentDraftTimeSearchIndex.cs`

- `Build(timeline, drafts)`: ソート済みActive割当と各Segment内の単調な配置アンカーを検証・コピーして不変索引を作成。Draft未提出・範囲不一致・owner不一致・revision不整合は拒否。今回の索引対象は**全Activeが明示的なEmptyから生成されたDraft**という範囲に限定する。Parkedは現行検索対象外。
- `FindAt(audioSample)`: 該当するActiveのSegmentIdと、その音声サンプル時刻に一致する配置アンカー全件を返す。Gapなら `SegmentId = null`。
- `FindRange(segmentId, startSample, endSampleExclusive)`: 指定Segment内だけの `[start,end)` のアンカーを返す。別のSegmentに跨ぐクエリや空・逆順の範囲は拒否。
- `SegmentDraftTimeMatch` は、所属セグメントの有無とヒット0件を区別。

## 主要REDケース

```text
A [0s,40s)    Gap [40s,50s)     B [50s,80s)       C [80s,120s)
タイル0枚      割当なし           50s,60s,60s,79s   80s

FindAt(0s)  → A, タイル0枚
FindAt(40s) → 所属なし
FindAt(50s) → B, 50sの1件
FindAt(60s) → B, 60sの2件（順序を維持）
FindAt(80s) → C, 80sの1件（Bには入らない）
FindAt(120s)→ 所属なし

FindRange(B,[50s,80s)) → Bに属する4件
FindRange(B,[60s,61s)) → 同時刻2件
FindRange(B,[79s,81s)) → REJECT（Cへ跨ぐ）
FindRange(B,[49s,51s)) → REJECT（割当前へ跨ぐ）

Build: Missing Draft / owner mismatch / stale range / unsorted timestamps /
duplicate TileId / authored revision falsely treated as draft → REJECT
```

## 非目標

- 本家ADOFAIタイルの正確な時刻検索、BPMやPauseによる大域的非単調性の分析、グリッド表示
- 複数セグメントをまたぐ自動のRange検索
- SegmentDraftの永続化・Git同期・UI編集・公開・完成譜面Export
- Source `.adofai` の読み書き、CI、自動GUI操作、既存mainの変更
- 保留中のPlanetRadius/PositionTrackの検証改修

## Windows REDコマンド

```powershell
git pull --ff-only
dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed (RED not established)" }

$env:EXTREMEEDITOR_SEGMENT_SEARCH_ONLY = '1'
try {
    dotnet run --project .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release --no-build
}
finally {
    Remove-Item Env:\EXTREMEEDITOR_SEGMENT_SEARCH_ONLY -ErrorAction SilentlyContinue
}
```

**期待RED:** `SegmentDraftTimeSearchIndex.Build` で

```text
System.NotImplementedException: RED: build a boundary-aware time index for independently authored Segment drafts.
```

コンパイルエラー／他のテストの失敗はRED成立ではない。Windowsで期待のNotImplementedExceptionを確認したらユーザーが `成立` を返し、その後GREENを実装する。
