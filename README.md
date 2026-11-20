# Tungsten

A controller-first quick menu for Windows. Press the Home / Xbox button on your controller and a compact,
Xbox-style bar drops in at the top of the screen. It works over fullscreen and borderless games, including
games that run as administrator.

<p align="center"><img src="Assets/Tungsten.png" width="96" alt="Tungsten icon"></p>

## What it does

- **Replaces the Windows controller bar.** Tungsten turns off Game Bar's "open with controller button"
  setting and keeps it off, then takes over the Home button itself.
- **Your games.** Pin games (`.exe`, shortcuts, or Steam `.url` files) with the **+** tile. Steam games
  get their library art. A glowing dot marks games that are running.
- **Options per game.** Press **Menu** on a game to resume, minimize, play, reorder, show the file, unpin,
  close or force quit it.
- **Minimize game.** Minimizes whatever you're playing into its tile. Select the tile to jump back in.
- **Blackout.** The button between Minimize and Settings covers every monitor in opaque black and
  hides the cursor. Press **Y** anywhere in the open bar to activate it, or select the button.
  Press **Xbox/Home or Y** to end it. Ordinary keys, mouse clicks, and Alt+F4 do not dismiss it.
  Ctrl+Alt+Delete / a secure-desktop switch, Task Manager, closing the bar process, and the hard
  eight-hour limit clear it for recovery. It is never saved or restored at startup. A separate,
  temporary helper owns the windows and checks recovery conditions even if the bar UI hangs.
- **Bluetooth.** A live-scanning device list. Connect, disconnect, pair and forget devices, and toggle
  the radio.
- **Wi-Fi.** Scans for networks and connects to them, with an on-screen controller keyboard for
  passwords.
- **Volume.** Between Bluetooth and Wi-Fi: choose a connected playback device, adjust its volume,
  and mute it. With the volume row selected, left/right changes volume in 5% steps; the slider
  also works with a mouse. The list updates while open. Selection changes Windows' default output
  for playback and communications; apps set to a specific device may need their own output changed.
- **Settings.** Close or force quit the current game, start with Windows, admin mode, a controller
  test screen, and quit.

## Controls

| Button | Action |
| --- | --- |
| Home / Xbox, or **Win+Esc** on the keyboard | Open or close Tungsten |
| D-pad / left stick | Move |
| LB / RB | Jump between the left actions, the games and the right actions |
| A | Select |
| Y (bar open) | Black out all monitors |
| Home / Xbox or Y (blackout active) | End blackout |
| B | Back / close |
| Menu, X or Down | Options for the selected tile |

The keyboard works too: arrow keys move, Enter/Space selects, Esc/Backspace goes back, the context-menu
key or M opens options, and Page Up/Page Down jump between groups. The shortcut can be changed with
`"Hotkey"` in `%APPDATA%\Tungsten\config.json` (e.g. `"Ctrl+Shift+Space"`, `"Alt+F1"`).

## Admin mode

Windows doesn't let a normal program take focus from, draw over or minimize a game that runs as
administrator (GTA V, for example). Turn on **Settings → Admin mode** once and accept the Windows
prompt. Tungsten then:

- installs itself to `C:\Program Files\Tungsten`, where only administrators can change it;
- registers a `Tungsten` scheduled task that starts it with highest privileges, so later starts don't
  prompt;
- still launches games as your normal user, through Explorer.

Games in exclusive fullscreen minimize themselves when anything else takes focus. Tungsten shows over
the desktop and puts the game back when you close the menu. Use borderless/windowed-fullscreen in a game
to see the menu on top of the game itself.

## Build

Requires Windows 10 2004+ and the .NET 8 SDK.

```powershell
dotnet publish -c Release -o "$env:LOCALAPPDATA\Programs\Tungsten"
& "$env:LOCALAPPDATA\Programs\Tungsten\Tungsten.exe" --show
```

Command-line options:

- `--show`: open the menu on start.
- `--background`: start in the tray.
- `--quit`: stop the running copy.
- `--install-admin` / `--uninstall-admin`: turn admin mode on or off.
- `--diag`: write a hardware report to `%APPDATA%\Tungsten\diag.txt`.
- `--press A,B,…`: send virtual controller presses to the running copy, for testing.

Settings and logs live in `%APPDATA%\Tungsten`.

## How it works

- **Home button:** read with XInput's hidden `XInputGetStateEx` (ordinal 100), the only XInput call that
  reports the Guide button.
- **Overlay:** a transparent, topmost WPF window on the monitor of the game in front.
- **Bluetooth and Wi-Fi:** WinRT `DeviceWatcher` / `DeviceInformationCustomPairing` and `WiFiAdapter`.
  Bluetooth audio devices connect through the kernel-streaming `KSPROPSETID_BtAudio` property.

## Credits

The minimize icon is from [Myna UI Icons](https://github.com/praveenjuge/mynaui-icons) by Praveen Juge
(MIT).
