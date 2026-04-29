namespace MouseClickTracker;

public class MainForm : Form
{
    private readonly MouseHook _hook = new();
    private readonly DataStore _store = new();
    private readonly WebServer _web;
    private readonly ActivityTracker _activity;
    private SyncService? _sync;
    private System.Threading.Timer? _resetTimer;
    private string _workStart = "";
    private string _workEnd   = "";
    private string _resetTime = "";
    private DateTime _lastReset = DateTime.MinValue;
    private int _clickCount;

    private readonly Label _labelTitle = new()
    {
        Text = "Кликов:",
        Font = new Font("Segoe UI", 14),
        AutoSize = true,
        Location = new Point(95, 35)
    };

    private readonly Label _labelCount = new()
    {
        Font = new Font("Segoe UI", 40, FontStyle.Bold),
        AutoSize = true,
        Location = new Point(100, 65)
    };

    private readonly Button _btnReset = new()
    {
        Text = "Сбросить",
        Location = new Point(90, 145),
        Size = new Size(110, 32),
        Font = new Font("Segoe UI", 10)
    };

    private readonly Button _btnSettings = new()
    {
        Text = "Настройки",
        Location = new Point(90, 185),
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
        Location  = new Point(12, 222)
    };

    public MainForm()
    {
        _activity = new ActivityTracker(_store);
        _web = new WebServer(_store, _activity);

        var cfg = AppConfig.Load();
        if (!string.IsNullOrWhiteSpace(cfg.ServerUrl))
            _sync = new SyncService(_store, _activity, cfg.ServerUrl, cfg.ResolvedMachineId, cfg.UserName,
                OnConfigReceived);
        _resetTimer = new System.Threading.Timer(CheckReset, null, 0, 30_000);

        Text = "Mouse Click Tracker";
        Size = new Size(300, 270);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        _clickCount = _store.Load();
        _labelCount.Text = _clickCount.ToString();

        _btnReset.Click += (_, _) =>
        {
            _store.Reset();
            _activity.Reset();
            _clickCount = 0;
            _labelCount.Text = "0";
        };

        _btnSettings.Click += (_, _) =>
        {
            var current = AppConfig.Load();
            using var dlg = new SettingsForm(current.ServerUrl, current.UserName);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            current.ServerUrl = dlg.ServerUrl;
            current.UserName  = dlg.UserName;
            current.Save();

            _sync?.Dispose();
            _sync = string.IsNullOrWhiteSpace(current.ServerUrl)
                ? null
                : new SyncService(_store, _activity, current.ServerUrl, current.ResolvedMachineId, current.UserName,
                    OnConfigReceived);
        };

        UpdateScheduleLabel();
        Controls.AddRange(new Control[] { _labelTitle, _labelCount, _btnReset, _btnSettings, _labelSchedule });

        _hook.Clicked += OnClicked;
        _hook.Activity += (_, _) => _activity.RegisterActivity();
        _hook.Start();
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

    private void CheckReset(object? state)
    {
        if (string.IsNullOrWhiteSpace(_resetTime)) return;
        if (!TimeOnly.TryParse(_resetTime, out var rt)) return;
        var now = DateTime.Now;
        if (now - _lastReset < TimeSpan.FromMinutes(1)) return;
        var t = TimeOnly.FromDateTime(now);
        if (t.Hour != rt.Hour || t.Minute != rt.Minute) return;

        _lastReset = now;
        _store.Reset();
        _activity.Reset();
        _clickCount = 0;
        if (IsHandleCreated)
            BeginInvoke(() => _labelCount.Text = "0");
    }

    private static bool IsInWorkHours(string workStart, string workEnd)
    {
        if (string.IsNullOrWhiteSpace(workStart) || string.IsNullOrWhiteSpace(workEnd)) return true;
        if (!TimeOnly.TryParse(workStart, out var start) || !TimeOnly.TryParse(workEnd, out var end)) return true;
        var now = TimeOnly.FromDateTime(DateTime.Now);
        return start <= end ? now >= start && now <= end : now >= start || now <= end;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _hook.Dispose();
        _resetTimer?.Dispose();
        _sync?.Dispose();
        _activity.Dispose();
        _web.Dispose();
        _store.Dispose();
        base.OnFormClosed(e);
    }
}
