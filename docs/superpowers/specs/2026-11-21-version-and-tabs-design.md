# Build version, chat tab names and the front tab

Three small changes to Context Wave, done together on one branch.

## 1. A build version you can read off a broken install

**Why:** when something breaks, it must be plain which build is installed. Several dated builds of
the same day have gone out (`-pr7`, `-arch`), so a date alone does not tell them apart.

**Approaches**
- **A. Package script stamps date + commit (picked).** `tools\package.ps1` already chooses the
  version (a date, or `-Version`). It also passes the short git commit, with `-dirty` added when the
  tree has uncommitted changes, so the app carries e.g. `2026.11.21+3635326`. No file to bump.
- B. A hand-bumped number in one file. Rejected: two builds between bumps look the same, the very
  confusion this is meant to end.
- C. Compute it from git in every build, dev ones too. Rejected: every build then needs git, and it
  adds MSBuild logic for little over A.

**Design**
- Every project gets the same version from a shared `Directory.Build.props`, `0.0.0-dev` by default.
  `package.ps1` overrides it with `-p:Version=<version>` and `-p:SourceRevisionId=<short commit>[-dirty]`.
- Core gains `AppVersion.Current`, read from the assembly's informational version and shortened to a
  7-character commit. A build that was not packaged reads `dev`.
- Shown in four places:
  - faint text at the right end of the bottom bar (next to the position);
  - the first line the app writes to each day's log: `Context Wave <version> started`;
  - the `status` reply, so Claude can say which build is running;
  - the bridge's `status` tool adds its own version, and says so when it differs from the app's
    (a half-finished install).

## 2. Chat tabs named "System32"

**Cause:** Claude starts the bridge with `C:\Windows\System32` as its working folder. A chat's first
tool call (such as `show_diff`) opens its tab before any reply has named it, titled after that
folder. Only a reply through the Stop hook renames it, and some chats never send one.

**Design**
- A folder inside the Windows folder says nothing about the chat, so ChatTabs treats it as no folder:
  it is neither used for the title nor kept as the tab's folder (which shows in the tooltip).
- A tab opened by a tool call with no usable folder is named after the diff's title when the call
  has one, otherwise "Claude". A later reply with a chat name or a real folder renames it as today.
- Tested in Core (ChatTabsTests): a show_diff from System32 with and without a title, and a later
  reply renaming the tab.

## 3. Seeing which tab is in front

**Why:** in dark themes the front tab differs from the others only by `#1e1e1e` against `#2b2b2b`
and semi-bold text; with several tabs open it is hard to tell which one is showing.

**Design** (`MainWindow.TabHeader`)
- The front tab has a 3px bar across its top in the highlight colour (`AccentBrush`), so it follows
  the colour the user picked and every theme, high contrast included.
- The front tab joins the page: page background and no line beneath it.
- The other tabs' titles are dimmed, and brighten on hover.
- The unread dot and close button stay as they are.

```
 ▔▔▔▔▔▔▔▔▔▔▔▔▔▔
 ● md5reader  × │  notes.md  ×   Claude  ×
━━━━━━━━━━━━━━━━┷━━━━━━━━━━━━━━━━━━━━━━━━━━━
 (page)
```

## Not in scope
- No About dialog or update check.
- Install-facing `MdReader` names stay as they are.

## Left to the builder
- Exact dim level and hover colour, checked in light, dark, dimmed and high contrast.
- How the commit reaches the informational version (the .NET SDK may already add the full commit
  hash; shorten it rather than fight it).
