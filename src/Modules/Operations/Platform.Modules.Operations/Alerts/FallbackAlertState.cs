namespace Platform.Modules.Operations.Alerts;

/// <summary>
/// F-60 acceptance "Given PostgreSQL stopped ... the platform admin receives one email naming the component": the
/// incident pipeline lives in PostgreSQL, so while the health store cannot be written the job alerts from this
/// process-local memory instead (a singleton in the worker). It remembers which components have had a "down" email
/// since the store became unavailable, and whether the "cannot record health results" email went out, so each is sent
/// once. Lost on a worker restart, which at worst repeats one "down" email; never persisted, since the only durable
/// store is the one that is down.
/// </summary>
internal sealed class FallbackAlertState
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _announcedDown = new(StringComparer.Ordinal);
    private bool _storeAlerted;

    public bool StoreAlerted
    {
        get
        {
            lock (_gate)
            {
                return _storeAlerted;
            }
        }
    }

    public void MarkStoreAlerted()
    {
        lock (_gate)
        {
            _storeAlerted = true;
        }
    }

    public bool WasAnnouncedDown(string component)
    {
        lock (_gate)
        {
            return _announcedDown.Contains(component);
        }
    }

    public void MarkAnnouncedDown(string component)
    {
        lock (_gate)
        {
            _announcedDown.Add(component);
        }
    }

    public IReadOnlySet<string> AnnouncedDown()
    {
        lock (_gate)
        {
            return new HashSet<string>(_announcedDown, StringComparer.Ordinal);
        }
    }

    /// <summary>The component is back under the normal incident pipeline (recovered, or its incident is recorded).</summary>
    public void Forget(string component)
    {
        lock (_gate)
        {
            _announcedDown.Remove(component);
        }
    }

    /// <summary>The store accepted results again: the next outage announces itself afresh.</summary>
    public void ClearStoreAlert()
    {
        lock (_gate)
        {
            _storeAlerted = false;
        }
    }
}
