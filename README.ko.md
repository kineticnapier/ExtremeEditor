# ExtremeEditor

[English](README.md) | [日本語](README.ja.md) | [한국어](README.ko.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md)

ExtremeEditor는 매우 큰 A Dance of Fire and Ice(`.adofai`) 레벨을 위한
Windows용 독립 실행형 에디터입니다. 제품 애플리케이션은 WPF와 네이티브 C++
뷰포트로 구성되며 Unity, UMM, Harmony 또는 Workbench 런타임에 의존하지 않습니다.

현재 버전은 `0.0.484-prototype`이며 기본 개발 브랜치는 `main`입니다.
WinForms에서 WPF로의 이전과 기존 WinForms 호스트 제거는 완료되었습니다.

## 릴리스

현재 공개 빌드는 **알파 품질**입니다. 첫 공개 릴리스 계열은
`v0.1.0-alpha.*`입니다.

릴리스 ZIP은 Windows x64용 self-contained 빌드로 배포하는 것을 전제로 하므로
.NET 런타임을 별도로 설치할 필요가 없습니다. ExtremeEditor는 ADOFAI 게임
에셋을 재배포하지 않으며, 에셋 설정은 합법적으로 설치된 로컬 게임에서 읽어옵니다.

## 요구 사항

- Windows
- .NET 8 SDK
- CMake
- Windows SDK 및 x64 지원이 포함된 Visual Studio C++ 빌드 도구
- 게임 에셋을 추출할 경우 합법적으로 설치된 ADOFAI

## 실행

기본 실행 명령:

```powershell
.\run.ps1
```

`run.ps1`은 `src/ExtremeEditor.Wpf/ExtremeEditor.Wpf.csproj`를 실행합니다.
Release 빌드를 직접 실행하려면:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Wpf
```

`--` 뒤에 레벨 경로를 전달할 수 있습니다:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Wpf -- "C:\path\to\level.adofai"
```

파일을 지정하지 않으면 작은 합성 레벨이 열립니다. `Open` 명령으로
`.adofai` 파일을 불러올 수 있습니다.

주요 조작:

- 마우스 휠: 포인터 기준 확대/축소
- 가운데 또는 오른쪽 드래그: 화면 이동
- 왼쪽 클릭: 타일 선택
- Alt + 왼쪽 드래그: 선택한 타일 범위를 PositionTrack으로 이동
- `F`: 전체 레벨 맞춤
- `O`: 레벨 열기
- `[` / `]`: 주 선택 타일을 -15° / +15° 회전
- `Ctrl+S`: 저장
- `Esc`: 선택 해제

## 제품 아키텍처

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

편집 의미론, 레벨 문서, 타이밍, undo/redo, 저장/불러오기, 오디오, 선택 상태는 C#이
관리합니다. C++은 제품 뷰포트 렌더링, 컬링, 히트 테스트, 카메라 조작 및 네이티브
뷰포트 입력을 담당합니다. 두 계층 사이에는 불변 네이티브 스냅샷을 평평한 P/Invoke
경계를 통해 전달합니다.

`src/ExtremeEditor.Wpf/LevelViewport*.cs`는 이전 WPF temporal/raster 렌더링 경로의
비교 및 회귀 테스트를 위해 의도적으로 남겨져 있습니다. `WpfPlaybackPresenter`,
`WpfFloorRenderer`, `WpfIconRenderer`, `WpfLevelLoader`, raster-cache 클래스와
`SpatialGridIndex`는 이러한 테스트와 공통 비제품 책임을 지원합니다. 이는 두 번째
제품 뷰포트가 아니며, `MainWindow`는 `NativeLevelViewport`만 호스트합니다.

제거된 WinForms 호스트는 역사적 코드일 뿐 시작, 빌드, 제품 렌더링에 포함되지 않습니다.

## 에셋 설정

WPF 앱은 독립 실행형 `ExtremeEditor.AssetExtractor.exe`를 사용합니다.
WPF 프로젝트는 extractor 라이브러리를 직접 참조하지 않으며, 빌드 시 실행 파일을
생성·복사하고 에셋 설정 시 하위 프로세스로 실행합니다.

에셋 설정 절차:

1. 저장된 ADOFAI 설치 경로 확인
2. Steam 라이브러리와 ADOFAI app manifest 검색
3. 자동 검색 실패 시 폴더 선택 제공
4. 유효한 게임 경로 저장
5. `%LocalAppData%\ExtremeEditor\AssetCache`에 에셋 추출

기존 AssetProbe/수동 가져오기 방식은 폐기되었으며 지원하지 않습니다.

## 빌드 및 테스트

제품 앱 빌드:

```powershell
dotnet build src/ExtremeEditor.Wpf/ExtremeEditor.Wpf.csproj -c Release --nologo
```

managed 회귀 테스트:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Wpf.Tests
dotnet run -c Release --project src/ExtremeEditor.Core.Tests
dotnet run -c Release --project src/ExtremeEditor.Audio.Tests
```

`build/native-renderer`가 생성되어 있다면 native 테스트:

```powershell
ctest --test-dir build/native-renderer -C Release --output-on-failure
```

WPF 회귀 테스트에는 기존 WinForms 호스트가 복원되거나 비교용 WPF 뷰포트가 제품
`MainWindow`로 되돌아오는 것을 막는 계약 테스트가 포함되어 있습니다.

## 벤치마크 및 진단

Headless Core 벤치마크:

```powershell
dotnet run -c Release --project src/ExtremeEditor.Bench -- "C:\path\to\level.adofai"
```

첫 로드 및 오디오 진단 기록:

```powershell
$env:EXTREMEEDITOR_DIAGNOSTICS = "$PWD\first-load.log"
dotnet run -c Release --project src/ExtremeEditor.Wpf -- "C:\path\to\level.adofai"
```

`EXTREMEEDITOR_DIAGNOSTICS=1`을 사용하면 임시 디렉터리의 기본 진단 로그에 기록합니다.
진단은 기본적으로 비활성화되어 있습니다.

## 호환성 및 성능 정책

- 제품 뷰포트에서 최소 60 FPS를 목표로 함
- 고밀도 차트에서도 가까운 타일은 ADOFAI 스타일 외형을 유지함
- 타일을 점으로 바꾸는 최적화는 허용하지 않음
- 오디오 동기화 정확도를 유지함
- ADOFAI DLL/디컴파일 구현에서 관찰한 동작을 호환성 기준으로 삼음
- 대형 차트 작업량은 가능한 한 현재 보이거나 시간상 관련된 집합에 비례하게 유지함

ExtremeEditor는 아직 원본 에디터의 byte-for-byte 대체품이 아닙니다.
호환성 작업에는 집중된 회귀 테스트를 추가하는 것을 원칙으로 합니다.

## 로드맵

완료:

- Native-only 제품 뷰포트
- 기존 WinForms 호스트 제거
- README 및 마이그레이션 문서 정리
- UI 정리와 진단 표시 간소화

`docs/superpowers` 아래 문서는 과거의 설계 및 구현 스냅샷입니다.
사용하기 전에 `docs/README.md`를 확인하세요. 해당 체크리스트와 명령은 현재 로드맵이 아닙니다.
