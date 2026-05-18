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

    private readonly Label _labelTitle = new()
    {
        Text = "Кликов:",
        Font = new Font("Segoe UI", 12),
        AutoSize = true,
        Location = new Point(55, 30)
    };

    private readonly Label _labelCount = new()
    {
        Text = "0",
        Font = new Font("Segoe UI", 28, FontStyle.Bold),
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleCenter,
        Location = new Point(15, 55),
        Size = new Size(170, 60)
    };

    private readonly Label _labelKeyTitle = new()
    {
        Text = "Клавиш:",
        Font = new Font("Segoe UI", 12),
        AutoSize = true,
        Location = new Point(240, 30)
    };

    private readonly Label _labelKeyCount = new()
    {
        Text = "0",
        Font = new Font("Segoe UI", 28, FontStyle.Bold),
        AutoSize = false,
        TextAlign = ContentAlignment.MiddleCenter,
        Location = new Point(195, 55),
        Size = new Size(170, 60)
    };

    private readonly Button _btnSettings = new()
    {
        Text = "Настройки",
        Location = new Point(135, 135),
        Size = new Size(110, 28),
        Font = new Font("Segoe UI", 9),
        FlatStyle = FlatStyle.Flat,
        ForeColor = Color.Gray
    };

    private readonly Label _labelSchedule = new()
    {
        Font      = new Font("Segoe UI", 7.5f),
        ForeColor = Color.Gray,
        AutoSize  = true,
        Location  = new Point(12, 170)
    };

    private readonly Label _labelMachineInfo = new()
    {
        Font      = new Font("Segoe UI", 7.5f),
        ForeColor = Color.Silver,
        AutoSize  = true,
        Location  = new Point(12, 188)
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
                OnConfigReceived);
            _updater = new UpdaterService(cfg.ResolvedServerUrl);
        }
        _resetTimer = new System.Threading.Timer(CheckReset, null, 0, 30_000);

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Открыть", null, (_, _) => ShowWindow());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Выход",   null, (_, _) => { _realClose = true; Close(); });

        _tray = new NotifyIcon
        {
            Icon             = SystemIcons.Application,
            Text             = "Mouse Click Tracker",
            ContextMenuStrip = trayMenu,
            Visible          = true
        };
        _tray.DoubleClick += (_, _) => ShowWindow();

        Text = "Mouse Click Tracker";
        Size = new Size(400, 240);
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
                    OnConfigReceived);
                _updater = new UpdaterService(current.ResolvedServerUrl);
            }
            UpdateMachineInfoLabel(current);
        };

        UpdateScheduleLabel();
        Controls.AddRange(new Control[] { _labelTitle, _labelCount, _labelKeyTitle, _labelKeyCount, _btnSettings, _labelSchedule, _labelMachineInfo });

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
            BeginInvoke(UpdateScheduleLabel);
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

    private void OnClicked(object? sender, EventArgs e)
    {
        if (!IsInWorkHours(_workStart, _workEnd)) return;
        _store.Increment();
        _clickCount++;
        Invoke(() => _labelCount.Text = _clickCount.ToString());
    }

    private void OnKeyPressed(object? sender, EventArgs e)
    {
        if (!IsInWorkHours(_workStart, _workEnd)) return;
        _store.IncrementKey();
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
            });
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
        _sync?.Dispose();
        _updater?.Dispose();
        _activity.Dispose();
        _web.Dispose();
        _store.Dispose();
        base.OnFormClosed(e);
    }
}
