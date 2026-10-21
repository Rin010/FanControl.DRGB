using FanControl.Plugins;

namespace FanControl.DRGB;

public sealed class DrgbPlugin : IPlugin3
{
    private readonly object _gate = new();
    private readonly IPluginLogger _logger;
    private readonly Func<IReadOnlyList<DeviceEndpoint>> _discover;
    private readonly Func<bool> _vendorRunning;
    private readonly List<DrgbController> _controllers = new();
    private HashSet<string> _present = new(StringComparer.Ordinal);
    private DateTime _nextDiscovery;
    private bool _initialized;
    private bool _blocked;
    private bool _refreshPending;
    private bool _retryInitialization;
    public string Name => "DeepRGB V5F";
    public event Action? RefreshRequested;

    public DrgbPlugin(IPluginLogger logger) : this(logger, DeviceDiscovery.Find, DeviceDiscovery.VendorAppRunning) { }

    internal DrgbPlugin(IPluginLogger logger, Func<IReadOnlyList<DeviceEndpoint>> discover, Func<bool> vendorRunning)
    { _logger = logger; _discover = discover; _vendorRunning = vendorRunning; }

    public void Initialize()
    {
        lock (_gate)
        {
            CloseControllers();
            _refreshPending = false;
            _retryInitialization = false;
            _initialized = true;
            _blocked = _vendorRunning();
            _nextDiscovery = DateTime.UtcNow.AddSeconds(10);
            _present.Clear();
            Log($"Starting {typeof(DrgbPlugin).Assembly.GetName().Version}; positive targets use 0x99 renewed every 1s; verified U16 firmware uses fan-only 0xCC for zero and mode changes.");
            if (_blocked)
            {
                Log("DRGB.exe is running. Close the vendor application; the plugin will request a sensor refresh.");
                return;
            }
            try
            {
                var endpoints = _discover();
                _present = endpoints.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
                foreach (var endpoint in endpoints)
                {
                    try
                    {
                        var controller = new DrgbController(endpoint, Log, _vendorRunning);
                        _controllers.Add(controller);
                        Log($"Connected {controller.Name} [{controller.Key[..6]}], firmware {controller.FirmwareVersion}, {controller.FanCount} fans.");
                    }
                    catch (Exception error) { _retryInitialization = true; Log($"Cannot initialize {endpoint.Name}: {error.Message}"); }
                }
            }
            catch (Exception error) { _retryInitialization = true; Log($"HID discovery failed: {error.Message}"); }
        }
    }

    public void Load(IPluginSensorsContainer container)
    {
        lock (_gate)
        foreach (var controller in _controllers)
        for (int i = 0; i < controller.FanCount; i++)
        {
            var fan = new DrgbFanSensor(controller, i);
            container.FanSensors.Add(fan);
            // V283 stores PairedFanSensorId verbatim, but wraps ordinary sensor IDs
            // as plugin.Name + "/" + sensor.Id. Pair against that full identifier.
            container.ControlSensors.Add(new DrgbControlSensor(controller, i, Name + "/" + fan.Id));
        }
    }

    public void Update()
    {
        Action? refresh = null;
        lock (_gate)
        {
            if (!_initialized || _refreshPending) return;
            try
            {
                bool blockedNow = _vendorRunning();
                if (blockedNow)
                {
                    // Controller.Update also invalidates cached readings and closes HID.
                    foreach (var controller in _controllers) controller.Update();
                }
                else if (_blocked) { refresh = RequestRefresh(); }
                else
                {
                    foreach (var controller in _controllers) controller.Update();
                    if (DateTime.UtcNow >= _nextDiscovery)
                    {
                        _nextDiscovery = DateTime.UtcNow.AddSeconds(10);
                        var present = _discover().Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
                        if (!_present.SetEquals(present) || _retryInitialization) refresh = RequestRefresh();
                    }
                }
                _blocked = blockedNow;
            }
            catch (Exception error) { Log($"Update failed: {error.Message}"); }
        }
        // FanControl may call Close synchronously in response; do not invoke under our lock.
        refresh?.Invoke();
    }

    private Action? RequestRefresh()
    {
        if (RefreshRequested == null) return null;
        _refreshPending = true;
        return RefreshRequested;
    }

    public void Close()
    {
        lock (_gate) { _initialized = false; _refreshPending = false; CloseControllers(); }
    }

    private void CloseControllers()
    {
        foreach (var controller in _controllers)
        {
            try { controller.Dispose(); }
            catch (Exception error) { Log($"Close failed: {error.Message}"); }
        }
        _controllers.Clear();
    }

    private void Log(string message) => _logger.Log("[FanControl.DRGB] " + message);
}

internal sealed class DrgbFanSensor(DrgbController controller, int index) : IPluginSensor
{
    public string Id => $"DRGB/{controller.Key}/fan/{index + 1}/rpm";
    public string Name => $"{controller.Name} [{controller.Key[..6]}] Fan {index + 1}";
    public float? Value => controller.Rpm(index);
    public void Update() { } // IPlugin3.Update polls the entire controller once.
}

internal sealed class DrgbControlSensor(DrgbController controller, int index, string pairedId) : IPluginControlSensor2
{
    public string Id => $"DRGB/{controller.Key}/fan/{index + 1}/control";
    public string Name => $"{controller.Name} [{controller.Key[..6]}] Fan {index + 1}";
    public string PairedFanSensorId => pairedId;
    public float? Value => controller.Percent(index);
    public void Set(float val) => controller.Request(index, val);
    public void Reset() => controller.Reset(index);
    public void Update() { }
}
