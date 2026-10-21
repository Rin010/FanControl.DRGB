namespace FanControl.DRGB;

internal readonly record struct DeviceModel(int VendorId, int ProductId, string Name);
internal readonly record struct FanState(byte Mode, byte Value, ushort Reading)
{
    // DRGB's own UI labels 0..100 as percent, not RPM. Never fabricate RPM from it.
    public float? Rpm => Reading > 100 ? Reading : null;
}
internal sealed record FanReport(byte GlobalMode, FanState[] Fans);

internal static class Protocol
{
    internal static readonly DeviceModel[] SupportedDevices =
    [
        new(0x2486, 0x3217, "DeepRGB U16 V5F"),
        new(0x2486, 0x3229, "DeepRGB C16 V5F"),
        new(0x2486, 0x3232, "DeepRGB S16 V5F")
    ];
    internal static byte[] HandshakeRequest() => [0x00, 0x02, 0x00];
    internal static byte[] ReadRequest() => [0x00, 0x30, 0x33, 0xEE];

    internal static (Version Version, int FanCount) ParseHandshake(byte[] data)
    {
        if (data.Length < 8 || data[2] != 0 || data[3] != 9)
            throw new InvalidDataException("Unexpected DRGB handshake (expected firmware family 0.9).");
        int count = data[7];
        if (count < 1 || count > 32)
            throw new InvalidDataException($"Invalid DRGB fan count: {count}.");
        return (new Version(data[2], data[3], data[4], data[5]), count);
    }

    internal static bool IsFanReport(byte[] data) =>
        data.Length >= 7 && data[2] == 0x30 && data[3] == 0x33 && data[4] == 0xEE;

    internal static FanReport ParseReport(byte[] data, int expectedCount)
    {
        if (!IsFanReport(data)) throw new InvalidDataException("Unexpected DRGB fan response header.");
        if (data[6] != expectedCount || data.Length < 7 + 5 * expectedCount)
            throw new InvalidDataException("DRGB fan response is truncated or its channel count changed.");
        var fans = new FanState[expectedCount];
        for (int i = 0; i < fans.Length; i++)
        {
            int p = 7 + i * 5;
            fans[i] = new FanState(data[p + 1], data[p + 2],
                (ushort)((data[p + 3] << 8) | data[p + 4]));
        }
        return new FanReport(data[5], fans);
    }

    internal static byte[] BuildSettings(byte globalMode, IReadOnlyList<FanState> fans)
    {
        var data = new byte[6 + fans.Count * 3];
        data[1] = 0x30; data[2] = 0x33; data[3] = 0xF0; data[4] = globalMode;
        for (int i = 0; i < fans.Count; i++)
        {
            int p = 6 + i * 3;
            data[p + 1] = fans[i].Mode;
            data[p + 2] = fans[i].Value;
        }
        return data;
    }

    internal static bool SupportsFanOnlySettings(DeviceModel? model, Version firmware, int fanCount) =>
        model is { VendorId: 0x2486, ProductId: 0x3217 } &&
        firmware == new Version(0, 9, 212, 38) && fanCount == 10;

    internal static byte[] BuildFanOnlySettings(IReadOnlyList<FanState> fans)
    {
        // Matching U16 firmware separates fan configuration (CC) from the RGB
        // action in F0. M must remain zero: 8..12 are shared special actions.
        if (fans.Any(f => f.Mode > 5 || f.Value > 100))
            throw new InvalidDataException("Fan-only settings require valid modes and percentages.");
        var data = BuildSettings(0, fans);
        data[3] = 0xCC;
        return data;
    }

    internal static byte ToPercent(float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "A finite percentage is required.");
        return (byte)Math.Round(Math.Clamp(value, 0f, 100f), MidpointRounding.AwayFromZero);
    }

    internal static bool IsExternalMode(byte mode) => mode is >= 2 and <= 5;

    internal static byte[] BuildRuntime(IReadOnlyList<FanState> fans, IReadOnlyDictionary<int, byte> overrides)
    {
        var data = new byte[6 + fans.Count * 3];
        data[1] = 0x30; data[2] = 0x33; data[3] = 0x99;
        for (int i = 0; i < fans.Count; i++)
        {
            if (overrides.ContainsKey(i) && !IsExternalMode(fans[i].Mode))
                throw new InvalidDataException("Runtime PWM requires an external-control channel mode.");
            if (overrides.TryGetValue(i, out byte target) && target == 0)
                throw new InvalidDataException("Runtime command 0x99 ignores zero; use verified fan-only fixed-zero settings.");
            // Follow the vendor's layout: external modes get a percentage;
            // modes 0/1 get zero. The matched U16 ignores zero in 99, so a zero
            // fallback keeps an unowned channel's previous runtime target.
            byte value = IsExternalMode(fans[i].Mode)
                ? overrides.TryGetValue(i, out byte requested) ? requested : fans[i].Value : (byte)0;
            if (value > 100) throw new InvalidDataException("A runtime percentage is outside 0..100.");
            data[8 + 3 * i] = value;
        }
        return data;
    }

    internal static void ValidateRuntimeAcknowledgement(byte[] data, int expectedCount)
    {
        if (data.Length < 7 + 5 * expectedCount || data[2] != 0x30 || data[3] != 0x33 ||
            data[4] != 0x99 || data[6] != expectedCount)
            throw new InvalidDataException("No matching DRGB runtime PWM acknowledgement.");
    }
}
