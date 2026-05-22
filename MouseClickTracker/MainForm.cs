namespace MouseClickTracker;

public class MainForm : Form
{
    private readonly MouseHook _hook = new();
    private readonly KeyboardHook _kbHook = new();
    private readonly DataStore _store = new();
    private readonly WebServer _web;
    private readonly ActivityTracker _activity;
    private SyncService? _sync;
    private UpdaterService? _updater;
    private System.Threading.Timer? _resetTimer;
    private string _workStart = "";
    private string _workEnd   = "";
    private string _resetTime = "";
    private DateTime _lastReset = DateTime.MinValue;
    private DateOnly? _lastResetDate;
    private int _clickCount;
    private int _keyCount;

    // ── Tile grid (2×2): clicks · keys / active · inactive ──────────────────
    private static readonly Font TileTitleFont = new("Segoe UI", 8.5f, FontStyle.Bold);
    private static readonly Color TileTitleColor = Color.FromArgb(140, 140, 150);
    private static readonly Color TileValueColor = Color.FromArgb(30, 30, 40);

    private static Label MakeTileTitle(string text, int x, int y) => new()
    {
        Text      = text.ToUpper(),
        Font      = TileTitleFont,
        ForeColor = TileTitleColor,
        AutoSize  = false,
        TextAlign = ContentAlignment.MiddleCenter,
        Location  = new Point(x, y),
        Size      = new Size(175, 16),
    };

    private static Label MakeTileValue(string text, int x, int y, float fontSize) => new()
    {
        Text      = text,
        Font      = new Font("Segoe UI", fontSize, FontStyle.Bold),
        ForeColor = TileValueColor,
        AutoSize  = false,
        TextAlign = ContentAlignment.MiddleCenter,
        Location  = new Point(x, y),
        Size      = new Size(175, 50),
    };

    private readonly Label _titleClicks    = MakeTileTitle("Кликов",    10, 32);
    private readonly Label _labelCount     = MakeTileValue("0",         10, 48, 26f);

    private readonly Label _titleKeys      = MakeTileTitle("Клавиш",    205, 32);
    private readonly Label _labelKeyCount  = MakeTileValue("0",         205, 48, 26f);

    private readonly Label _titleActive    = MakeTileTitle("Активно",   10, 110);
    private readonly Label _labelActive    = MakeTileValue("0ч 0м",     10, 126, 22f);

    private readonly Label _titleInactive  = MakeTileTitle("Неактивно", 205, 110);
    private readonly Label _labelInactive  = MakeTileValue("0ч 0м",     205, 126, 22f);

    private readonly Button _btnSettings = new()
    {
        Text = "Настройки",
        Location = new Point(285, 198),
        Size = new Size(95, 22),
        Font = new Font("Segoe UI", 8.5f),
        FlatStyle = FlatStyle.Flat,
        ForeColor = Color.Gray
    };

    private readonly Label _labelHourTitle = new()
    {
        Text      = "Активность по часам (сегодня)",
        Font      = new Font("Segoe UI", 8f),
        ForeColor = Color.DimGray,
        AutoSize  = true,
        Location  = new Point(15, 200)
    };

    private readonly HourChart _hourChart = new()
    {
        Location = new Point(15, 220),
        Size     = new Size(365, 78),
    };

    private readonly Label _labelSchedule = new()
    {
        Font      = new Font("Segoe UI", 7.5f),
        ForeColor = Color.Gray,
        AutoSize  = true,
        Location  = new Point(12, 308)
    };

    private readonly Label _labelMachineInfo = new()
    {
        Font      = new Font("Segoe UI", 7.5f),
        ForeColor = Color.Silver,
        AutoSize  = true,
        Location  = new Point(12, 326)
    };

    private System.Windows.Forms.Timer? _chartTimer;

    private readonly Label _labelWins = new()
    {
        Text      = "★ 0",
        Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
        ForeColor = Color.Silver,
        AutoSize  = true,
        Location  = new Point(330, 12)
    };

    private readonly Label _labelRank = new()
    {
        Text      = "Место: —",
        Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
        ForeColor = Color.DimGray,
        AutoSize  = true,
        Location  = new Point(15, 12)
    };


    private readonly NotifyIcon _tray;
    private bool _realClose;

    public MainForm()
    {
        _activity = new ActivityTracker(_store);
        _web = new WebServer(_store, _activity);

        var cfg = AppConfig.Load();
        if (!string.IsNullOrWhiteSpace(cfg.ServerUrl))
        {
            _sync = new SyncService(_store, _activity, cfg.ResolvedServerUrl, cfg.ResolvedMachineId, cfg.UserName,
                OnConfigReceived, OnWinsReceived);
            _updater = new UpdaterService(cfg.ResolvedServerUrl);
        }
        _resetTimer = new System.Threading.Timer(CheckReset, null, 0, 30_000);

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Открыть",   null, (_, _) => ShowWindow());
        trayMenu.Items.Add("Настройки", null, (_, _) => { ShowWindow(); _btnSettings.PerformClick(); });
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Выход",     null, (_, _) => { _realClose = true; Close(); });

        var appIcon = LoadAppIcon();

        _tray = new NotifyIcon
        {
            Icon             = appIcon ?? SystemIcons.Application,
            Text             = "Mouse Click Tracker",
            ContextMenuStrip = trayMenu,
            Visible          = true
        };
        _tray.DoubleClick += (_, _) => ShowWindow();

        Text = "Mouse Click Tracker";
        if (appIcon != null) Icon = appIcon;
        Size = new Size(400, 380);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        _clickCount      = _store.Load();
        _keyCount        = _store.LoadKeys();
        _lastResetDate   = _store.LoadLastResetDate();
        _labelCount.Text    = _clickCount.ToString();
        _labelKeyCount.Text = _keyCount.ToString();
        UpdateMachineInfoLabel(cfg);

        _btnSettings.Click += (_, _) =>
        {
            var current = AppConfig.Load();
            using var dlg = new SettingsForm(current.ServerUrl, current.UserName);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            current.ServerUrl = dlg.ServerUrl;
            current.UserName  = dlg.UserName;
            current.Save();

            _sync?.Dispose();
            _updater?.Dispose();
            if (string.IsNullOrWhiteSpace(current.ServerUrl))
            {
                _sync    = null;
                _updater = null;
            }
            else
            {
                _sync = new SyncService(_store, _activity, current.ResolvedServerUrl, current.ResolvedMachineId, current.UserName,
                    OnConfigReceived, OnWinsReceived);
                _updater = new UpdaterService(current.ResolvedServerUrl);
            }
            UpdateMachineInfoLabel(current);
        };

        UpdateScheduleLabel();
        Controls.AddRange(new Control[] {
            _labelRank, _labelWins,
            _titleClicks, _labelCount, _titleKeys, _labelKeyCount,
            _titleActive, _labelActive, _titleInactive, _labelInactive,
            _btnSettings, _labelHourTitle, _hourChart, _labelSchedule, _labelMachineInfo
        });
        UpdateActiveTimeLabel();

        RefreshHourChart();
        _chartTimer = new System.Windows.Forms.Timer { Interval = 5_000 };
        _chartTimer.Tick += (_, _) =>
        {
            RefreshHourChart();
            UpdateActiveTimeLabel();
        };
        _chartTimer.Start();

        _hook.Clicked  += OnClicked;
        _hook.Activity += (_, _) => _activity.RegisterActivity();
        _hook.Start();

        _kbHook.Pressed  += OnKeyPressed;
        _kbHook.Activity += (_, _) => _activity.RegisterActivity();
        _kbHook.Start();

        _web.Start();
    }

    private void OnConfigReceived(string start, string end, string reset)
    {
        _workStart = start;
        _workEnd   = end;
        _resetTime = reset;
        if (IsHandleCreated)
            BeginInvoke(() =>
            {
                UpdateScheduleLabel();
                RefreshHourChart();
            });
    }

    private void RefreshHourChart()
    {
        try
        {
            var hours = _store.LoadTodayHours();
            _hourChart.SetData(hours, _workStart, _workEnd);
        }
        catch { /* swallow — UI shouldn't crash on a transient SQLite lock */ }
    }

    private void OnWinsReceived(long wins, int rank, int total)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _labelWins.Text      = $"★ {wins}";
            _labelWins.ForeColor = wins > 0 ? Color.FromArgb(200, 144, 8) : Color.Silver;
            if (rank > 0 && total > 0)
            {
                _labelRank.Text      = $"Место: {rank} из {total}";
                _labelRank.ForeColor = rank == 1 ? Color.FromArgb(22, 163, 74) : Color.DimGray;
            }
            else
            {
                _labelRank.Text      = "Место: —";
                _labelRank.ForeColor = Color.DimGray;
            }
        });
    }

    private void UpdateActiveTimeLabel()
    {
        var a = _activity.ActiveSeconds;
        var i = _activity.InactiveSeconds;
        string Fmt(long s) => $"{s / 3600}ч {(s % 3600) / 60:D2}м";
        _labelActive.Text   = Fmt(a);
        _labelInactive.Text = Fmt(i);
    }

    private void UpdateMachineInfoLabel(AppConfig cfg)
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        var user    = string.IsNullOrWhiteSpace(cfg.UserName) ? "—" : cfg.UserName;
        _labelMachineInfo.Text = $"{cfg.ResolvedMachineId} · {user} · v{version}";
    }

    private void UpdateScheduleLabel()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_workStart) && !string.IsNullOrWhiteSpace(_workEnd))
            parts.Add($"Учёт: {_workStart}–{_workEnd}");
        if (!string.IsNullOrWhiteSpace(_resetTime))
            parts.Add($"Сброс: {_resetTime}");

        _labelSchedule.Text      = parts.Count > 0 ? string.Join("  ", parts) : "Расписание: не настроено";
        _labelSchedule.ForeColor = parts.Count > 0 ? Color.SteelBlue : Color.Gray;
    }

    private void OnClicked(object? sender, MouseClickedEventArgs e)
    {
        if (!IsInWorkHours(_workStart, _workEnd)) return;
        _store.Increment(injected: e.IsInjected);
        _clickCount++;
        Invoke(() => _labelCount.Text = _clickCount.ToString());
    }

    private void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (!IsInWorkHours(_workStart, _workEnd)) return;
        // Drop auto-repeats from a held key: they shouldn't inflate the daily count.
        if (e.IsRepeat) return;
        _store.IncrementKey(injected: e.IsInjected);
        _keyCount++;
        Invoke(() => _labelKeyCount.Text = _keyCount.ToString());
    }

    private void CheckReset(object? state)
    {
        if (string.IsNullOrWhiteSpace(_resetTime)) return;
        if (!TimeOnly.TryParse(_resetTime, out var rt)) return;

        var now              = DateTime.Now;
        var today            = DateOnly.FromDateTime(now);
        var todayResetMoment = today.ToDateTime(rt);

        if (now < todayResetMoment) return;          // ещё не настал момент сегодняшнего сброса
        if (_lastResetDate >= today) return;         // сегодня уже сбрасывали
        if (now - _lastReset < TimeSpan.FromMinutes(1)) return;

        _lastReset     = now;
        _lastResetDate = today;
        _store.Reset();
        _activity.Reset();
        _clickCount = 0;
        _keyCount   = 0;
        if (IsHandleCreated)
            BeginInvoke(() =>
            {
                _labelCount.Text    = "0";
                _labelKeyCount.Text = "0";
                RefreshHourChart();
                UpdateActiveTimeLabel();
            });
    }

    private static Icon? LoadAppIcon()
    {
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("MouseClickTracker.app.ico");
            return s == null ? null : new Icon(s);
        }
        catch { return null; }
    }

    private static bool IsInWorkHours(string workStart, string workEnd)
    {
        if (string.IsNullOrWhiteSpace(workStart) || string.IsNullOrWhiteSpace(workEnd)) return true;
        if (!TimeOnly.TryParse(workStart, out var start) || !TimeOnly.TryParse(workEnd, out var end)) return true;
        var now = TimeOnly.FromDateTime(DateTime.Now);
        return start <= end ? now >= start && now <= end : now >= start || now <= end;
    }

    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_realClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tray.Visible = false;
        _tray.Dispose();
        _hook.Dispose();
        _kbHook.Dispose();
        _resetTimer?.Dispose();
        _chartTimer?.Dispose();
        _sync?.Dispose();
        _updater?.Dispose();
        _activity.Dispose();
        _web.Dispose();
        _store.Dispose();
        base.OnFormClosed(e);
    }
}
