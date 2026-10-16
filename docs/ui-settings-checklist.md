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
