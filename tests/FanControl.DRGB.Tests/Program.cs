using FanControl.DRGB;
using FanControl.Plugins;

var tests = new (string Name, Action Run)[]
{
    ("Wire format and big-endian RPM agree with REA evidence", WireFormat),
    ("Malformed reports are rejected before state is used", Malformed),
    ("Percent input is finite, clamped and rounded", Percent),
    ("Initialization only queries hardware", Initialization),
    ("Control changes batch and preserve other channels/global mode", Batch),
    ("Unchanged targets avoid duplicate writes within the renewal interval", NoRepeatedWrites),
    ("Reset restores one channel while another remains controlled", ResetOne),
    ("Close restores every channel modified by this instance", CloseRestores),
    ("Reset before update never writes", ResetBeforeUpdate),
    ("Stored configuration changes require read-back confirmation", BadReadback),
    ("Disconnected readings become null", Disconnect),
    ("Vendor process guard prevents queries and settings writes", VendorGuard),
    ("Truncated status never becomes a settings packet", TruncatedStatus),
    ("Plugin lifecycle and RPM/control pairing match host contract", Lifecycle),
    ("Vendor process blocks initialization, then requests refresh", BlockedRefresh),
    ("Percent feedback is never presented as fabricated RPM", NoFakeRpm),
    ("Write timeout keeps a restoration snapshot", UncertainWrite),
    ("Failed Reset retries restoration on a later update", RetryReset),
    ("Reconnection reapplies active controls after the backoff", Reconnect),
    ("Vendor takeover discards stale restoration snapshots", VendorTakeover),
    ("Real FanControl wrappers resolve the paired fan identifier", HostPairing),
    ("Changing percentages on external modes never writes F0", LightingActions),
    ("External-mode Reset and Close also avoid lighting actions", RestoreLightingActions),
    ("A mode transition uses F0 once, subsequent percentages use 99", ModeTransition),
    ("Runtime acknowledgement must match command and channel count", BadRuntimeAck),
    ("Runtime PWM does not require the stored setting to echo it", StoredValueIsNotRuntimeEcho),
    ("Unowned external channels receive their stored fallback", UnownedFallback),
    ("A constant target is renewed for 20s without repeated Set calls", ConstantTargetRenewal),
    ("Renewals are limited to 1s while target changes send immediately", RenewalInterval),
    ("Reset stops runtime renewals for a released controller", ResetStopsRenewals),
    ("An idle controller never starts runtime renewals", IdleDoesNotRenew),
    ("Fan-only configuration fixes M at zero and validates all records", FanOnlyFormat),
    ("Runtime zero overrides are rejected because firmware ignores them", RuntimeZeroRejected),
    ("80 to zero latches fixed zero without F0 or a fake zero runtime ACK", ZeroAfterHigh),
    ("Zero to positive restores the original external mode and fallback", PositiveAfterZero),
    ("Zero remains latched for 20 seconds without runtime renewal", ZeroHold),
    ("A zero channel coexists with another channel's runtime renewals", MixedZeroAndPositive),
    ("Reset and Close restore zero channels through fan-only settings", ZeroRestoration),
    ("Unknown firmware refuses zero before any write", UnsupportedZero),
    ("A lost CC acknowledgement still retains the original restoration snapshot", LostFanOnlyAck),
    ("An original external zero fallback is preloaded before restoration", ExternalZeroRestoration),
    ("CC read-back failure never reports a zero target as applied", BadZeroReadback),
    ("Verified fixed and motherboard transitions never send F0", FanOnlyModeTransition)
};
int failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + test.Name + "\n" + error); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed; no physical HID device was opened.");
return failed == 0 ? 0 : 1;

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
}
static void Bytes(byte[] expected, byte[] actual)
{
    if (!expected.SequenceEqual(actual)) throw new Exception($"Expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}.");
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static FakeTransport Device() => new(7,
    new FanState(0, 25, 1500), new FanState(3, 45, 1234), new FanState(1, 60, 2000));
static DrgbController Controller(FakeTransport device, Func<bool>? blocked = null) =>
    new(new DeviceEndpoint("ABCDEF0123456789ABCDEF01", "Test DRGB", () => device),
        _ => { }, blocked ?? (() => false), () => { });

static void WireFormat()
{
    Bytes([0, 2, 0], Protocol.HandshakeRequest());
    Bytes([0, 0x30, 0x33, 0xEE], Protocol.ReadRequest());
    var dev = Device();
    var parsed = Protocol.ParseReport(dev.Status(), 3);
    Equal((ushort)1500, parsed.Fans[0].Reading);
    Equal((byte)7, parsed.GlobalMode);
    Bytes([0, 0x30, 0x33, 0xF0, 7, 0, 0, 1, 50, 0, 3, 45, 0, 1, 60],
        Protocol.BuildSettings(7, [new(1, 50, 0), new(3, 45, 0), new(1, 60, 0)]));
    Bytes([0, 0x30, 0x33, 0x99, 0, 0, 0, 0, 80, 0, 0, 45, 0, 0, 0],
        Protocol.BuildRuntime([new(2, 40, 0), new(3, 45, 0), new(1, 60, 0)], new Dictionary<int, byte> { [0] = 80 }));
    Equal(3, Protocol.ParseHandshake(dev.Handshake()).FanCount);
}
static void Malformed()
{
    Throws<InvalidDataException>(() => Protocol.ParseHandshake([]));
    Throws<InvalidDataException>(() => Protocol.ParseHandshake([0, 0, 0, 8, 0, 0, 0, 3]));
    Throws<InvalidDataException>(() => Protocol.ParseHandshake([0, 0, 0, 9, 0, 0, 0, 0]));
    var dev = Device(); var wrongHeader = dev.Status(); wrongHeader[4] = 0xF0;
    Throws<InvalidDataException>(() => Protocol.ParseReport(wrongHeader, 3));
    Throws<InvalidDataException>(() => Protocol.ParseReport(dev.Status()[..10], 3));
    Throws<InvalidDataException>(() => Protocol.ParseReport(dev.Status(), 2));
}
static void Percent()
{
    Equal((byte)0, Protocol.ToPercent(-3)); Equal((byte)100, Protocol.ToPercent(101));
    Equal((byte)51, Protocol.ToPercent(50.5f)); Equal((byte)50, Protocol.ToPercent(50.49f));
    Throws<ArgumentOutOfRangeException>(() => Protocol.ToPercent(float.NaN));
    Throws<ArgumentOutOfRangeException>(() => Protocol.ToPercent(float.PositiveInfinity));
}
static void Initialization()
{
    var dev = Device(); using var c = Controller(dev);
    Equal(0, dev.Settings.Count); Equal(3, c.FanCount); Equal<float?>(1500, c.Rpm(0));
    Equal<float?>(null, c.Percent(0));
}
static void Batch()
{
    var dev = Device(); using var c = Controller(dev);
    c.Request(0, 40); c.Request(1, 70); Equal(0, dev.Settings.Count);
    c.Update(); Equal(1, dev.Settings.Count);
    Equal(new FanState(2, 40, 1500), dev.Fans[0]);
    Equal(new FanState(3, 45, 1234), dev.Fans[1]);
    Equal((byte)40, dev.Pwm[0]); Equal((byte)70, dev.Pwm[1]); Equal(1, dev.Runtime.Count);
    Equal(new FanState(1, 60, 2000), dev.Fans[2]); Equal((byte)7, dev.Mode);
    Equal<float?>(40, c.Percent(0));
}
static void NoRepeatedWrites()
{
    var dev = Device(); using var c = Controller(dev); c.Request(0, 50); c.Update();
    c.Request(0, 50); c.Update(); c.Update(); Equal(1, dev.Settings.Count); Equal(1, dev.Runtime.Count);
}
static void ResetOne()
{
    var dev = Device(); using var c = Controller(dev); c.Request(0, 50); c.Request(1, 80); c.Update();
    // Another unowned channel changes independently; reset must merge its live state.
    dev.Fans[2] = new FanState(1, 90, 2200);
    c.Reset(0); Equal((byte)0, dev.Fans[0].Mode); Equal((byte)25, dev.Fans[0].Value);
    Equal((byte)80, dev.Pwm[1]); Equal((byte)45, dev.Fans[1].Value); Equal((byte)90, dev.Fans[2].Value);
    c.Update(); Equal((byte)0, dev.Fans[0].Mode);
}
static void CloseRestores()
{
    var dev = Device(); var c = Controller(dev); c.Request(0, 50); c.Request(1, 80); c.Update();
    c.Dispose(); c.Dispose(); Equal(2, dev.Settings.Count);
    Equal(new FanState(0, 25, 1500), dev.Fans[0]); Equal(new FanState(3, 45, 1234), dev.Fans[1]);
    Equal(true, dev.Disposed); Equal<float?>(null, c.Rpm(0));
}
static void ResetBeforeUpdate()
{
    var dev = Device(); using var c = Controller(dev); c.Request(0, 10); c.Reset(0); c.Update();
    Equal(0, dev.Settings.Count);
}
static void BadReadback()
{
    var dev = Device(); using var c = Controller(dev); dev.IgnoreSettings = true;
    c.Request(0, 50); c.Update(); Equal<float?>(null, c.Percent(0)); Equal(true, dev.Disposed);
}
static void Disconnect()
{
    var dev = Device(); using var c = Controller(dev); dev.Fail = true; c.Update();
    Equal<float?>(null, c.Rpm(0)); Equal<float?>(null, c.Percent(0));
}
static void VendorGuard()
{
    bool blocked = false; var dev = Device(); var c = Controller(dev, () => blocked);
    c.Request(0, 80); blocked = true; int before = dev.Requests.Count; c.Update();
    Equal(before, dev.Requests.Count); Equal(0, dev.Settings.Count); Equal<float?>(null, c.Rpm(0)); c.Dispose();
}
static void TruncatedStatus()
{
    var dev = Device(); using var c = Controller(dev); dev.Truncated = true;
    c.Request(0, 30); c.Update(); Equal(0, dev.Settings.Count); Equal<float?>(null, c.Rpm(0));
}
static void Lifecycle()
{
    var logger = new TestLogger(); var devices = new List<FakeTransport>();
    DeviceEndpoint endpoint = new("ABCDEF0123456789ABCDEF01", "Test DRGB", () => { var d = Device(); devices.Add(d); return d; });
    var plugin = new DrgbPlugin(logger, () => [endpoint], () => false);
    for (int pass = 0; pass < 2; pass++)
    {
        plugin.Initialize(); var sensors = new TestContainer(); plugin.Load(sensors);
        Equal(3, sensors.ControlSensors.Count); Equal(3, sensors.FanSensors.Count); Equal(0, sensors.TempSensors.Count);
        for (int i = 0; i < 3; i++)
            Equal(plugin.Name + "/" + sensors.FanSensors[i].Id, ((IPluginControlSensor2)sensors.ControlSensors[i]).PairedFanSensorId);
        sensors.ControlSensors[0].Set(50); plugin.Update(); Equal<float?>(50, sensors.ControlSensors[0].Value);
        plugin.Close(); Equal(true, devices[^1].Disposed); Equal((byte)0, devices[^1].Fans[0].Mode);
    }
}
static void BlockedRefresh()
{
    bool blocked = true; int opens = 0; int refreshes = 0;
    var plugin = new DrgbPlugin(new TestLogger(), () => { opens++; return []; }, () => blocked);
    plugin.RefreshRequested += () => refreshes++;
    plugin.Initialize(); plugin.Load(new TestContainer()); Equal(0, opens);
    blocked = false; plugin.Update(); plugin.Update(); Equal(1, refreshes); plugin.Close();
}
static void NoFakeRpm()
{
    Equal<float?>(null, new FanState(1, 50, 50).Rpm);
    Equal<float?>(null, new FanState(1, 100, 100).Rpm);
    Equal<float?>(101, new FanState(1, 50, 101).Rpm);
    Equal<float?>(null, new FanState(1, 0, 0).Rpm);
}
static void UncertainWrite()
{
    var dev = Device(); var c = Controller(dev); dev.ThrowAfterApply = true;
    c.Request(0, 20); c.Update(); Equal((byte)20, dev.Fans[0].Value);
    dev.ThrowAfterApply = false; c.Dispose(); Equal((byte)0, dev.Fans[0].Mode); Equal((byte)25, dev.Fans[0].Value);
}
static void RetryReset()
{
    DateTime now = DateTime.UtcNow; var dev = Device();
    using var c = new DrgbController(new("ABCDEF0123456789ABCDEF01", "Test DRGB", () => dev),
        _ => { }, () => false, () => { }, () => now);
    c.Request(0, 20); c.Update();
    dev.IgnoreSettings = true; c.Reset(0); dev.IgnoreSettings = false;
    now = now.AddSeconds(6); c.Update();
    Equal((byte)0, dev.Fans[0].Mode); Equal((byte)25, dev.Fans[0].Value);
}
static void Reconnect()
{
    DateTime now = DateTime.UtcNow; var dev = Device(); int opens = 0;
    using var c = new DrgbController(new("ABCDEF0123456789ABCDEF01", "Test DRGB", () => { opens++; return dev; }),
        _ => { }, () => false, () => { }, () => now);
    c.Request(0, 20); c.Update(); dev.Fail = true; c.Update();
    Equal<float?>(null, c.Rpm(0)); dev.Fail = false;
    dev.Fans[0] = new(0, 40, 1500); c.Update(); Equal(1, opens);
    now = now.AddSeconds(6); c.Update(); Equal(2, opens);
    Equal((byte)2, dev.Fans[0].Mode); Equal((byte)20, dev.Pwm[0]);
}
static void VendorTakeover()
{
    bool blocked = false; var dev = Device(); var c = Controller(dev, () => blocked);
    c.Request(0, 20); c.Update(); blocked = true; c.Update();
    dev.Fans[0] = new(1, 90, 2000); blocked = false; c.Dispose();
    Equal((byte)90, dev.Fans[0].Value);
}

static void HostPairing()
{
    // Use the shipped V283 wrappers, not a duplicate implementation of its prefix rule.
    string hostDir = Environment.GetEnvironmentVariable("DRGB_TEST_HOST_DIR") ?? @"C:\Program Files (x86)\FanControl";
    var host = System.Reflection.Assembly.LoadFrom(Path.Combine(hostDir, "FanControl.Library.dll"));
    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
    var plugin = new DrgbPlugin(new TestLogger(), () => [new("ABCDEF0123456789ABCDEF01", "Test DRGB", () => Device())], () => false);
    plugin.Initialize();
    try
    {
        var sensors = new TestContainer(); plugin.Load(sensors);
        Type fanType = host.GetType("FanControl.Domain.BackendProviders.Plugin.PluginSensor", true)!;
        Type controlType = host.GetType("FanControl.Domain.BackendProviders.Plugin.PluginControlSensor", true)!;
        var fanCtor = fanType.GetConstructors(flags).Single();
        var controlCtor = controlType.GetConstructors(flags).Single();
        // PluginBackendProvider.<Open>b__20_0 uses enum value 2 for RPM sensors.
        object kind = Enum.ToObject(fanCtor.GetParameters()[2].ParameterType, 2);
        object wrappedFan = fanCtor.Invoke([sensors.FanSensors[0], plugin.Name, kind]);
        object wrappedControl = controlCtor.Invoke([sensors.ControlSensors[0], plugin.Name]);
        string fanIdentifier = (string)fanType.GetProperty("Identifier")!.GetValue(wrappedFan)!;
        object pair = controlType.GetProperty("PairedFanSensor")!.GetValue(wrappedControl)!;
        string pairedIdentifier = (string)pair.GetType().GetProperty("Identifier")!.GetValue(pair)!;
        Equal(fanIdentifier, pairedIdentifier);
    }
    finally { plugin.Close(); }
}

static void LightingActions()
{
    // Verify absence of any configuration command, regardless of unknown firmware
    // lighting internals. This is the default mode observed on the real controller.
    var dev = Device(); dev.Mode = 0; dev.Fans[0] = new(2, 25, 1500);
    using var c = Controller(dev);
    foreach (int percent in new[] { 20, 35, 70, 80, 50 })
    {
        dev.LightingRestarts = 0;
        c.Request(0, percent); c.Update();
        Equal((byte)percent, dev.Pwm[0]); Equal<float?>(percent, c.Percent(0));
        Equal((byte)25, dev.Fans[0].Value);
        Equal(0, dev.LightingRestarts);
        Equal((byte)0, dev.Mode);
        Equal(0, dev.Settings.Count); Equal((byte)0x99, dev.Runtime[^1][3]);
    }
}

static void RestoreLightingActions()
{
    var dev = Device(); dev.Mode = 0; dev.Fans[0] = new(2, 25, 1500);
    var c = Controller(dev); c.Request(0, 80); c.Update();
    c.Reset(0); Equal(0, dev.LightingRestarts);
    c.Request(1, 65); c.Update(); c.Dispose();
    Equal(0, dev.LightingRestarts); Equal((byte)0, dev.Mode);
    Equal(0, dev.Settings.Count); Equal((byte)25, dev.Pwm[0]); Equal((byte)45, dev.Pwm[1]);
}

static void ModeTransition()
{
    var dev = Device(); using var c = Controller(dev);
    c.Request(0, 50); c.Update(); Equal(1, dev.Settings.Count); Equal((byte)2, dev.Fans[0].Mode);
    c.Request(0, 70); c.Update(); c.Request(0, 30); c.Update();
    Equal(1, dev.Settings.Count); Equal(3, dev.Runtime.Count); Equal((byte)30, dev.Pwm[0]);
}
static void BadRuntimeAck()
{
    var dev = Device(); dev.Fans[0] = new(2, 25, 1500);
    var c = Controller(dev); dev.BadRuntimeAcknowledgement = true;
    c.Request(0, 80); c.Update(); Equal<float?>(null, c.Percent(0));
    dev.BadRuntimeAcknowledgement = false; c.Dispose(); Equal((byte)25, dev.Pwm[0]);
}
static void StoredValueIsNotRuntimeEcho()
{
    var dev = Device(); dev.Fans[0] = new(2, 40, 1500);
    using var c = Controller(dev); c.Request(0, 80); c.Update();
    Equal((byte)40, dev.Fans[0].Value); Equal((byte)80, dev.Pwm[0]); Equal<float?>(80, c.Percent(0));
    Equal(false, dev.Disposed);
}
static void UnownedFallback()
{
    var dev = Device(); dev.Fans[0] = new(2, 25, 1500); dev.Pwm[1] = 90;
    using var c = Controller(dev); c.Request(0, 80); c.Update();
    Equal((byte)45, dev.Pwm[1]); Equal((byte)60, dev.Pwm[2]);
    Equal((byte)45, dev.Fans[1].Value); Equal((byte)3, dev.Fans[1].Mode);
}

static void ConstantTargetRenewal()
{
    DateTime now = new(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
    var dev = new FakeTransport(0, new FanState(2, 40, 1000)) { Clock = () => now, RuntimeTimeout = TimeSpan.FromSeconds(5) };
    using var c = new DrgbController(new("ABCDEF0123456789ABCDEF01", "Test DRGB", () => dev), _ => { }, () => false, () => { }, () => now);
    c.Request(0, 80); c.Update();
    for (int second = 1; second <= 20; second++)
    {
        now = now.AddSeconds(1); c.Update();
        Equal((byte)80, dev.Pwm[0]); Equal<float?>(80, c.Percent(0));
        Equal((byte)40, dev.Fans[0].Value); Equal(0, dev.Settings.Count); Equal(0, dev.LightingRestarts);
    }
    Equal(21, dev.Runtime.Count);
}
static void RenewalInterval()
{
    DateTime start = new(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc), now = start;
    var dev = new FakeTransport(0, new FanState(2, 40, 1000));
    using var c = new DrgbController(new("ABCDEF0123456789ABCDEF01", "Test DRGB", () => dev), _ => { }, () => false, () => { }, () => now);
    c.Request(0, 80); c.Update();
    for (int i = 1; i < 10; i++) { now = start.AddMilliseconds(i * 100); c.Request(0, 80); c.Update(); }
    Equal(1, dev.Runtime.Count);
    now = start.AddSeconds(1); c.Update(); Equal(2, dev.Runtime.Count);
    now = start.AddMilliseconds(1100); c.Request(0, 65); c.Update(); Equal(3, dev.Runtime.Count); Equal((byte)65, dev.Pwm[0]);
    now = start.AddMilliseconds(2000); c.Update(); Equal(3, dev.Runtime.Count);
    now = start.AddMilliseconds(2100); c.Update(); Equal(4, dev.Runtime.Count); Equal(0, dev.Settings.Count);
}
static void ResetStopsRenewals()
{
    DateTime now = new(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
    var dev = new FakeTransport(0, new FanState(2, 40, 1000));
    using var c = new DrgbController(new("ABCDEF0123456789ABCDEF01", "Test DRGB", () => dev), _ => { }, () => false, () => { }, () => now);
    c.Request(0, 80); c.Update(); Equal(true, c.Reset(0));
    int count = dev.Runtime.Count;
    now = now.AddSeconds(10); c.Update();
    Equal(count, dev.Runtime.Count); Equal((byte)40, dev.Pwm[0]); Equal(0, dev.Settings.Count);
}
static void IdleDoesNotRenew()
{
    DateTime now = new(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
    var dev = new FakeTransport(0, new FanState(2, 40, 1000));
    using var c = new DrgbController(new("ABCDEF0123456789ABCDEF01", "Test DRGB", () => dev), _ => { }, () => false, () => { }, () => now);
    for (int i = 0; i < 20; i++) { now = now.AddSeconds(1); c.Update(); }
    Equal(0, dev.Runtime.Count); Equal(0, dev.Settings.Count);
}

static FakeTransport VerifiedDevice(FanState? first = null)
{
    var fans = Enumerable.Repeat(new FanState(2, 40, 1000), 10).ToArray();
    if (first is { } state) fans[0] = state;
    return new FakeTransport(0, fans) { Firmware = new Version(0, 9, 212, 38) };
}
static DrgbController VerifiedController(FakeTransport device, Func<DateTime>? clock = null) =>
    new(new DeviceEndpoint("ABCDEF0123456789ABCDEF01", "Verified U16", () => device, Protocol.SupportedDevices[0]),
        _ => { }, () => false, () => { }, clock);

static void FanOnlyFormat()
{
    Bytes([0, 0x30, 0x33, 0xCC, 0, 0, 0, 1, 0, 0, 3, 45],
        Protocol.BuildFanOnlySettings([new(1, 0, 0), new(3, 45, 0)]));
    Throws<InvalidDataException>(() => Protocol.BuildFanOnlySettings([new(2, 101, 0)]));
    Throws<InvalidDataException>(() => Protocol.BuildFanOnlySettings([new(6, 40, 0)]));
    Equal(true, Protocol.SupportsFanOnlySettings(Protocol.SupportedDevices[0], new(0, 9, 212, 38), 10));
    Equal(false, Protocol.SupportsFanOnlySettings(Protocol.SupportedDevices[1], new(0, 9, 212, 38), 10));
    Equal(false, Protocol.SupportsFanOnlySettings(Protocol.SupportedDevices[0], new(0, 9, 212, 39), 10));
    Equal(false, Protocol.SupportsFanOnlySettings(Protocol.SupportedDevices[0], new(0, 9, 212, 38), 9));
}
static void RuntimeZeroRejected()
{
    Throws<InvalidDataException>(() => Protocol.BuildRuntime([new(2, 40, 0)], new Dictionary<int, byte> { [0] = 0 }));
    var dev = VerifiedDevice();
    var packet = Protocol.BuildRuntime(dev.Fans, new Dictionary<int, byte> { [0] = 80 });
    dev.Exchange(packet); packet[8] = 0; dev.Exchange(packet);
    Equal((byte)80, dev.Pwm[0]); // Match the firmware's (P-1)&255 <=99 filter.
}
static void ZeroAfterHigh()
{
    var dev = VerifiedDevice(); using var c = VerifiedController(dev);
    c.Request(0, 80); c.Update(); Equal((byte)80, dev.Pwm[0]);
    int runtime = dev.Runtime.Count;
    c.Request(0, 0); c.Update();
    Equal((byte)0, dev.Pwm[0]); Equal(new FanState(1, 0, 1000), dev.Fans[0]);
    Equal<float?>(0, c.Percent(0)); Equal<float?>(1000, c.Rpm(0));
    Equal(runtime, dev.Runtime.Count); Equal(1, dev.Settings.Count);
    Equal((byte)0xCC, dev.Settings[0][3]); Equal(0, dev.LightingRestarts);
}
static void PositiveAfterZero()
{
    var dev = VerifiedDevice(new(3, 45, 1000)); using var c = VerifiedController(dev);
    c.Request(0, 0); c.Update(); c.Request(0, 80); c.Update();
    Equal(new FanState(3, 45, 1000), dev.Fans[0]); Equal((byte)80, dev.Pwm[0]);
    Equal<float?>(80, c.Percent(0)); Equal(2, dev.Settings.Count);
    Equal(true, dev.Settings.All(p => p[3] == 0xCC)); Equal(0, dev.LightingRestarts);
}
static void ZeroHold()
{
    DateTime now = new(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
    var dev = VerifiedDevice(); dev.Clock = () => now; dev.RuntimeTimeout = TimeSpan.FromSeconds(5);
    using var c = VerifiedController(dev, () => now);
    c.Request(0, 80); c.Update(); c.Request(0, 0); c.Update();
    for (int i = 0; i < 20; i++)
    {
        now = now.AddSeconds(1); c.Update();
        Equal((byte)0, dev.Pwm[0]); Equal<float?>(0, c.Percent(0));
    }
    Equal(1, dev.Settings.Count); Equal(1, dev.Runtime.Count); Equal(0, dev.LightingRestarts);
}
static void MixedZeroAndPositive()
{
    DateTime now = new(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc);
    var dev = VerifiedDevice(); using var c = VerifiedController(dev, () => now);
    c.Request(0, 0); c.Request(1, 80); c.Update();
    for (int i = 0; i < 10; i++) { now = now.AddSeconds(1); c.Update(); }
    Equal((byte)0, dev.Pwm[0]); Equal((byte)80, dev.Pwm[1]); Equal(11, dev.Runtime.Count);
    Equal(true, c.Reset(0)); Equal((byte)40, dev.Pwm[0]); Equal((byte)80, dev.Pwm[1]);
    Equal(0, dev.LightingRestarts);
}
static void ZeroRestoration()
{
    var dev = VerifiedDevice(); var c = VerifiedController(dev);
    c.Request(0, 0); c.Request(1, 0); c.Update();
    Equal(true, c.Reset(0)); Equal(new FanState(2, 40, 1000), dev.Fans[0]);
    Equal((byte)40, dev.Pwm[0]); Equal((byte)0, dev.Pwm[1]);
    c.Dispose(); Equal(new FanState(2, 40, 1000), dev.Fans[1]); Equal((byte)40, dev.Pwm[1]);
    Equal(true, dev.Settings.All(p => p[3] == 0xCC)); Equal(0, dev.LightingRestarts);
}
static void UnsupportedZero()
{
    var dev = VerifiedDevice(); dev.Firmware = new Version(0, 9, 212, 39);
    using var c = VerifiedController(dev); c.Request(0, 0); c.Update();
    Equal<float?>(null, c.Percent(0)); Equal((byte)40, dev.Pwm[0]);
    Equal(0, dev.Runtime.Count); Equal(0, dev.Settings.Count);
}
static void LostFanOnlyAck()
{
    var dev = VerifiedDevice(); var c = VerifiedController(dev); dev.ThrowAfterApply = true;
    c.Request(0, 0); c.Update(); Equal((byte)0, dev.Pwm[0]); Equal<float?>(null, c.Percent(0));
    dev.ThrowAfterApply = false; c.Dispose();
    Equal(new FanState(2, 40, 1000), dev.Fans[0]); Equal((byte)40, dev.Pwm[0]);
    Equal(0, dev.LightingRestarts);
}
static void ExternalZeroRestoration()
{
    var dev = VerifiedDevice(new(3, 0, 1000)); using var c = VerifiedController(dev);
    c.Request(0, 80); c.Update(); Equal((byte)80, dev.Pwm[0]);
    Equal(true, c.Reset(0)); Equal(new FanState(3, 0, 1000), dev.Fans[0]); Equal((byte)0, dev.Pwm[0]);
    Equal(2, dev.Settings.Count); Equal(0, dev.LightingRestarts);
}
static void BadZeroReadback()
{
    var dev = VerifiedDevice(); using var c = VerifiedController(dev); dev.IgnoreSettings = true;
    c.Request(0, 0); c.Update(); Equal<float?>(null, c.Percent(0)); Equal((byte)40, dev.Pwm[0]);
}
static void FanOnlyModeTransition()
{
    var dev = VerifiedDevice(new(0, 60, 1000)); var c = VerifiedController(dev);
    c.Request(0, 80); c.Update(); c.Request(0, 0); c.Update(); c.Request(0, 50); c.Update();
    c.Dispose(); Equal(new FanState(0, 60, 1000), dev.Fans[0]);
    Equal(true, dev.Settings.All(p => p[3] == 0xCC)); Equal(0, dev.LightingRestarts);
}

sealed class FakeTransport(byte mode, params FanState[] fans) : ITransport
{
    public byte Mode = mode;
    public FanState[] Fans = fans;
    public int InputReportLength => 512;
    public int OutputReportLength => 512;
    public bool Fail, IgnoreSettings, Truncated, ThrowAfterApply, Disposed;
    public int LightingRestarts;
    public bool BadRuntimeAcknowledgement;
    public Version Firmware = new(0, 9, 1, 0);
    public Func<DateTime>? Clock;
    public TimeSpan RuntimeTimeout = TimeSpan.MaxValue;
    private DateTime? _lastRuntimeWrite;
    public byte[] Pwm = fans.Select(f => f.Value).ToArray();
    public List<byte[]> Runtime { get; } = [];
    public List<byte[]> Requests { get; } = [];
    public List<byte[]> Settings { get; } = [];
    public byte[] Handshake() => [0, 0, (byte)Firmware.Major, (byte)Firmware.Minor, (byte)Firmware.Build, (byte)Firmware.Revision, 0, (byte)Fans.Length];
    public byte[] Status()
    {
        var response = new byte[7 + 5 * Fans.Length]; response[2] = 0x30; response[3] = 0x33;
        response[4] = 0xEE; response[5] = Mode; response[6] = (byte)Fans.Length;
        for (int i = 0; i < Fans.Length; i++)
        {
            int p = 7 + i * 5; response[p] = (byte)(i + 1); response[p + 1] = Fans[i].Mode;
            response[p + 2] = Fans[i].Value; response[p + 3] = (byte)(Fans[i].Reading >> 8); response[p + 4] = (byte)Fans[i].Reading;
        }
        return response;
    }
    public byte[] Exchange(byte[] request)
    {
        Requests.Add((byte[])request.Clone());
        if (Fail) throw new IOException("Simulated unplug.");
        if (request.SequenceEqual(Protocol.HandshakeRequest())) return Handshake();
        if (request.SequenceEqual(Protocol.ReadRequest()))
        {
            if (Clock != null && _lastRuntimeWrite is { } sent && Clock() - sent >= RuntimeTimeout)
                for (int i = 0; i < Fans.Length; i++)
                    if (Protocol.IsExternalMode(Fans[i].Mode)) Pwm[i] = Fans[i].Value;
            return Truncated ? Status()[..8] : Status();
        }
        if (request.Length >= 6 && request[3] is 0xF0 or 0xCC)
        {
            Settings.Add((byte[])request.Clone());
            if (!IgnoreSettings)
            {
                // DeviceInfo's F0 selector includes actions: RGB 0..3, reset 8,
                // fan PWM enable 9, fan stop 10, 25% options 11/12.
                if (request[3] == 0xF0 && request[4] <= 3) { Mode = request[4]; LightingRestarts++; }
                for (int i = 0; i < Fans.Length; i++)
                {
                    Fans[i] = Fans[i] with { Mode = request[7 + 3 * i], Value = request[8 + 3 * i] };
                    if (Fans[i].Mode == 1) Pwm[i] = Fans[i].Value;
                }
            }
            if (ThrowAfterApply) throw new TimeoutException("Simulated lost ACK after successful write.");
            return [0]; // Deliberately not a meaningful ACK: read-back must establish success.
        }
        if (request.Length == 6 + 3 * Fans.Length && request[3] == 0x99)
        {
            Runtime.Add((byte[])request.Clone());
            _lastRuntimeWrite = Clock?.Invoke();
            for (int i = 0; i < Fans.Length; i++)
                if (Protocol.IsExternalMode(Fans[i].Mode) && request[8 + 3 * i] is >= 1 and <= 100)
                    Pwm[i] = request[8 + 3 * i];
            if (ThrowAfterApply) throw new TimeoutException("Lost runtime ACK after PWM changed.");
            var ack = Status(); ack[4] = BadRuntimeAcknowledgement ? (byte)0xEE : (byte)0x99;
            return ack;
        }
        throw new Exception("Unknown request: " + Convert.ToHexString(request));
    }
    public void Dispose() => Disposed = true;
}
sealed class TestLogger : IPluginLogger
{
    public List<string> Entries { get; } = [];
    public void Log(string message) => Entries.Add(message);
}
sealed class TestContainer : IPluginSensorsContainer
{
    public List<IPluginControlSensor> ControlSensors { get; } = [];
    public List<IPluginSensor> FanSensors { get; } = [];
    public List<IPluginSensor> TempSensors { get; } = [];
}
