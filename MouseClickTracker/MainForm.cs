namespace MouseClickTracker;

public class MainForm : Form
{
    private readonly MouseHook _hook = new();
    private readonly DataStore _store = new();
    private readonly WebServer _web;
    private readonly ActivityTracker _activity;
    private SyncService? _sync;
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

    public MainForm()
    {
        _activity = new ActivityTracker(_store);
        _web = new WebServer(_store, _activity);

        var cfg = AppConfig.Load();
        if (!string.IsNullOrWhiteSpace(cfg.ServerUrl))
            _sync = new SyncService(_store, _activity, cfg.ServerUrl, cfg.ResolvedMachineId, cfg.UserName);

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
                : new SyncService(_store, _activity, current.ServerUrl, current.ResolvedMachineId, current.UserName);
        };

        Controls.AddRange(new Control[] { _labelTitle, _labelCount, _btnReset, _btnSettings });

        _hook.Clicked += OnClicked;
        _hook.Activity += (_, _) => _activity.RegisterActivity();
        _hook.Start();
        _web.Start();
    }

    private void OnClicked(object? sender, EventArgs e)
    {
        _store.Increment();
        _clickCount++;
        Invoke(() => _labelCount.Text = _clickCount.ToString());
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _hook.Dispose();
        _sync?.Dispose();
        _activity.Dispose();
        _web.Dispose();
        _store.Dispose();
        base.OnFormClosed(e);
    }
}
