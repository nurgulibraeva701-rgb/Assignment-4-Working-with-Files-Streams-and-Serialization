namespace ColdChainMonitor;

/// <summary>Pure analysis. The current time is never read here; it is passed in.</summary>
public static class ReadingAnalyzer
{
    public const decimal AbruptChangeThreshold = 4.0m;

    public static (decimal Min, decimal Max) GetSafeRange(StorageClass storageClass) => storageClass switch
    {
        StorageClass.Cold => (2.0m, 8.0m),
        StorageClass.Frozen => (-22.0m, -15.0m),
        _ => throw new ArgumentOutOfRangeException(nameof(storageClass), storageClass, "Undeclared storage class.")
    };

    /// <summary>Limits are inclusive: exactly the minimum or maximum is safe.</summary>
    public static bool IsOutsideRange(Reading reading)
    {
        var (min, max) = GetSafeRange(reading.StorageClass);
        return reading.Temperature < min || reading.Temperature > max;
    }

    public static bool IsAbruptChange(Reading previous, Reading current) =>
        Math.Abs(current.Temperature - previous.Temperature) > AbruptChangeThreshold;

    /// <summary>
    /// (item[0], item[1]), (item[1], item[2]), ... The source is materialized once,
    /// so it is enumerated a single time even if it is a lazy query.
    /// </summary>
    public static IEnumerable<(T Previous, T Current)> Pairwise<T>(IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var items = source as IReadOnlyList<T> ?? source.ToList();
        return items.Zip(items.Skip(1), (previous, current) => (Previous: previous, Current: current));
    }

    public static IReadOnlyList<Alert> FindAlerts(IEnumerable<Reading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        var alerts = new List<Alert>();

        // 1) Group by sensor FIRST, 2) sort each group by time, 3) only then compare neighbours.
        foreach (var sensorGroup in readings.GroupBy(r => r.SensorId, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = sensorGroup.OrderBy(r => r.Timestamp).ToList();

            // The first reading has no previous one: it cannot be abrupt, but it can be out of range.
            var first = ordered[0];
            if (IsOutsideRange(first))
            {
                alerts.Add(new Alert(first, OutsideRange: true, AbruptChange: false));
            }

            foreach (var (previous, current) in Pairwise(ordered))
            {
                var outside = IsOutsideRange(current);
                var abrupt = IsAbruptChange(previous, current);

                // At most one alert per current reading, carrying both reasons.
                if (outside || abrupt)
                {
                    alerts.Add(new Alert(current, outside, abrupt));
                }
            }
        }

        return alerts
            .OrderBy(a => a.Reading.SensorId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Reading.Timestamp)
            .ToList()
            .AsReadOnly();
    }

    public static MonitoringArchive BuildArchive(ImportResult import, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(import);
        return new MonitoringArchive(
            createdAtUtc.ToUniversalTime(),
            import.Readings,
            import.Errors,
            FindAlerts(import.Readings));
    }
}
