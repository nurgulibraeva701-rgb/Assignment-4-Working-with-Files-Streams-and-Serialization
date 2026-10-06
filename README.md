# ColdChainMonitor — Assignment 4

Console app that reads cold-chain sensor readings through a `Stream`, finds out-of-range readings and
abrupt changes, writes `archive.json` and reads it back to verify it.

## How to run

```
cd ColdChainMonitor
dotnet run                 # reads readings.txt, writes archive.json, verifies the round trip

cd ../ColdChainMonitor.Tests
dotnet test
```

Both projects target `net8.0` (change `TargetFramework` if you use another SDK).

## Layout

| File | Role |
| --- | --- |
| `Models.cs` | `StorageClass`, `Reading`, `ImportError`, `ImportResult`, `Alert`, `MonitoringArchive` |
| `ReadingParser.cs` | **pure** `TryParseReading` for one line |
| `ReadingImporter.cs` | `ReadReadings(Stream)`: I/O, line numbers, per-sensor class rule |
| `ReadingAnalyzer.cs` | **pure** range check, `Pairwise`, `FindAlerts`, `BuildArchive` |
| `ArchiveStore.cs` | `WriteArchive` / `ReadArchive` (System.Text.Json), `ArchiveComparer` |
| `Program.cs` | owns the `FileStream`s, reads the clock once, prints the report |

## Result for the supplied file

10 input lines, 6 valid readings, 4 import errors (lines 7–10: bad timestamp, numeric enum `99`,
comma decimal `5,5`, class mismatch), 2 alerts: S1 08:05 (8.7) and S2 08:06 (-12.0), both
"outside range + abrupt change". S1 at 08:15 with exactly 8.0 is safe.

## Answers

1. **Who owns each stream?** `Program.cs` creates the `FileStream`s and disposes them with `using`.
   The helper methods only borrow a stream. If a helper closed it, the caller could not reuse it
   (for example rewind a `MemoryStream` and read it back), so every `StreamReader` is created with
   `leaveOpen: true`, and `JsonSerializer.Serialize(Stream)` never closes the stream by itself.

2. **Why is a lazy sequence tied to a `StreamReader` unsafe?** A lazy iterator does nothing until someone
   enumerates it. The `using` block ends when the method returns, so the reader is already disposed
   when the caller finally enumerates, and `ReadLine` fails. Reading everything into a `List` inside
   the method fixes it.

3. **Why reset `MemoryStream.Position`?** A stream has one position shared by reads and writes. After
   writing, it points to the end, and reading starts from the current position, so there is
   nothing left to read. `Position = 0` moves it back to the start of the bytes just written.

4. **Why group before making pairs?** Sorted by time only, neighbours can belong to different sensors
   (S1, S2, S1, S2), and comparing their temperatures is meaningless. Grouping by sensor first
   (then sorting each group) makes sure only readings of the same sensor are compared.

5. **Why `Enum.IsDefined` after `Enum.TryParse`?** `TryParse` accepts any number and casts it, so `"99"`
   succeeds and gives `(StorageClass)99`, which is not a declared member. `IsDefined` rejects such
   values. `TryParse` also treats `"Cold, Frozen"` as a flags combination (0 | 1 = `Frozen`), so the
   parser rejects any class text containing a comma.

6. **Pure vs I/O.** Pure: `TryParseReading`, `IsOutsideRange`, `IsAbruptChange`, `Pairwise`,
   `FindAlerts`, `BuildArchive`, `ArchiveComparer`. I/O: `ReadReadings`, `WriteArchive`,
   `ReadArchive` (they touch streams) and `Program.cs` (files, console, clock).

7. **Why compare values, not references?** Deserialization always builds new objects, so references
   are never equal. Records compare by their fields, but list properties compare by reference, so
   the lists are compared item by item (`SequenceEqual`).

8. **Determinism and injected `CreatedAtUtc`.** A fixed alert order gives the same output for the same
   input, so tests can compare exact sequences. Passing `CreatedAtUtc` in (instead of
   `DateTime.Now`) lets a test use a fixed time, so the archive is reproducible.

## Debugging explanations

**4.2 `ReadLines` with a lazy iterator.** `return ReadAll(reader)` returns immediately; `ReadAll`
has not run yet. The `using` then disposes the reader. When the caller enumerates later, the
iterator uses a disposed reader and throws. Fix: materialize inside the `using` (`ToList()`),
which is what `ReadReadings` does.

**4.3 Deserialize right after Serialize.** After `Serialize` the position is at the end of the
stream, and `Deserialize` reads from the current position, so it sees no JSON and throws.
Fix: `stream.Position = 0;` before deserializing.

**4.4 Comparing unrelated sensors.** `OrderBy(Timestamp)` over all readings mixes sensors, so a pair
can be (S1, S2) and show a fake "abrupt change". Fix: `GroupBy(SensorId)` first, then
`OrderBy(Timestamp)` and `Zip`/`Skip(1)` inside each group.

## Notes

- `archive.json` is regenerated on each run (the `createdAtUtc` value changes).
- JSON: System.Text.Json, camelCase names, indented, enums as strings (`JsonStringEnumConverter`,
  numbers rejected on read). A JSON `null` archive is rejected.
