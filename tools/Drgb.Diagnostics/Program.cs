using System.Diagnostics;
using FanControl.DRGB;

if (args.Length == 0 || args[0] is not ("--list" or "--probe" or "--exercise" or "--runtime-exercise" or "--hold-check" or "--zero-check" or "--fan-only-zero-check"))
{
    Console.WriteLine("DRGB diagnostics\n  --list\n  --probe [--trace]\n  --exercise --channel <1-based> --percent <0..100> --seconds <1..60> [--trace]\n  --runtime-exercise (fan 1, 80%, 3s; requires external mode)\n  --hold-check (fan 1, 80%, one-shot 15s then renewed 15s; requires external mode)\n  --zero-check --channel <1-based> [--trace] (80%, 0%, 1%, 80%, then restore; verified U16 only)\n  --fan-only-zero-check --channel <1-based> (raw CC fixed-zero probe; verified U16 only)");
    return 2;
}
try
{
    var endpoints = DeviceDiscovery.Find();
    foreach (var endpoint in endpoints) Console.WriteLine($"{endpoint.Name} [{endpoint.Key}]");
    if (args[0] == "--list") return 0;
    if (DeviceDiscovery.VendorAppRunning()) throw new InvalidOperationException("Close DRGB before probing or controlling the HID device.");
    var fanControl = Process.GetProcessesByName("FanControl");
    try { if (fanControl.Length != 0) throw new InvalidOperationException("Close FanControl before using the standalone probe."); }
    finally { foreach (var process in fanControl) process.Dispose(); }
    if (args[0] is "--exercise" or "--zero-check" or "--fan-only-zero-check" && endpoints.Count != 1)
        throw new InvalidOperationException("An exercise requires exactly one supported controller connected.");
    if (args[0] == "--fan-only-zero-check")
    {
        int option = Array.IndexOf(args, "--channel");
        if (option < 0 || option + 1 >= args.Length || !int.TryParse(args[option + 1], out int channel))
            throw new ArgumentException("A 1-based --channel is required.");
        using var transport = new TracingTransport(endpoints[0].Open());
        var info = Protocol.ParseHandshake(transport.Exchange(Protocol.HandshakeRequest()));
        if (!Protocol.SupportsFanOnlySettings(endpoints[0].Model, info.Version, info.FanCount))
            throw new InvalidOperationException("Fan-only zero check is restricted to verified U16 firmware 0.9.212.38.");
        FanReport Read() => Protocol.ParseReport(transport.Exchange(Protocol.ReadRequest()), info.FanCount);
        var original = Read();
        void WriteFanConfiguration(IReadOnlyList<FanState> fans)
        {
            transport.Exchange(Protocol.BuildFanOnlySettings(fans));
        }
        int index = channel - 1;
        if (index < 0 || index >= info.FanCount || !Protocol.IsExternalMode(original.Fans[index].Mode))
            throw new InvalidOperationException("A valid existing external-mode channel is required.");
        void Runtime(IReadOnlyList<FanState> fans, IReadOnlyDictionary<int, byte> values) =>
            Protocol.ValidateRuntimeAcknowledgement(transport.Exchange(Protocol.BuildRuntime(fans, values)), info.FanCount);
        void Verify(FanReport report, FanState[] expected)
        {
            if (report.GlobalMode != original.GlobalMode || !report.Fans.Select(f => (f.Mode, f.Value)).SequenceEqual(expected.Select(f => (f.Mode, f.Value))))
                throw new IOException("Fan-only-zero stored configuration read-back mismatch.");
        }
        var zero = (FanState[])original.Fans.Clone(); zero[index] = zero[index] with { Mode = 1, Value = 0 };
        try
        {
            for (int second = 1; second <= 5; second++)
            {
                Runtime(original.Fans, new Dictionary<int, byte> { [index] = 80 }); Thread.Sleep(1000);
                Console.WriteLine($"FAN-ONLY-ZERO warmup channel={channel} target=80% t={second}s raw={Read().Fans[index].Reading}");
            }
            WriteFanConfiguration(zero); Thread.Sleep(200); Verify(Read(), zero);
            for (int second = 1; second <= 10; second++)
            {
                Runtime(zero, new Dictionary<int, byte>()); Thread.Sleep(1000);
                var report = Read(); Verify(report, zero);
                Console.WriteLine($"FAN-ONLY-ZERO channel={channel} mode={report.Fans[index].Mode} stored={report.Fans[index].Value} t={second}s raw={report.Fans[index].Reading}");
            }
        }
        finally
        {
            WriteFanConfiguration(original.Fans); Thread.Sleep(200);
            Verify(Read(), original.Fans); Runtime(original.Fans, new Dictionary<int, byte>());
            Console.WriteLine("Fan-only-zero restore: all original modes/values/global match; original fallback targets sent.");
        }
        return 0;
    }
    if (args[0] == "--hold-check")
    {
        if (endpoints.Count != 1) throw new InvalidOperationException("A hold check requires exactly one controller.");
        using ITransport transport = args.Contains("--trace") ? new TracingTransport(endpoints[0].Open()) : endpoints[0].Open();
        var info = Protocol.ParseHandshake(transport.Exchange(Protocol.HandshakeRequest()));
        FanReport Read() => Protocol.ParseReport(transport.Exchange(Protocol.ReadRequest()), info.FanCount);
        var original = Read();
        if (!Protocol.IsExternalMode(original.Fans[0].Mode)) throw new InvalidOperationException("Fan 1 must already be in external mode; this check never sends F0.");
        if (original.Fans[0].Value is < 1 or > 100) throw new InvalidOperationException("This raw runtime check requires a nonzero restorable fallback; use the verified controller zero check instead.");
        void Verify(FanReport report)
        {
            if (report.GlobalMode != original.GlobalMode || !report.Fans.Select(f => (f.Mode, f.Value)).SequenceEqual(original.Fans.Select(f => (f.Mode, f.Value))))
                throw new IOException("Stored configuration changed during the hold check.");
        }
        int writes = 0;
        void Write(byte value)
        {
            Protocol.ValidateRuntimeAcknowledgement(transport.Exchange(Protocol.BuildRuntime(original.Fans, new Dictionary<int, byte> { [0] = value })), info.FanCount);
            writes++;
        }
        Console.WriteLine($"Hold baseline: firmware={info.Version}, fans={info.FanCount}, mode={original.Fans[0].Mode}, stored={original.Fans[0].Value}, RPM={original.Fans[0].Reading}, global={original.GlobalMode}.");
        try
        {
            foreach (bool renew in new[] { false, true })
            {
                string phase = renew ? "renew-1s" : "one-shot";
                Write(80); var timer = Stopwatch.StartNew();
                for (int second = 1; second <= 15; second++)
                {
                    Thread.Sleep(1000);
                    if (renew) Write(80);
                    var report = Read(); Verify(report);
                    Console.WriteLine($"HOLD {phase} t={timer.Elapsed.TotalSeconds:F1}s RPM={report.Fans[0].Reading} stored={report.Fans[0].Value} global={report.GlobalMode} writes99={writes}");
                }
            }
        }
        finally
        {
            Write(original.Fans[0].Value); Thread.Sleep(200); Verify(Read());
            Console.WriteLine($"Restored stored fallback target {original.Fans[0].Value}%; all stored modes/values/global unchanged; F0 writes=0.");
        }
        return 0;
    }
    if (args[0] == "--runtime-exercise")
    {
        if (endpoints.Count != 1) throw new InvalidOperationException("A runtime exercise requires one controller.");
        using var runtimeTransport = new TracingTransport(endpoints[0].Open());
        var info = Protocol.ParseHandshake(runtimeTransport.Exchange(Protocol.HandshakeRequest()));
        FanReport Read() => Protocol.ParseReport(runtimeTransport.Exchange(Protocol.ReadRequest()), info.FanCount);
        var original = Read();
        if (original.Fans[0].Mode is < 2 or > 5) throw new InvalidOperationException("Fan 1 must already be in external curve mode for this bounded test.");
        if (original.Fans[0].Value is < 1 or > 100) throw new InvalidOperationException("This raw runtime check requires a nonzero restorable fallback; use the verified controller zero check instead.");
        Console.WriteLine($"Runtime-only test: mode={original.Fans[0].Mode}, original setting={original.Fans[0].Value}, original RPM={original.Fans[0].Reading}, lighting={original.GlobalMode}.");
        try
        {
            runtimeTransport.Exchange(Protocol.BuildRuntime(original.Fans, new Dictionary<int, byte> { [0] = 80 }));
            for (int second = 0; second < 3; second++)
            {
                Thread.Sleep(1000); var status = Read();
                Console.WriteLine($"Runtime fan 1: mode={status.Fans[0].Mode}, setting={status.Fans[0].Value}, RPM={status.Fans[0].Reading}, lighting={status.GlobalMode}.");
            }
        }
        finally
        {
            runtimeTransport.Exchange(Protocol.BuildRuntime(original.Fans, new Dictionary<int, byte> { [0] = original.Fans[0].Value }));
            Thread.Sleep(200); var restored = Read();
            bool matches = restored.GlobalMode == original.GlobalMode && restored.Fans.Select(f => (f.Mode, f.Value)).SequenceEqual(original.Fans.Select(f => (f.Mode, f.Value)));
            Console.WriteLine($"Runtime restore mode/value/global-mode match: {matches}.");
            if (!matches) throw new IOException("Runtime restore read-back differs from the original state.");
        }
        return 0;
    }
    foreach (var endpoint in endpoints)
    {
        var diagnosticEndpoint = args.Contains("--trace")
            ? endpoint with { Open = () => new TracingTransport(endpoint.Open()) } : endpoint;
        using var controller = new DrgbController(diagnosticEndpoint, Console.Error.WriteLine, DeviceDiscovery.VendorAppRunning);
        var before = controller.Snapshot()!;
        Console.WriteLine($"Firmware={controller.FirmwareVersion}; FanCount={controller.FanCount}; GlobalMode=0x{before.GlobalMode:X2}");
        for (int i = 0; i < controller.FanCount; i++)
            Console.WriteLine($"Fan {i + 1}: mode={before.Fans[i].Mode}; stored setting={before.Fans[i].Value}; raw={before.Fans[i].Reading}; RPM={controller.Rpm(i)?.ToString() ?? "unknown"}; control percent={controller.Percent(i)?.ToString() ?? "unknown"}");
        if (args[0] == "--zero-check")
        {
            if (!controller.HasFanOnlySettings) throw new InvalidOperationException("Zero check requires verified fan-only settings support.");
            int position = Array.IndexOf(args, "--channel");
            if (position < 0 || position + 1 >= args.Length || !int.TryParse(args[position + 1], out int channel) || channel < 1 || channel > controller.FanCount)
                throw new ArgumentException("A valid 1-based --channel is required.");
            if (!Protocol.IsExternalMode(before.Fans[channel - 1].Mode)) throw new InvalidOperationException("Zero check requires an existing external mode; it must not send F0.");
            try
            {
                foreach (var phase in new[] { (Percent: 80, Seconds: 5), (Percent: 0, Seconds: 10), (Percent: 1, Seconds: 8), (Percent: 80, Seconds: 6) })
                {
                    controller.Request(channel - 1, phase.Percent); controller.Update();
                    var expected = (FanState[])before.Fans.Clone();
                    if (phase.Percent == 0) expected[channel - 1] = expected[channel - 1] with { Mode = 1, Value = 0 };
                    var timer = Stopwatch.StartNew();
                    for (int second = 1; second <= phase.Seconds; second++)
                    {
                        Thread.Sleep(1000); controller.Update();
                        var report = controller.Snapshot() ?? throw new IOException("Controller disconnected during zero check.");
                        if (report.GlobalMode != before.GlobalMode || !report.Fans.Select(f => (f.Mode, f.Value)).SequenceEqual(expected.Select(f => (f.Mode, f.Value))))
                            throw new IOException("Unexpected stored configuration during zero check.");
                        if (controller.Percent(channel - 1) != phase.Percent)
                            throw new IOException("Zero check target was not acknowledged and verified.");
                        Console.WriteLine($"ZERO-CHECK channel={channel} target={phase.Percent}% t={timer.Elapsed.TotalSeconds:F1}s acknowledged={controller.Percent(channel - 1)}% raw={report.Fans[channel - 1].Reading} RPM={controller.Rpm(channel - 1)?.ToString() ?? "unknown"}");
                    }
                }
            }
            finally
            {
                if (!controller.Reset(channel - 1)) throw new IOException("Restoration after zero check was not confirmed.");
                var after = controller.Snapshot() ?? throw new IOException("No restoration read-back.");
                if (after.GlobalMode != before.GlobalMode || !after.Fans.Select(f => (f.Mode, f.Value)).SequenceEqual(before.Fans.Select(f => (f.Mode, f.Value))))
                    throw new IOException("Stored configuration differs after zero check restoration.");
                Console.WriteLine("Zero check complete: restored stored fallback; all stored modes/values/global match.");
            }
        }
        if (args[0] == "--exercise")
        {
            int Option(string name)
            {
                int position = Array.IndexOf(args, name);
                if (position < 0 || position + 1 >= args.Length || !int.TryParse(args[position + 1], out int value))
                    throw new ArgumentException("Missing integer option " + name);
                return value;
            }
            int channel = Option("--channel"), percent = Option("--percent"), seconds = Option("--seconds");
            if (channel < 1 || channel > controller.FanCount || percent < 0 || percent > 100 || seconds < 1 || seconds > 60)
                throw new ArgumentException("Exercise options are outside the documented range.");
            Console.WriteLine($"Temporarily setting fan {channel} to {percent}% for {seconds}s; original configuration and stored fallback target will be restored.");
            controller.Request(channel - 1, percent); controller.Update();
            if (controller.Percent(channel - 1) != percent) throw new IOException("Runtime request was not acknowledged and verified.");
            Console.WriteLine($"Control target confirmed: {controller.Percent(channel - 1)}%; zero uses fixed mode/value read-back, positive runtime targets are not echoed in stored values.");
            for (int elapsed = 0; elapsed < seconds; elapsed++)
            {
                Thread.Sleep(1000); controller.Update();
                Console.WriteLine($"Fan {channel}: RPM={controller.Rpm(channel - 1)?.ToString() ?? "unknown"}");
            }
            if (!controller.Reset(channel - 1)) throw new IOException("The device did not confirm restoration; inspect the diagnostic log.");
            var after = controller.Snapshot()!;
            for (int i = 0; i < controller.FanCount; i++)
                if (before.Fans[i].Mode != after.Fans[i].Mode || before.Fans[i].Value != after.Fans[i].Value)
                    throw new IOException($"Channel {i + 1} settings differ after restoration.");
            if (before.GlobalMode != after.GlobalMode) throw new IOException("Global mode changed during the exercise.");
            Console.WriteLine($"Restored fan {channel}: mode={after.Fans[channel - 1].Mode}; setting={after.Fans[channel - 1].Value}.");
            Console.WriteLine("Read-back confirmed: original modes/settings for ALL channels and the global mode are unchanged.");
        }
    }
    return endpoints.Count == 0 ? 1 : 0;
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }

sealed class TracingTransport(ITransport inner) : ITransport
{
    public int InputReportLength => inner.InputReportLength;
    public int OutputReportLength => inner.OutputReportLength;
    public byte[] Exchange(byte[] request)
    {
        Console.WriteLine("HID TX " + Convert.ToHexString(request));
        byte[] response = inner.Exchange(request);
        int meaningfulLength = response.Length >= 7 && response[2] == 0x30 && response[3] == 0x33 ? Math.Min(response.Length, 7 + 5 * response[6]) : Math.Min(response.Length, 16);
        Console.WriteLine($"HID RX ({response.Length} bytes; prefix) " + Convert.ToHexString(response.AsSpan(0, meaningfulLength)));
        return response;
    }
    public void Dispose() => inner.Dispose();
}
