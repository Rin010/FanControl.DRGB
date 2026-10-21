namespace FanControl.DRGB;

// Positive PWM uses 99. Verified U16 firmware uses fan-only CC for mode changes
// and fixed zero, because 99 explicitly ignores zero. Legacy F0 can restart RGB.
// EE.Value is a stored fallback; runtime PWM is NOT echoed in that field.
internal sealed class DrgbController : IDisposable
{
    private readonly object _gate = new();
    private readonly DeviceEndpoint _endpoint;
    private readonly Action<string> _log;
    private readonly Action _settle;
    private readonly Func<bool> _blocked;
    private readonly Func<DateTime> _utcNow;
    private ITransport? _transport;
    private FanReport? _latest;
    private readonly Dictionary<int, byte> _requested = new();
    private readonly Dictionary<int, byte> _acknowledged = new();
    private readonly Dictionary<int, FanState> _originals = new();
    private bool _closed;
    private DateTime _nextReconnect, _nextErrorLog, _nextRuntimeWrite;
    internal int FanCount { get; private set; }
    internal Version FirmwareVersion { get; private set; } = new(0, 0);
    internal string Key => _endpoint.Key;
    internal string Name => _endpoint.Name;
    internal bool HasFanOnlySettings => Protocol.SupportsFanOnlySettings(_endpoint.Model, FirmwareVersion, FanCount);

    internal DrgbController(DeviceEndpoint endpoint, Action<string> log, Func<bool> blocked,
        Action? settle = null, Func<DateTime>? utcNow = null)
    {
        _endpoint = endpoint; _log = log; _blocked = blocked;
        _settle = settle ?? (() => Thread.Sleep(200));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        try { Connect(); } catch { Disconnect(); throw; }
    }

    private void Connect()
    {
        if (_blocked()) throw new IOException("DRGB vendor application is running.");
        _transport = _endpoint.Open();
        var info = Protocol.ParseHandshake(_transport.Exchange(Protocol.HandshakeRequest()));
        if (FanCount != 0 && FanCount != info.FanCount)
            throw new InvalidDataException("DRGB channel count changed; refresh FanControl sensors.");
        if (_transport.InputReportLength < 7 + 5 * info.FanCount ||
            _transport.OutputReportLength < 6 + 3 * info.FanCount)
            throw new InvalidDataException("The HID report sizes cannot hold the announced DRGB fan count.");
        FanCount = info.FanCount; FirmwareVersion = info.Version;
        _latest = ReadState();
    }

    private FanReport ReadState()
    {
        if (_blocked()) throw new IOException("DRGB vendor application is running.");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            byte[] response = _transport!.Exchange(Protocol.ReadRequest());
            if (Protocol.IsFanReport(response)) return Protocol.ParseReport(response, FanCount);
        }
        throw new InvalidDataException("No matching DRGB fan response after three queries.");
    }

    internal void Request(int index, float value)
    {
        byte percent = Protocol.ToPercent(value);
        lock (_gate)
        {
            if (_closed) return;
            ValidateIndex(index); _requested[index] = percent;
        }
    }

    internal void Update()
    {
        lock (_gate)
        {
            if (_closed) return;
            try
            {
                if (_blocked())
                {
                    if (_originals.Count != 0) _log($"{Name}: DRGB took ownership; old restoration snapshots were discarded.");
                    _originals.Clear(); _requested.Clear(); Disconnect(); return;
                }
                if (_transport == null)
                {
                    if (_utcNow() < _nextReconnect) return;
                    Connect();
                }
                FanReport current = ReadState();
                _latest = current;
                if (!HasFanOnlySettings && _requested.Any(r => r.Value == 0))
                    throw new NotSupportedException("0% requires verified U16 V5F firmware 0.9.212.38; 0x99 would keep the previous output.");
                var restoring = _originals.Keys.Where(i => !_requested.ContainsKey(i)).ToArray();
                current = PrepareZeroRestoration(current, restoring);
                var desired = (FanState[])current.Fans.Clone();
                foreach (int index in restoring) desired[index] = _originals[index];
                foreach (var request in _requested)
                {
                    // Capture before even an uncertain/lost-ACK write.
                    _originals.TryAdd(request.Key, current.Fans[request.Key]);
                    if (request.Value == 0)
                        desired[request.Key] = current.Fans[request.Key] with { Mode = 1, Value = 0 };
                    else if (!Protocol.IsExternalMode(current.Fans[request.Key].Mode))
                    {
                        var original = _originals[request.Key];
                        desired[request.Key] = Protocol.IsExternalMode(original.Mode)
                            ? original : current.Fans[request.Key] with { Mode = 2, Value = request.Value };
                    }
                }
                var changed = ConfigurationChanges(current.Fans, desired);
                if (changed.Length != 0) current = WriteConfiguration(current.GlobalMode, desired, changed);
                bool needsRuntime = changed.Length != 0 ||
                    restoring.Any(i => Protocol.IsExternalMode(desired[i].Mode)) ||
                    (_requested.Count != 0 && _utcNow() >= _nextRuntimeWrite) ||
                    _requested.Any(r => !_acknowledged.TryGetValue(r.Key, out byte sent) || sent != r.Value);
                if (needsRuntime)
                {
                    var targets = RuntimeTargets(restoring);
                    if (targets.Count != 0) SendRuntime(current, targets);
                }
                // Unlike 99's ACK, EE confirms the fixed mode/value used for zero.
                foreach (var request in _requested.Where(r => r.Value == 0))
                    _acknowledged[request.Key] = 0;
                foreach (int index in restoring) { _originals.Remove(index); _acknowledged.Remove(index); }
            }
            catch (Exception error) { Fail(error); }
        }
    }

    private static int[] ConfigurationChanges(FanState[] current, FanState[] desired) =>
        Enumerable.Range(0, current.Length).Where(i => current[i].Mode != desired[i].Mode ||
            current[i].Value != desired[i].Value).ToArray();

    private FanReport WriteConfiguration(byte globalMode, FanState[] desired, IReadOnlyList<int> changed)
    {
        if (_blocked()) throw new IOException("DRGB vendor application is running; write refused.");
        _transport!.Exchange(HasFanOnlySettings
            ? Protocol.BuildFanOnlySettings(desired) : Protocol.BuildSettings(globalMode, desired));
        _settle();
        var actual = ReadState(); _latest = actual;
        if (HasFanOnlySettings && actual.GlobalMode != globalMode)
            throw new IOException("Fan-only configuration unexpectedly changed the global mode.");
        foreach (int index in HasFanOnlySettings ? Enumerable.Range(0, desired.Length) : changed)
            if (actual.Fans[index].Mode != desired[index].Mode || actual.Fans[index].Value != desired[index].Value)
                throw new IOException($"DRGB fan {index + 1} did not confirm its configuration.");
        return actual;
    }

    private FanReport PrepareZeroRestoration(FanReport current, IReadOnlyCollection<int> restoring)
    {
        // An original external-mode fallback of zero cannot be restored via 99.
        // Latch zero in mode 1 first, then restore its original external mode.
        // Changing to an external mode preserves that runtime zero in this firmware.
        var zero = restoring.Where(i => Protocol.IsExternalMode(_originals[i].Mode) && _originals[i].Value == 0).ToArray();
        if (zero.Length == 0) return current;
        if (!HasFanOnlySettings)
            throw new NotSupportedException("Restoring an external zero fallback needs verified fan-only configuration support.");
        var desired = (FanState[])current.Fans.Clone();
        foreach (int index in zero) desired[index] = desired[index] with { Mode = 1, Value = 0 };
        var changed = ConfigurationChanges(current.Fans, desired);
        return changed.Length == 0 ? current : WriteConfiguration(current.GlobalMode, desired, changed);
    }

    private Dictionary<int, byte> RuntimeTargets(IReadOnlyCollection<int> restoring)
    {
        var targets = _requested.Where(r => r.Value > 0 && !restoring.Contains(r.Key)).ToDictionary(r => r.Key, r => r.Value);
        foreach (int index in restoring)
            if (Protocol.IsExternalMode(_originals[index].Mode) && _originals[index].Value > 0)
                targets[index] = _originals[index].Value;
        return targets;
    }

    private void SendRuntime(FanReport current, IReadOnlyDictionary<int, byte> targets)
    {
        if (_blocked()) throw new IOException("DRGB vendor application is running; runtime write refused.");
        byte[] response = _transport!.Exchange(Protocol.BuildRuntime(current.Fans, targets));
        Protocol.ValidateRuntimeAcknowledgement(response, FanCount);
        _settle();
        var actual = ReadState(); _latest = actual;
        // Actual 0.9.212.38 firmware leaves EE.Value unchanged after 99. Check
        // stored configuration integrity, and report ACKed command separately.
        if (actual.GlobalMode != current.GlobalMode || ConfigurationChanges(current.Fans, actual.Fans).Length != 0)
            throw new IOException("Runtime PWM unexpectedly changed a stored mode/value.");
        foreach (var target in targets)
            if (_requested.ContainsKey(target.Key)) _acknowledged[target.Key] = target.Value;
        // 99 is a temporary output, not a latched setting. The vendor renews it
        // about once per second even when the target has not changed.
        _nextRuntimeWrite = _utcNow().AddSeconds(1);
    }

    internal bool Reset(int index)
    {
        lock (_gate)
        {
            if (_closed) return false;
            ValidateIndex(index); _requested.Remove(index);
            if (!_originals.ContainsKey(index)) { _acknowledged.Remove(index); return true; }
            try { Restore([index]); return true; }
            catch (Exception error) { Fail(error); return false; }
        }
    }

    private void Restore(IReadOnlyList<int> indices)
    {
        if (_transport == null) Connect();
        FanReport current = ReadState();
        current = PrepareZeroRestoration(current, indices.ToArray());
        var desired = (FanState[])current.Fans.Clone();
        foreach (int index in indices) desired[index] = _originals[index];
        var changed = ConfigurationChanges(current.Fans, desired);
        if (changed.Length != 0) current = WriteConfiguration(current.GlobalMode, desired, changed);
        var targets = RuntimeTargets(indices.ToArray());
        if (targets.Count != 0) SendRuntime(current, targets);
        foreach (int index in indices) { _originals.Remove(index); _acknowledged.Remove(index); }
    }

    internal float? Rpm(int index) { lock (_gate) return _latest?.Fans[index].Rpm; }
    internal FanReport? Snapshot()
    {
        lock (_gate) return _latest is { } state ? new(state.GlobalMode, (FanState[])state.Fans.Clone()) : null;
    }
    internal float? Percent(int index)
    {
        lock (_gate)
        {
            if (_latest == null) return null;
            if (_acknowledged.TryGetValue(index, out byte commanded)) return commanded;
            return _latest.Fans[index].Mode == 1 && _latest.Fans[index].Value <= 100
                ? _latest.Fans[index].Value : null;
        }
    }

    private void Fail(Exception error)
    {
        if (_utcNow() >= _nextErrorLog)
        {
            _log($"{Name} [{Key[..6]}]: {error.GetType().Name}: {error.Message}");
            _nextErrorLog = _utcNow().AddSeconds(30);
        }
        Disconnect(); _nextReconnect = _utcNow().AddSeconds(5);
    }
    private void Disconnect()
    {
        _latest = null; _acknowledged.Clear(); _nextRuntimeWrite = DateTime.MinValue;
        try { _transport?.Dispose(); } finally { _transport = null; }
    }
    private void ValidateIndex(int index)
    {
        if ((uint)index >= (uint)FanCount) throw new ArgumentOutOfRangeException(nameof(index));
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            try
            {
                if (_originals.Count != 0)
                {
                    if (_blocked()) _log($"{Name}: original settings could not be restored because DRGB is running.");
                    else Restore(_originals.Keys.ToArray());
                }
            }
            catch (Exception error) { _log($"{Name}: restore failed: {error.Message}"); }
            finally { _closed = true; _requested.Clear(); Disconnect(); }
        }
    }
}
