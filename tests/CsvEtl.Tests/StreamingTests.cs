using CsvEtl.Csv;
using CsvEtl.Pipeline;
using CsvEtl.Synthetic;
using Xunit.Abstractions;

namespace CsvEtl.Tests;

/// <summary>
/// Streaming memory checks. Honest limits of the measurement: GC.GetTotalMemory(forceFullCollection: true) reports live
/// managed bytes at the instant it is called (we sample it every 250k rows), so a short-lived spike between samples is not
/// seen, and native memory / process working set are not measured at all. The negative control below shows the same
/// probe does notice a pipeline that retains rows. These tests run serially so other tests do not pollute the heap numbers.
/// </summary>
[Collection("Serial")]
public class StreamingTests(ITestOutputHelper output)
{
    private const long Mib = 1024 * 1024;

    [Fact]
    public void ThreeMillionRowsStreamInBoundedMemory()
    {
        const int rows = 3_000_000;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        long peak = long.MinValue; // may stay negative: the live heap during the run can be below the post-GC baseline
        int samples = 0;

        using var input = new EnumerableTextReader(SyntheticCustomers.Chunks(rows, seed: 11, dirtyRate: 0.1));
        var result = EtlRunner.Run(input, T.SampleSchema(),
            new EtlOptions
            {
                MaxErrors = 100,
                MaxKeys = 10_000, // bounded dedupe table; with 3M distinct ids an unbounded one would itself be ~100+ MB
                ProgressEvery = 250_000,
                OnProgress = _ =>
                {
                    peak = Math.Max(peak, GC.GetTotalMemory(true) - baseline);
                    samples++;
                },
            },
            new ICleanSink[] { new CleanCsvSink(TextWriter.Null) },
            new RejectsCsvSink(TextWriter.Null));

        long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        long inputBytes = input.CharsProduced * 2; // as UTF-16; roughly the same size as the UTF-8 file for this ASCII-heavy data
        output.WriteLine($"rows read {result.RowsRead:N0}, clean {result.RowsClean:N0}, rejected {result.RowsRejected:N0}");
        output.WriteLine($"input ~{inputBytes / Mib:N0} MiB as UTF-16 ({input.CharsProduced:N0} chars), total allocated {allocated / Mib:N0} MiB");
        output.WriteLine($"baseline live heap {baseline / Mib:N1} MiB; max live heap seen over {samples} samples {(baseline + peak) / Mib:N1} MiB (delta {peak / Mib:N1} MiB)");

        Assert.Equal(rows, result.RowsRead);
        Assert.True(samples >= 10);
        Assert.True(inputBytes > 400 * Mib, "input should be hundreds of MiB for the comparison to mean anything");
        Assert.True(allocated > 20 * Math.Max(peak, Mib), "the run must allocate far more than it retains, otherwise this test proves nothing");
        Assert.True(peak < 48 * Mib, $"live heap peaked at {peak / Mib} MiB");
        Assert.True(peak * 20 < inputBytes, "peak live memory should be a small fraction of the input size");
        Assert.True(result.Report.Issues.Count <= 100);
        Assert.True(result.DedupeSaturated);
    }

    [Fact]
    public void NegativeControl_MaterialisingRowsIsVisibleToTheSameProbe()
    {
        GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        using var input = new EnumerableTextReader(SyntheticCustomers.Chunks(100_000, seed: 11, dirtyRate: 0.1));
        var all = CsvReader.Read(input).ToList(); // retains everything
        long live = GC.GetTotalMemory(true) - baseline;
        output.WriteLine($"retaining 100k records: live heap +{live / Mib:N1} MiB");
        Assert.True(live > 48 * Mib, $"probe saw only {live / Mib} MiB, so it could not have detected a leak");
        GC.KeepAlive(all);
    }

    [Fact]
    public void ReaderAloneStreamsHalfAMillionRowsInConstantMemory()
    {
        GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        long peak = long.MinValue, n = 0;
        using var input = new EnumerableTextReader(SyntheticCustomers.Chunks(500_000, seed: 5, dirtyRate: 0.1));
        foreach (var rec in CsvReader.Read(input))
        {
            if (++n % 100_000 == 0) peak = Math.Max(peak, GC.GetTotalMemory(true) - baseline);
            Assert.NotNull(rec.Fields);
        }

        output.WriteLine($"{n:N0} records, peak live delta {peak / Mib:N1} MiB");
        Assert.True(n >= 500_000);
        Assert.True(peak < 16 * Mib);
    }

    [Fact]
    public void ErrorReportStaysBoundedUnderATotallyBrokenHalfMillionRowFile()
    {
        IEnumerable<string> Chunks()
        {
            yield return "customer_id,full_name,balance,signup_date\n";
            for (int i = 0; i < 500_000; i++) yield return "bad,,x,y\n";
        }

        GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        var r = EtlRunner.Run(new EnumerableTextReader(Chunks()), T.SampleSchema(), new EtlOptions { MaxErrors = 50 },
            rejects: new RejectsCsvSink(TextWriter.Null));
        long live = GC.GetTotalMemory(true) - baseline;
        Assert.Equal(500_000, r.RowsRejected);
        Assert.True(r.Report.TotalIssues >= 2_000_000);
        Assert.Equal(50, r.Report.Issues.Count);
        Assert.True(live < 16 * Mib);
    }
}
