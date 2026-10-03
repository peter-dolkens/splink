using SpLink.Protocol;
using Xunit;

namespace SpLink.Protocol.Tests;

public class SpProLogTests
{
    /// <summary>A real Daily Summary record read from an SPMC482 at address 0xC8090 on 2026-10-03.</summary>
    private static readonly ushort[] RealDailyRecord =
    [
        31999, 12400, 21, 0, 17, 0, 75, 0, 34, 0, 0, 0, 73, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 217, 0, 6489, 0, 1440, 0, 181, 1965,
        5300, 2934, 1050, 16000, 530, 0,
    ];

    [Fact]
    public void Timestamps_use_a_2001_epoch()
    {
        // The decompiled helper is named "Year2000" but constructs 2001-01-01; hardware confirms 2001.
        Assert.Equal(new DateTime(2026, 10, 2, 23, 59, 59), SpProLogDecoder.Timestamp(31999, 12400));
        Assert.Equal(new DateTime(2001, 1, 1), SpProLogDecoder.Timestamp(0, 0));
    }

    [Fact]
    public void Daily_summary_decodes_a_real_record()
    {
        var record = new SpProLogRecord(SpProLogType.DailySummary, 0xC8090, RealDailyRecord);
        var d = SpProLogDecoder.DecodeDailySummary(record, version: 5);

        Assert.Equal(new DateTime(2026, 10, 2, 23, 59, 59), d.Timestamp);
        Assert.Equal(2.584, d["DcInputTotal"]!.Value, 3);
        Assert.Equal(8.314, d["AcLoadTotal"]!.Value, 3);
        // A full day in float: 1440 minutes.
        Assert.Equal(24.0, d["FloatHours"]!.Value, 3);
        Assert.Equal(108.15, d["ModulationHours"]!.Value, 3);
        Assert.Equal(0.959, d["BatteryChargeEfficiencyIndex"]!.Value, 3);
        // v5 packs AC-coupled energy into words 27/28 with the halves swapped.
        Assert.Equal(20.615, d["AcCoupledTotal"]!.Value, 3);
    }

    [Fact]
    public void Daily_summary_scales_come_from_the_second_to_last_five_words()
    {
        var s = SpProLogScales.FromTail(RealDailyRecord, 6);
        Assert.Equal(new SpProLogScales(5300, 2934, 1050, 16000, 530), s);
    }

    [Fact]
    public void Detailed_scales_come_from_the_last_five_words()
    {
        ushort[] w = [.. Enumerable.Repeat((ushort)0, 39), 5300, 2934, 1050, 16000, 530];
        Assert.Equal(new SpProLogScales(5300, 2934, 1050, 16000, 530), SpProLogScales.FromTail(w));
    }

    [Fact]
    public void Blank_event_slots_are_recognised()
    {
        var blank = new SpProLogRecord(SpProLogType.AlertEvents, 0, [0, 0, 0xFFFF, .. Enumerable.Repeat((ushort)0, 33)]);
        var real = new SpProLogRecord(SpProLogType.AlertEvents, 0, [0, 0, 21, .. Enumerable.Repeat((ushort)0, 33)]);
        Assert.True(SpProLogDecoder.IsBlankEvent(blank));
        Assert.False(SpProLogDecoder.IsBlankEvent(real));
    }

    [Fact]
    public void Event_labels_match_sp_links_tables()
    {
        Assert.Equal("Inverter - Low Battery Voltage Alert", SpProLogLabels.Lookup("AlertEvent", 2));
        Assert.Equal("System Beeper - Lock Out Inactive", SpProLogLabels.Lookup("OperationalEvent", 2));
        Assert.Equal("Not Running", SpProLogLabels.Lookup("GeneratorStatus", 0));
        Assert.Equal("Idle", SpProLogLabels.Lookup("OperationalMode", 0));
        Assert.Null(SpProLogLabels.Lookup("AlertEvent", 60000));
    }

    [Fact]
    public void Sensor_fault_and_disabled_sentinels_decode_to_null()
    {
        var w = new ushort[44];
        w[26] = 0x7FFF;  // transformer temperature fault
        w[31] = 0xFFFF;  // SoC monitoring disabled
        w[^5] = 5300; w[^4] = 2934; w[^3] = 1050; w[^2] = 16000; w[^1] = 530;

        var d = SpProLogDecoder.DecodeDetailed(new SpProLogRecord(SpProLogType.Detailed, 0, w), version: 3);

        // The indexer yields double?, so a sentinel surfaces as null rather than a bogus reading.
        Assert.Null(d["TransformerTempMax"]);
        Assert.Null(d["StateOfCharge"]);
        Assert.NotNull(d.Fields.Single(f => f.Name == "StateOfCharge"));
    }

    [Fact]
    public void Each_log_has_a_distinct_register_block()
    {
        var all = SpProLogRegisters.All;
        Assert.Equal(4, all.Count);
        Assert.Equal(4, all.Values.Select(r => r.NumberOfSectors).Distinct().Count());
        // The date-range search is triggered by a write landing on SearchStatus, four words past the start date.
        Assert.All(all.Values, r => Assert.Equal(r.RequiredStartDate + 4, r.SearchStatus));
    }

    [Theory]
    [InlineData(36, 7)]   // events and daily summary
    [InlineData(44, 5)]   // detailed
    public void Entries_per_read_respects_the_256_word_limit(int entrySize, int expected)
    {
        var info = new SpProLogInfo(SpProLogType.Detailed, 2, entrySize, 0, 10);
        Assert.Equal(expected, info.EntriesPerRead);
        Assert.True(info.EntriesPerRead * entrySize <= 256);
    }
}
