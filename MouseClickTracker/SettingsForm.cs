namespace MouseClickTracker;

public sealed class SettingsForm : Form
{
    public string ServerUrl => _txtServer.Text.Trim();
    public string UserName  => _txtUser.Text.Trim();

    private readonly TextBox _txtServer = new()
    {
        Font = new Font("Segoe UI", 10), Size = new Size(240, 26), Location = new Point(12, 32)
    };
    private readonly TextBox _txtUser = new()
    {
        Font = new Font("Segoe UI", 10), Size = new Size(240, 26), Location = new Point(12, 90)
    };
    private readonly Button _btnCheckUpdate = new()
    {
        Text = "Проверить обновление",
        Size = new Size(240, 28), Location = new Point(12, 130),
        Font = new Font("Segoe UI", 9),
        FlatStyle = FlatStyle.Flat, ForeColor = Color.SteelBlue
    };
    private readonly Label _lblStatus = new()
    {
        Font = new Font("Segoe UI", 8), ForeColor = Color.Gray,
        AutoSize = false, Size = new Size(240, 16), Location = new Point(12, 162),
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly Button _btnSave = new()
    {
        Text = "Сохранить", Size = new Size(110, 32), Location = new Point(12, 188), Font = new Font("Segoe UI", 10)
    };
    private readonly Button _btnCancel = new()
    {
        Text = "Отмена", Size = new Size(86, 32), Location = new Point(130, 188),
        Font = new Font("Segoe UI", 10), DialogResult = DialogResult.Cancel
    };

    public SettingsForm(string serverUrl, string userName)
    {
        Text            = "Настройки";
        Size            = new Size(290, 270);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;
        AcceptButton    = _btnSave;
        CancelButton    = _btnCancel;

        _txtServer.Text = serverUrl;
        _txtUser.Text   = userName;

        _btnSave.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };

        _btnCheckUpdate.Click += async (_, _) =>
        {
            _btnCheckUpdate.Enabled = false;
            _lblStatus.ForeColor    = Color.Gray;
            _lblStatus.Text         = "Проверка…";

            var (result, msg) = await UpdaterService.CheckAsync(_txtServer.Text.Trim());

            _lblStatus.ForeColor = result switch
            {
                UpdaterService.CheckResult.Updating => Color.SeaGreen,
                UpdaterService.CheckResult.Error    => Color.IndianRed,
                _                                   => Color.Gray
            };
            _lblStatus.Text         = msg;
            _btnCheckUpdate.Enabled = true;

            if (result == UpdaterService.CheckResult.Updating)
            {
                await Task.Delay(2000);
                Environment.Exit(0);
            }
        };

        Controls.AddRange([
            new Label { Text = "Адрес сервера:",    Font = new Font("Segoe UI", 9), AutoSize = true, Location = new Point(12, 12) },
            _txtServer,
            new Label { Text = "Имя пользователя:", Font = new Font("Segoe UI", 9), AutoSize = true, Location = new Point(12, 70) },
            _txtUser,
            _btnCheckUpdate,
            _lblStatus,
            _btnSave,
            _btnCancel
        ]);
    }
}
