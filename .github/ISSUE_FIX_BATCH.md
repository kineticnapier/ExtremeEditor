# Consolidated Issue Fix Batch

This branch is the single working branch for accumulating verified fixes to open issues in ExtremeEditor.

## Workflow

- Before each change, check open/closed issues and existing pull requests to avoid duplicate work.
- Implement fixes on this branch and add focused regression tests.
- Reference each addressed issue in the consolidated pull request and relevant commit messages.
- Do not claim an issue fixed until code and tests support it.
- Do not automatically merge the pull request.
- Preserve the repository's existing behavior and compatibility except for explicitly documented fixes.

## Implemented issues

- #111 — Standard WPF test entry now invokes 10 existing video export/background suites; corrected the streaming encoder contract to allow the required atomic MP4 temporary path. **Implemented; CI verified** (GitHub Actions run 40680819674: WPF/Core/Audio/native tests all passed on Windows). Focused-only environment-variable run not separately executed.

- #113 — Reload the native level image table when an active CustomBackground bgImage changes, then re-upload the playback timeline. Added resolver and source-wiring regressions. **Implemented; CI pending; visual GUI verification pending**.
