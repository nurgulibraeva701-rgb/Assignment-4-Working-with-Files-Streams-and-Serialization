using System.Globalization;
using System.Text.Json;
using ColdChainMonitor;

var inputPath = args.Length > 0 ? args[0] : "readings.txt";
var archivePath = args.Length > 1 ? args[1] : "archive.json";

// The clock is read here, once, and passed down. Parsing and analysis never call DateTime.Now.
var createdAtUtc = DateTimeOffset.UtcNow;

static string Describe(Alert alert)
{
    var r = alert.Reading;
    var reasons = new List<string>();
    if (alert.OutsideRange) reasons.Add("outside range");
    if (alert.AbruptChange) reasons.Add("abrupt change");

    var time = r.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    var temperature = r.Temperature.ToString(CultureInfo.InvariantCulture);
    return $"{r.SensorId} {time} {temperature} ({r.StorageClass}): {string.Join("; ", reasons)}";
}

try
{
    // Program.cs owns every FileStream and disposes it with using.
    ImportResult import;
    using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        import = ReadingImporter.ReadReadings(input);
    }

    var archive = ReadingAnalyzer.BuildArchive(import, createdAtUtc);

    using (var output = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
    {
        ArchiveStore.WriteArchive(output, archive);
    }

    MonitoringArchive restored;
    using (var verify = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        restored = ArchiveStore.ReadArchive(verify);
    }

    Console.WriteLine($"Input lines:    {import.LineCount}");
    Console.WriteLine($"Valid readings: {archive.Readings.Count}");
    Console.WriteLine($"Import errors:  {archive.Errors.Count}");
    foreach (var e in archive.Errors)
    {
        Console.WriteLine($"  line {e.LineNumber}: {e.Message}  [{e.RawLine}]");
    }

    Console.WriteLine($"Alerts:         {archive.Alerts.Count}");
    foreach (var a in archive.Alerts)
    {
        Console.WriteLine($"  {Describe(a)}");
    }

    Console.WriteLine($"Archive written to {archivePath}");

    var roundTripOk = ArchiveComparer.AreEqual(archive, restored);
    Console.WriteLine(roundTripOk
        ? "Round-trip check: OK (values and collection contents match)"
        : "Round-trip check: FAILED");

    return roundTripOk ? 0 : 1;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
