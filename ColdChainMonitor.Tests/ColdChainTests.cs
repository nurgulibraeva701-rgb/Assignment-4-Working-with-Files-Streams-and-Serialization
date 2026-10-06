using System.Text;
using System.Text.Json;
using ColdChainMonitor;

namespace ColdChainMonitor.Tests;

public class ColdChainTests
{
    private static readonly string[] SuppliedLines =
    {
        "S1|2026-09-22T08:10:00Z|Cold|5.2",
        "S2|2026-09-22T08:01:00Z|Frozen|-18.0",
        "S1|2026-09-22T08:00:00Z|Cold|4.0",
        "S1|2026-09-22T08:05:00Z|Cold|8.7",
        "S2|2026-09-22T08:06:00Z|Frozen|-12.0",
        "S1|2026-09-22T08:15:00Z|Cold|8.0",
        "S3|bad-date|Cold|3.5",
        "S2|2026-09-22T08:11:00Z|99|-17.0",
        "S1|2026-09-22T08:20:00Z|Cold|5,5",
        "S1|2026-09-22T08:25:00Z|Frozen|-17.0",
    };

    private static readonly string SuppliedText = string.Join("\n", SuppliedLines) + "\n";

    // ---------- helpers ----------

    private static MemoryStream Utf8(string text) => new(Encoding.UTF8.GetBytes(text));

    private static ImportResult Import(string text)
    {
        using var stream = Utf8(text);
        return ReadingImporter.ReadReadings(stream);
    }

    private static Reading R(string sensor, int minute, StorageClass storageClass, decimal temperature) =>
        new(sensor, new DateTimeOffset(2026, 9, 22, 8, minute, 0, TimeSpan.Zero), storageClass, temperature);

    private static MonitoringArchive SuppliedArchive() =>
        ReadingAnalyzer.BuildArchive(Import(SuppliedText), new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));

    // ---------- 11 mandatory tests ----------

    [Fact]
    public void SuppliedFile_Produces6Readings_4Errors_2Alerts()
    {
        var import = Import(SuppliedText);
        var alerts = ReadingAnalyzer.FindAlerts(import.Readings);

        Assert.Equal(10, import.LineCount);
        Assert.Equal(6, import.Readings.Count);
        Assert.Equal(new[] { 7, 8, 9, 10 }, import.Errors.Select(e => e.LineNumber).ToArray());
        Assert.Equal(2, alerts.Count);

        // valid readings keep the original input order
        Assert.Equal(
            new[] { 5.2m, -18.0m, 4.0m, 8.7m, -12.0m, 8.0m },
            import.Readings.Select(r => r.Temperature).ToArray());

        // alert order: S1 08:05, then S2 08:06; both reasons on each
        Assert.Equal("S1", alerts[0].Reading.SensorId);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 8, 5, 0, TimeSpan.Zero), alerts[0].Reading.Timestamp);
        Assert.Equal(8.7m, alerts[0].Reading.Temperature);
        Assert.Equal("S2", alerts[1].Reading.SensorId);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 8, 6, 0, TimeSpan.Zero), alerts[1].Reading.Timestamp);
        Assert.Equal(-12.0m, alerts[1].Reading.Temperature);
        Assert.All(alerts, a =>
        {
            Assert.True(a.OutsideRange);
            Assert.True(a.AbruptChange);
        });

        // S1 at 08:15 with exactly 8.0 is safe -> no alert for it
        Assert.DoesNotContain(alerts, a => a.Reading.Temperature == 8.0m);
    }

    [Fact]
    public void NumericEnumText99_IsRejected_EvenThoughEnumTryParseSucceeds()
    {
        // The trap: TryParse succeeds for "99" ...
        Assert.True(Enum.TryParse<StorageClass>("99", ignoreCase: true, out var raw));
        Assert.False(Enum.IsDefined(raw));

        // ... but our parser must still reject it.
        var ok = ReadingParser.TryParseReading("S1|2026-09-22T08:00:00Z|99|4.0", out var reading, out var error);

        Assert.False(ok);
        Assert.Null(reading);
        Assert.Contains("99", error);
    }

    [Theory]
    [InlineData("5,5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e3")]
    [InlineData("")]
    public void InvalidTemperatureText_IsRejected_UnderInvariantParsing(string temperature)
    {
        var ok = ReadingParser.TryParseReading(
            $"S1|2026-09-22T08:00:00Z|Cold|{temperature}", out var reading, out var error);

        Assert.False(ok);
        Assert.Null(reading);
        Assert.NotNull(error);
    }

    [Fact]
    public void StorageClassChange_ForSameSensor_BecomesImportError()
    {
        var import = Import(
            "S1|2026-09-22T08:00:00Z|Cold|4.0\n" +
            "s1|2026-09-22T08:05:00Z|Frozen|-17.0\n"); // different casing, same sensor

        Assert.Single(import.Readings);
        var error = Assert.Single(import.Errors);
        Assert.Equal(2, error.LineNumber);
        Assert.Contains("Cold", error.Message);
        Assert.Contains("Frozen", error.Message);
    }

    [Theory]
    [InlineData(StorageClass.Cold, 8.0, false)]
    [InlineData(StorageClass.Cold, 2.0, false)]
    [InlineData(StorageClass.Cold, 8.1, true)]
    [InlineData(StorageClass.Cold, 1.9, true)]
    [InlineData(StorageClass.Frozen, -15.0, false)]
    [InlineData(StorageClass.Frozen, -22.0, false)]
    [InlineData(StorageClass.Frozen, -14.9, true)]
    [InlineData(StorageClass.Frozen, -22.1, true)]
    public void SafeRangeLimits_AreInclusive(StorageClass storageClass, double temperature, bool expectedOutside)
    {
        var reading = R("S1", 0, storageClass, (decimal)temperature);

        Assert.Equal(expectedOutside, ReadingAnalyzer.IsOutsideRange(reading));
    }

    [Fact]
    public void AdjacentComparisons_NeverCrossSensorBoundaries()
    {
        var readings = new[]
        {
            R("S1", 0, StorageClass.Cold, 5.0m),
            R("S2", 1, StorageClass.Frozen, -18.0m),
            R("S1", 2, StorageClass.Cold, 5.5m),
            R("S2", 3, StorageClass.Frozen, -17.5m),
        };

        // The trap exists: globally ordered neighbours belong to different sensors (5.0 vs -18.0 ...).
        var naivePairs = ReadingAnalyzer.Pairwise(readings.OrderBy(r => r.Timestamp)).ToList();
        Assert.Contains(naivePairs, p => p.Previous.SensorId != p.Current.SensorId);

        // Grouping first means no cross-sensor comparison, so there is no false "abrupt change".
        Assert.Empty(ReadingAnalyzer.FindAlerts(readings));
    }

    [Fact]
    public void InputOrder_DoesNotChangeTheAlerts()
    {
        var valid = Import(SuppliedText).Readings;
        var expected = ReadingAnalyzer.FindAlerts(valid);

        Assert.Equal(2, expected.Count);
        Assert.True(expected.SequenceEqual(ReadingAnalyzer.FindAlerts(valid.OrderBy(r => r.Timestamp))));
        Assert.True(expected.SequenceEqual(ReadingAnalyzer.FindAlerts(valid.OrderByDescending(r => r.Timestamp))));
    }

    [Fact]
    public void ReadReadings_LeavesCallerOwnedStreamOpen()
    {
        using var stream = Utf8(SuppliedText);

        ReadingImporter.ReadReadings(stream);

        Assert.True(stream.CanRead);
        _ = stream.ReadByte(); // would throw ObjectDisposedException if the stream had been closed
    }

    [Fact]
    public void WriteArchive_LeavesCallerOwnedStreamOpen()
    {
        using var stream = new MemoryStream();

        ArchiveStore.WriteArchive(stream, SuppliedArchive());

        Assert.True(stream.CanWrite);
        Assert.True(stream.Position > 0);
        stream.WriteByte((byte)' '); // would throw ObjectDisposedException if the stream had been closed
    }

    [Fact]
    public void MemoryStreamRoundTrip_PreservesValues_AndStringEnumNames()
    {
        var archive = SuppliedArchive();

        using var stream = new MemoryStream();
        ArchiveStore.WriteArchive(stream, archive);
        var json = Encoding.UTF8.GetString(stream.ToArray()); // ToArray ignores Position

        stream.Position = 0; // the written bytes are behind us until we rewind
        var restored = ArchiveStore.ReadArchive(stream);

        Assert.NotSame(archive, restored);                       // different objects ...
        Assert.True(ArchiveComparer.AreEqual(archive, restored)); // ... equal values
        Assert.Equal(6, restored.Readings.Count);
        Assert.Equal(4, restored.Errors.Count);
        Assert.Equal(2, restored.Alerts.Count);
        Assert.Equal(StorageClass.Frozen, restored.Readings[1].StorageClass);

        Assert.Contains("\"storageClass\": \"Cold\"", json);
        Assert.Contains("\"storageClass\": \"Frozen\"", json);
        Assert.DoesNotContain("\"storageClass\": 0", json);
        Assert.Contains("\"createdAtUtc\"", json); // camelCase names
    }

    [Fact]
    public void StoredResults_CanBeEnumerated_AfterSourceStreamIsDisposed()
    {
        var stream = Utf8(SuppliedText);
        var import = ReadingImporter.ReadReadings(stream);
        stream.Dispose();

        // Nothing is lazy: all of this works although the stream is gone.
        Assert.Equal(6, import.Readings.Count());
        Assert.Equal(4, import.Errors.Count());
        Assert.Equal(2, ReadingAnalyzer.FindAlerts(import.Readings).Count);
        Assert.Equal("S1", import.Readings.First().SensorId);
    }

    // ---------- additional tests ----------

    [Fact]
    public void ReadReadings_StartsAtCurrentPosition_AndDoesNotRewind()
    {
        using var stream = Utf8(SuppliedText);
        stream.Position = Encoding.UTF8.GetByteCount(SuppliedLines[0] + "\n"); // skip line 1 (S1 5.2)

        var import = ReadingImporter.ReadReadings(stream);

        Assert.Equal(9, import.LineCount);
        Assert.Equal(5, import.Readings.Count);
        Assert.DoesNotContain(import.Readings, r => r.Temperature == 5.2m);
        Assert.Equal(new[] { 6, 7, 8, 9 }, import.Errors.Select(e => e.LineNumber).ToArray());
    }

    [Fact]
    public void ReadArchive_RejectsJsonNull()
    {
        using var stream = Utf8("null");

        Assert.Throws<JsonException>(() => ArchiveStore.ReadArchive(stream));
    }

    [Fact]
    public void ReadArchive_WithoutRewind_FindsNothingToRead()
    {
        using var stream = new MemoryStream();
        ArchiveStore.WriteArchive(stream, SuppliedArchive());
        // Position is at the END of the written bytes: forgetting "Position = 0" means no JSON is left.

        Assert.ThrowsAny<JsonException>(() => ArchiveStore.ReadArchive(stream));
    }

    [Fact]
    public void ReadArchive_RejectsNumericEnumValues()
    {
        const string json =
            "{\"createdAtUtc\":\"2026-10-03T09:00:00+00:00\"," +
            "\"readings\":[{\"sensorId\":\"S1\",\"timestamp\":\"2026-09-22T08:00:00+00:00\"," +
            "\"storageClass\":0,\"temperature\":4.0}],\"errors\":[],\"alerts\":[]}";
        using var stream = Utf8(json);

        Assert.ThrowsAny<JsonException>(() => ArchiveStore.ReadArchive(stream));
    }

    [Fact]
    public void ChangeOfExactlyFour_IsNotAbrupt_ButJustOverFourIs()
    {
        var exactlyFour = ReadingAnalyzer.FindAlerts(new[]
        {
            R("S1", 0, StorageClass.Cold, 3.0m),
            R("S1", 5, StorageClass.Cold, 7.0m),
        });
        Assert.Empty(exactlyFour);

        var justOver = ReadingAnalyzer.FindAlerts(new[]
        {
            R("S1", 0, StorageClass.Cold, 3.0m),
            R("S1", 5, StorageClass.Cold, 7.1m),
        });
        var alert = Assert.Single(justOver);
        Assert.True(alert.AbruptChange);
        Assert.False(alert.OutsideRange);
    }

    [Fact]
    public void FirstReadingOutsideRange_AlertsWithoutAbruptChange()
    {
        var alerts = ReadingAnalyzer.FindAlerts(new[] { R("S1", 0, StorageClass.Cold, 9.0m) });

        var alert = Assert.Single(alerts);
        Assert.True(alert.OutsideRange);
        Assert.False(alert.AbruptChange);
    }

    [Theory]
    [InlineData("S1|2026-09-22T08:00:00Z|Cold")]                // too few fields
    [InlineData("S1|2026-09-22T08:00:00Z|Cold|4.0|extra")]      // too many fields
    [InlineData("  |2026-09-22T08:00:00Z|Cold|4.0")]            // empty sensor id
    [InlineData("S1|2026-09-22 08:00:00|Cold|4.0")]             // wrong timestamp format
    [InlineData("S1|2026-09-22T08:00:00|Cold|4.0")]             // missing Z
    [InlineData("S1|2026-09-22T08:00:00+02:00|Cold|4.0")]       // offset instead of Z
    [InlineData("S1|2026-09-22T08:00:00Z|Warm|4.0")]            // unknown storage class
    [InlineData("S1|2026-09-22T08:00:00Z|Cold, Frozen|4.0")]    // TryParse treats it as flags: 0 | 1 = Frozen
    public void MalformedLines_AreRejected(string line)
    {
        Assert.False(ReadingParser.TryParseReading(line, out var reading, out var error));
        Assert.Null(reading);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Parser_TrimsFields_IgnoresCase_AndAcceptsLeadingSign()
    {
        var ok = ReadingParser.TryParseReading(
            "  s1 | 2026-09-22T08:00:00Z | cOLD | +4.5 ", out var reading, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("s1", reading!.SensorId);
        Assert.Equal(StorageClass.Cold, reading.StorageClass);
        Assert.Equal(4.5m, reading.Temperature);
        Assert.Equal(TimeSpan.Zero, reading.Timestamp.Offset);
    }

    [Fact]
    public void BlankLines_AreSkipped_ButLineNumbersStayOriginal()
    {
        var import = Import(
            "S1|2026-09-22T08:00:00Z|Cold|4.0\n" +
            "\n" +
            "   \n" +
            "S1|bad|Cold|4.0\n");

        Assert.Equal(4, import.LineCount);
        Assert.Single(import.Readings);
        Assert.Equal(4, Assert.Single(import.Errors).LineNumber);
    }
}
