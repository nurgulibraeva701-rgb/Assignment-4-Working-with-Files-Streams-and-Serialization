using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ColdChainMonitor;

public static class ArchiveStore
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // Keeps characters like ' readable in the file instead of \u0027 (the file is not HTML).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Enums are written as "Cold"/"Frozen"; numbers such as 0 or 99 are rejected when reading.
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    /// <summary>Writes at the stream's current position. The stream stays open (caller owns it).</summary>
    public static void WriteArchive(Stream output, MonitoringArchive archive)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(archive);

        // JsonSerializer.Serialize(Stream) never closes the stream.
        JsonSerializer.Serialize(output, archive, Options);
        output.Flush();
    }

    /// <summary>
    /// Reads from the stream's CURRENT position (no rewind) and leaves it open.
    /// A JSON "null" is rejected.
    /// </summary>
    public static MonitoringArchive ReadArchive(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var archive = JsonSerializer.Deserialize<MonitoringArchive>(input, Options)
                      ?? throw new JsonException("Archive JSON must not be null.");

        if (archive.Readings is null || archive.Errors is null || archive.Alerts is null)
        {
            throw new JsonException("Archive must contain readings, errors and alerts.");
        }

        return archive;
    }
}

/// <summary>Compares archives by VALUE (never by reference). Records compare their fields,
/// but their list properties would be compared by reference, so lists are compared item by item.</summary>
public static class ArchiveComparer
{
    public static bool AreEqual(MonitoringArchive a, MonitoringArchive b) =>
        a.CreatedAtUtc == b.CreatedAtUtc
        && a.Readings.SequenceEqual(b.Readings)
        && a.Errors.SequenceEqual(b.Errors)
        && a.Alerts.SequenceEqual(b.Alerts);
}
