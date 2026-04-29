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
    private readonly Button _btnSave = new()
    {
        Text = "Сохранить", Size = new Size(110, 32), Location = new Point(12, 136), Font = new Font("Segoe UI", 10)
    };
    private readonly Button _btnCancel = new()
    {
        Text = "Отмена", Size = new Size(86, 32), Location = new Point(130, 136),
        Font = new Font("Segoe UI", 10), DialogResult = DialogResult.Cancel
    };

    public SettingsForm(string serverUrl, string userName)
    {
        Text            = "Настройки";
        Size            = new Size(290, 220);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;
        AcceptButton    = _btnSave;
        CancelButton    = _btnCancel;

        _txtServer.Text = serverUrl;
        _txtUser.Text   = userName;

        _btnSave.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };

        Controls.AddRange([
            new Label { Text = "Адрес сервера:",    Font = new Font("Segoe UI", 9), AutoSize = true, Location = new Point(12, 12) },
            _txtServer,
            new Label { Text = "Имя пользователя:", Font = new Font("Segoe UI", 9), AutoSize = true, Location = new Point(12, 70) },
            _txtUser,
            _btnSave,
            _btnCancel
        ]);
    }
}
