# Handover: work that needs a non-DevDrive checkout

Written 2026-09-05, against `00a1ad8`. Delete this file once both tasks below are done.

## Why this exists

`LiveAppSessionTests` cannot pass on `D:\DotNet\AI\RoseMCP` because that is a Dev Drive. The
tests register a loose UWP layout with `Add-AppxPackage -Register`
([LiveAppSessionTests.cs:2370](tests/RoseMcp.IntegrationTests/LiveAppSessionTests.cs#L2370)), and
Appx registration appears not to work from a hot-pluggable, file-backed VHD. The symptom is
activation failing with `0x8027025B` — "The app didn't start" — before the provider is ever
injected, so it looks like a XAML bug and is not one.

Everything else builds and runs here fine, including the native provider. Only the Appx half is
blocked.

## State of the repository

Clean at `00a1ad8`, nothing of mine outstanding. `#78` is merged (PR #86). Nothing below is
half-applied — read the note in Task 2 about a split I deliberately threw away.

## Task 1 — establish an integration baseline (do this first)

This is the blocking one. Nothing else can be trusted until it is done, because there is currently
**no known-good run to compare against.**

```powershell
Get-AppxPackage 'RoseMcp.ProbeApp.UwpClassic'   # expect: registered, after the run below
dotnet build RoseMcp.slnx
./tests/RoseMcp.IntegrationTests/bin/Debug/net10.0/RoseMcp.IntegrationTests.exe -class '*LiveAppSessionTests'
```

On the Dev Drive this gave **10 failures of 23**, in about 9.5 minutes. The four I captured:

| Test | Failure |
|---|---|
| `Launches_and_debugs_the_classic_uwp_probe_app` | `expected Ready, got Faulted: The app didn't start. (0x8027025B) (arch X64)` |
| `Arms_interactive_select_mode_on_the_classic_uwp_probe` | faulted session |
| `Clears_a_selection_whose_element_leaves_the_tree` | `Expected: Ready, Actual: Faulted` |
| `Reads_a_corner_radius_the_framework_renders_as_nothing` | `Assert.NotNull() Failure: Value is null` |

**Capture the full list this time** — I only have the tail of that run, and the comparison is the
whole point.

What to conclude:

- **All 10 pass at the new location** — confirmed environmental, and the Dev Drive limit is worth a
  line in `CLAUDE.md` under Commands so the next person does not spend an afternoon on it.
- **Some still fail** — those are real, and predate anything from this session; the tree is
  unmodified at `00a1ad8`.

I could not settle this here. I started a baseline run against stashed changes and it was
interrupted, so **treat "environmental" as the hypothesis, not the finding.** The reasoning behind
the hypothesis is that `0x8027025B` is raised by activation, which happens before the provider is
loaded, so the provider cannot be its cause — but `Reads_a_corner_radius...` does exercise the
provider, and a null there is equally well explained by the app never starting.

## Task 2 — #73, the provider split

Not started in the tree. I built it once, proved it, and threw it away — read the warning first,
because it is the most useful thing in this document.

### The warning: the input goes stale

I sliced the split out of `RoseMcp.Xaml.Uwp.Tap.cpp` when it was 2,498 lines and proved it correct
against that file. While the work was stashed, `main` was reset to `00a1ad8` and the same file
became **3,292 lines** — #11, #12 and #96 landed the live-edit and resource-dictionary work.

Had I committed, the split would have silently reverted about 800 lines of that. It would still
have compiled, because the shared headers were internally consistent; only a diff against the
current file showed it. So:

- **Re-derive every boundary from the file in front of you.** Never carry line numbers across a
  pull, a stash, or a session.
- **Verify with the identity proof below, against the version you actually sliced**, and re-run it
  immediately before committing.

### The method, which does work

The seam is smaller than it looks. `RoseTap` is almost pure `xamlOM.h` ABI, shared by UWP and
WinUI 3 verbatim; `RoseOverlay` is projection-bound but written entirely against six namespace
aliases, so **the same source serves both frameworks if the provider defines the aliases first.**
No templates, and the moved code needs no edits at all.

Target layout:

```
src/RoseMcp.Xaml.Tap/
  tap_channel.h      globals, Hex, Utf8, Log, Escape, Command, Tokens   no framework at all
  tap_diagnostics.h  Provenance, IsComposition, TreeNode                xamlOM: UWP + WinUI 3
  tap_overlay.h      overlay constants, RoseOverlay, g_overlay          needs the aliases
  tap_object.h       RoseTap, RoseTapFactory, the two DLL exports       needs CLSID_RoseTap
src/RoseMcp.Xaml.Uwp.Tap/
  RoseMcp.Xaml.Uwp.Tap.cpp   identity, projection includes, aliases, CLSID, then the four includes
```

`TreeNode` belongs in `tap_diagnostics.h`, **not** the channel — it holds `InstanceHandle`, an
xamlOM type. Putting it in the channel is a compile error, and a useful one: it is what proves the
channel is genuinely framework-free. `Command` is pure `std::wstring` and stays.

Include order in the provider, which reproduces the original exactly:

1. `RoseTapName` / `RoseTapLogFile` — before the channel, which logs through them
2. `tap_channel.h`, then `tap_diagnostics.h`
3. the `winrt/...` projection includes (after the ABI headers, as the original comment requires)
4. the six `namespace xaml = ...` aliases
5. `CLSID_RoseTap`
6. `tap_overlay.h`, then `tap_object.h`

Two lines of `Log` change so the log names its own provider — two providers writing adjacent work
folders must not write the same filename. Anchor the edit on text **containing no backslashes**;
escaping through a heredoc will silently eat them.

`build.ps1`, the `.def`, and every C# reference need **no change at all** — the DLL name, output
path and exports are untouched. That is the check that the split stayed inside its own boundary.

### Verifying it

```bash
strip() { sed 's://.*::' "$@" | sed 's/[[:space:]]*$//' | grep -v '^[[:space:]]*$'; }
git show HEAD:src/RoseMcp.Xaml.Uwp.Tap/RoseMcp.Xaml.Uwp.Tap.cpp | strip > /tmp/a.txt
cat src/RoseMcp.Xaml.Tap/tap_channel.h src/RoseMcp.Xaml.Tap/tap_diagnostics.h \
    src/RoseMcp.Xaml.Tap/tap_overlay.h src/RoseMcp.Xaml.Tap/tap_object.h \
    src/RoseMcp.Xaml.Uwp.Tap/RoseMcp.Xaml.Uwp.Tap.cpp | strip > /tmp/b.txt
diff <(sort /tmp/a.txt) <(sort /tmp/b.txt)
```

Sorting makes pure relocation cancel out, so the only differences should be the scaffolding: the
`#include` lines, one `#pragma once` per header, the two identity constants, and the two `Log`
lines. **Anything else is code you moved by accident.** When I ran this the accounting was exact —
1,643 code lines in, 1,656 out, 13 added, all named.

Then:

```powershell
./src/RoseMcp.Xaml.Uwp.Tap/build.ps1 -Platform x64 -Configuration Debug   # expect exit 0, no warnings
```

and re-run Task 1. #73's own success criterion is that **no test has to change**; if one does, the
split is wrong.

## Filed this session, not addressed

Against **1.1**, none WinUI-specific, all found while confirming #79:

- **#83** — locals are always `local_N`. `CorDebugSession.cs:898` hard-codes the name and never
  opens a PDB, while `rose_debug_evaluate` says "local names need a PDB", which reads as a
  condition you can satisfy.
- **#84** — a failed `rose_debug_detach` reported the target safe and the process was gone. Its
  error says to read the reason from `rose_debug_events`, but the same call closes the session.
  **Worth reproducing on `tests/DebugProbeTarget`** to see whether it is UWP/WinUI-specific.
- **#85** — breakpoint module inference takes the first namespace segment, so
  `RoseMcp.Tray.MainWindow.Refresh` looks for an assembly called `RoseMcp`. The docstring describes
  a broader rule than the code implements.

The **WinUI 3** milestone is #73–#77, with #78 and #79 closed. #73 gates the rest.

## The move itself

- **The worktree will break.** `.claude/worktrees/integration-tests` is registered at an absolute
  path (`git worktree list` shows it pinned to `106f8f1`). After moving, run `git worktree repair`,
  or `git worktree remove` it if it has served its purpose.
- **Restart the tray.** The running instance is `%LOCALAPPDATA%\BinaryVibrance\RoseMCP\tray`, and
  its warm worker holds the old solution path. It binds `127.0.0.1:5077`.
- **Re-point the MCP registration** if it names a path — `claude mcp list` will show it. The server
  itself lives under `%LOCALAPPDATA%`, not in the repo, so it likely needs nothing.
- There was an orphaned `RoseMcp.LiveApp` host from the worktree checkout during this session.
  Worth a `Get-Process RoseMcp.LiveApp` before and after; orphaned Roslyn and debug hosts are
  invisible until the machine is out of RAM.
