# Couch Session — settings review

> **Scope correction, 26 July.** This review was written assuming the settings window is operated
> from a sofa with a game controller. **It is not.** The user works in settings at their desk, with a
> mouse and keyboard, and the window is not being optimised for a controller or for TV viewing
> distance. Controller-first still governs the *session* — the end prompt, toasts, hotkeys, Big
> Picture — but not this window.
>
> What that retires: **finding 5 (TV scaling)** was the largest item here and is now out of scope.
> **Finding 4's** three "stranded user" traps stop being lockouts, because a keyboard is always
> within reach. The hover-contrast halves of **finding 3** stop mattering; the Start button and the
> jumped-to marker still do, for plain legibility rather than distance. Most of **finding 7**
> (scroll affordance) goes away, since a wheel works. On the Shortcuts page, the argument that the
> keyboard box should be demoted below the controller box is void.
>
> What rises: **finding 6 (focus rings and keyboard activation)** — at a desk, Tab, Escape and a
> visible focus ring are ordinary expectations, and several controls still cannot be pressed by
> keyboard at all.
>
> Everything else stands: the defects, the untrue statements, the dead code, and the per-page
> content problems are unaffected by where the window is used.

Every page except HDR, reviewed against the code as it stands. Findings are ordered by value, not
by page. Each is anchored to a file and line so it can be checked before it is acted on.

Two things to know before reading. First, the biggest wins are **cross-cutting** — they are single
changes in shared code that improve every page at once, and they are worth doing before any
per-page tidying. Second, several findings are **defects rather than opinions**: code that cannot
do what it was written to do, or text that states something the code does not do. Those are marked
**[BUG]** and are not design suggestions.

---

## The short version

If only five things get done:

1. **Make the whole settings row a hit target.** One change in `AddToggle`; every toggle on every
   page goes from 44×24 to roughly 900×70.
2. **[BUG] Make "jump to this setting" actually scroll.** It is a no-op today, which silently
   breaks the Home page's entire navigation model.
3. **Fix the three measured contrast failures** — hover at 1.14:1, the setting highlight at 1.20:1,
   and white on the green Start button at 2.35:1.
4. **Close the three bootstrapping traps** where a controller user can strand themselves in a
   window they can no longer operate.
5. **Correct the untrue statements**, particularly the Game Bar lifetime and the Performance page's
   promise that settings are put back.

---

## Cross-cutting — fix once, every page improves

### 1. The only hit target in a 900-pixel row is 44 × 24 pixels
**Quick win · `Controls.cs:143`, `SettingsForm.cs:2995-3092`**

`ToggleSwitch` is `44 × 24`, pinned to the right of a row up to 880px wide and 50–80px tall.
`AddToggle` wires a click handler to the switch alone — the title, the description and the rest of
the row are inert. Roughly 1,000 live pixels against 60,000 dead ones.

This is the single most-repeated interaction in the app, performed with a stick-driven pointer from
across a room. Every miss lands on the card and does nothing.

**Change:** attach a click handler to the row panel and its text children that flips the toggle,
and add a hover wash. Exclude interactive descendants (dropdown, slider, `InlineCheck`) via a
`sender` check. Consider exempting the two security switches on Performance so a stray row click
cannot disable the firewall.

The same sizing problem repeats at `Dropdown.cs:179` (popup rows at 30px), `SettingsForm.cs:3384`
(reset buttons at 96×26) and `SettingsForm.cs:2748-2757` (About buttons at 30px, the smallest in
the window). Raising these to 34–40px is a constants-only change.

### 2. [BUG] "Jump to this setting" never scrolls to the setting
**Medium · `SettingsForm.cs:1596` and `1625`**

```csharp
try { _pageHost.ScrollControlIntoView(row); }
```

`_pageHost` is a plain `Panel` with `AutoScroll` left false (`SettingsForm.cs:804`), and
`ScrollableControl.ScrollControlIntoView` does nothing when `AutoScroll` is false. The control that
actually scrolls is `ScrollHost`, which deliberately avoids `AutoScroll` and moves its content
control instead (`ScrollHost.cs:5-12, 80-106`) — and does not override `ScrollControlIntoView`.
There is no path by which either call can move anything.

This is not a cosmetic miss. All 21 Home tiles are jump links (`SettingsForm.cs:1299`). Clicking
one opens the right page at the top, with the highlight band painted somewhere below the fold. The
user then hunts down a page of near-identical switches with a stick — exactly what the comment at
`SettingsForm.cs:1566-1573` says the feature exists to prevent. It reads as broken rather than
absent, which is worse.

**Change:** add `ScrollHost.ScrollIntoView(Control)` that walks the target up to `_content`, sums
`Top`, and sets `Offset` so the row sits ~80px below the viewport top. Call it on the page's
`ScrollHost` rather than on `_pageHost`. It must run after the layout pass that
`RefreshVisiblePage` triggers via `BeginInvoke` (`SettingsForm.cs:1023`), or it will measure an
unlaid-out stack.

### 3. Three measured contrast failures, all on things the user must see
**Quick win · `Controls.cs:336, 67-68`, `SettingsForm.cs:5501-5507`**

Measured as WCAG relative luminance from the actual colour values:

| What | Colours | Ratio | Needs |
|---|---|---|---|
| Nav hover fill vs `Rail` | (34,37,44) vs (23,26,33) | **1.14:1** | ≥1.6:1 |
| Tile / row hover vs `Surface` | `SurfaceHi` vs `Surface` | **1.20:1** | ≥1.6:1 |
| Setting-highlight band | `Mix(Surface, Accent, 0.16f)` | **1.20:1** | ≥1.7:1 |
| White on Start button | White on `Good` (62,191,122) | **2.35:1** | 4.5:1 |
| `TextFaint` on `Surface` | (126,135,152) vs (30,34,43) | **4.40:1** | 4.5:1 |

With no keyboard there is no focus ring, so **hover is the only signal that says "the bumper will
hit this."** At 1.14:1 on a TV it is invisible; the user presses the bumper to find out where the
cursor is.

**Change:** raise hover fills and add a 1px outline on hover — a border survives viewing distance
far better than a fill. For the highlight band, add a solid 4px accent bar on the leading edge; the
nav item already uses exactly this device for exactly this reason (`Controls.cs:341-342`). For the
Start button, use near-black ink on the green (≈7.5:1) — which is also the console convention for a
confirm button — and keep white on the red.

### 4. Three ways to strand a controller user in a window they can no longer operate
**Mixed · highest-severity items in the review**

- **[BUG] Opening the Keyboard shortcut box kills the pointer.** `ShortcutBox.Listen()` raises
  `AnyListening`, which `TrayApp.cs:186` feeds into `ControllerUiCapturing`, which makes
  `PointerControl.IsLauncher` return false — the pad stops moving *and* clicking
  (`PointerControl.cs:115-124`). The only exits are a key press, Escape, or losing focus. Nothing
  calls `Cancel()` and there is no timeout. On the sofa this freezes the settings window until the
  user walks to the desk. **Fix:** only `Source.Controller` boxes should stand the pointer down —
  a keyboard box needs no raw pad access, so the pointer can keep steering and clicking away
  cancels. Add a ~10s auto-cancel as a backstop.

- **Turning off "Use your controller as a mouse" is one click and irreversible from the couch.**
  400ms later the pointer is dead and there is no pad-reachable way to turn it back on. **Fix:**
  keep the pointer live while the settings window is foreground, regardless of the setting — then
  say so in the description.

- **[BUG] Making the window big enough to read disables the pointer.** `PointerControl` treats any
  window covering ≥90% of its monitor as a game (`PointerControl.cs:573-575`). On 1280×720 the
  default settings size computes to 93.75% × 91.9% — over the line, so the app classifies its own
  settings window as a game and stands the cursor down. With a visible taskbar it lands at 85.8%
  and survives, so the behaviour flips on a taskbar setting. **Fix:** exempt the app's own windows
  in `IsLauncher` — `FindDesktopWindow`/`IsShell` are already special-cased the same way.

### 5. The settings window is the one window with no TV scaling
**Large · `UiScale.cs:103-107`, `Theme.cs:68-91`**

`UiScale` exists and doubles sizes on a 4K panel, but only `Toast` and `SessionEndPrompt` consume
it. The window containing 40 settings, 40 descriptions, 21 tiles and every explanatory note does
not.

Concretely: `Theme.Small` (10pt) is every setting description. On a 55" 4K TV at 2.5m that is a cap
height of **≈4.1 arcmin**. Normal acuity resolves ~5 arcmin at full contrast; 10-foot-UI guidance
asks for 20+. `Theme.Caption` (9pt, in `TextFaint` at 4.40:1) is smaller and dimmer still.

**Change:** not full per-monitor DPI — a user-facing "Text size: Normal / Large / TV" on the General
page, writing a float to config, routed through a `Theme.Scale` multiplier plus the chrome
constants. The layout is already fully rebuildable at a new width (`RebuildAt`), which is most of
the hard part. This is the largest item here and probably the highest-value one for the stated user.

**Related:** the type scale itself is compressed — 11 / 10 / 9pt is a 1.1× step, below the ~1.2
ratio at which hierarchy is perceived. Open the ratios out at the same time; doing it alone would
make the small text smaller.

### 6. No focus indicator anywhere, and several controls cannot be activated by keyboard
**Medium · across `Controls.cs`**

The only focus visual in 1,251 lines of `Controls.cs` is the slider knob (`Slider.cs:169`).
`FlatButton`, `NavItem`, `RoundedButton` and `PowerButton` have no `OnKeyDown` at all — they are in
the tab order but cannot be pressed. There is no `KeyPreview`, no `ProcessCmdKey`, no Escape-to-close.

Worth doing on its own merits, but the pointed reason is this: **a focus visual is the prerequisite
for D-pad navigation**, which is the real long-term answer to items 1, 3 and 7. You cannot ship pad
navigation without one.

### 7. Scrolling depends on a 10px thumb at 1.72:1, with no coarse control
**Medium · `ThinScrollBar.cs:44, 133-135` — DONE (the sizing half)**

Stick-scroll works well (`PointerControl.cs:342-359`) and largely rescues this — but only when
mouse control is on, with the right stick, and while the hold button is held. Otherwise the only
route is dragging a 10px-wide, 1.72:1 target, which is the hardest gesture there is with a stick.

**Change:** widen to 14px in an 18px lane and lift the rest state. Then add what removes the need
for it — a full-width "More below ↓" strip pinned to the bottom when there is more to see, that
pages on click. It doubles as the missing "there is more here" signal.

**Done:** the bar is 14px in an 18px lane (`ThinScrollBar.cs`, `ScrollHost.cs`), and the rest state
went from a 0.18 white mix to 0.28, with hover and drag lifted to 0.40 and 0.52 so the bar still
reacts by a visible step. The HDR game list had its own bar at 8px — the longest scroll in the app
on the narrowest target — and it now matches at 14px.

**Not done, and deliberately:** the pinned "More below ↓" strip. It was there to replace a gesture
that is hard with a stick, and the settings window is not driven with a stick — a wheel does the
job. Revisit only if this window is ever aimed at the couch.

### 8. [BUG] At short window heights, four pages become unreachable
**Quick win · `SettingsForm.cs:852-859, 249` — FIXED**

`_nav` is a `FlowLayoutPanel` with `AutoScroll` false and `WrapContents` false. Eight items at 42px
plus a 74px brand block needs 410px of rail. At `MinimumSize.Height = 380` the rail gets ~192px —
about 4.5 items. Hotkeys, Performance, General and About are clipped with no scrollbar, no wrap and
no other route to them.

**Change:** raise the height floor to ~490. `FitToContent` never produces a window that short
anyway, so this costs nothing and removes the failure mode rather than managing it.

**Fixed:** the floor is now measured in `FitToContent` from the rail's own contents — `BrandHeight`
plus every nav entry's height and margin, plus the title bar and footer — rather than written down
as a number. That lands at ~524 for eight pages and moves by itself when a ninth is added, instead
of quietly re-creating this bug. It is clamped to the screen's working area as well, so on a display
too short for the full rail the floor gives way rather than producing a minimum taller than the
maximum `FitToContent` sets straight afterwards. `RestorePlacement` already clamps a saved size to
`MinimumSize`, so a window saved at 380 comes back at the new floor instead of clipped.

### 9. Auto-save confirmation is in the wrong place, and there is no undo
**Medium · `SettingsForm.cs:5274-5297`**

Auto-save is the right call and the reasoning at `SettingsForm.cs:1694-1697` is sound. But the
confirmation is 10pt text in the footer at (372, 26), shown for 2.5s, while the user's eyes are on
a toggle in the middle-right of the window. It will essentially never be read.

Compounding it, the only undo is "Reset all settings". A mis-clicked toggle — which finding 1 makes
likely — has no proportionate remedy.

**Change:** confirm at the point of change. Flash the changed row's own background via the existing
`Card.Highlight` machinery for ~600ms. It costs no space, it is where the eye already is, and it
names *which* setting saved. Add an "Undo" button in the footer for ~8s after a change.

---

## Statements that are not true — ALL FIXED, 26 July

The house rule is that UI text never states something the code does not do. Every row below has been
corrected in `Words.cs`, and each correction carries a comment above it saying what the old text
claimed and which line of code disproved it, so nobody re-introduces one by "tidying the wording".

| Where | Said | Actually |
|---|---|---|
| `DisableGameBarButtonWhy` | Game Bar off "during a session", back on "when you quit" | App-lifetime, not per session (`TrayApp.cs:386`, restored at app exit). Also clears `AppCaptureEnabled` and `GameDVR_Enabled` — background clip recording — which the text never mentioned. Both now stated. |
| `PagePerformanceWhy` | "put your settings back when it ends" | Four of seven do not. Now says some go back and some stay, and each row says which — which meant adding the missing "stays until you change it here" to `DisableUacWhy` and `DisableFirewallWhy`, and "ends when that game closes" to `GamePriorityWhy`. |
| `WelcomeSteps` | "the list on the Home page walks you through them" | That checklist was deleted. Now points at Display & Audio and at the alert strip, which is what actually flags a missing essential. |
| `TvDisplayWhy` | "Only displays Windows can see right now are listed — switch your TV on" | `ListDisplays()` queries `AllPaths` and keeps inactive displays, dimmed and marked disconnected. The old text sent the user to switch the TV on in the app's most common situation. |
| `ShortcutControllerNote` | Controller "never opened or taken over" | It **is** opened (`HidPoll.cs:320`), just never exclusively. Now says "opened for reading only and never exclusively". |
| `ControllerTriggerNote` | Trigger "paused while you are on this page" | Also the Hotkeys page, and **not** paused during a session. Both now named. |
| `TriggerPad` | "Controller that starts and **ends** a session" | A disconnect only ever raises a prompt. Title is now chosen at runtime between "starts a session" and "Controller to watch". |
| `UseTrackpadWhy` | "tap the trackpad to left-click" | It is a physical button and must be pressed. |
| `MouseDeadzoneLow` | Slider low end labelled "None" | 25% deflection is required regardless. Now "Smallest". |
| `DeviceNoteOne` / `DeviceNoteMany` | "{n} displays **available**" | The count includes disconnected displays, on purpose. Now "known". |
| `CheckSummaryBody` / `CheckSomeFailed` | "**Every one** was tried for a moment and put straight back" | `PerformanceCheck.cs:82-86` — UAC and firewall are read, never switched, yet were counted as failures. Both strings now say so, and the failure line no longer blames a vendor utility for Windows being secure. |

### Found by a second sweep the same day, after the day's changes

The table above was written before the Controller page rework, the prompt shortcuts and the
notification changes. A second pass over every string in `Words.cs` against the code found five more,
all now fixed:

| Where | Said | Actually |
|---|---|---|
| `ConfirmEndSessionHint` on the game prompt | "End session" | Both prompts map Square to `Choice.Close`, and `Close` runs `ReturnToDesktop` with `BeforeTeardown = CloseRunningGame` — so on the "a game is still running" prompt, one press of Square **closes the game**, immediately and with no second confirmation. That prompt now uses `ConfirmEndGameHint`, "Close game & end". The most destructive control on the screen was the one with the vaguest label. |
| `GuideEndsSession` | "End a session with the PS / Xbox button" | `CheckGuideButton` returns early when this is off, *before* `GuideStartsSession` — so switching it off also stops the button starting a session and resuming a minimized one. Retitled to name both directions. |
| `MouseHoldWhy` | while held it works "everywhere, including over a game" | `asked` overrides the game check but not `_bigPictureInFront`, which stands the pointer down unconditionally. Big Picture is where the user spends most of a session. |
| `FeatureControllerOff` | "Ending the session when a controller disconnects" | Directly contradicted by `ControllerDisconnectNote` — "a disconnect never ends your session on its own". Now "noticing when a controller disconnects mid-session". |
| `HdrNotSupported` | "no HDR support, **so nothing on this page will change it**" | The note is chosen from the EDID answer, but `SetPrimaryHdr` gates on Windows' `advancedColorSupported` — and the whole reason the EDID check exists is that those two disagree. The note now reports what the display says and stops promising what will happen, since it is not the thing that decides. |

**Still open, and it is a code question rather than a wording one.** The HDR page and the HDR control
path use two different tests for the same thing. The page reads the monitor's EDID; `SetPrimaryHdr`
reads Windows. Until they agree, a panel can be labelled one way on the page and behave the other way
when the hotkey is pressed. The per-display override already designed for the fake-HDR 1080p panel is
the natural place to settle it.

One behaviour change went with this pass: the performance check no longer emits a "Keep the TV awake"
row. That setting is withdrawn — the call in `PerformanceTuning.Apply` is commented out, `Save` forces
the value false, and no page has a toggle for it — so the row could only ever say "switched off, turn
it on if you want to", pointing at a switch that does not exist.

---

## Dead code and orphaned work

Worth clearing in a cleanup phase, and two items are features that were built and never switched on.

- **49 of 338 strings in `Words.cs` are never referenced.** Full list in the appendix of the copy
  review. Several are the best-written text in the file — `BigPictureIsSessionNote` ("Closing it
  always brings your desktop back, exactly as you left it") is the single most reassuring sentence
  in the app and appears nowhere, despite a code comment at `SettingsForm.cs:2670-2675` claiming it
  is still there.
- **`Words.DisplayPrimary` ("— currently primary") is defined and never applied.** So is
  `Words.WarnCouchDisplayIsPrimary`, a well-written warning for the most destructive mistake
  available on the Display page — picking the desk monitor as the couch display. Both features were
  written; neither was wired up.
- **`CheckForUpdates` config exists and is read nowhere.** The update path is a closed loop: the
  Home alert "a new version is available" can only fire *after* you visit About and press Check.
  The comment at `SettingsForm.cs:4170-4172` claims there is an automatic startup check. There is
  not. This is ~90% built and switched off by accident.
- **`RunPerformanceCheck` and `CheckRow` are never called** (`SettingsForm.cs:4269`), so all eight
  `Lifetime*` strings — the text that tells the user what survives a session — are unreachable.
  This is the fix for the Performance page's honesty problem, already written. `ShowToggleNote`
  (`SettingsForm.cs:3105`) drops `result.Lifetime` on the floor; appending it is a quick win.
- **`AppConfig.cs:353-356`** says hold-to-activate is "On by default" — line 357 is `= false`.

---

## Per page

### Home

- **The tiles have no groups.** Six groups exist in source comments (`// ── the session ──` etc.)
  and none are drawn, so 21 tiles wrap row-major into an undifferentiated grid. "HDR Switching"
  ends up separated from its own two hotkeys by a row break; the last row is one tile and three
  holes, which reads as a failed load. Inserting full-width caption labels into the same flow forces
  clean breaks and matches every other page's `NewSection`.
- **Dimming means three different things.** "Your TV is off and sound will not move" renders
  identically to "you chose not to change Game Mode". The state most worth catching is the one made
  hardest to see. Three states, three treatments: live / off-by-choice / configured-but-absent.
- **Five tiles express the same boolean in three vocabularies** — `off` / `no` / `unchanged`. One
  vocabulary. (Keep "unchanged" for Game Mode only, where it is a real distinction.)
- **The page subtitle promises a Start button that is not on the page.**
- **Any alert change scrolls the page back to the top**, because `RebuildPage` builds a fresh
  `ScrollHost` at offset 0. Capture and restore the offset.
- **The Controller tile duplicates the title-bar strip**, which also shows battery and links to the
  same page.
- **Expanded warnings can insert 25 wrapped rows above everything**, dismissible only via a 22px
  unlabelled cross adjacent to a control that does something different.

### Display & Audio

- **Nothing marks which entry is the desk monitor**, so with the TV off the user picks blind — and
  picking the desk monitor is the most destructive available mistake. Both the marker string and the
  warning string already exist, unused.
- **When the saved display is gone entirely the page goes silent** — an empty dropdown, no message.
  `RefreshVideoModes`' own comment says this is "the one outcome that reads as a fault rather than a
  state", and this is the case it does not handle.
- **The audio pickers are not indented under the toggle that controls them**, breaking the rule
  `AddToggle`'s own docs state, and a full-width hairline visually cuts them off from it.
- **`TvDisplayName` is saved with its "— disconnected" suffix**, producing "LG TV — disconnected
  (not detected)" in the Home tile and polluting the diagnostics. Audio already strips its suffix;
  a `PlainName` helper already exists.
- **The TV sorts last in both pickers** because both sort active-first — and the TV is the one entry
  the user came to select. `BestGuess` already scores *inactive* displays higher for exactly this
  reason; the picker contradicts its own guesser.
- **No way to confirm which physical screen or speaker an entry is.** `AddPick` already supports a
  button beside the dropdown, used once elsewhere. An "Identify" and a "Test tone" would close the
  loop that currently costs a walk to the TV.

### Shortcuts

- **Clicking the Controller box with a bumper records the click buttons.** `PollPad` reads the pad
  directly, so R1 is down at capture time. With hold-to-activate on you can silently record
  "R2 + R1" — after which every pointer click toggles the session. **Fix:** baseline the held
  buttons on `Listen()` and ignore each until released once.
- **The Keyboard box is given top billing on a page used from a sofa** — the first control in each
  pair is the one that cannot be operated, and it is the one that freezes the pointer (finding 4).
- **No conflict detection at all, and the one message shown is false.** Set the same key combo for
  Session and HDR and Windows refuses the second — but the toast says "already being used by
  something else on this machine", sending the user hunting through other software for a conflict
  that is this app. Set the same *pad* combo for both and both fire, silently.
- **A combo containing the Guide button double-fires**, because the Guide watcher runs on its own
  timer and does not care what else is held.
- **Nothing stops a shortcut colliding with the pad-mouse buttons** — L1 + R1 fires whenever you
  left- and right-click together. The mouse hold picker already guards against exactly this hazard
  (`SettingsForm.cs:1963`); the shortcut boxes do not.
- **The description hardcodes "Back + Start"** while the glyphs beside it correctly render Create /
  Options on a DualSense. The app contradicts itself in one row.
- **Both explanatory notes are stranded at the bottom**, away from the rows they explain.

### Controller

- **Neither slider gives live feedback, and the debounce guarantees it.** `MarkDirty` restarts the
  400ms timer on every value change, so during a drag the save never fires and the pointer keeps its
  old speed until 400ms after release. The one control whose effect could be felt directly gives
  none. **Fix:** push pointer settings live from `ValueChanged`, keep the debounce for the disk write.
- **A drag slider is the wrong control for a stick pointer.** At the top setting the cursor crosses
  a 4K TV in under 0.3s, making the 26px reset button nearly unhittable; at the bottom it takes 14
  seconds corner to corner. Both ends make the page that fixes them hard to operate. Add ◀ / ▶ step
  buttons at dropdown height and a numeric readout.
- **Neither slider shows its value.**
- **"Is my pad connected?" is answered only by the absence of the title-bar strip** — and an absent
  element is indistinguishable from one you did not notice. Add a live presence line to the card.
- **`PadPresence` computes `OnBattery` and it is rendered nowhere.**
- **Consider putting the Mouse card first** — it governs how you operate this very page, and it is
  currently below the fold on a 720p TV.

### Performance

- **Split the page by what survives, not by what it touches.** Two sections — "For this session"
  (power plan, notifications, priority) and "Changed until you change them back" (Game Mode, Game
  Bar, UAC, firewall). The heading does the work four per-row captions would otherwise do, and the
  two red security switches stop setting the tone for the whole page by leading it.
- **Two permanent security changes fire on a single click**, on mouse-*down*, with no confirmation
  — despite both control classes' docs claiming "the caller warns first". The caller does not.
  Worse, turning UAC off raises an elevation prompt on the secure desktop, **where the app's own
  controller-mouse cannot go** — the exact dead end the app exists to remove. At minimum, add a
  sentence saying to do it at the desk.
- **The power plan picker is greyed rather than hidden** despite a comment claiming it is not
  offered. `WhenOn` now exists and does this.

### General

- **The Notifications section holds three switches that all *show* notifications**, while "Silence
  Windows notifications" — the one a user hunting for peace actually wants — is on Performance. Add
  a cross-reference.
- **"Minimize to system tray" is filed under "Startup"** and is the most consequential setting on
  the page: turning it off means the app quits and HDR switching stops.
- **`BackgroundFeatures()` already computes exactly what stops working, for this user's config,
  including the game count** — and writes it to a log file nobody on a sofa will open. Show it
  inline under the toggle when it is unchecked. Zero new logic.

### About

- **The page explains a "Report a bug" button that is not on it** — it moved to the footer. The
  paragraph now reads as an instruction for one of the three buttons above it, most likely
  misread as "Save diagnostics", which does something else entirely.
- **"Save diagnostics" opens a wall of plain text in Notepad on the television.** Select it in
  Explorer instead, as "Report a bug" already does.
- **Smallest buttons in the window (30px), two labels hardcoded past `Words.cs`, no page subtitle,
  no captions saying what any button produces** — on the page users reach when something has gone
  wrong.
- **The update download reports nothing.** `InstallAsync` takes a progress callback
  (`Updates.cs:92`) and is called without one, so a multi-megabyte download shows no feedback until
  the app restarts.

---

## Copy

Full detail in the copy review; the headline items:

- **British spellings in live strings:** `recognises` (`Words.cs:160`), `prioritise` (`:293`).
  Also `tick`/`untick` throughout, where American is check/uncheck — and the column those describe
  is headed "On" with buttons labelled "All"/"None", so the copy uses a third and fourth verb for
  the same action.
- **Three names for the product state:** "Couch Session", "couch session", "couch mode". The last is
  namespace leakage and survives in three live strings.
- **Four names for one concept on the Shortcuts page:** the nav says "Hotkeys", the strings say
  shortcut, combo, and combination.
- **Two names for the Guide button on adjacent pages:** Home says "Guide button", Hotkeys says
  "PS / Xbox button".
- **"pad" vs "controller"** — both, one sentence apart, at `Words.cs:729-730`.
- **The ten longest descriptions can lose 12–25 words each** without losing meaning. The Mouse
  section is the wordiest area of the file: 62 + 49 + 42 + 38 + 34 + 22 words across six adjacent rows.
- **Emphasis is overused where it matters most.** `ShortcutTaken` is 52% bold — and the bold half is
  the diagnosis, while the actionable half ("Pick a different combination") is left plain.
  `CheckAllGood` is 100% bold, which emphasises nothing.
- **The house voice is good and worth standardising on.** The model strings are
  `SilenceNotificationsWhy`, `ChangePowerPlanWhy`, `SwitchAudioWhy`: one sentence for what it does,
  one clause for why you would care, nothing about how it works. Three other voices have crept in —
  implementation notes, tutorials, and marketing — and the Check page reads as a separate dialect.

---

## Suggested order

**First, because they are defects:** the jump-to-setting no-op (2), the keyboard-box pointer
freeze (4), the nav clipping at short heights (8), the shortcut-records-your-click bug, the
untrue statements table.

**Then, because they are cheap and improve everything:** row-wide hit targets (1), the contrast
fixes (3), control sizing constants, the dead-string sweep.

**Then, because they are the real experience:** TV scaling (5), focus visuals (6), scroll
affordance (7), per-page reorganisation.

**Leave alone:** `Frozen` / `RebuildPageCore` and the freeze-depth counter, `Theme.PaintBackdrop`,
`ScrollHost.WheelRouter`'s pass-through rules, the deliberate absence of wheel support on sliders,
and muted-not-disabled offline devices. All were checked and all are right.
