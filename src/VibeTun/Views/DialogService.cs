using System.Collections.Generic;
using System.Windows;
using VibeTun.Models;
using VibeTun.Services;
using VibeTun.Views.Dialogs;

namespace VibeTun.Views;

/// <summary>Реализация диалогов поверх WPF-окон приложения.</summary>
public sealed class DialogService : IDialogService
{
    private readonly Window _owner;

    public DialogService(Window owner) => _owner = owner;

    public bool EditProfile(ProxyProfile profile, LatencyService latency)
    {
        var window = new ProfileEditorWindow(profile, latency) { Owner = _owner };
        return window.ShowDialog() == true;
    }

    public List<ProxyProfile> ImportProfiles(string? prefill = null)
    {
        var window = new ImportWindow(prefill) { Owner = _owner };
        return window.ShowDialog() == true ? window.Profiles : new List<ProxyProfile>();
    }

    public string? PromptText(string title, string caption, string initial = "")
    {
        var window = new PromptWindow(title, caption, initial) { Owner = _owner };
        return window.ShowDialog() == true ? window.Value : null;
    }

    public (string Name, string Address)? EditDnsServer(string title, string okText, string name = "", string address = "")
    {
        var window = new DnsServerWindow(title, okText, name, address) { Owner = _owner };
        return window.ShowDialog() == true ? (window.ServerName, window.Address) : null;
    }

    public DpiServiceEditResult? EditDpiService(DpiServiceEditRequest request)
    {
        var window = new DpiServiceWindow(request, this) { Owner = _owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public bool Confirm(string title, string text, string? okText = null, bool danger = true)
    {
        var window = new ConfirmWindow(title, text, okText ?? L.T("S0083"), danger, okOnly: false) { Owner = _owner };
        return window.ShowDialog() == true;
    }

    public ProfileGroupDeleteChoice ChooseProfileGroupDeletion(string groupName, int profileCount)
    {
        var window = new ProfileGroupDeleteWindow(groupName, profileCount) { Owner = _owner };
        return window.ShowDialog() == true ? window.Choice : ProfileGroupDeleteChoice.Cancel;
    }

    public SubscriptionImportChoice ChooseSubscriptionImport(
        string subscriptionName, int profileCount, IReadOnlyList<SubscriptionGroupOption> groups)
    {
        var window = new SubscriptionImportWindow(subscriptionName, profileCount, groups) { Owner = _owner };
        return window.ShowDialog() == true ? window.Choice : new SubscriptionImportChoice(SubscriptionImportAction.Cancel);
    }

    public void Info(string title, string text)
    {
        var window = new ConfirmWindow(title, text, L.T("S0909"), danger: false, okOnly: true) { Owner = _owner };
        window.ShowDialog();
    }

    public void Error(string title, string text)
    {
        var window = new ConfirmWindow(title, text, L.T("S0017"), danger: false, okOnly: true) { Owner = _owner };
        window.ShowDialog();
    }

    public List<RunningApp> PickApplications()
    {
        var window = new AppPickerWindow { Owner = _owner };
        return window.ShowDialog() == true ? window.Picked : new List<RunningApp>();
    }

    public string? OpenFile(string filter, string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string description, string? initialDirectory = null)
    {
        // WinForms-диалог выбора папки: в .NET 8 для WPF отдельного нет; System.Windows.Forms подключён в проекте
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
            dialog.SelectedPath = initialDirectory;
        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    public string? SaveFile(string filter, string defaultName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = filter,
            FileName = defaultName
        };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public bool ConfirmElevate(string title, string text)
    {
        var window = new ConfirmWindow(title, text, L.T("S0243"), danger: false, okOnly: false) { Owner = _owner };
        return window.ShowDialog() == true;
    }

    public void ShowCoreLog(string logText)
    {
        var window = new CoreLogWindow(L.T("S0910"), string.IsNullOrWhiteSpace(logText) ? L.T("S0911") : logText)
        {
            Owner = _owner
        };
        window.ShowDialog();
    }
}
