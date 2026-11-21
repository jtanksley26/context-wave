# Build version, tab names and front tab Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stamp every packaged build with date + commit and show it; stop tabs being named "System32"; make the front tab obvious in every theme.

**Architecture:** Version comes from MSBuild properties set by `tools\package.ps1`, read once in Core (`AppVersion`) and surfaced by the App (bottom bar, log) and through the pipe's status result to the Bridge. Tab naming is a Core rule in `ChatTabs`. Tab styling is App-only in `MainWindow.TabHeader`.

**Tech Stack:** .NET 8, WPF, xUnit, PowerShell packaging.

**Spec:** `docs/superpowers/specs/2026-11-21-version-and-tabs-design.md`

## Global Constraints

- Packaged version reads `<version>+<7-char commit>`, with `-dirty` after the commit when the tree had uncommitted changes, e.g. `2026.11.21+3635326`, `2026.11.21+3635326-dirty`.
- A build not made by `package.ps1` reads exactly `dev`.
- Install-facing `MdReader` / `md-reader` names stay unchanged.
- Test names are sentences with underscores; comments short and say why.
- `dotnet build ContextWave.sln` has 0 warnings; `dotnet test ContextWave.sln` passes (1 manual test skipped).
- Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

- The .NET SDK appends the full 40-char commit (`+<sha>`) to the informational version on its own; a dev build then carries a hash but must still read `dev`. → Task 1 test `A_build_that_was_not_packaged_reads_dev` covers `0.0.0-dev+<40 hex>`.
- A `-Version` with a suffix such as `2026.11.19-arch` must keep the suffix. → Task 1 test case `2026.11.19-arch+3635326…`.
- An older app with no version in its status reply talking to a new bridge must not report a mismatch as if the app's version were known. → Task 2 test `An_app_that_reports_no_version_is_shown_as_unknown`.
- A chat whose folder is `C:\Windows` itself, or System32 in different casing / with a trailing slash. → Task 3 theory cases.
- A diff with an empty or whitespace title must not give a blank tab name. → Task 3 test case.

---

### Task 1: AppVersion and build stamping

**Files:**
- Create: `Directory.Build.props` (repo root)
- Create: `src/ContextWave.Core/AppVersion.cs`
- Modify: `tools/package.ps1` (the two `dotnet publish` calls)
- Test: `tests/ContextWave.Tests/AppVersionTests.cs`

**Interfaces:**
- Produces: `public static class AppVersion { public static string Current { get; } public static string Format(string? informationalVersion); }`

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory]
[InlineData("2026.11.21+3635326abcdef0123456789abcdef0123456789", "2026.11.21+3635326")]
[InlineData("2026.11.21+3635326-dirty", "2026.11.21+3635326-dirty")]
[InlineData("2026.11.19-arch+3635326abcdef0123456789abcdef0123456789", "2026.11.19-arch+3635326")]
public void A_packaged_build_reads_its_version_and_short_commit(string informational, string expected)
    => Assert.Equal(expected, AppVersion.Format(informational));

[Theory]
[InlineData(null)]
[InlineData("")]
[InlineData("0.0.0-dev")]
[InlineData("0.0.0-dev+3635326abcdef0123456789abcdef0123456789")]
public void A_build_that_was_not_packaged_reads_dev(string? informational)
    => Assert.Equal("dev", AppVersion.Format(informational));

[Fact]
public void The_running_build_has_a_version() => Assert.False(string.IsNullOrWhiteSpace(AppVersion.Current));
```

- [ ] **Step 2: Run** `dotnet test tests/ContextWave.Tests --filter AppVersionTests` — expect a compile failure (no `AppVersion`).
- [ ] **Step 3: Implement.**
  - `Directory.Build.props`: `<Version>0.0.0-dev</Version>` in a PropertyGroup (applies to all four projects).
  - `AppVersion.Format`: null/empty, or the part before `+` is `0.0.0-dev` → `dev`. Otherwise keep the part before `+`; after `+`, keep the first 7 characters of the commit and keep a trailing `-dirty` if present.
  - `AppVersion.Current`: `Format` of `typeof(AppVersion).Assembly`'s `AssemblyInformationalVersionAttribute`, computed once.
  - `package.ps1`: compute `$commit = git -C $root rev-parse --short=7 HEAD`, add `-dirty` when `git -C $root status --porcelain` prints anything; pass `-p:Version=$Version -p:SourceRevisionId=$commit` to both `dotnet publish` calls. (When `SourceRevisionId` is set the SDK uses it instead of the full hash.)
- [ ] **Step 4: Run** the filtered tests — PASS; then the full suite.
- [ ] **Step 5: Commit** `feat: stamp packaged builds with date and commit`.

### Task 2: Show the version

**Files:**
- Modify: `src/ContextWave.Core/Protocol.cs` (`PipeResult`)
- Modify: `src/ContextWave.Core/ChatTabs.cs` (status result, ~line 166)
- Modify: `src/ContextWave.Bridge/ReaderTools.cs` (`Call`'s status formatting)
- Modify: `src/ContextWave.App/MainWindow.xaml` + `MainWindow.xaml.cs` (bottom bar), `src/ContextWave.App/App.xaml.cs` (log line at startup)
- Test: `tests/ContextWave.Tests/ChatTabsTests.cs`, `tests/ContextWave.Tests/ReaderToolsTests.cs`

**Interfaces:**
- Consumes: `AppVersion.Current` (Task 1).
- Produces: `PipeResult.Version` (`string?`, init) — the app's version in a status reply.

- [ ] **Step 1: Write the failing tests**
  - ChatTabsTests `The_status_reply_carries_the_apps_version`: status request → `Result.Version == AppVersion.Current`.
  - ReaderToolsTests `Status_shows_the_app_and_bridge_versions`: fake link returns a status result with `Version = AppVersion.Current` → output contains `version: {AppVersion.Current}` and no `differs`.
  - ReaderToolsTests `A_bridge_from_another_build_says_so`: result `Version = "2026.01.01+aaaaaaa"` → output contains `app version: 2026.01.01+aaaaaaa`, `bridge version: {AppVersion.Current}` and the words `different builds`.
  - ReaderToolsTests `An_app_that_reports_no_version_is_shown_as_unknown`: `Version = null` → output contains `app version: unknown` and `bridge version: {AppVersion.Current}`, and not `different builds`.
- [ ] **Step 2: Run** the new tests — FAIL.
- [ ] **Step 3: Implement.**
  - `PipeResult.Version`; ChatTabs status sets it to `AppVersion.Current`.
  - Bridge status text: one `version: X` line when equal; otherwise `app version: <X or unknown>` and `bridge version: Y` lines, plus `note: the app and the bridge are different builds; reinstall Context Wave.` only when the app's version is known and differs.
  - App: first `FileLog.Write` at startup in `App.xaml.cs` is `Context Wave {AppVersion.Current} started`. Bottom bar: a `TextBlock x:Name="VersionText"` docked right after `PositionText`, text `AppVersion.Current`, `Opacity` ≈ 0.55, small left margin, tooltip `Context Wave build`.
- [ ] **Step 4: Run** the full suite — PASS.
- [ ] **Step 5: Commit** `feat: show the build version in the app, log and status`.

### Task 3: No tabs named after the Windows folder

**Files:**
- Modify: `src/ContextWave.Core/ChatTabs.cs` (`ToolTab`, `SessionTab`, `TitleFor`)
- Test: `tests/ContextWave.Tests/ChatTabsTests.cs`

**Interfaces:**
- Produces: `internal static bool IsUsefulFolder(string? folder)` in ChatTabs (or a small static class in Core) — false for null/empty and for `Environment.GetFolderPath(SpecialFolder.Windows)` and anything inside it, case-insensitive, trailing separators ignored.

- [ ] **Step 1: Write the failing tests** (use the file's existing helpers for sending requests and finding tabs)
  - Theory `A_tool_call_from_the_windows_folder_does_not_name_the_tab` over `C:\Windows\System32`, `c:\windows\system32\`, `C:\Windows`: a `show_diff` with that `Folder`, a `Process`, and no title → tab title `"Claude"`, tab `Folder` null.
  - `A_diff_from_the_windows_folder_names_the_tab_after_its_title`: same with `Title = "Arch test diff"` → tab title `"Arch test diff"`.
  - `A_blank_diff_title_falls_back_to_Claude`: `Title = "  "` → `"Claude"`.
  - `A_later_reply_still_renames_a_tab_opened_from_the_windows_folder`: then a reply for that process's session with `Folder = @"D:\Projects\md5reader"` → title `"md5reader"`.
  - `A_real_folder_still_names_the_tab`: show_diff from `D:\Projects\md5reader` → `"md5reader"` (guards the existing behaviour).
- [ ] **Step 2: Run** — the Windows-folder cases FAIL (title is `System32`).
- [ ] **Step 3: Implement.** Requests whose folder is not useful are treated as having no folder (not stored on the tab, not used for the title). In `ToolTab` a new tab's title is `TitleFor(folder)` when the folder is useful, else the trimmed `request.Title` if non-blank, else `"Claude"`. `SessionTab` keeps a non-default existing title when the reply brings neither a name nor a useful folder.
- [ ] **Step 4: Run** the full suite — PASS.
- [ ] **Step 5: Commit** `fix: never name a chat tab after the Windows folder`.

### Task 4: The front tab stands out

**Files:**
- Modify: `src/ContextWave.App/MainWindow.xaml.cs` (`TabHeader`), and `MainWindow.xaml` only if the strip needs a margin change.

**Interfaces:** none (App-only).

- [ ] **Step 1: Implement in `TabHeader`.**
  - Front tab: top border 3px using `AccentBrush` (keep 1px sides in `ControlBorderBrush`; use an inner/outer Border pair if one Border cannot have two brushes), background `PageBrush`, bottom margin `-1` so it covers the strip's bottom line and joins the page, title SemiBold at full opacity.
  - Other tabs: background `ChromeBrush`, 1px top in `ControlBorderBrush` plus 2px top padding so titles line up, title `Opacity` 0.65; on `MouseEnter` opacity 1 and background `ControlHoverBrush`, restored on `MouseLeave`.
  - Use `SetResourceReference` for every brush so theme changes apply live (as today).
- [ ] **Step 2: Build** `dotnet build ContextWave.sln` — 0 warnings; full suite passes.
- [ ] **Step 3: Commit** `feat: make the front tab stand out`.
- Visual check in light, dark, dimmed and high contrast is done by the controller in the app after merge.

---

## Execution

Per the user's standing preference, run with Sonnet subagents. Tasks 1 and 2 go to one agent (2 needs 1). Task 3 and Task 4 each go to their own agent in parallel, each in its own worktree; the controller cherry-picks onto `version-and-tabs`, runs the suite, packages, installs and checks in the app.
