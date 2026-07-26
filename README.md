# Couch Session

Move your gaming PC to the television and back with one press, then put everything exactly as it was.

Couch Session switches your display and your sound to the TV, sets the resolution and refresh rate you
picked for it, turns HDR on if you want it, and opens Steam Big Picture. When you are done it puts your
monitors, your audio device, your HDR state, your power plan and your desktop windows back where they
were. It sits in the tray and stays out of the way in between.

It is built for the moment you sit down on the sofa with a controller in your hand and do not want to
get up again. Everything a session needs can be reached from the pad: starting it, ending it, toggling
HDR, and a pointer driven from the stick for the launchers that still want a mouse.

**Status: beta (0.9.0).** It works and it is in daily use, but it has not been through many hands yet.
Bug reports are genuinely useful right now.

## What a session actually does

Starting one switches the display over — TV only, or TV alongside your monitors, whichever you chose —
moves audio to the TV, applies the resolution and refresh rate you set for that screen, and brings up
Big Picture. Ending one reverses all of it, including restoring the desktop windows that got shuffled
when the display arrangement changed.

Sessions can start from a keyboard shortcut, a controller button combination, the PlayStation or Xbox
Guide button, plugging in a controller, waking the PC, or just launching Steam Big Picture yourself.
Big Picture *is* the session — opening it however you like moves everything to the TV, and closing it
brings your desktop back.

HDR can switch on for the whole session, or only while a game from a list you choose is running, with
the display put back afterwards either way. Whether a display can actually do HDR is read from the
monitor's own EDID rather than from Windows, because Windows reports wide colour gamut and HDR through
the same flag and a plain SDR panel will happily claim it.

## Requirements

Windows 11 is what it is developed and tested against. Windows 10 works; the only difference you will
notice is square window corners instead of rounded ones.

No .NET install is needed. Releases are a single self-contained executable with the runtime inside it.
There is no installer and nothing is written outside `%AppData%\CouchSession`.

## Settings that change Windows itself

Most of what this app does is temporary and undone when a session ends. A few things are not, and they
are worth reading before you switch them on. Every one of them is **off unless you turn it on**, and
each says on its own row what it does and how long it lasts.

**User Account Control** and **Windows Defender Firewall** can both be switched off from the
Performance page. These lower your PC's security and they are not tied to a session — they stay off
until you turn them back on here. They exist because a UAC prompt appears on a secure desktop that a
controller cannot reach, and the firewall's "allow this app?" dialog needs a mouse and blocks a game's
networking until it is answered. Both are dead ends when the only thing in your hand is a gamepad. If
that trade is not one you want, leave them alone — nothing else in the app depends on them.

**Windows Game Mode** is only ever switched on, never back off, because it is a Windows-wide preference
and flipping it back would fight anyone who turned it on themselves.

**The Xbox Game Bar** is switched off for the whole time Couch Session is running, not just during a
session, and put back when you close the app. This also disables background clip recording. If Win+G
has stopped working at your desk, this is why.

The power plan, notification silencing and the running game's priority are all restored when a session
ends.

## Controllers

Xbox, PlayStation and anything else Windows recognises, with no extra software. The controller is
opened for reading only and never exclusively, so it runs alongside Steam Input, DS4Windows and games
rather than competing with them.

Nothing here injects code, hooks another process, reads another process's memory, or synthesises
gamepad input. That is a deliberate design rule, not an accident — it is what keeps the app clear of
anti-cheat. It reads HID devices directly and asks Windows for its own windows, and that is all.

## Building from source

You need the .NET 8 SDK.

```
git clone https://github.com/Sub-Wolfer/couch-session
cd couch-session
dotnet build -c Release
```

For the single-file executable a release ships as:

```
dotnet publish -c Release
```

The console button artwork is not in this repository. Both marks are trademarks and cannot be
redistributed here, so the app draws its own approximations of them; that is the shipping behaviour and
nothing is missing without them. If you have the rights to real artwork, drop `ps-button.png` and
`xbox-button.png` beside the `.csproj` as square transparent PNGs of 128px or larger and they will be
embedded automatically.

## Reporting a bug

The **Report a bug** button in the app's footer is the fastest route — it collects the log from
`%AppData%\CouchSession\couchsession.log` alongside your settings, which is almost always what the
answer is hiding in. Otherwise, open an issue and attach that log.

## Licence

MIT. See [LICENSE](LICENSE).
