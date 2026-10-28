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

- #111 — Standard WPF test entry now invokes 10 existing video export/background suites; corrected the streaming encoder contract to allow the required atomic MP4 temporary path. **Implemented, verification pending** (Windows .NET 8 and CI not run in this environment).
