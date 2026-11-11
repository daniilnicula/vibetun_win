# UI settings regression checks

## Automated checks

On any platform (Python 3, standard library only):

```sh
python scripts/validate-ui.py
```

Checks the RU/EN catalog, format placeholders, references, XAML, palette keys and defaults.

On Windows with .NET 8 SDK:

```powershell
dotnet build VibeTun.sln -c Release
dotnet run --project tests/VibeTun.Ui.Tests -c Release
```

The standalone STA regression executable links the production services directly, without
third-party test packages. It checks default/custom log retention, immediate trimming,
deduplication, concurrent producers, language bindings and view-model notifications,
interrupted color transitions, Black, device-theme resolution and accent preservation.
It also runs the archive guard (`ZipGuard`) that stands before every unpacking of a
downloaded update or core: a regular archive is extracted byte for byte; a zip bomb, an
entry that escapes the target folder, too many entries, a truncated download and a file
with a wrong checksum are rejected. The choice of the core archive is checked against the
file lists of real releases: the main archive of the architecture wins over its variants
(`-legacy-windows-7`, `-purego`), which sort before it.

The same steps run on GitHub Actions for every push to `arena/**`
(`.github/workflows/build.yml`), followed by a launch smoke test of the published
`VibeTun.exe` (`.github/scripts/ci-smoke.ps1`): it walks through every tab, hovers a hint
icon, pastes a link into a new group through "Add connection" and imports a subscription
from a local listener, checking the result in `data\profile-layout.json`. It then opens the
profile editor (the protocol hint is shown for AmneziaWG and takes no room for VLESS),
installs the core from GitHub with the banner button (the main archive must be taken and
Settings must name its source; network trouble on the runner only skips this check),
captures the settings page in a window of the default size, switches the sidebar blur on
and drags the window by its caption (see `window-ping-checks.md`), and finally restarts
the application with a test panel to follow its browser: unloaded when the page was never
opened, asleep and awake again when it was. Screenshots and the combined log are attached
to the commit as check runs. `[win10]` in the commit message runs the job on
`windows-2022`, where the application takes its Windows 10 path; `[panel-only]` runs the
panel stage alone.

## Logs

1. With a new settings file, generate over 100 distinct entries: the main log list retains
   only the newest 100. Open `app.log`: earlier entries and repetitions must remain there.
2. Set the limit to 10 while the application is running: older displayed entries disappear
   immediately. Set it to 200: new entries accumulate up to 200; trimmed entries do not reappear.
3. Empty, nonnumeric, zero and negative input must not replace the last valid limit.
4. Verify that the file retains original core lines, including timestamps/levels; only the
   displayed copy is shortened/deduplicated. Clear the log panel: the file is unchanged.
5. Restart and verify that the custom limit persists. Reset settings: the limit returns to 100.

## Languages

1. New settings start in Russian. Select English: navigation, every tab, input placeholders,
   tooltips, DNS pinned rows/provider descriptions/filter labels, built-in routing set labels,
   connection state, release dates and tray context menu update without restarting.
2. Open profile import/editor, DNS editor, app picker, confirmations and SSH credentials:
   their interface uses English. Trigger an error/notification and check its language.
3. Switch back to Russian and repeat. The selected page, connection, DNS selection,
   accent and entered text must be preserved. Names/URLs/configurations entered by users,
   website contents, SSH output, external core messages and release notes are not translated.
4. Restart with English selected: English is applied before the main window opens.
   Reset settings: Russian is restored immediately.

## Themes (Windows 10 and 11)

1. Open the accent flyout: **Device / Light / Dark / Black** is directly beneath the color wheel.
   There is no separate dark-theme switch in settings or the caption bar.
2. Click each segment: exactly one remains selected; the marker slides like the DNS tabs.
   Clicking the selected segment again must not deselect it.
3. Light/Dark retain the existing palettes; Black has a truly black window background.
   Text, inputs, cards, dialogs and tooltips remain legible.
4. Switch rapidly: palette colors interpolate from the visible colors, without flashing or
   jumping back after an interrupted animation. The custom accent remains unchanged.
5. Select Device, then change Windows **app mode** between light and dark while VibeTun
   stays open: the palette follows automatically. Explicit Light/Dark/Black ignore OS changes.
6. Restart: the selected theme/accent persist. Verify flyout layout in both languages at
   100% and 150% DPI, normal/maximized, and after hiding/restoring from the tray.

## Hints, system accent, request camouflage, core source

1. Settings: "Use Windows system colors", "Remember state on exit", "Precise latency test
   via core", "When closing the window", "Masquerade as" and "Extended build" have an (i)
   icon next to the label and no grey explanation line under the control. Routing: the same
   for "Default (no rule matched)", TUN mode and the "Domains and IPs" title. DNS: next to
   the title of the DNS used by the DPI bypass.
2. Hover an icon: the tooltip opens after a short delay, wraps at about 380 px instead of
   running off the screen and stays while the pointer is on the icon. Switch the language:
   the hint texts follow it. Section descriptions and warnings remain ordinary text.
3. "Use Windows system colors": the accent follows Windows (Personalization > Colors),
   also when the Windows accent changes while VibeTun is open. The switch in the accent
   flyout and the one in Settings always show the same state. Picking a color in the flyout
   turns the switch off and keeps the picked color after a restart.
4. "Masquerade as": the (i) tooltip shows the User-Agent that requests are signed with
   right now. Auto / Chrome / Firefox / Edge give a current browser User-Agent; "Refresh
   now" updates the browser version and the "Last refreshed" time on the same line.
   "No camouflage (VibeTun)" shows a warning for as long as it is selected. With a capture
   proxy check that the subscription download, the update check, the core download, icon
   downloads and the latency probe all carry the same User-Agent.
5. "sing-box core": under the path of the core there is a line "Build: official / extended
   - source: ..." that names where the installed binary came from: the GitHub repository
   and release, the bundled copy, an archive or a file chosen by the user. Install the core
   in each of these ways and check the line; a core placed in the folder by hand or
   installed by an earlier version shows "not recorded". Without a core there is no line
   and no empty row in its place. "Update from GitHub" on x64 downloads
   `sing-box-<version>-windows-amd64.zip`, not the `-legacy-windows-7` archive (the name is
   in the log).
6. Every scrollable page, list and dialog has the same thin scrollbar with a gap between
   the bar and the content; no page has the bar flush against cards or the window edge.

## Updates and downloaded archives

1. Install an update and a core from GitHub: both succeed, and the log shows no archive or
   checksum errors.
2. Serve a modified release asset through an intercepting proxy: the update stops with the
   SHA-256 mismatch message before anything is unpacked, and the installed application is
   not touched. The expected checksum is the one GitHub publishes for the asset.
3. "Install from archive" with a file that is not a zip, with a truncated zip and with an
   archive containing `..\evil.txt`: each is refused with a message naming the reason,
   and nothing is written outside the core folder.
