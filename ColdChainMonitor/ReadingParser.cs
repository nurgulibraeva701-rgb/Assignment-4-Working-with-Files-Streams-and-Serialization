using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ColdChainMonitor;

/// <summary>Pure parsing of one line. No files, no streams, no clock.</summary>
public static class ReadingParser
{
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static bool TryParseReading(
        string line,
        [NotNullWhen(true)] out Reading? reading,
        [NotNullWhen(false)] out string? error)
    {
        reading = null;
        error = null;

        var fields = line.Split('|');
        if (fields.Length != 4)
        {
            error = $"Expected 4 fields separated by '|' but found {fields.Length}.";
            return false;
        }

        var sensorId = fields[0].Trim();
        var timestampText = fields[1].Trim();
        var classText = fields[2].Trim();
        var temperatureText = fields[3].Trim();

        if (sensorId.Length == 0)
        {
            error = "SensorId must not be empty.";
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                timestampText,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            error = $"Invalid timestamp '{timestampText}'; expected {TimestampFormat}.";
            return false;
        }

        // Enum.TryParse happily accepts "99" and returns (StorageClass)99,
        // so IsDefined is needed to make sure the value is a declared member.
        // It also treats "Cold, Frozen" as a flags combination (0 | 1 = Frozen), so commas are rejected.
        if (classText.Contains(',')
            || !Enum.TryParse<StorageClass>(classText, ignoreCase: true, out var storageClass)
            || !Enum.IsDefined(storageClass))
        {
            error = $"Invalid storage class '{classText}'; expected Cold or Frozen.";
            return false;
        }

        if (!decimal.TryParse(
                temperatureText,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var temperature))
        {
            error = $"Invalid temperature '{temperatureText}'; use invariant format such as -18.0 or 4.5.";
            return false;
        }

        reading = new Reading(sensorId, timestamp, storageClass, temperature);
        return true;
    }
}
