using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Editor.Core.PerfTests;

public sealed class PerfLog : IDisposable
{
    public List<PerfRow> Rows { get; } = [];
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "editor-perf-" + Guid.NewGuid().ToString("n"));

    public PerfLog() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        var path = Environment.GetEnvironmentVariable("PERF_RESULTS_PATH");
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(Directory.GetCurrentDirectory(), "perf-results.json");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var payload = new
        {
            recordedUtc = DateTime.UtcNow,
            coldStart = "Gated at 400 ms by the Windows cold-start job. Linux timings are not the acceptance numbers.",
            rows = Rows
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed record PerfRow(
    string Name,
    double ElapsedMs,
    double LimitMs,
    long ManagedDeltaBytes,
    long WorkingSetDeltaBytes,
    long FileBytes,
    string Detail);

public sealed class PerfGates : IClassFixture<PerfLog>
{
    private readonly PerfLog _log;

    public PerfGates(PerfLog log) => _log = log;

    [Fact]
    public void Open_100mb_reaches_first_page_within_300ms_and_stays_under_memory_budget()
    {
        var path = Path.Combine(_log.Root, "hundred.txt");
        const long size = 100L * 1024 * 1024;
        WritePattern(path, size);
        Warmup();

        Collect();
        var managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var workingSetBefore = Process.GetCurrentProcess().WorkingSet64;

        var clock = Stopwatch.StartNew();
        using var document = TextDocument.Open(path);
        var page = document.LayoutFirstPage(40, 120);
        clock.Stop();

        Collect();
        var managedAfter = GC.GetTotalMemory(forceFullCollection: true);
        var workingSetAfter = Process.GetCurrentProcess().WorkingSet64;
        var managedDelta = Math.Max(0, managedAfter - managedBefore);
        var workingSetDelta = Math.Max(0, workingSetAfter - workingSetBefore);
        var budget = (long)(size * 2.5);

        var row = new PerfRow(
            "open-100mb",
            clock.Elapsed.TotalMilliseconds,
            300,
            managedDelta,
            workingSetDelta,
            size,
            $"lines={page.Lines.Count}; scanned={document.ScannedBytes}; indexed={document.IsLineIndexComplete}");
        _log.Rows.Add(row);

        Assert.True(page.Lines.Count > 0);
        Assert.False(document.IsLineIndexComplete);
        Assert.True(clock.Elapsed.TotalMilliseconds <= 300, $"open+first page took {clock.Elapsed.TotalMilliseconds:F1} ms (limit 300)");
        Assert.True(managedDelta <= budget, $"managed delta {managedDelta} exceeds 2.5x file ({budget})");
        Assert.True(workingSetDelta <= budget + (64L * 1024 * 1024), $"working set delta {workingSetDelta} exceeds 2.5x file plus 64 MB");
    }

    [Fact]
    public void Open_1gb_returns_without_scanning_the_file()
    {
        const long size = 1024L * 1024 * 1024;
        var path = Corpus(size, "gig.txt");
        Warmup();

        var clock = Stopwatch.StartNew();
        using var document = TextDocument.Open(path);
        var page = document.LayoutFirstPage(40, 80);
        clock.Stop();

        var row = new PerfRow("open-1gb", clock.Elapsed.TotalMilliseconds, 1500, 0, 0, size, $"scanned={document.ScannedBytes}");
        _log.Rows.Add(row);

        Assert.True(page.Lines.Count > 0);
        Assert.False(document.IsLineIndexComplete);
        Assert.True(document.ScannedBytes < 1_000_000, $"scanned {document.ScannedBytes} bytes");
        Assert.True(clock.Elapsed.TotalMilliseconds <= 1500, $"1 GB open took {clock.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void Insert_at_middle_of_1gb_stays_within_16ms()
    {
        const long size = 1024L * 1024 * 1024;
        var path = Corpus(size, "gig.txt");
        Warmup();

        using var document = TextDocument.Open(path);
        _ = document.LayoutFirstPage(10, 40);
        var midpoint = document.ByteLength / 2;
        document.InsertAtByteOffset(midpoint, "warmup");
        Assert.True(document.Undo());

        var samples = new double[11];
        for (var i = 0; i < samples.Length; i++)
        {
            var clock = Stopwatch.StartNew();
            document.InsertAtByteOffset(document.ByteLength / 2, "y");
            clock.Stop();
            samples[i] = clock.Elapsed.TotalMilliseconds;
            Assert.True(document.Undo());
        }

        Array.Sort(samples);
        var median = samples[samples.Length / 2];
        var max = samples[^1];
        _log.Rows.Add(new PerfRow("insert-middle-1gb", median, 16, 0, 0, size, $"max={max:F3}"));
        Assert.True(median <= 16, $"median insert {median:F3} ms, max {max:F3} ms (limit 16)");
    }

    [Fact]
    public void Open_1mb_reaches_first_page_within_100ms()
    {
        const long size = 1024L * 1024;
        var path = Path.Combine(_log.Root, "one.txt");
        WritePattern(path, size);
        Warmup();
        var clock = Stopwatch.StartNew();
        using var document = TextDocument.Open(path);
        var page = document.LayoutFirstPage(40, 80);
        clock.Stop();
        _log.Rows.Add(new PerfRow("open-1mb", clock.Elapsed.TotalMilliseconds, 100, 0, 0, size, $"scanned={document.ScannedBytes}"));
        Assert.True(page.Lines.Count > 0);
        Assert.False(document.IsLineIndexComplete);
        Assert.True(document.ScannedBytes < 256 * 1024);
        Assert.True(clock.Elapsed.TotalMilliseconds <= 100, $"1 MB open took {clock.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void Single_10mb_line_lays_out_the_first_page_without_reading_the_line()
    {
        const long size = 10L * 1024 * 1024;
        var path = Path.Combine(_log.Root, "oneline.txt");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var block = new byte[1024 * 1024];
            Array.Fill(block, (byte)'a');
            for (var i = 0; i < 10; i++)
                stream.Write(block);
        }

        Warmup();
        var clock = Stopwatch.StartNew();
        using var document = TextDocument.Open(path);
        var page = document.LayoutFirstPage(20, 80);
        clock.Stop();
        _log.Rows.Add(new PerfRow("open-10mb-line", clock.Elapsed.TotalMilliseconds, 100, 0, 0, size, $"scanned={page.BytesScanned}; chars={page.Lines[0].Text.Length}"));
        Assert.True(page.Lines.Count > 0);
        Assert.True(page.Lines[0].Text.Length <= 80);
        Assert.True(page.BytesScanned < 256 * 1024);
        Assert.False(document.IsLineIndexComplete);
        Assert.True(clock.Elapsed.TotalMilliseconds <= 100, $"10 MB line took {clock.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void Find_on_100mb_reaches_a_marker_at_the_end()
    {
        const long size = 100L * 1024 * 1024;
        var path = Path.Combine(_log.Root, "find100.txt");
        WritePattern(path, size - 6);
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
            stream.Write(Encoding.ASCII.GetBytes("NEEDLE"));

        Warmup();
        using var document = TextDocument.Open(path);
        var clock = Stopwatch.StartNew();
        var matches = document.Find("NEEDLE", FindOptions.Plain(matchCase: true));
        clock.Stop();
        _log.Rows.Add(new PerfRow("find-100mb", clock.Elapsed.TotalMilliseconds, 2000, 0, 0, size, $"matches={matches.Count}"));
        Assert.Single(matches);
        Assert.True(clock.Elapsed.TotalMilliseconds <= 2000, $"find took {clock.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact]
    public void End_goto_and_scroll_jump_on_1gb_stay_within_100ms()
    {
        const long size = 1024L * 1024 * 1024;
        var path = Corpus(size, "gig.txt");
        Warmup();
        using var document = TextDocument.Open(path);
        _ = document.LayoutFirstPage(10, 40);
        var scanned = document.ScannedBytes;

        var clock = Stopwatch.StartNew();
        var end = document.JumpToEnd();
        var line = document.JumpToLine(5_000_000);
        var scroll = document.JumpToFraction(0.95);
        clock.Stop();

        _log.Rows.Add(new PerfRow(
            "jump-1gb",
            clock.Elapsed.TotalMilliseconds,
            100,
            0,
            0,
            size,
            $"endTouched={end.BytesTouched}; lineTouched={line.BytesTouched}; scrollTouched={scroll.BytesTouched}; scanned={document.ScannedBytes}"));
        Assert.Equal(scanned, document.ScannedBytes);
        Assert.False(document.IsLineIndexComplete);
        Assert.True(end.BytesTouched < 1_000_000);
        Assert.True(line.BytesTouched < 1_000_000);
        Assert.True(scroll.BytesTouched < 1_000_000);
        Assert.True(clock.Elapsed.TotalMilliseconds <= 100, $"jumps took {clock.Elapsed.TotalMilliseconds:F1} ms");
    }

    private string Corpus(long size, string name)
    {
        var path = Path.Combine(_log.Root, name);
        lock (_log)
        {
            if (!File.Exists(path) || new FileInfo(path).Length != size)
                WritePattern(path, size);
        }
        return path;
    }

    private static void Warmup()
    {
        using var document = TextDocument.FromText("warmup\nline\n");
        _ = document.LayoutFirstPage(5, 20);
        document.InsertAtByteOffset(1, "z");
        document.Undo();
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void WritePattern(string path, long size)
    {
        var line = Encoding.UTF8.GetBytes(new string('a', 79) + "\n");
        var chunk = new byte[line.Length * 1024];
        for (var offset = 0; offset < chunk.Length; offset += line.Length)
            line.CopyTo(chunk.AsSpan(offset));

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        long written = 0;
        while (written + chunk.Length <= size)
        {
            stream.Write(chunk);
            written += chunk.Length;
        }
        if (written < size)
            stream.Write(chunk, 0, (int)(size - written));
        stream.Flush(flushToDisk: true);
    }

}
