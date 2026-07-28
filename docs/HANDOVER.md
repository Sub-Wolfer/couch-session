# Couch Session — handover, 26 July 2026 (second pass)

All four open tasks from the previous handover are done. Unlike last time, **every change in this
pass was compiled**: `csc` was run against the full source with the framework assemblies taken from
`bin/Release/net8.0-windows/win-x64/`, and the tree builds with **0 errors and 0 warnings**.

How that was possible without the SDK, in case it is needed again: nuget.org is unreachable from the
sandbox, so the Windows Desktop targeting pack cannot be restored and `dotnet build` fails at
restore. The way around it is to skip MSBuild entirely — reference `Microsoft.NETCore.App.Ref` from
the installed SDK, add `System.Windows.Forms.dll`, `System.Windows.Forms.Primitives.dll`,
`System.Drawing.Common.dll`, `Accessibility.dll`, `Microsoft.Win32.SystemEvents.dll` and
`System.Windows.Extensions.dll` from the project's own build output, run the Json and Regex source
generators as `-analyzer:`, and supply a two-line stub for `ApplicationConfiguration.Initialize`
(which the WinForms generator would normally emit). That is a full semantic check of every file.

Standing rules:

- **Controller-first applies to the session, not to the settings window.** Once the app has switched
  to the TV the user is on a pad, and anything needing a mouse or keyboard from there is a failure —
  that covers the session-end prompt, the toasts, the hotkeys, Big Picture and the guide button. The
  settings window is a different thing: it is opened at the desk, with a mouse and a keyboard, and it
  is **not** being optimised for a controller or for TV viewing distance. Stated by the user on
  26 July, and it retires a whole class of suggestion — TV-sized type, huge hit targets, pad
  navigation of settings. Do not reintroduce them.
- Never assume. Research and prove it — the log file is usually the answer.
- Nothing may risk an anti-cheat ban: no injection, no hooking, no reading another process's memory,
  no synthetic gamepad input. Reading a HID device is fine; that is what SDL does.

---

## Settled: direct HID polling works

The previous handover left this untested and it is the single most consequential thing in the log.

`couchsession.log` says, on every app start from 00:52 onwards:

> `Direct controller polling works on this hardware; the input stream can be released when idle
> without losing the buttons.`

So `HidPoll`'s `ReadFile` fallback reads the DualSense. `HidPoll.ProvenOn` returns true, and the
idle trade-off the whole class exists to remove is removable.

**It has not actually been removed yet, and the log shows why that matters.** The release/resume
cycle is still thrashing:

```
01:41:21  Controller input released so the display can sleep
01:41:39  Input seen 18281ms after the pad was released; taking it back so a button press is not missed.
01:41:39  Controller input resumed.
```

That pattern repeats every couple of minutes, sometimes with the pad taken back 281ms after being
let go. Every one of those `Idle check` lines also reads `cursor 0s` — Windows' last-input clock is
being reset constantly, which is the thing that stops the display sleeping in the first place.

The next piece of work is therefore: now that `ProvenOn` is true, stop taking the subscription back.
The "input seen, taking it back" path exists to cover a released pad being invisible, and on this
hardware it is no longer invisible. Before changing it, find what is pinning `cursor` at 0s —
`PointerControl`/`MouseInjector` are the obvious suspects, and `HidPad` already carries a
stick-centring fix for exactly that class of bug.

Still never established: whether the pad is on a USB cable. If it is, it will never power off, and
that is charging rather than a fault. Ask before investigating further. Steam's own controller
power-saving timeout (Settings → Controller, default ~15 min) is still worth checking is not set to
"never".

---

## Done this pass

### 1. HDR is remembered for non-Steam games

`RememberHdrForRunningGame` began with `SteamRunningAppId()` and returned null when it was zero, so
turning HDR on during an Epic, GOG or Browse-added game remembered nothing.

`HdrCoordinator.RunningGame()` now answers in three steps, most exact first: Steam's RunningAppID,
then a game the watcher is already following, then `GameWatcher.MatchRunning(candidates)` — a new
public helper that reuses the watcher's own snapshot / path-lookup / `IsInside` machinery to find
which candidate has a process running inside its folder. Deepest folder wins, so a game inside a
library root beats the root.

Candidates are the discovered library plus whatever is already on the HDR list — the second is not
redundant, because Browse accepts a folder no launcher knows about and that game still has to be
recognisable in order to be dropped from the list again.

`PollArmed` (the 45-second window after arming HDR by hotkey) now uses the same route, against a
candidate list snapshotted once at arm time rather than rebuilt every 600ms. `_armBaselineAppId`
became `_armBaselineKey`.

Two "Steam games only." sentences were removed from `Words.cs`, since they are no longer true.

### 2. One three-way HDR choice instead of two toggles

`Off / Per game / For the whole session`, as a `Dropdown` through the existing `AddPick` pattern.

**No config migration was needed**, contrary to what the previous handover expected. `AppConfig`
gained a `[JsonIgnore] HdrSwitching` property that reads and writes the two existing booleans:

| Mode          | `AutoHdrEnabled` | `HdrForWholeSession` |
|---------------|------------------|----------------------|
| Off           | false            | false                |
| Per game      | true             | false                |
| Whole session | true             | true                 |

Whole-session deliberately leaves `AutoHdrEnabled` set. It is what starts the process watcher, and
`GameActivityChanged` — which Big Picture's front guard listens to — comes from there. Clearing it
would have quietly taken that away. Every existing reader of the two booleans is untouched, old
configs load, and an older build still reads a config written by this one.

**A real bug fell out of this.** Two places claimed the game list still governs HDR at the desk when
whole-session is on:

- `Words.HdrWholeSessionWhy`: "it still decides what happens for games played at your desk"
- the comment above `_gameList.Enabled` in `UpdateDependentStates`

`EngageFor` returns on `if (_config.HdrForWholeSession)` unconditionally — not "during a session" —
so per-game HDR never fires in that mode, desk or not. Both claims were false. The three-way makes
the modes genuinely exclusive, which is what the code already did, and both sentences are gone.

The two home-page tiles became one, because they could previously read "on · 12 games" and
"whole session: yes" simultaneously, describing a state the app has never been in.

### 3. The Native HDR badge is clickable

`GameListView` gained a `BadgeClicked` event and one `OverBadge(x)` hit test shared by the click
handler, the pointer shape and the tooltip, so the area that looks, behaves and explains itself as
clickable cannot drift apart. Handled before the checkbox test and returned from outright, so
correcting a tag never also ticks the row or moves the range anchor; excluded from
`OnMouseDoubleClick` for the same reason.

`SettingsForm.CycleTag` flips what the badge says on every click, so the gesture always answers.
When the flip lands on what the lookups already thought, the manual tag is dropped rather than
restated — an answer agreeing with the automatic one is not worth marking as yours, and the star
clears with it. The right-click menu is unchanged and remains the way to force a tag back to
automatic outright.

**Bug found while doing it:** `OnMouseMove` returned early on `if (index == _hovered)` *before*
calling `UpdateTagTip`, so the manual-tag tooltip only ever appeared if you arrived at the badge by
crossing a row boundary — moving sideways onto it did nothing. The call moved above the guard. The
tooltip now shows on every row, not only hand-tagged ones, because a pill that can be clicked has to
say so on the rows nobody has corrected yet.

### 4. The HDR page says whether the displays can do HDR

**The previous handover was wrong about this one.** It said "There is no capability check anywhere
in the codebase — this needs new CCD interop (`DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO`)". That
interop already exists: `HdrControl.StatusOf` has used `GetAdvancedColorInfo = 9` all along, and
`HdrStatus.Supported` is what the hotkey's "The main display does not support HDR." message reads.
Nothing was missing except the page saying so. No new interop was written.

What was added: `HdrControl.StatusFor(monitorDevicePath)`, and `DisplayManager.ActivePathFor` /
`CurrentPrimaryDevicePath`, so support can be read for a *named* display rather than only for
whichever is primary.

That distinction is the whole point. Settings are read at a desk, where the primary display is the
monitor — so a warning about "your display" would routinely be about the wrong screen, and wrong in
the direction that makes a working feature look broken. The note names both, on the user's explicit
instruction:

> **Dell U2720Q** (main display now): no HDR support, so nothing on this page will change it.
> **LG C2** (couch display): HDR supported.

Collapses to one line when the television is already the main display. A display that is not
attached reads "not connected, so it cannot be checked" — never "no HDR support", because it has
said nothing about HDR and nobody has been able to ask it. Refreshed on every entry to the page,
since a television that was off when the window opened is the ordinary case.

### 5. Every settings page can be reset on its own

The footer's "Reset all settings" was the only undo in the window, which made it the right button
about once — after making a mess of everything — and the wrong one every other time. Someone who has
spent ten minutes getting their television and audio right and then tangled up the mouse settings
should not have to choose between living with it and starting over.

A low-emphasis **Reset this page** now sits at the foot of Display & Audio, HDR Switching,
Controller, Hotkeys, Performance and General. Home and About own no settings, so they do not have
one. Each confirms first, defaults to No, and names what goes *beyond* the switches — the chosen TV
and audio devices, the ticked HDR game list, the trigger controller, the shortcut buttons — because
those are the parts nobody expects a page-level reset to take with it.

`SettingsOn(int page)` is the list of what each page owns, written out by hand rather than derived
from the controls. A page and a setting are not one-to-one: some settings are stored as two
properties, some controls read Windows rather than the config, and some properties are bookkeeping
that no page shows. A list that can be read start to finish beat a clever one.

Four things it has to get right, each of which was a bug caught in review before it shipped:

- **Collections are cleared by hand, never copied.** `Defaults` is a `static readonly AppConfig`, so
  `property.SetValue(Config, property.GetValue(Defaults))` on a `List` or `Dictionary` would hand the
  live config the static default's own instance and every later edit would write into the defaults.
  Only value types, enums and strings go through reflection.
- **Save happens after the reload, not before.** `LoadFromConfig` does not simply mirror the defaults
  onto the page: `GuessUnset` fills the emptied display and audio pickers with its best guess, which
  is the point of clearing them. Saving first left that guess on screen only — the file still said
  nothing was chosen, so the pad shortcut answered "Couch Session is not set up yet" while the window
  plainly showed a television picked. It now calls `Save(silent: true)`, which gathers from the
  controls, so what is on disk is what is on screen.
- **`StartWithWindows` is not in the config file.** It lives in Task Scheduler; the property is a
  mirror, and the toggle is loaded from `StartupRegistration.IsEnabled()`. Writing the default to the
  config alone left the switch showing off after a reset that claimed to turn it on. The page reset
  calls `StartupRegistration.Set` directly.
- **`HdrGamesChosen` stays true.** It is the flag that stops `SeedNativeGames` re-ticking every
  native-HDR game it can find. Resetting it emptied the list exactly as the confirmation promised —
  and then the game watcher quietly re-ticked the lot within five minutes and auto-saved. Clearing
  the list has to mean the list stays cleared, so that flag is deliberately excluded.

`HdrTags` and `SeenGames` are excluded for the same kind of reason: neither is a setting. The first
is downloaded data about which games support HDR, the second is the record of which games have been
offered already, and clearing them would throw away a download and make every game in the library
announce itself as new.

The Performance page carries a note saying its UAC and firewall switches are left alone, because
those are Windows' own settings rather than this app's. A reset that silently skipped two of the
switches on the page would be lying about what it did.

---

### 6. The session-end prompt is opaque again, and stopped explaining the d-pad

Two things came back off, on the user's instruction.

The window was briefly translucent at 94%. It was the wrong trade: whatever is behind it is a game,
and therefore arbitrarily bright and busy, so even a mild amount showing through competes with the
text of the question being asked. It is fully opaque now. The drifting background lights already
keep it from reading as a flat slab, so nothing was lost.

The hint strip no longer carries **⬆⬇ Move**. A vertical list with one row visibly selected already
says which way the stick goes, and the user's words were that they are not dumb. It was also the
widest hint — two glyphs to everyone else's one — so removing it is what lets the rest fit on a
single line instead of wrapping into a strip only tall enough for one. What remains names the things
that are *not* guessable from looking at the list: which button commits, which one backs out, and
what the Square shortcut does.

---

---

## Ruled out — do not re-propose these

**"Something we changed fixed the Big Picture menu sound."** Disproved. The log says `The TV audio
device never appeared, so audio was left unchanged` many times over, including every session since
the sound started working, and `RestartSteamForAudio` is false. Whatever fixed it was outside this
app.

**Note the corollary:** that warning firing every session means the TV audio switch has never
actually worked for this user. The feature is doing nothing. Worth raising when they have appetite
for it.

**DLL-injection overlays, the Steam and Discord method.** Researched and declined: trademark
exposure, anti-cheat risk, maintenance cost.

**Firing the Guide button on release so the press can wake the subscription.** Cannot work: with no
subscription the press never reaches us at all.

**`SwitchToThisWindow` for raising Big Picture.** It emulates Alt-Tab, which minimizes fullscreen
windows — the opposite of what it was called for.

**obs64 "would not go back to its saved place".** `SetWindowPlacement` was refused by Windows,
almost certainly because OBS runs elevated. Nothing to fix short of running elevated.

**A config migration for the HDR mode.** Considered and rejected — see the table above. The two
booleans already express the three states unambiguously, so a new stored field would only add a
version to reason about.

---

## Things the user has said plainly

- Be concise. Cut words that do not change the meaning.
- Do not remove functions when reworking a layout.
- Do not over-explain in UI text, and do not state things that are not true.
- Spelling is American in user-facing text (`minimizes`, not `minimises`).
- Blue text is violet (`Theme.Info`). The PlayStation glyph blue and the app icon's gradient are
  deliberately left alone — those are console and brand colours, not app text.

---

## Where things live

- Backup of the pre-overhaul source: `_backup-before-ui-overhaul/`. Revert by copying contents back,
  but **not** the `.csproj` files — they carry the `Compile Remove="_backup*/**/*.cs"` exclusion,
  without which the backup breaks the build with hundreds of duplicate-symbol errors.
- Log: `%AppData%\CouchSession\couchsession.log`. First place to look for anything.
- The live project is `CouchSession.csproj`, and the source lives under `src/`. This section used to
  name `CouchPotato.csproj`, which was renamed along with the app.
- The housekeeping listed here is done: the stale executables, the redundant second csproj and the
  stray video file are all gone.
- This folder **is** a git repository, on `main`, pushed to
  `https://github.com/Sub-Wolfer/couch-session`. The line saying otherwise predates it, as does the
  claim that `_backup-before-ui-overhaul/` is the only safety net — that folder is now a snapshot
  nobody needs, excluded from both compilation and git.
