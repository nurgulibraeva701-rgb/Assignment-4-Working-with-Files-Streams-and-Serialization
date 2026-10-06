namespace ColdChainMonitor;

public enum StorageClass { Cold, Frozen }

public sealed record Reading(
    string SensorId,
    DateTimeOffset Timestamp,
    StorageClass StorageClass,
    decimal Temperature);

public sealed record ImportError(
    int LineNumber, string RawLine, string Message);

/// <summary>Result of reading a whole input. Fully materialized: it holds no reader or stream.</summary>
public sealed record ImportResult(
    IReadOnlyList<Reading> Readings,
    IReadOnlyList<ImportError> Errors,
    int LineCount);

/// <summary>One alert per reading; both reasons can be true at the same time.</summary>
public sealed record Alert(
    Reading Reading,
    bool OutsideRange,
    bool AbruptChange);

public sealed record MonitoringArchive(
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<Reading> Readings,
    IReadOnlyList<ImportError> Errors,
    IReadOnlyList<Alert> Alerts);
