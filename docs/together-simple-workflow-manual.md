# Together simplified workflow — Windows manual check

Automated tests do not drive the GUI. Before release, verify this flow by hand:

1. Open Together and choose **共同編集を作成**.
2. Select a real audio file. Confirm the waveform appears and the section list
   contains one section covering the complete song.
3. Seek to two positions and press **現在位置で区切る** at each. Confirm three
   adjacent section bands remain with no visual gap.
4. Select each row, enter a different participant name, and press
   **担当者を設定**. Exercise **次の区間と結合** and
   **境界を現在位置へ移動**, then restore the desired three sections.
5. Choose an empty destination folder and create the collaboration. Close and
   reopen it. Confirm all empty sections, times, and participants return.
6. Open a second checkout of the collaboration. Select a participant name and
   confirm only that person's rows permit tile editing; other rows are visible
   but read-only.
7. Place a tile, save/share it, receive it in the other checkout, edit a
   different owner's section, and exchange the change back.
8. Confirm Space play/pause, click seek, Ctrl+wheel zoom, wheel pan, section
   loop playback, and the independently moving playhead.
9. Complete all sections, export `.adofai`, reload it in ExtremeEditor, then
   verify timing and playback in the stock game.

Automated Windows commands:

```powershell
dotnet build .\src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj -c Release --no-restore
cmd /c "set EXTREMEEDITOR_TOGETHER_SECTION_PLAN_ONLY=1&& dotnet .\src\ExtremeEditor.Core.Tests\bin\Release\net8.0\ExtremeEditor.Core.Tests.dll"
cmd /c "set EXTREMEEDITOR_TIME_H_E2E_ONLY=1&& dotnet .\src\ExtremeEditor.Core.Tests\bin\Release\net8.0\ExtremeEditor.Core.Tests.dll"
dotnet build .\src\ExtremeEditor.Wpf.Tests\ExtremeEditor.Wpf.Tests.csproj -c Release --no-restore
cmd /c "set EXTREMEEDITOR_TOGETHER_SIMPLE_UI_ONLY=1&& dotnet .\src\ExtremeEditor.Wpf.Tests\bin\Release\net8.0-windows\ExtremeEditor.Wpf.Tests.dll"
cmd /c "set EXTREMEEDITOR_TOGETHER_TIMELINE_ONLY=1&& dotnet .\src\ExtremeEditor.Wpf.Tests\bin\Release\net8.0-windows\ExtremeEditor.Wpf.Tests.dll"
cmd /c "set EXTREMEEDITOR_TOGETHER_TIME_E2E_ONLY=1&& dotnet .\src\ExtremeEditor.Wpf.Tests\bin\Release\net8.0-windows\ExtremeEditor.Wpf.Tests.dll"
```
