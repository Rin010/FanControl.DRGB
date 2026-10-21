using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HidSharp;

namespace FanControl.DRGB;

internal interface ITransport : IDisposable
{
    int InputReportLength { get; }
    int OutputReportLength { get; }
    byte[] Exchange(byte[] request);
}

internal sealed record DeviceEndpoint(string Key, string Name, Func<ITransport> Open, DeviceModel? Model = null);

internal static class DeviceDiscovery
{
    internal static IReadOnlyList<DeviceEndpoint> Find()
    {
        var endpoints = new List<DeviceEndpoint>();
        foreach (var model in Protocol.SupportedDevices)
        foreach (var device in DeviceList.Local.GetHidDevices(model.VendorId, model.ProductId))
        {
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device.DevicePath.ToLowerInvariant())))[..24];
            endpoints.Add(new DeviceEndpoint(key, model.Name, () => new HidTransport(device), model));
        }
        return endpoints.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
    }

    internal static bool VendorAppRunning()
    {
        var processes = Process.GetProcessesByName("DRGB");
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

internal sealed class HidTransport : ITransport
{
    private readonly HidStream _stream;
    // Also excludes a second copy of this plugin/diagnostic utility in another process.
    private readonly Semaphore _ownership;
    private int _disposed;
    public int InputReportLength { get; }
    public int OutputReportLength { get; }

    internal HidTransport(HidDevice device)
    {
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device.DevicePath.ToLowerInvariant())));
        _ownership = new Semaphore(1, 1, @"Local\FanControl.DRGB." + id);
        if (!_ownership.WaitOne(0))
        {
            _ownership.Dispose();
            throw new IOException("Another FanControl.DRGB instance owns this device.");
        }
        HidStream? opened = null;
        try
        {
            InputReportLength = device.GetMaxInputReportLength();
            OutputReportLength = device.GetMaxOutputReportLength();
            if (!device.TryOpen(out opened)) throw new IOException("Cannot open DRGB USB HID device.");
            opened.ReadTimeout = 500;
            opened.WriteTimeout = 500;
            _stream = opened;
        }
        catch
        {
            opened?.Dispose();
            _ownership.Release();
            _ownership.Dispose();
            throw;
        }
    }

    public byte[] Exchange(byte[] request)
    {
        if (request.Length > OutputReportLength) throw new InvalidDataException("DRGB command exceeds the device output report length.");
        var padded = new byte[OutputReportLength];
        request.CopyTo(padded, 0);
        _stream.Write(padded);
        return _stream.Read();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _stream.Dispose(); }
        finally { _ownership.Release(); _ownership.Dispose(); }
    }
}
