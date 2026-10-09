using GoreBox.Models;
using GoreBox.Services;

namespace GoreBox.ViewModels;

/// <summary>Правило домена/IP с возможностью правки прямо в списке.</summary>
public sealed class RuleItemVm : ObservableObject
{
    private string _pattern;
    private MatchKind _kind;
    private RouteAction _action;
    private bool _enabled;

    public RuleItemVm(RouteRule model)
    {
        Model = model;
        _pattern = model.Pattern;
        _kind = model.Kind;
        _action = model.Action;
        _enabled = model.Enabled;
    }

    public RouteRule Model { get; }

    public event Action? Changed;

    public string Pattern
    {
        get => _pattern;
        set
        {
            if (!SetProperty(ref _pattern, value)) return;
            Model.Pattern = value;
            AutoDetectKind(value);
            Changed?.Invoke();
        }
    }

    /// <summary>Тип записи определяется по содержимому (домен/IP/regex); выпадающего списка больше нет.</summary>
    private void AutoDetectKind(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return;
        var (_, kind) = Utils.Text.NormalizeUserPattern(pattern);
        if (_kind == kind) return;
        _kind = kind;
        Model.Kind = kind;
        Raise(nameof(Kind));
        Raise(nameof(KindLabel));
    }

    public MatchKind Kind
    {
        get => _kind;
        set
        {
            if (!SetProperty(ref _kind, value)) return;
            Model.Kind = value;
            Raise(nameof(KindLabel));
            Changed?.Invoke();
        }
    }

    public string KindLabel => Kind switch
    {
        MatchKind.Domain => "домен",
        MatchKind.Ip => "IP/подсеть",
        MatchKind.Keyword => "ключ",
        MatchKind.Regex => "regex",
        _ => "—"
    };

    public RouteAction Action
    {
        get => _action;
        set
        {
            if (!SetProperty(ref _action, value)) return;
            Model.Action = value;
            Raise(nameof(IsProxy));
            Raise(nameof(IsDirect));
            Raise(nameof(IsBlock));
            Changed?.Invoke();
        }
    }

    public bool IsProxy
    {
        get => _action == RouteAction.Proxy;
        set
        {
            if (value) Action = RouteAction.Proxy;
            else Raise(nameof(IsProxy));
        }
    }

    public bool IsDirect
    {
        get => _action == RouteAction.Direct;
        set
        {
            if (value) Action = RouteAction.Direct;
            else Raise(nameof(IsDirect));
        }
    }

    public bool IsBlock
    {
        get => _action == RouteAction.Block;
        set
        {
            if (value) Action = RouteAction.Block;
            else Raise(nameof(IsBlock));
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) return;
            Model.Enabled = value;
            Changed?.Invoke();
        }
    }

    public string? Note => Model.Note;
    public bool HasNote => !string.IsNullOrWhiteSpace(Model.Note);
}

/// <summary>Правило приложения.</summary>
public sealed class AppRuleItemVm : ObservableObject
{
    private RouteAction _action;
    private bool _enabled;
    private System.Windows.Media.ImageSource? _icon;

    public AppRuleItemVm(AppRule model)
    {
        Model = model;
        _action = model.Action;
        _enabled = model.Enabled;
    }

    public AppRule Model { get; }

    public event Action? Changed;

    public string Name => string.IsNullOrWhiteSpace(Model.Name) ? Model.ProcessName : Model.Name;
    public string ProcessName => Model.ProcessName;
    public string Path => Model.DisplayPath;
    public bool HasIcon => true;

    public System.Windows.Media.ImageSource? Icon
    {
        get
        {
            if (_icon is null && !string.IsNullOrWhiteSpace(Model.Path))
                _icon = Utils.IconHelper.ForExecutable(Model.Path);
            return _icon;
        }
    }

    public RouteAction Action
    {
        get => _action;
        set
        {
            if (!SetProperty(ref _action, value)) return;
            Model.Action = value;
            Raise(nameof(IsProxy));
            Raise(nameof(IsDirect));
            Raise(nameof(IsBlock));
            Changed?.Invoke();
        }
    }

    public bool IsProxy
    {
        get => _action == RouteAction.Proxy;
        set
        {
            if (value) Action = RouteAction.Proxy;
            else Raise(nameof(IsProxy));
        }
    }

    public bool IsDirect
    {
        get => _action == RouteAction.Direct;
        set
        {
            if (value) Action = RouteAction.Direct;
            else Raise(nameof(IsDirect));
        }
    }

    public bool IsBlock
    {
        get => _action == RouteAction.Block;
        set
        {
            if (value) Action = RouteAction.Block;
            else Raise(nameof(IsBlock));
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) return;
            Model.Enabled = value;
            Changed?.Invoke();
        }
    }

    public void Refresh()
    {
        _icon = null;
        Raise(nameof(Name));
        Raise(nameof(Path));
        Raise(nameof(Icon));
    }
}

/// <summary>Профиль маршрутизации (локальный вход, TUN, набор правил).</summary>
public sealed class RouteProfileItemVm : ObservableObject
{
    private RouteProfile _model;
    private bool _isSelected;

    public RouteProfileItemVm(RouteProfile model) => _model = model;

    public event Action? Changed;

    public RouteProfile Model
    {
        get => _model;
        private set => _model = value;
    }

    public string Id => _model.Id;

    public string Name
    {
        get => _model.Name;
        set
        {
            if (_model.Name == value) return;
            _model.Name = value;
            Raise();
            Raise(nameof(Summary));
            Changed?.Invoke();
        }
    }

    public string ListenAddress
    {
        get => _model.ListenAddress;
        set
        {
            if (_model.ListenAddress == value) return;
            _model.ListenAddress = value;
            Raise();
            Raise(nameof(Summary));
            Changed?.Invoke();
        }
    }

    public int ListenPort
    {
        get => _model.ListenPort;
        set
        {
            if (_model.ListenPort == value) return;
            _model.ListenPort = value;
            Raise();
            Raise(nameof(Summary));
            Changed?.Invoke();
        }
    }

    public ListenMode Mode
    {
        get => _model.Mode;
        set
        {
            if (_model.Mode == value) return;
            _model.Mode = value;
            Raise();
            Raise(nameof(Summary));
            Changed?.Invoke();
        }
    }

    public bool UseSystemProxy
    {
        get => _model.UseSystemProxy;
        set
        {
            if (_model.UseSystemProxy == value) return;
            _model.UseSystemProxy = value;
            Raise();
            Raise(nameof(Summary));
            Changed?.Invoke();
        }
    }

    public bool TunEnabled
    {
        get => _model.TunEnabled;
        set
        {
            if (_model.TunEnabled == value) return;
            _model.TunEnabled = value;
            Raise();
            Raise(nameof(Summary));
            Changed?.Invoke();
        }
    }

    public string TunStack
    {
        get => _model.TunStack;
        set
        {
            if (_model.TunStack == value) return;
            _model.TunStack = value;
            Raise();
            Changed?.Invoke();
        }
    }

    public int TunMtu
    {
        get => _model.TunMtu;
        set
        {
            if (_model.TunMtu == value) return;
            _model.TunMtu = value;
            Raise();
            Changed?.Invoke();
        }
    }

    public bool TunStrictRoute
    {
        get => _model.TunStrictRoute;
        set
        {
            if (_model.TunStrictRoute == value) return;
            _model.TunStrictRoute = value;
            Raise();
            Changed?.Invoke();
        }
    }

    public bool DirectPrivate
    {
        get => _model.DirectPrivate;
        set
        {
            if (_model.DirectPrivate == value) return;
            _model.DirectPrivate = value;
            Raise();
            Changed?.Invoke();
        }
    }

    public bool SniffTraffic
    {
        get => _model.SniffTraffic;
        set
        {
            if (_model.SniffTraffic == value) return;
            _model.SniffTraffic = value;
            Raise();
            Changed?.Invoke();
        }
    }

    public string DnsMode
    {
        get => _model.DnsMode;
        set
        {
            if (_model.DnsMode == value) return;
            _model.DnsMode = value;
            Raise();
            Changed?.Invoke();
        }
    }

    public string DnsServer
    {
        get => _model.DnsServer;
        set
        {
            if (_model.DnsServer == value) return;
            _model.DnsServer = value;
            Raise();
            Changed?.Invoke();
        }
    }

    public string? RouteSetId
    {
        get => _model.RouteSetId;
        set
        {
            if (_model.RouteSetId == value) return;
            _model.RouteSetId = value;
            Raise();
            Changed?.Invoke();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{_model.ListenAddress}:{_model.ListenPort}" };
            parts.Add(_model.Mode switch
            {
                ListenMode.Mixed => "mixed",
                ListenMode.Socks => "socks",
                _ => "http"
            });
            if (_model.TunEnabled) parts.Add("TUN");
            if (_model.UseSystemProxy) parts.Add("системный прокси");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Набор правил (ruleset) в списке.</summary>
public sealed class RouteSetItemVm : ObservableObject
{
    public RouteSetItemVm(RouteSet model) => Model = model;

    public RouteSet Model { get; }

    public string Id => Model.Id;
    public string Name => Model.Name;
    public string? Description => Model.Description;
    public bool BuiltIn => Model.BuiltIn;
    public string FileName => Model.FileName;
    public string RulesSummary => Model.RulesSummary;

    public void Refresh()
    {
        Raise(nameof(Name));
        Raise(nameof(Description));
        Raise(nameof(RulesSummary));
    }

    /// <summary>На случай, если где-то элемент попадёт в ComboBox без шаблона отображения.</summary>
    public override string ToString() => Name;
}
