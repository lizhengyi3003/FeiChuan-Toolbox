namespace TunHotspot.Core;

// Recovery requires a current user request. Merely observing an existing hotspot never arms it.
public sealed class HotspotRecoveryPolicy
{
    public bool Requested { get; private set; }
    public int Attempts { get; private set; }
    public bool Exhausted => Requested && Attempts >= 3;
    private DateTimeOffset? _offSince, _onSince;
    private DateTimeOffset _nextAttempt;

    public void SetRequested(bool requested)
    {
        Requested = requested;
        Attempts = 0;
        _offSince = _onSince = null;
        _nextAttempt = default;
    }

    public bool Observe(DateTimeOffset now, bool tunConnected, string state)
    {
        if (!Requested) return false;
        if (state == "On")
        {
            _offSince = null;
            _onSince ??= now;
            // A brief flap must not reset the retry budget and cause an endless restart loop.
            if (now - _onSince >= TimeSpan.FromMinutes(1)) Attempts = 0;
            return false;
        }
        _onSince = null;
        if (!tunConnected || state != "Off") { _offSince = null; return false; }
        _offSince ??= now;
        return !Exhausted && now - _offSince >= TimeSpan.FromSeconds(12) && now >= _nextAttempt;
    }

    public void AttemptStarted(DateTimeOffset now)
    {
        Attempts++;
        _offSince = null;
        _nextAttempt = now + TimeSpan.FromSeconds(Attempts switch { 1 => 15, 2 => 30, _ => 60 });
    }
}
