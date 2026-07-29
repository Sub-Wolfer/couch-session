<!--
  THE WORKING README. Not the published one.

  This is the draft for the NEXT release. It describes features that are built but not yet
  in any download, so it must not replace README.md until that release is out. A reader who
  follows the download button gets whatever was last published, and the page beside it has
  to describe that build and no other.

  Edit this file, not README.md. At release time it is copied over README.md in one move.
-->
<div align="center">

# 🛋️ Couch Session

**Your gaming PC on the TV, and back again, with one press.**

<sub>For a gaming PC with a TV plugged into it. Not a streaming app. No account, no telemetry.</sub>

[![Release](https://img.shields.io/github/v/release/Sub-Wolfer/couch-session?label=download&color=6d5bff)](https://github.com/Sub-Wolfer/couch-session/releases/latest)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4)](#-what-you-need)
[![Beta](https://img.shields.io/badge/status-beta-orange)](#-a-word-about-beta)

</div>

---

**Your desk PC is also your living room console. Getting between the two is the tedious part.**

You already have the TV wired to the PC. The games already run. What you do every single time is the
chore: switch the display. Move the sound. Fix the resolution. Turn on HDR. Open Big Picture. Then undo
every one of them when you're done, while working out which window ended up where.

Couch Session does the whole round trip. One press and the living room TV behaves like a console:
the right picture and sound, HDR on, Big Picture waiting, a controller in your hand and nothing that
wants a keyboard. One press back and the desk is exactly as you left it.

<div align="center">

![Couch Session](docs/screenshots/banner.png)

</div>

> ### 👥 Who this is for
>
> **One PC doing two jobs.** A machine you work and play on at a desk, that you also want to game on
> from the sofa. That's the whole reason this exists: the going-to-the-TV part is easy enough to do by
> hand, and it's *coming back* that nobody wants to do twice a day.
>
> If you have a dedicated living-room PC that never leaves the TV, most of this is solving a
> problem you don't have.
>
> **No TV? There's a mode for that.** Pick **At my desk only** and the couch half is put
> away: no display switching, no sessions, no controller triggers, and the pages for them leave the
> window. What's left is HDR that switches itself around your games, and the performance settings.
> Plenty of people will want the app for that alone, and it asks which you are the first time it opens.

> ### 🔌 What this is not
>
> **This is not game streaming.** Not Steam Link, not Moonlight and Sunshine, not GeForce NOW. There
> is no second device, no encoder and no network in the middle, so there is nothing to add latency,
> nothing to compress your picture and nothing to drop a frame.
>
> **Your TV is a monitor.** It is plugged into the same graphics card your desk monitor is, by an HDMI
> or DisplayPort cable you have already run. Games render on that card and go straight out to the set,
> at full quality, exactly as they do at your desk.
>
> **All this app does is the switching.** Every step above is something you can already do by hand,
> through Windows display settings, the sound menu, the HDR toggle and Steam. Doing all of it twice an
> evening is the part nobody keeps up. That is the whole job: automate the trip, and put everything
> back the way it was.

---

## ✨ What it actually does

| | |
|---|---|
| 🖥️&nbsp;**Display** | Switches to your TV, on its own or alongside your monitors, at the resolution and refresh rate you picked for it. Puts your desktop and its windows back afterwards. |
| 🔊&nbsp;**Sound** | Moves audio to the TV on the way out and back to your desk on the way in. Can mute a game you left running behind you. |
| 🌈&nbsp;**HDR** | On for the whole session, or only while a game you ticked is running. It follows your main display, so starting a session mid-game carries HDR to the TV. Off again when the game closes. |
| 🎮&nbsp;**Controller** | Start and end sessions from the pad. Use it as a mouse for launchers that ignore controllers. Decide what happens if it disconnects. Tested on Xbox and PS5 pads. |
| ⚡&nbsp;**Performance** | Power plan, game priority, and silencing notifications for the session. Shortcuts to a few Windows settings that are not. |
| 🧹&nbsp;**Free up memory** | Ask chosen apps to close when a session starts, and open them again after. It asks, exactly as Alt+F4 asks, and never forces one. |
| 👀&nbsp;**Preview** | See every change a session will make before you start it, and whether each one is put back afterwards. |
| 🔄&nbsp;**Updates** | Shows you what changed in a new version as soon as it finds one, and installs it with one click. Never while you're playing. |
| 🖱️&nbsp;**Desk only** | Turn the couch half off entirely and keep HDR and performance. One switch, and the settings it hides are remembered. |

---

## 🚀 Getting started

**1. Download and run it.** Grab `CouchSession.exe` from the
[latest release](https://github.com/Sub-Wolfer/couch-session/releases/latest). There's no installer
and no .NET to install. It's one file with everything inside.

**2. Say how you'll use it.** The first time settings opens it asks: on a TV, or at your
desk only. Choosing the desk puts the couch features away and leaves HDR and performance. Either
answer can be changed later under **General**.

<div align="center">

<img src="docs/screenshots/first-run.png" width="620" alt="The first-run question: on my television, or at my desk only">

</div>

**3. Pick your TV and your speakers.** Go to **Display & Audio** and choose them. Your TV doesn't
need to be switched on to appear in the list. It's there, dimmed, marked disconnected.

**4. Set a way to start a session.** On the **Hotkeys** page, record a keyboard combination, a
controller combination, or both. The PlayStation and Xbox Guide buttons already work without setting
anything up.

That's the whole setup. Everything else has a sensible default.

---

## 🔏 Windows will warn you the first time

`CouchSession.exe` is not code signed, so Windows SmartScreen shows **"Windows protected your PC"**
when you run it. Click **More info**, then **Run anyway**. Your antivirus may also want a word, for the
same reason.

**Why it isn't signed.** A code signing certificate costs a few hundred dollars a year, renewed forever,
and even then a new certificate carries no reputation with SmartScreen until enough people have
downloaded it for Microsoft to trust it. For a free tool written by one person and given away, that is
a recurring bill to make a warning disappear. It may be worth it later. It isn't yet.

**You don't have to take it on trust.** Every release is built by GitHub from the source in this
repository, on their machines rather than mine, from the exact commit the version tag points at. The
[build log for each release](https://github.com/Sub-Wolfer/couch-session/actions) is public, so you can
read what went into the file you downloaded. Or skip the download and
[build it yourself](#-building-it-yourself). It's two commands.

---

## 🎯 How you'll actually use it

**From your desk**, press your hotkey. The TV comes on, sound moves, Big Picture opens.

**From the sofa**, press the Guide button in the middle of your pad: the PlayStation logo or the Xbox
button. On the desktop it starts a session. Inside one, it asks what you want to do.

**When you're done**, the same button. If a game is still running you get three answers: leave it
running and go back to your desk, close it and stay on the TV for the next one, or close it and finish
up. Anything that would close a running game asks once more first, naming the game, until you turn
that check off.

**If your controller disconnects**, what happens depends on how it went. Holding the Guide button
until the pad powers down brings your desktop back, because that's you saying you're finished. A
battery going flat does nothing at all, because you're probably still on the sofa looking for a cable.
They're two separate settings, and either can be set to come back, to ask first, or to do nothing.

> ### ⚠️ Closing Big Picture closes your game
>
> Quitting Big Picture from Steam's own menu ends the session **and shuts down the game you were
> playing**, without asking. That isn't an oversight. Big Picture is the only thing on the TV
> able to show a question, so once it's gone there's nothing left to ask with and nobody holding a
> controller who could answer.
>
> To leave a game running, end the session with the Guide button instead and pick one of the answers
> above.

<div align="center">

![The same prompt with only Big Picture open](docs/screenshots/session-prompt-big-picture.jpg)

<sub>With no game running, the same button asks a shorter question.</sub>

</div>

---

## ⚙️ Every setting explains itself

No setting in this app is a bare label. Each one says what it does, what it costs, and whether it's
put back when the session ends, because a switch you can't reason about is a switch you leave alone.

Nothing is hidden behind a wiki either. If a setting has a catch, it says so on its own row.

<div align="center">

<img src="docs/screenshots/performance.png" width="800" alt="The Performance page, every row explaining itself">

</div>

The Home page collects the lot into one grid, so you can see how the app is set without opening
seven pages.

<div align="center">

<img src="docs/screenshots/home-at-a-glance.png" width="800" alt="Settings at a glance on the Home page">

</div>

---

## ⚙️ Settings that outlast the session

Most of what this app does is temporary. **The power plan, notification silencing and the game's
priority are all put back**, so a session leaves nothing behind.

The Performance page also holds four switches that are not like that, because they aren't really this
app's settings at all. They're Windows' own, shown here so you don't have to go looking for them. Each
one changes Windows the moment you press it and stays changed. **All four are off until you turn them
on**, and each says so on its own row.

### 👍 Worth having on

🏎️ **Windows Game Mode** asks Windows to prioritize whichever game is running and hold back background
work. This is the same switch as Windows Settings ▸ Gaming ▸ Game Mode, and pressing it here is the
same as pressing it there. Nothing puts it back afterwards, because it was never ours to put back.

🎮 **Switch off the Xbox Game Bar** stops the Guide button opening the Game Bar over your game, which
is awkward from a sofa. Two things to know: it takes background clip recording with it, so if you
record your play you'll lose that, and **closing the app does not turn it back on**. It stays off until
you switch it back on, here or in Windows. If Win+G has stopped working at your desk, this is why.

### ⚠️ Your call, and they lower your security

> 🔓 **UAC prompts** and **Windows Defender Firewall** can both be switched off from the Performance
> page. **These reduce your PC's security**, and like the two above they stay off until you turn them
> back on.
>
> They exist because a UAC prompt appears on a secure desktop a controller cannot reach, and the
> firewall's "allow this app?" dialog needs a mouse and blocks a game's networking until it's answered.
> Both are dead ends when the only thing in your hand is a gamepad. If that isn't a trade you want,
> leave them alone. Nothing else in the app depends on them.
>
> To be exact about the first one, because it matters: **User Account Control itself stays on.** The
> switch sets Windows to never notify, so an app asking for admin rights is elevated without putting
> a prompt on a screen you cannot answer from the sofa. That is a real reduction in security, and a
> smaller one than turning UAC off, which this does not do.

---

## 🌈 HDR, with or without a session

Tick the games you want HDR for and it comes on when one starts and goes off when it closes,
whether a session is running or not. Steam, Epic and GOG libraries are found on their own, and
Smart HDR keeps the list current by watching which games you switch it on for by hand.

<div align="center">

<img src="docs/screenshots/hdr-switching.png" width="800" alt="The HDR Switching page with a game list">

</div>

---

## 🧹 Freeing up the machine

A browser, a chat client and a music player are gigabytes of memory and a steady trickle of
background work that nobody is using from the sofa. Pick the apps you want out of the way and
they're asked to close when a session starts, and opened again when it ends.

**Asked, never forced.** Each one gets the same close request Alt+F4 sends. An app that refuses,
which usually means it has something unsaved, is left running and the log says so. Nothing here
ever ends a process, and that is a deliberate limit rather than a missing feature.

Choose them from a list of what's open right now, with the memory each one is holding, or point
at an executable yourself. The shell, Steam and Couch Session can never be closed whatever you
pick, and anything running as administrator isn't offered, because this app runs without those
rights and could not close it anyway.

Two browser windows closed come back as two. Your tabs and documents don't, because Windows offers
no way to ask an app to reopen what it had, so each window opens however that app chooses to
start.

---

## 👀 See what a session will do first

This app changes things outside itself: your display arrangement, the default sound device, HDR,
the power plan, and now other people's open applications. Every one of those is explained on its
own row on some page, and nobody reads seven pages before pressing a button.

**Preview session** lists them in one place, worked out from your settings as they stand, with one
column answering the question underneath the question: what happens when it ends. Your desktop
comes back, your sound comes back, HDR goes off, those apps are opened again or left closed.

Anything that would stop a session working comes first, in warning color, and the button to start
one is not offered while that's true.

---

## 🕹️ Controllers

**Tested with:** Xbox controllers and the PS5 DualSense.

Anything else Windows recognizes should work. The app reads standard HID and XInput rather than
anything vendor-specific, and buttons are handled by position, so a combination set on one pad is the
same physical buttons on another. But "should work" is not "tested", and right now those two are the
only ones that have been. If you have a DualShock 4, a Switch Pro controller, an 8BitDo or anything
else, [a quick report](https://github.com/Sub-Wolfer/couch-session/issues) either way is genuinely
useful.

No extra software is needed for any of them.

<div align="center">

<img src="docs/screenshots/controller.png" width="800" alt="The Controller page">

</div>

Your controller is **never taken over**. It's opened for reading only and never exclusively, so it
runs alongside Steam Input, DS4Windows and games rather than competing with them. The Guide button is
only listened for, never intercepted, so Steam still does everything it normally does with it.

Nothing here injects code, hooks another process, reads another process's memory, or synthesizes
gamepad input. That's a deliberate design rule, not an accident: it's what keeps the app clear of
anti-cheat. It reads HID devices directly and asks Windows about its own windows, and that's all.

---

## 💻 What you need

- **A TV connected to the PC**, by HDMI or DisplayPort, showing up in Windows as a second display.
  If your TV is not plugged into this machine, this app has nothing to switch to. (The HDR and
  performance half still works on its own. See **At my desk only** above.)
- **Windows 11**, which is what it's developed and tested against. Windows 10 works, and the only
  difference you'll notice is square window corners instead of rounded ones.
- **Steam**, for the Big Picture half of a session. The HDR and display features work without it.
- Nothing else. No runtime, no installer, no admin rights unless you choose the two security settings
  above.

Settings live in `%AppData%\CouchSession`. Nothing is written anywhere else.

---

## 🔒 What it touches

No account, no telemetry, no analytics, no crash reporting. There is nothing to sign into, and
nothing is collected about you or the way you use it.

- **Your settings** are one folder, `%AppData%\CouchSession`.
- **The network** is used for three things and nothing else: GitHub, to see whether there's a newer
  release; Steam's store API and PCGamingWiki, to find out whether a game supports HDR. Worth being
  precise about the last two, because it is the one real cost: each lookup asks about one game by
  name or by Steam id, so those two services see which games you have installed. Answers are cached,
  so each game is asked about once.
- **Windows settings** change only where you switch them on yourself: Game Mode, the Game Bar, UAC
  prompts, the firewall, the power plan, and notification silencing. Each of those rows says on its
  face whether it's put back when the session ends, and three of them deliberately are not.
- **Start with Windows** adds a scheduled task, and only if you turn it on.
- **To remove it entirely**, switch Start with Windows off, then delete the executable and that one
  folder. There is no uninstaller because there is nothing else to undo.

---

## ⚠️ Known issues

Things that are known to be wrong or awkward. Listed here rather than left to be discovered, because finding out on your own costs you an evening and reading it here costs you a minute.

- **Swapping to the desktop and back can upset a running game.** Moving displays around a game that
  is already running is not something every game copes with: some come back at the wrong resolution,
  some keep the resolution of the screen they left, and some lose their display mode entirely.
  **Borderless fullscreen handles it best** and is worth setting for anything you play this way, but
  even that is not guaranteed. If a game ends up wrong, the reliable fix is to end the session, quit
  the game, and start both again. A game that launches inside a session gets the right display from
  the beginning and has nothing to recover from.

- **Exclusive-fullscreen games minimize when the session prompt appears.** Windows does that itself the moment such a game loses focus, and it cannot be avoided while also taking the controller away from the game so you can answer. Setting a game to *borderless* fullscreen avoids it entirely, which is worth doing anyway.

- **Big Picture doesn't open if you start a session while a game is already running.** It's left
  closed on purpose, and opened when you choose **Close the game, stay on the TV** at the end of a
  session, which is when you actually need it.

  It isn't needed. Steam's **Use the Big Picture Overlay when using a controller** setting, which is
  on by default, means pressing the Guide button in a game opens Steam's controller-friendly Big
  Picture *overlay*, the thing the session prompt appears over, without any Big Picture window
  being open at all. Opening one as well only put a shell behind your game that could take the
  foreground and your controller with it. Leave that Steam setting on and this works as intended.

- **HDR can quietly do nothing on a display that supports it.** HDR is only switched when Windows reports the display as capable, and Windows does not always agree with the display itself. Nothing on screen says so, there is no way to overrule it yet, and the log is the only place it is recorded.

- **Some windows do not go back where they were.** OBS is a reliable example. A window moved to a second monitor shortly before a session may also come back on the wrong one. Under investigation, and the log now records both cases.

- **Moving the app breaks "Start with Windows" for one sign-in.** Windows records where the file is, so moving it leaves the sign-in launch pointing at the old folder. Couch Session repairs this the next time it runs and tells you it did, but if you move it and then restart without opening it, that one sign-in is missed.

- **HDR can switch off on the wrong screen.** Start a game you have ticked at your desk, then begin a session while it is still running, and closing that game switches HDR off on the TV rather than on the monitor it was turned on for. The app records that it turned HDR on, but not which display it turned it on for.

- **The display can be slow to sleep while a controller is connected.** A pad that streams continuously, as a DualSense does even sitting still, holds Windows' idle timer open. The app lets go after about ninety seconds of a genuinely untouched controller.

- **Couch audio set to the same device you already use does nothing.** There is nothing to move and nothing to restore. The app says so once per run and points at the setting.

- **A TV's audio output only appears in the list while the TV is on.** One seen before stays selectable, marked as off, so it can still be chosen with the set cold.

---

## 🧪 A word about beta

This is version 0.9.x and it's in daily use, but it hasn't been through many hands yet. Bug reports
are genuinely useful right now.

The fastest route is the **Report a bug** button in the app's footer. It gathers the log from
`%AppData%\CouchSession\couchsession.log` alongside your settings, which is almost always where the
answer is hiding. Otherwise [open an issue](https://github.com/Sub-Wolfer/couch-session/issues) and
attach that log.

---

## 🔨 Building it yourself

You'll need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
git clone https://github.com/Sub-Wolfer/couch-session
cd couch-session
dotnet build -c Release
```

For the single-file executable a release ships as:

```bash
dotnet publish -c Release
```

<details>
<summary><strong>Regenerating the app icon</strong></summary>

<br>

The icon is drawn by the app itself rather than maintained as a separate file, so there's one
definition of the mark. To rebuild `CouchSession.ico` from it:

```bash
CouchSession.exe --write-icon
```

Then build again to embed the result.

</details>

---

## 📄 License

MIT. See [LICENSE](LICENSE). Do what you like with it.

<div align="center">
<br>
<sub>Built for the ten feet between the desk and the sofa.</sub>
</div>
