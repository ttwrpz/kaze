# Kaze for airCloud Go

**Kaze** (風, "wind") is a small Windows 11 tray app for Hitachi air conditioners that use the
**airCloud Go** Wi-Fi service, such as the RAK-DH10PCAST. Change the temperature, mode, fan and swing
from the taskbar, set timers and schedules, follow the room temperature and check energy use,
without picking up your phone.

> [!IMPORTANT]
> **Kaze is an independent, unofficial project. It is not made, endorsed or supported by Hitachi.**
> Hitachi and airCloud Go are trademarks of their owners and are named here only to say what Kaze
> works with. Please don't contact Hitachi support about Kaze.
>
> Kaze signs in to the airCloud Go cloud service the same way the phone app does, using your own
> account. That service has no public API, so Hitachi can change it at any time and Kaze may stop
> working until it is updated. Use it at your own risk.

### What Kaze sends where

- **Your airCloud Go email and password** go only to Hitachi's airCloud Go servers, to sign in.
  The password is kept in Windows Credential Manager, never in a file or a log.
- **Weather** (optional): only the coordinates of the town you pick go to [Open-Meteo](https://open-meteo.com).
- **Update checks** (optional): Kaze reads the latest release of this repository from GitHub.
- No analytics, no telemetry, no accounts of its own. The local HTTP API is off by default.

### Before you use it

> [!WARNING]
> **Use Kaze at your own risk.** It is provided as is, without warranty of any kind (see [LICENSE](LICENSE)).
> The author is not responsible for damage, extra energy costs, discomfort or account problems
> that come from using it.

- **It can break without warning.** Kaze depends on an undocumented cloud service. A change on
  Hitachi's side can stop it working, or make a command behave differently, until Kaze is updated.
- **Your airCloud Go account.** Using an unofficial app may go against airCloud Go's terms of
  service, and Hitachi could limit or block accounts that do. Kaze keeps its requests modest, but
  this is a risk only you can accept.
- **Don't rely on it for anything important.** Timers, schedules, the sleep curve and automations run
  on your PC. They don't run while the PC is off, asleep or offline, and a command can fail if the
  cloud is down. Don't use Kaze where health or safety depends on the air conditioner, for example
  to keep a room cool for a baby, an elderly or unwell person, pets or equipment.
- **It controls a real appliance.** Commands, schedules and the local API can turn the unit on while
  nobody is home. Check your schedules, and keep the local API token private, because it gives full
  control of your units.
- **Frost Wash** starts the unit's own cleaning cycle. Follow your air conditioner's manual.
- **Energy figures** come from Hitachi's estimates and are not suitable for billing.

Kaze needs an internet connection, because it works through the airCloud Go cloud service.

## Features

- **Tray icon** in the Windows 11 style: the set temperature in the taskbar's text colour, with a small bar
  in the mode colour underneath. It shows the unit outline when the unit is off and a warning badge when
  it is offline. It follows the light or dark taskbar and is drawn at the right size for your display scale.
  The tooltip shows the room temperature, set temperature, mode and fan.
- **Quick menu** (right-click): a Windows 11 style menu (WinUI, acrylic, Fluent icons) with power, warmer/cooler, mode, fan speed, swing, presets, off timer, Frost Wash, unit switch and "turn all off".
- **Flyout** (left-click): power button, a large +/− temperature control, mode, fan and swing buttons, presets, an off timer and Frost Wash.
- **Frost Wash**: starts the indoor coil self cleaning cycle on units that report it, after a confirmation. Controls are locked while it runs.
- **Fault notice** when the unit reports an error code.
- **Main window**: Control (all units), Schedules and timers, Energy (kWh/cost per month), Automations and presets, Settings.
  Follows the Windows 11 design guidance: Mica, the WinUI title bar, Fluent icons, the type ramp and settings cards.
  The layout is responsive: the navigation pane collapses to icons and then to a menu button, and pages
  switch to one column with 16 px margins on narrow windows.
- **Timers and weekly schedules**, run by this PC (the PC must be on and the app running).
- **Global hotkeys** (defaults): `Ctrl+Alt+P` power, `Ctrl+Alt+↑/↓` temperature, `Ctrl+Alt+A` quick panel.
- **Smart automations**: turn off when the PC locks (with a delay) or sleeps, turn back on at unlock,
  and show notifications for changes made from the phone app, offline units and schedules that ran.
- **Several airCloud accounts**: add accounts and switch between them from the title bar, the tray menu,
  Settings or the command line (`--account`). Each password is stored separately in Windows Credential Manager.
- **Presets**, editable, for example "Sleep: 26° Silent".
- **Room temperature history**: a 24 hour and 7 day chart on the Control page (room temperature and set point,
  with a hover readout) and a small trend line next to the room temperature. Kaze records a sample every
  5 minutes while it runs and keeps 90 days in `%LOCALAPPDATA%\Kaze\history`.
- **Sleep curve**: relaxes the set point overnight (by default +1° after 1 hour and +2° after 3 hours when cooling,
  colder when heating), lowers the fan and turns the unit off after 8 hours. Start it from the Control page,
  the tray, the taskbar jump list or `--sleep`. The steps are set in Automations.
- **Weather**: pick your town in Automations and the Control page shows the outdoor temperature from
  [Open-Meteo](https://open-meteo.com) (no account needed, only the chosen coordinates are sent).
  Schedules can run only when it is at least or at most a given temperature outside, for example
  "cool to 25° at 17:30 on weekdays, only if it is 30° or warmer outside".
- **Reminders**: a Frost Wash reminder every 4 weeks (with Start and "remind me in a week" buttons),
  a monthly energy budget with alerts at 80% and 100%, and a warning 5 minutes before an off timer.
- **Notification buttons**: "Turn back on" after an automation or timer turns the unit off,
  "Snooze 30 minutes" and "Cancel" on off timers, "Install" on updates.
- **Taskbar jump list**: right-click the taskbar or Start menu icon for Turn on, Turn off, Start sleep curve,
  Quick panel and your presets.
- **Thai and English**: follows the Windows display language, or choose one in Settings (applies after a restart).
- **Local HTTP API** for Stream Deck, PowerToys, Home Assistant and scripts (off by default, see below).
- **Installer and updates**: a per-user setup program (no admin rights), an Apps & features entry,
  and an update check against the project's GitHub releases with one-click install.
- **Command line** for scripts and Task Scheduler. A second launch forwards its arguments to the running instance:

  ```
  Kaze.exe --power on|off|toggle
  Kaze.exe --temp 25        (or --temp +1 / --temp -0.5)
  Kaze.exe --mode cool|heat|dry|fan|auto --fan auto|1..6 --swing off|vertical
  Kaze.exe --preset "Sleep" [--unit "Bedroom" | --all]
  Kaze.exe --sleep [--unit "Bedroom"]
  Kaze.exe --account "home@example.com" --power off
  Kaze.exe --show | --flyout | --minimized | --exit
  ```
- Start with Windows (minimized to the tray), light/dark theme, multiple units.

Your password is kept in **Windows Credential Manager** (`Kaze/airCloudGo`). Settings live in
`%LOCALAPPDATA%\Kaze\settings.json` and logs in `%LOCALAPPDATA%\Kaze\logs`.

## Local API

Turn it on in **Settings > Local API**. It listens on `http://localhost:8765` (port configurable).
Every request needs the token shown there, as the header `Authorization: Bearer <token>`.
Read-only `GET` requests also accept `?token=<token>` for tools that can only open a URL, but
anything that changes a unit needs the header. Treat the token like a password: it gives full
control of your units, and you can make a new one in Settings at any time.

By default only programs on this PC can connect. "Allow devices on my network" opens it to devices
with a private (home network) address, for example Home Assistant. Plain HTTP is not encrypted, so
use it only on a network you trust, and allow Kaze through the firewall for private networks only.

| Request | What it does |
|---|---|
| `GET /api` | App version and connection state |
| `GET /api/units` | All units with their current state |
| `GET /api/units/{unit}` | One unit. `{unit}` is the name, id or `selected` |
| `POST /api/units/{unit}/control` | Change settings, body below |
| `POST /api/units/{unit}/sleep` | Start the sleep curve (`?cancel=1` stops it) |
| `POST /api/units/{unit}/frost-wash` | Start Frost Wash |
| `GET /api/units/{unit}/history?hours=24` | Room temperature samples |
| `GET /api/presets` | Saved presets |
| `POST /api/presets/{name}` | Run a preset |

The control body takes any of `power` (`"on"`, `"off"`, `"toggle"`, `true`, `false`),
`mode` (`cool`, `heat`, `dry`, `fan`, `auto`), `fan` (`auto`, `1` to `6`),
`swing` (`off`, `vertical`, `horizontal`, `both`) and `temperature` (`25`, or `"+1"` / `"-0.5"`).

```powershell
$h = @{ Authorization = "Bearer <token>" }
Invoke-RestMethod http://localhost:8765/api/units -Headers $h
Invoke-RestMethod http://localhost:8765/api/units/selected/control -Method Post -Headers $h `
    -ContentType application/json -Body '{"power":"on","mode":"cool","temperature":25}'
```

Home Assistant example (`configuration.yaml`, with "Allow devices on my network" on):

```yaml
rest_command:
  study_ac_cool:
    url: "http://192.168.1.10:8765/api/units/selected/control"
    method: post
    headers:
      Authorization: !secret kaze_token
    content_type: application/json
    payload: '{"power":"on","mode":"cool","temperature":25}'
```

## Install

Download `Kaze-Setup-<version>.exe` from the [releases page](https://github.com/ttwrpz/kaze/releases) and run it. It installs for your
Windows account into `%LOCALAPPDATA%\Programs\Kaze`, adds a Start menu shortcut and an
Apps & features entry, and needs no admin rights. Uninstall from Apps & features. Your settings,
history and saved passwords are kept.

Prefer nothing installed? Each release also has:

- `Kaze-<version>-portable.exe`: the whole app in one file. Run it from anywhere. The first start
  takes a few seconds longer while it unpacks itself to `%TEMP%\.net\Kaze`.
- `Kaze-<version>-win-x64.zip`: the same app as a folder. Unzip and run `Kaze.exe`.

Both keep settings in the same place as the installed app. Their in-app update installs the regular
version.

Setup switches: `/silent` (no questions, used by winget) and `/relaunch` (start the app afterwards,
used by the in-app updater). Kaze checks this repository for new releases once a day. Turn that
off, or point it at a fork, in **Settings > Updates**.

## Build and run

Requirements: Windows 10 1809+ / Windows 11, and the .NET 10 SDK.

```powershell
dotnet test --project tests/Kaze.Core.Tests
dotnet run  --project src/Kaze.App -p:Platform=x64

# Self-contained build that runs from a folder, with no install step:
dotnet publish src/Kaze.App -c Release -r win-x64 -p:Platform=x64 -o publish/win-x64

# Release build (setup, portable exe, zip, winget manifests) in ./artifacts
./build/publish.ps1 -Version 1.1.0
```

Release builds trim the unused parts of .NET and the Windows App SDK and keep only the English and
Thai system resources, so the app folder is about half its untrimmed size.

To publish a release, bump `<Version>` and push a tag such as `v1.1.0`. The release workflow builds,
signs and publishes the setup, its `.sig`, the portable exe and the zip. The in-app updater installs
only `Kaze-Setup-<version>.exe` with a valid signature. The winget manifests are kept as a build
artifact of the run, ready for a pull request to
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs).

Translations live in `src/Kaze.App/Strings`. `en.json` holds the XAML text keys and `th.json`
maps both those keys and the English strings used in code to Thai. A test checks that every key is
translated and that `{0}` placeholders match.

## Probe tool (API diagnostics)

`tools/Kaze.Probe` signs in and dumps what airCloud returns for your account (homes, units,
model capabilities, energy, and raw WebSocket frames) to `./probe-output`. Tokens are never written.
Use it when something looks wrong, or after Hitachi changes the API.

```powershell
dotnet run --project tools/Kaze.Probe                        # dump everything
dotnet run --project tools/Kaze.Probe -- watch 120           # stream live state pushes for 120 s
dotnet run --project tools/Kaze.Probe -- set --temp 25       # send a test command
```

## Architecture

```
src/Kaze.Core      UI-free: airCloud client (REST + STOMP WebSocket), AcController
                         (optimistic updates, debounce, stale-push guard), Scheduler, command line parser
src/Kaze.App       WinUI 3 app: tray (H.NotifyIcon), flyout/main windows (WinUIEx), services
tools/Kaze.Probe   console diagnostics
tools/Kaze.Setup   per-user installer, carries the published app as an embedded zip
build/publish.ps1        release build
tests/                   xUnit v3 tests for Core
```

Backends implement `IAcBackend`. Another backend can be added next to `AirCloudBackend` later
without touching the UI.

## License

Kaze is released under the [MIT License](LICENSE). It comes with no warranty.

Hitachi and airCloud Go are trademarks of their respective owners. Kaze is not affiliated with,
endorsed by or supported by Hitachi, and uses these names only to describe compatibility.

## Credits

The API details come from community work, in particular
[svmironov/aircloud_ha](https://github.com/svmironov/aircloud_ha) (MIT).

Icons in the app and its taskbar actions come from
[Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) (MIT, Copyright (c) 2020 Microsoft Corporation).
