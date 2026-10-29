# AryanSenthil/Konnect: live-editor fork

A fork of [mixelpixx/Konnect](https://github.com/mixelpixx/Konnect) v0.13.0 (AGPL-3.0; upstream was
archived 2026-10-25). One change: **Konnect can edit a schematic while KiCad's Schematic Editor has it
open**, the way Fusion's MCP edits an open design.

## Why upstream refuses

KiCad writes `~<file>.kicad_sch.lck` while the Schematic Editor has a schematic open. The editor keeps
its own copy in memory and never notices changes on disk, so its next save silently overwrites
anything written underneath it. KiCad 10's IPC API cannot save or revert a schematic (KiCad 11 adds
`SaveDocument`/`RevertDocument` for schematics), so upstream fails closed and asks you to close the
editor.

## What the fork does

`tools/kicad-editor-bridge/` is a small Windows program (C#, built with the .NET Framework compiler
that ships with Windows) that drives the open editor through its own native File menu: `status`,
`save` (File > Save, wait for the `*` to clear) and `reload` (File > Revert, answer KiCad's
"Confirmation" dialog). It sends menu commands, not keystrokes, so it needs no focus and works while
you're in another window. It only clicks a dialog that appeared because of its own Revert.

`crates/konnect-sexp/src/writer.rs` gains `set_editor_lock_hook`: when a schematic's KiCad lock is
present, an installed hook may allow the write. With no hook, behaviour is upstream's.

`crates/konnect/src/live_editor.rs` installs that hook at startup (Windows, when
`kicad-editor-bridge.exe` sits beside `konnect.exe`):

1. First write to a locked schematic: save the user's unsaved work, check no dialog is open, allow.
2. Further writes in the burst: allowed while the editor stays clean.
3. `KONNECT_LIVE_EDITOR_DEBOUNCE_MS` (default 1500) after the last write: File > Revert, so the open
   editor shows the result.
4. If the user edits in KiCad between a write and the reload, nothing is reverted and the next write
   is refused, telling them to choose (File > Revert keeps the agent's edit, Ctrl+S keeps theirs).

Refusals carry a reason through every lock message (`schematic_lock_message`): a dialog is open, a
conflict, or no editor window matches the lock (stale lock). `KONNECT_LIVE_EDITOR=0` restores
upstream behaviour; `KONNECT_EDITOR_BRIDGE` overrides the bridge path.

## Limits

- Windows only. Matches the editor by window title (`<root sheet> — Schematic Editor`) and English
  menu labels (File / Save / Revert).
- Hierarchical sub-sheet files have no KiCad lock of their own, so writes to them are not
  coordinated (same as upstream).
- PCB editing is unchanged: it already goes through KiCad's live IPC API.

## Build

`cargo build --release -p konnect` as upstream (MSVC or the GNU target with MinGW, protoc and CMake),
then compile the bridge:

```
%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize /out:kicad-editor-bridge.exe tools\kicad-editor-bridge\KicadEditorBridge.cs
```

and put `kicad-editor-bridge.exe` beside `konnect.exe`. `tools/kicad-editor-bridge/test-bridge.ps1`
proves the mechanism on a demo copy: an outside edit is lost without the reload and kept with it.
