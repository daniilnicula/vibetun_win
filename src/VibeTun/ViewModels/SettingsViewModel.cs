using System.Diagnostics;
using System.Globalization;
using System.IO;
using VibeTun.Models;
using VibeTun.Services;
using VibeTun.Utils;
using VibeTun.Views.Controls;

namespace VibeTun.ViewModels;

/// <summary>Вкладка «Настройки»: тема, автозапуск, состояние, ядро, служебные действия.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly CoreService _core;
    private readonly IDialogService _dialogs;
    private bool _busy;

    public SettingsViewModel(SettingsService settings, CoreService core, IDialogService dialogs)
    {
        _settings = settings;
        _core = core;
        _dialogs = dialogs;

        InstallCoreCommand = new AsyncRelayCommand(InstallCoreAsync, () => !Busy);
        RestoreBundledCoreCommand = new AsyncRelayCommand(RestoreBundledCoreAsync, () => !Busy);
        ChooseCoreFileCommand = new RelayCommand(_ => ChooseCoreFile());
        CheckCoreCommand = new AsyncRelayCommand(CheckCoreAsync, () => !Busy);
        InstallFromFileCommand = new AsyncRelayCommand(InstallFromFileAsync, () => !Busy);
        OpenFolderCommand = new RelayCommand(p => OpenFolder(p as string));
        ResetSettingsCommand = new RelayCommand(_ => ResetSettings());
        ChooseCustomFolderCommand = new RelayCommand(_ => PickCustomStorage());
        RestartNowCommand = new RelayCommand(_ => RestartRequested?.Invoke());
    }

    public event Action<string, bool>? Notify;
    public event Action? ThemeChanged;

    /// <summary>
    /// «Сбросить настройки» заменил весь settings.json значениями по умолчанию. MainViewModel
    /// перечитывает раздел «DNS»: его выбор и DNS обхода тоже живут в settings.json.
    /// </summary>
    public event Action? SettingsReset;

    public AsyncRelayCommand InstallCoreCommand { get; }

    /// <summary>Восстановить ядро из вшитой в приложение копии.</summary>
    public AsyncRelayCommand RestoreBundledCoreCommand { get; }
    public RelayCommand ChooseCoreFileCommand { get; }
    public AsyncRelayCommand CheckCoreCommand { get; }
    public AsyncRelayCommand InstallFromFileCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ResetSettingsCommand { get; }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (!SetProperty(ref _busy, value)) return;
            InstallCoreCommand.RaiseCanExecuteChanged();
            CheckCoreCommand.RaiseCanExecuteChanged();
            InstallFromFileCommand.RaiseCanExecuteChanged();
        }
    }

    // ------------------------------------------------------------------- тема
    public bool IsDark
    {
        get => ThemeService.EffectiveMode != ThemeMode.Light;
        set => SetTheme(value ? ThemeMode.Dark : ThemeMode.Light);
    }

    public bool ThemeSystem { get => _settings.Current.Theme == ThemeMode.System; set { if (value) SetTheme(ThemeMode.System); else Raise(); } }
    public bool ThemeLight { get => _settings.Current.Theme == ThemeMode.Light; set { if (value) SetTheme(ThemeMode.Light); else Raise(); } }
    public bool ThemeDark { get => _settings.Current.Theme == ThemeMode.Dark; set { if (value) SetTheme(ThemeMode.Dark); else Raise(); } }
    public bool ThemeBlack { get => _settings.Current.Theme == ThemeMode.Black; set { if (value) SetTheme(ThemeMode.Black); else Raise(); } }

    private void SetTheme(ThemeMode mode)
    {
        if (_settings.Current.Theme == mode) { RaiseTheme(); return; }
        _settings.Current.Theme = mode;
        _settings.SaveSoon();
        ThemeService.Apply(mode);
        RaiseTheme();
        ThemeChanged?.Invoke();
    }

    public string ThemeLabel => IsDark ? L.T("S0145") : L.T("S0144");

    public string Language
    {
        get => _settings.Current.Language;
        set
        {
            if (value is not ("ru" or "en") || value == Language) return;
            _settings.Current.Language = value;
            _settings.SaveSoon();
            L.SetLanguage(value);
            Raise();
        }
    }

    public int LogLimit
    {
        get => _settings.Current.LogLimit;
        set
        {
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), L.T("LogLimitPositive"));
            if (value == LogLimit) return;
            _settings.Current.LogLimit = value;
            _settings.SaveSoon();
            Raise();
        }
    }

    // ------------------------------------------------------------------ анимации
    /// <summary>Множитель скорости анимаций: 0.3 — медленнее, 2 — быстрее.</summary>
    public double AnimationSpeed
    {
        get => _settings.Current.AnimationSpeed;
        set
        {
            var speed = Math.Round(Math.Clamp(value, Motion.MinSpeed, Motion.MaxSpeed), 1);
            if (Math.Abs(_settings.Current.AnimationSpeed - speed) < 0.001) return;
            _settings.Current.AnimationSpeed = speed;
            _settings.SaveSoon();
            Motion.SetSpeed(speed);
            Raise();
            Raise(nameof(AnimationSpeedLabel));
        }
    }

    public string AnimationSpeedLabel =>
        AnimationSpeed.ToString("0.0", CultureInfo.InvariantCulture) + "×";

    // ---------------------------------------------------------------- поведение
    public bool RunAtStartup
    {
        get => _settings.Current.RunAtStartup;
        set
        {
            if (_settings.Current.RunAtStartup == value) return;
            _settings.Current.RunAtStartup = value;
            AutostartService.Apply(value, _settings.Current.StartMinimized);
            _settings.SaveSoon();
            Raise();
        }
    }

    public bool StartMinimized
    {
        get => _settings.Current.StartMinimized;
        set
        {
            if (_settings.Current.StartMinimized == value) return;
            _settings.Current.StartMinimized = value;
            if (_settings.Current.RunAtStartup) AutostartService.Apply(true, value);
            _settings.SaveSoon();
            Raise();
        }
    }

    public CloseBehavior CloseBehavior
    {
        get => _settings.Current.CloseBehavior;
        set
        {
            if (_settings.Current.CloseBehavior == value) return;
            _settings.Current.CloseBehavior = value;
            _settings.SaveSoon();
            Raise();
        }
    }

    /// <summary>Режим закрытия изменён извне (диалог «Скрыть или выйти» запомнил выбор) —
    /// поднимаем PropertyChanged, чтобы комбобокс в настройках перечитал значение.</summary>
    public void OnCloseBehaviorChangedExternally() => Raise(nameof(CloseBehavior));

    public bool RememberState
    {
        get => _settings.Current.RememberState;
        set
        {
            if (_settings.Current.RememberState == value) return;
            _settings.Current.RememberState = value;
            _settings.SaveSoon();
            Raise();
        }
    }

    public bool PreciseLatencyTest
    {
        get => _settings.Current.PreciseLatencyTest;
        set
        {
            if (_settings.Current.PreciseLatencyTest == value) return;
            _settings.Current.PreciseLatencyTest = value;
            _settings.SaveSoon();
            Raise();
        }
    }

    public bool AutoUpdateCore
    {
        get => _settings.Current.AutoUpdateCore;
        set
        {
            if (_settings.Current.AutoUpdateCore == value) return;
            _settings.Current.AutoUpdateCore = value;
            _settings.SaveSoon();
            Raise();
        }
    }

    // ---------------------------------------------------------------- ядро
    public CoreFlavor CoreFlavor
    {
        get => _settings.Current.CoreFlavor;
        set
        {
            if (_settings.Current.CoreFlavor == value) return;
            _settings.Current.CoreFlavor = value;
            _settings.SaveSoon();
            Raise();
            Raise(nameof(IsExtendedFlavor));
        }
    }

    public bool IsExtendedFlavor
    {
        get => _settings.Current.CoreFlavor == CoreFlavor.SingBoxExtended;
        set
        {
            var flavor = value ? CoreFlavor.SingBoxExtended : CoreFlavor.SingBox;
            if (_settings.Current.CoreFlavor == flavor) return;
            _settings.Current.CoreFlavor = flavor;
            _settings.SaveSoon();
            Raise();
            Raise(nameof(CoreFlavor));

            // переключаемся на ядро выбранного варианта, если оно уже есть на диске
            _ = SwitchCoreAsync(flavor);
        }
    }

    private async Task SwitchCoreAsync(CoreFlavor flavor)
    {
        var extended = flavor == CoreFlavor.SingBoxExtended;
        var expected = extended ? AppPaths.CoreExtendedBinary : AppPaths.CoreBinary;
        var bundled = extended ? BundledCores.HasExtended : BundledCores.HasOfficial;

        // что берём: файл варианта в каталоге ядра или путь, который пользователь указал сам
        var source = FindCoreFile(extended, _settings.Current.CorePath);

        // ядра выбранного варианта нет ни там, ни там, но оно вшито в приложение — распаковываем
        if (source is null && bundled)
        {
            IProgress<string> unpack = new Progress<string>(s => Notify?.Invoke(s, false));
            await CoreInstaller.InstallBundledAsync(_core, flavor, unpack);
            source = FindCoreFile(extended, _settings.Current.CorePath);
        }

        // иначе переключатель «Расширенная сборка» молча ничего не делает: предлагаем скачать
        if (source is null)
        {
            var download = _dialogs.Confirm(L.T("S0841"),
                extended
                    ? L.T("S0842") +
                      L.F("S0843", CoreInstaller.AwgRepo)
                    : L.T("S0844"),
                L.T("S0725"), danger: false);

            if (!download)
            {
                Notify?.Invoke(extended
                    ? L.T("S0845")
                    : L.T("S0841"), true);
                RefreshCoreInfo();
                return;
            }

            Busy = true;
            try
            {
                IProgress<string> progress = new Progress<string>(s => Notify?.Invoke(s, false));
                var downloaded = flavor == CoreFlavor.SingBoxExtended
                    ? await CoreInstaller.InstallAwgCoreAsync(_core, progress)
                    : await CoreInstaller.InstallLatestAsync(_core, flavor, progress);
                if (downloaded is not { Exists: true })
                {
                    Notify?.Invoke(L.T("S0846"), true);
                    return;
                }
            }
            catch (Exception ex)
            {
                _dialogs.Error(L.T("S0847"), ex.Message + "\n\n" + CoreInstaller.ManualHint);
                return;
            }
            finally
            {
                Busy = false;
            }

            source = File.Exists(expected) ? expected : _settings.Current.CorePath;
        }

        var info = await _core.DetectAsync(source, flavor);
        if (info is not { Exists: true })
        {
            Notify?.Invoke(L.T("S0848"), true);
            return;
        }

        // строка версии — не гарантия: спрашиваем у самого ядра, понимает ли оно обфускацию
        if (extended) await _core.ProbeAmneziaSupportAsync(force: true);

        _settings.Current.CorePath = info.Path;
        _settings.Current.CoreVersion = info.Version;
        _settings.SaveSoon();
        RefreshCoreInfo();

        Notify?.Invoke(extended
            ? (_core.Info.SupportsAmnezia
                ? L.T("S0849")
                : L.T("S0850")
                  + (string.IsNullOrWhiteSpace(_core.Info.EndpointError) ? "" : ": " + _core.Info.EndpointError))
            : L.T("S0851") + _core.Info.VersionNumber,
            extended && !_core.Info.SupportsAmnezia);
    }

    /// <summary>
    /// Ищем файл ядра нужного варианта: сначала стандартное имя в каталоге ядра, затем путь
    /// из настроек (если по имени он подходит под выбранный вариант).
    /// </summary>
    private static string? FindCoreFile(bool extended, string? configured)
    {
        var expected = extended ? AppPaths.CoreExtendedBinary : AppPaths.CoreBinary;
        if (File.Exists(expected)) return expected;

        if (string.IsNullOrWhiteSpace(configured) || !File.Exists(configured)) return null;

        var name = Path.GetFileName(configured);
        var looksExtended = name.Contains("extended", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("awg", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("-lx", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("amnezia", StringComparison.OrdinalIgnoreCase);
        return looksExtended == extended ? configured : null;
    }

    public string CoreVersionText => _core.Info.Exists
        ? _core.Info.Version
        : L.T("S0311");

    public string CorePathText => _core.Info.Exists ? _core.Info.Path : CoreInstaller.ManualHint;

    /// <summary>Есть ли в этой сборке приложения вшитые ядра.</summary>
    public bool HasBundledCores => BundledCores.HasOfficial || BundledCores.HasExtended;

    public string BundledCoresText => (BundledCores.HasOfficial, BundledCores.HasExtended) switch
    {
        (true, true) => L.T("S0852"),
        (true, false) => L.T("S0853"),
        (false, true) => L.T("S0854"),
        _ => L.T("S0855")
    };

    public bool CoreMissing => !_core.Info.Exists;

    public string CoreSupportText
    {
        get
        {
            if (!_core.Info.Exists) return L.T("S0856");

            var parts = new List<string>
            {
                L.F("S0857", (_core.Info.SupportsModernRules ? L.T("Extra952") : L.T("Extra953"))),
                L.F("S0858", (_core.Info.SupportsEndpoints ? L.T("Extra952") : L.T("Extra953"))),
                L.F("S0859", (_core.Info.SupportsAnyAwg ? L.T("Extra952") : L.T("Extra953"))) +
                (_core.Info.AmneziaProbed is null ? L.T("S0860") : L.T("S0861")),
                L.F("S0862", (_core.Info.SupportsAwgTrailers ? L.T("Extra952") : L.T("Extra953")))
            };
            var text = string.Join(" · ", parts);

            // от схемы конфига зависит, какие параметры обфускации вообще удастся передать
            if (_core.Info.SupportsAwgFlat)
                text += L.T("S0863") +
                        L.T("S0864");
            else if (_core.Info.SupportsAwgEndpoint)
                text += L.T("S0865");
            else if (_core.Info.SupportsAmnezia)
                text += L.T("S0866") +
                        L.T("S0867");

            if (!string.IsNullOrWhiteSpace(_core.Info.EndpointError))
                text += L.T("S0868") + _core.Info.EndpointError;

            return text;
        }
    }

    // --------------------------------------------------- место хранения данных

    /// <summary>Нужен перезапуск, чтобы выбранное место хранения вступило в силу.</summary>
    public event Action? RestartRequested;

    public RelayCommand ChooseCustomFolderCommand { get; }
    public RelayCommand RestartNowCommand { get; }

    public bool StorageIsLocal
    {
        get => StorageLocation.Config.Mode == StorageMode.Local;
        set
        {
            if (value) ApplyStorage(StorageMode.Local, null);
            else RaiseStorage();
        }
    }

    public bool StorageIsExe
    {
        get => StorageLocation.Config.Mode == StorageMode.Exe;
        set
        {
            if (value) ApplyStorage(StorageMode.Exe, null);
            else RaiseStorage();
        }
    }

    public bool StorageIsCustom
    {
        get => StorageLocation.Config.Mode == StorageMode.Custom;
        set
        {
            if (value) PickCustomStorage();
            else RaiseStorage();
        }
    }

    public bool IsCustomStorage => StorageLocation.Config.Mode == StorageMode.Custom;

    public string LocalDataDirText => StorageLocation.LocalDataDir;

    public string ExeDataDirText => StorageLocation.ExeDataDir;

    public string CustomDataDirText => string.IsNullOrWhiteSpace(StorageLocation.Config.CustomPath)
        ? L.T("S0869")
        : StorageLocation.Config.CustomPath!;

    /// <summary>Где данные лежат сейчас.</summary>
    public string CurrentDataRootText => L.T("S0870") + AppPaths.DataRoot;

    /// <summary>Выбрано другое место: оно применится после перезапуска.</summary>
    public bool StoragePending => StorageLocation.IsPending;

    public string StoragePendingText =>
        L.T("S0871") + StorageLocation.DesiredRoot +
        L.T("S0872");

    public bool HasStorageNotice => !string.IsNullOrEmpty(StorageLocation.FallbackReason);

    public string StorageNoticeText => StorageLocation.FallbackReason ?? "";

    private void PickCustomStorage()
    {
        var start = StorageLocation.Config.CustomPath;
        var picked = _dialogs.PickFolder(L.T("S0873"),
            string.IsNullOrWhiteSpace(start) ? AppPaths.DataRoot : start);
        if (string.IsNullOrWhiteSpace(picked))
        {
            RaiseStorage();   // отмена: переключатель возвращаем к прежнему варианту
            return;
        }
        ApplyStorage(StorageMode.Custom, picked);
    }

    private void ApplyStorage(StorageMode mode, string? customPath)
    {
        var error = StorageLocation.TryChange(mode, customPath);
        RaiseStorage();
        if (error is not null)
        {
            _dialogs.Error(L.T("S0874"), error);
            return;
        }
        if (!StorageLocation.IsPending) return;

        Notify?.Invoke(L.T("S0875"), false);
        var restartNow = _dialogs.Confirm(
            L.T("S0876"),
            L.T("S0877"),
            L.T("S0243"),
            danger: false);
        if (restartNow) RestartRequested?.Invoke();
    }

    private void RaiseStorage()
    {
        Raise(nameof(StorageIsLocal));
        Raise(nameof(StorageIsExe));
        Raise(nameof(StorageIsCustom));
        Raise(nameof(IsCustomStorage));
        Raise(nameof(CustomDataDirText));
        Raise(nameof(CurrentDataRootText));
        Raise(nameof(StoragePending));
        Raise(nameof(StoragePendingText));
        Raise(nameof(HasStorageNotice));
        Raise(nameof(StorageNoticeText));
    }

    /// <summary>Подтянуть переключатель варианта ядра после его программной смены.</summary>
    public void RaiseCoreFlavor()
    {
        Raise(nameof(CoreFlavor));
        Raise(nameof(IsExtendedFlavor));
    }

    public void RefreshCoreInfo()
    {
        Raise(nameof(CoreVersionText));
        Raise(nameof(CorePathText));
        Raise(nameof(CoreMissing));
        Raise(nameof(CoreSupportText));
    }

    /// <summary>Обновить зависимые свойства темы (вызывается из главного VM).</summary>
    public void RaiseTheme()
    {
        Raise(nameof(IsDark));
        Raise(nameof(ThemeLabel));
        Raise(nameof(ThemeSystem));
        Raise(nameof(ThemeLight));
        Raise(nameof(ThemeDark));
        Raise(nameof(ThemeBlack));
    }

    /// <summary>Публичная обёртка установки ядра (для кнопки в статус-баре).</summary>
    public Task InstallCorePublicAsync() => InstallCoreAsync();

    /// <summary>Переустановить ядро из вшитой в приложение копии.</summary>
    private async Task RestoreBundledCoreAsync()
    {
        Busy = true;
        try
        {
            IProgress<string> progress = new Progress<string>(s => Notify?.Invoke(s, false));
            var flavor = _settings.Current.CoreFlavor;
            var info = await CoreInstaller.InstallBundledAsync(_core, flavor, progress);

            if (info is { Exists: true })
            {
                _settings.Current.CorePath = info.Path;
                _settings.Current.CoreVersion = info.Version;
                _settings.SaveSoon();
                RefreshCoreInfo();
                Notify?.Invoke(L.T("S0878") + info.Version, false);
            }
            else
            {
                Notify?.Invoke(L.T("S0879"), true);
            }
        }
        catch (Exception ex)
        {
            _dialogs.Error(L.T("S0880"), ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task CheckCoreAsync()
    {
        Busy = true;
        try
        {
            await _core.DetectAsync(_settings.Current.CorePath);
            // точная проверка AmneziaWG: спрашиваем у самого ядра, а не у строки версии
            if (_core.Info.SupportsEndpoints) await _core.ProbeAmneziaSupportAsync(force: true);

            _settings.Current.CoreVersion = _core.Info.Version;
            _settings.SaveSoon();
            RefreshCoreInfo();
            Notify?.Invoke(_core.Info.Exists ? L.T("S0881") + _core.Info.Version : L.T("S0311"), !_core.Info.Exists);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task InstallCoreAsync()
    {
        Busy = true;
        try
        {
            IProgress<string> progress = new Progress<string>(s => Notify?.Invoke(s, false));
            progress.Report(L.T("S0882"));
            var flavor = _settings.Current.CoreFlavor;
            var info = flavor == CoreFlavor.SingBoxExtended
                ? await CoreInstaller.InstallAwgCoreAsync(_core, progress)
                : await CoreInstaller.InstallLatestAsync(_core, flavor, progress);
            if (info is { Exists: true })
            {
                if (flavor == CoreFlavor.SingBoxExtended) await _core.ProbeAmneziaSupportAsync(force: true);

                _settings.Current.CorePath = info.Path;
                _settings.Current.CoreVersion = info.Version;
                _settings.SaveSoon();
                RefreshCoreInfo();
                Notify?.Invoke(L.T("S0883") + info.Version, false);
            }
            else
            {
                Notify?.Invoke(L.T("S0884"), true);
            }
        }
        catch (Exception ex)
        {
            _dialogs.Error(L.T("S0847"), ex.Message + "\n\n" + CoreInstaller.ManualHint);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task InstallFromFileAsync()
    {
        var path = _dialogs.OpenFile(L.T("S0885"));
        if (path is null) return;

        Busy = true;
        try
        {
            CoreInfo? info;
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(AppPaths.CoreDir);
                File.Copy(path, AppPaths.CoreBinary, true);
                info = await _core.DetectAsync(AppPaths.CoreBinary);
            }
            else
            {
                info = await CoreInstaller.InstallFromZipAsync(_core, path);
            }

            if (info is { Exists: true })
            {
                _settings.Current.CorePath = info.Path;
                _settings.Current.CoreVersion = info.Version;
                _settings.SaveSoon();
                RefreshCoreInfo();
                Notify?.Invoke(L.T("S0883") + info.Version, false);
            }
        }
        catch (Exception ex)
        {
            _dialogs.Error(L.T("S0884"), ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    private void ChooseCoreFile()
    {
        var path = _dialogs.OpenFile(L.T("S0886"));
        if (path is null) return;

        _settings.Current.CorePath = path;
        _settings.SaveSoon();
        _ = CheckCoreAsync();
    }

    private void OpenFolder(string? kind)
    {
        var path = kind switch
        {
            "core" => AppPaths.CoreDir,
            "logs" => AppPaths.LogsDir,
            _ => AppPaths.DataRoot
        };
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _dialogs.Error(L.T("S0887"), ex.Message);
        }
    }

    private void ResetSettings()
    {
        if (!_dialogs.Confirm(L.T("S0246"), L.T("S0888"),
                L.T("S0889"))) return;

        var installedTag = _settings.Current.InstalledReleaseTag;
        var installedSha = _settings.Current.InstalledExeSha256;
        var fresh = new AppSettings();
        foreach (var prop in typeof(AppSettings).GetProperties())
            prop.SetValue(_settings.Current, prop.GetValue(fresh));
        _settings.Current.InstalledReleaseTag = installedTag;
        _settings.Current.InstalledExeSha256 = installedSha;
        _settings.SaveSoon();
        ThemeService.Apply(_settings.Current.Theme);
        ThemeService.ApplyAccent(_settings.Current.AccentColor);
        L.SetLanguage(_settings.Current.Language);
        Raise(null);
        AutostartService.Apply(false, false);
        Motion.SetSpeed(_settings.Current.AnimationSpeed);
        Notify?.Invoke(L.T("S0890"), false);
        ThemeChanged?.Invoke();
        RefreshCoreInfo();
        Raise(nameof(IsDark));
        Raise(nameof(ThemeLabel));
        Raise(nameof(ThemeSystem));
        Raise(nameof(ThemeLight));
        Raise(nameof(ThemeDark));
        Raise(nameof(ThemeBlack));
        SettingsReset?.Invoke();
    }
}
