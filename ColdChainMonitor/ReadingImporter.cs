using System.Text;

namespace ColdChainMonitor;

public static class ReadingImporter
{
    /// <summary>
    /// Reads the stream from its CURRENT position to the end. The caller owns the stream:
    /// it is not rewound and not closed. The returned result is fully materialized.
    /// </summary>
    public static ImportResult ReadReadings(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var readings = new List<Reading>();
        var errors = new List<ImportError>();
        var classBySensor = new Dictionary<string, StorageClass>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;

        // leaveOpen: true -> disposing the reader does NOT close the caller's stream.
        using (var reader = new StreamReader(
                   input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                   bufferSize: 1024, leaveOpen: true))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lineNumber++;

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue; // empty lines are skipped, but still counted for numbering
                }

                if (!ReadingParser.TryParseReading(line, out var reading, out var error))
                {
                    errors.Add(new ImportError(lineNumber, line, error));
                    continue;
                }

                // After its first valid reading a sensor must keep the same storage class.
                if (classBySensor.TryGetValue(reading.SensorId, out var expected))
                {
                    if (expected != reading.StorageClass)
                    {
                        errors.Add(new ImportError(
                            lineNumber, line,
                            $"Sensor '{reading.SensorId}' was already registered as {expected}, " +
                            $"but this line says {reading.StorageClass}."));
                        continue;
                    }
                }
                else
                {
                    classBySensor[reading.SensorId] = reading.StorageClass;
                }

                readings.Add(reading);
            }
        }

        // Everything was read inside the using block; nothing here depends on the reader.
        return new ImportResult(readings.AsReadOnly(), errors.AsReadOnly(), lineNumber);
    }
}
