namespace MouseClickTracker;

public sealed class ActivityTracker : IDisposable
{
    private const int InactivityThresholdSeconds = 5;
    private const int SaveIntervalSeconds = 10;

    private readonly DataStore _store;
    private readonly System.Threading.Timer _timer;
    private readonly object _flushLock = new();

    private DateTime _lastActivity = DateTime.MinValue;
    private int _pendingActive;
    private int _pendingInactive;
    private int _ticksSinceSave;
    private readonly Dictionary<string, int> _pendingAppTime = new();

    public long ActiveSeconds { get; private set; }
    public long InactiveSeconds { get; private set; }

    public event EventHandler? Updated;

    public ActivityTracker(DataStore store)
    {
        _store = store;
        (ActiveSeconds, InactiveSeconds) = store.LoadActivity();
        _timer = new System.Threading.Timer(Tick, null, 1000, 1000);
    }

    public void RegisterActivity() => _lastActivity = DateTime.UtcNow;

    private void Tick(object? state)
    {
        bool isActive = _lastActivity != DateTime.MinValue
            && (DateTime.UtcNow - _lastActivity).TotalSeconds < InactivityThresholdSeconds;

        if (isActive)
        {
            ActiveSeconds++;
            _pendingActive++;
        }
        else
        {
            InactiveSeconds++;
            _pendingInactive++;
        }

        var (appName, _) = ForegroundApp.Get();
        if (!string.IsNullOrEmpty(appName))
        {
            _pendingAppTime.TryGetValue(appName, out int cur);
            _pendingAppTime[appName] = cur + 1;
        }

        _ticksSinceSave++;
        if (_ticksSinceSave >= SaveIntervalSeconds)
            Flush();

        Updated?.Invoke(this, EventArgs.Empty);
    }

    public void Flush()
    {
        lock (_flushLock)
        {
            if (_pendingActive > 0 || _pendingInactive > 0)
            {
                _store.AddTime(_pendingActive, _pendingInactive);
                _pendingActive = 0;
                _pendingInactive = 0;
            }

            if (_pendingAppTime.Count > 0)
            {
                _store.AddAppTime(new Dictionary<string, int>(_pendingAppTime));
                _pendingAppTime.Clear();
            }

            _ticksSinceSave = 0;
        }
    }

    public void Reset()
    {
        lock (_flushLock)
        {
            ActiveSeconds    = 0;
            InactiveSeconds  = 0;
            _pendingActive   = 0;
            _pendingInactive = 0;
            _pendingAppTime.Clear();
            _ticksSinceSave  = 0;
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        Flush();
    }
}
