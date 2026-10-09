namespace SpLink.Protocol;

/// <summary>One Service Settings word: where it sits in the block and what SP LINK calls it.</summary>
public readonly record struct ServiceSetting(int Offset, string Name)
{
    /// <summary>The register this word is read from.</summary>
    public uint Address => SpProServiceSettings.Block + (uint)Offset;
}

/// <summary>
/// SP LINK's <b>Service Settings</b>, which live in their own register block rather than in any of
/// the four configuration blocks <see cref="SpProConfigMap"/> covers.
/// <para>
/// This map is hand-maintained, unlike the generated ones: it did not come from the decompile. It
/// was contributed by GitHub user <c>mallinss</c>, who located the block by value-searching a SP
/// LINK Service Settings export against the register space on an SPMC482-AU running firmware 15.47,
/// matching all 39 named settings.
/// </para>
/// <para>
/// Verified here against a second SPMC482 on firmware 16.11, so the address holds across versions.
/// The spacer words at offsets 6-8 read <c>0000</c>, <c>FFFF</c>, <c>0000</c> — an arbitrary pattern
/// the map predicts in advance, which is what makes the match convincing rather than coincidental.
/// The named settings also agree with what is independently known about that installation: an
/// expansion card reported as fitted, and shunts reported as absent.
/// </para>
/// <para>
/// <b>Read-only, emphatically.</b> Several of these are grid-protection parameters — anti-islanding
/// sensitivity, ride-through, disconnection monitoring, export limits. splink has no configuration
/// write path at all, and this block is the last place that should ever change.
/// </para>
/// </summary>
public static class SpProServiceSettings
{
    /// <summary>First register of the block.</summary>
    public const uint Block = 49665;

    /// <summary>Length of the whole block, including the spacer words between named settings.</summary>
    public const int WordCount = 127;

    /// <summary>
    /// The 39 named words. Offsets not listed are spacers, which read 0 or 0xFFFF; they are omitted
    /// rather than named so that an unnamed word is unambiguous.
    /// </summary>
    public static readonly IReadOnlyList<ServiceSetting> All =
    [
        new(0, "FanType"),
        new(1, "InitialSoC"),
        new(2, "Shunt1UserZero"),
        new(3, "Shunt2UserZero"),
        new(4, "ACInputDisconnectionMonitor"),
        new(5, "ExpansionCardEnabled"),
        new(9, "SynchOverloadShutdownCountTrips"),
        new(10, "EmulatedInductanceRatio"),
        new(11, "ActiveAntiIslandingSensitivityPercent"),
        new(12, "MultiPhaseValuesSplink"),
        // Gates the Modbus power-override registers (8032/8033), which are otherwise writable.
        new(13, "AllowPowerOverride"),
        new(14, "IndependentGridDisconnect"),
        // The control loop that commands a managed AC-coupled solar inverter's output. One set of
        // gains applies while synchronised to an AC source, another while standalone.
        new(15, "AcSolarIntegralGainConnectedToSource"),
        new(16, "AcSolarDifferentialGainConnectedToSource"),
        new(17, "AcSolarFeedForwardGainConnectedToSource"),
        new(18, "AcSolarProportionalGainConnectedToSource"),
        new(19, "AcSolarIntegralGain"),
        new(20, "AcSolarDifferentialGain"),
        new(21, "AcSolarFeedForwardGain"),
        // A five-point proportional-gain schedule against voltage.
        new(22, "AcSolarProportionalVoltage0"),
        new(23, "AcSolarProportionalGain0"),
        new(24, "AcSolarProportionalVoltage1"),
        new(25, "AcSolarProportionalGain1"),
        new(26, "AcSolarProportionalVoltage2"),
        new(27, "AcSolarProportionalGain2"),
        new(28, "AcSolarProportionalVoltage3"),
        new(29, "AcSolarProportionalGain3"),
        new(30, "AcSolarProportionalVoltage4"),
        new(31, "AcSolarProportionalGain4"),
        new(32, "ServiceSettingReserved33"),
        new(33, "ServiceSettingReserved34"),
        new(34, "AemoRideThrough"),
        new(35, "ActivePowerDisconnect"),
        new(36, "AcSolarFilterRisingSync"),
        new(37, "AcSolarFilterFallingSync"),
        new(38, "AcSolarFilterRisingStandalone"),
        new(39, "AcSolarFilterFallingStandalone"),
        new(40, "DefaultExportLimit"),
        new(41, "DefaultExportLimitRampRate"),
    ];

    /// <summary>
    /// Reads the block and pairs each named word with its raw value. No conversions are applied:
    /// the contributed map names the settings but does not say how SP LINK scales or enumerates
    /// them, and inventing a scaling for a grid-protection parameter would be worse than showing
    /// the raw count.
    /// </summary>
    public static async Task<IReadOnlyList<ServiceSettingValue>> ReadAsync(
        SpProClient client, CancellationToken cancellationToken = default)
    {
        var words = await client.ReadWordsAsync(Block, WordCount, cancellationToken).ConfigureAwait(false);
        return [.. All.Where(s => s.Offset < words.Length)
                      .Select(s => new ServiceSettingValue(s, words[s.Offset]))];
    }
}

/// <summary>One Service Settings word as read from the inverter.</summary>
public readonly record struct ServiceSettingValue(ServiceSetting Setting, ushort Raw)
{
    public string Name => Setting.Name;
    public uint Address => Setting.Address;
}
