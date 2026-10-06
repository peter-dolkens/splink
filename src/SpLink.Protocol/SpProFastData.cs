namespace SpLink.Protocol;

/// <summary>
/// The subset of live data worth reading at high frequency: enough to decide whether load must
/// be shed, and nothing else.
/// </summary>
public sealed record SpProFastReading
{
    public DateTimeOffset TakenAt { get; init; }

    /// <summary>AC load power in kW — the figure a shedding decision turns on.</summary>
    public double AcLoadKilowatts { get; init; }

    /// <summary>The inverter's own AC power in kW, which is not the same as the load figure.</summary>
    public double InverterAcKilowatts { get; init; }

    public double? BatterySoCPercent { get; init; }
    public double BatteryAmps { get; init; }
    public double DcAmps { get; init; }
    public string ChargerState { get; init; } = "";
    public string InverterMode { get; init; } = "";
    public string AcSourceStatus { get; init; } = "";
}

/// <summary>
/// Decodes the fast windows. The offsets are the same ones <see cref="SpProLiveData"/> uses, so
/// a value read on the fast path and the same value read in a full sweep agree by construction.
/// </summary>
public static class SpProFastData
{
    public static SpProFastReading Decode(IReadOnlyDictionary<int, ushort> w, SpProScaleFactors scale)
    {
        ushort At(int i) => w.TryGetValue(i, out var v) ? v
            : throw new SpProProtocolException($"fast read is missing Now-block word {i}");

        int Int32At(int lo) => At(lo) | (At(lo + 1) << 16);

        return new SpProFastReading
        {
            TakenAt = DateTimeOffset.Now,
            InverterAcKilowatts = scale.AcKilowatts32(At(0), At(1)),
            AcLoadKilowatts = scale.AcKilowatts32(At(34), At(35)),
            ChargerState = SpProLiveData.ChargerStateName(At(36)),
            InverterMode = SpProLiveData.InverterModeName(At(37)),
            AcSourceStatus = SpProLiveData.AcSourceStatusName(At(40)),
            BatterySoCPercent = SpProScaling.Percent(At(41)),
            DcAmps = scale.DcAmps(At(43)),
            BatteryAmps = scale.DcAmps32(At(44), At(45)),
        };
    }
}
