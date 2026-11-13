using VibeTun.Models;
using VibeTun.Services;
using VibeTun.Utils;

namespace VibeTun.ViewModels;

/// <summary>Модель редактора профиля: работаем с копией, сохраняем по кнопке.</summary>
public sealed class ProfileEditorViewModel : ObservableObject
{
    private readonly LatencyService _latency;
    private int _ping = -1;
    private bool _pinging;

    public ProfileEditorViewModel(ProxyProfile source, LatencyService latency)
    {
        Profile = source.Clone();
        Profile.Id = source.Id;                 // сохраняем идентичность при правке
        Profile.CreatedAt = source.CreatedAt;
        Profile.RawJson = source.RawJson;
        Profile.LastPingMs = source.LastPingMs;
        _latency = latency;

        TestPingCommand = new AsyncRelayCommand(TestPingAsync, () => !_pinging);
    }

    public ProxyProfile Profile { get; }
    public ProxyNode Node => Profile.Node;

    public AsyncRelayCommand TestPingCommand { get; }

    /// <summary>
    /// Протоколы для ComboBox: базовый список плюс новые типы поддерживаемого ядра
    /// (см. <see cref="ProtocolRegistry"/>); текущий протокол профиля всегда присутствует.
    /// Кэш: ссылка меняется только когда состав реально изменился — иначе ComboBox при
    /// перестроении ItemsSource пушит обратно null (это роняло приложение при смене протокола).
    /// </summary>
    private IReadOnlyList<string>? _protocolList;

    public IReadOnlyList<string> ProtocolList => _protocolList ??= ProtocolRegistry.ProfileList(Profile.Protocol);

    public string Protocol
    {
        get => Profile.Protocol;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;   // отбрасываем null-push от ComboBox
            if (string.Equals(Profile.Protocol, value, StringComparison.Ordinal)) return;
            Profile.Protocol = value;
            Raise();
            Raise(nameof(IsTelegram));
            Raise(nameof(IsWireGuard));
            Raise(nameof(IsTlsCapable));
            Raise(nameof(IsQuic));
            Raise(nameof(IsTransportable));
            Raise(nameof(IsUdpBased));
            Raise(nameof(ShowUuidRow));
            Raise(nameof(ShowPasswordRow));
            Raise(nameof(ShowMethodRow));
            Raise(nameof(ShowFlowRow));
            Raise(nameof(ProtocolHint));

            var next = ProtocolRegistry.ProfileList(Profile.Protocol);
            if (_protocolList is null || !_protocolList.SequenceEqual(next))
            {
                _protocolList = next;
                Raise(nameof(ProtocolList));
            }
        }
    }

    public string ProtocolHint => (Profile.Protocol ?? "").ToLowerInvariant() switch
    {
        ProtocolIds.MtProto => L.T("S0794"),
        ProtocolIds.AmneziaWg => L.T("S0795"),
        ProtocolIds.ShadowsocksR => L.T("S0796"),
        ProtocolIds.Tunnel => L.T("S0797"),
        // новый протокол из схемы/пробы ядра: подсказываем, что он нестандартный
        _ => ProtocolRegistry.IsKnown(Profile.Protocol) ? "" : L.T("S1096")
    };

    public bool IsTelegram => Profile.Protocol == ProtocolIds.MtProto;

    public bool IsWireGuard => Profile.Protocol is ProtocolIds.Wireguard or ProtocolIds.AmneziaWg;
    public bool IsAmnezia => Profile.Protocol == ProtocolIds.AmneziaWg || Node.HasAwgParams;

    /// <summary>Секция TLS: у протоколов вне базового списка — показана, но выключена по умолчанию.</summary>
    public bool IsTlsCapable => ProtocolRegistry.IsTlsCapable(Profile.Protocol);
    public bool IsQuic => ProtocolRegistry.IsQuic(Profile.Protocol);
    public bool IsTransportable => ProtocolRegistry.IsTransportable(Profile.Protocol);
    public bool IsUdpBased => ProtocolIds.IsUdpBased(Profile.Protocol);

    // видимость строк учётных данных — по реестру протоколов (для неизвестных — универсальные строки)
    public bool ShowUuidRow => ProtocolRegistry.ShowUuidRow(Profile.Protocol);
    public bool ShowPasswordRow => ProtocolRegistry.ShowPasswordRow(Profile.Protocol);
    public bool ShowMethodRow => ProtocolRegistry.ShowMethodRow(Profile.Protocol);
    public bool ShowFlowRow => ProtocolRegistry.ShowFlowRow(Profile.Protocol);

    public bool Tls
    {
        get => Node.Tls;
        set
        {
            if (Node.Tls == value) return;
            Node.Tls = value;
            Raise();
        }
    }

    public bool Reality
    {
        get => Node.Reality;
        set
        {
            if (Node.Reality == value) return;
            Node.Reality = value;
            if (value) Node.Tls = true;
            Raise();
            Raise(nameof(Tls));
        }
    }

    public bool Insecure
    {
        get => Node.Insecure;
        set
        {
            if (Node.Insecure == value) return;
            Node.Insecure = value;
            Raise();
        }
    }

    public string RawUri
    {
        get => Profile.Uri ?? "";
        set
        {
            Profile.Uri = value;
            Raise();
        }
    }

    public string RawJson
    {
        get => Profile.RawJson ?? "";
        set
        {
            Profile.RawJson = string.IsNullOrWhiteSpace(value) ? null : value;
            Raise();
        }
    }

    public int Ping
    {
        get => _ping;
        private set
        {
            if (SetProperty(ref _ping, value)) Raise(nameof(PingText));
        }
    }

    public string PingText => _ping < 0 ? L.T("S0798") : _ping == 0 ? "<1 ms" : $"{_ping} ms";

    public async Task TestPingAsync()
    {
        _pinging = true;
        TestPingCommand.RaiseCanExecuteChanged();
        Ping = -1;
        Raise(nameof(PingText));
        try
        {
            Ping = await _latency.MeasureAsync(Profile);
        }
        finally
        {
            _pinging = false;
            TestPingCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Применить изменения к исходному профилю.</summary>
    public void ApplyTo(ProxyProfile target)
    {
        target.Name = Profile.Name;
        target.Protocol = Profile.Protocol;
        target.Node = Profile.Node;
        target.Uri = Profile.Uri;
        target.RawJson = Profile.RawJson;
        target.Group = Profile.Group;
        target.UpdatedAt = DateTime.UtcNow;
    }
}
