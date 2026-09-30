using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Tools;
using Xunit.Abstractions;

namespace VisionFlow.Tests;

[Trait("Category", "Soak")]
public class ContinuousRunTests
{
    private readonly ITestOutputHelper _output;

    public ContinuousRunTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(ExampleImageBaseline.RegionExample)]
    [InlineData(ExampleImageBaseline.LineExample)]
    public void RepeatedFlow_MatchesBaselineAndConfiguredBudgets(string exampleName)
    {
        SoakSettings settings = SoakSettings.Read(Environment.GetEnvironmentVariable);
        using var baseline = new ExampleImageBaseline(exampleName);
        using var process = Process.GetCurrentProcess();
        string reportDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("VISIONFLOW_SOAK_REPORT_DIRECTORY")
            ?? Path.Combine(Path.GetDirectoryName(RepoPaths.Find("VisionFlow.slnx"))!, "TestResults", "soak"));
        Directory.CreateDirectory(reportDirectory);
        string reportPath = Path.Combine(reportDirectory,
            $"{exampleName}-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.csv");
        using var report = new StreamWriter(new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        _output.WriteLine("CSV=" + reportPath);
        HOperatorSet.GetSystem("version", out HTuple version);
        using (version)
        {
            WriteInfo($"UTC={DateTimeOffset.UtcNow:O}; HALCON={version.S}; .NET={Environment.Version}; "
                + $"OS={RuntimeInformation.OSDescription}; Arch={RuntimeInformation.ProcessArchitecture}");
        }
        WriteInfo($"Flow={exampleName}; FlowSHA256={baseline.FlowSha256}; ImageSHA256={baseline.ImageSha256}");
        WriteInfo($"Iterations={settings.Iterations}; Warmup=10; SampleEvery={settings.SampleEvery}");
        WriteInfo($"Limits: PrivateMiB={settings.MaxMemoryGrowthMb?.ToString(CultureInfo.InvariantCulture) ?? "UNSET"}; "
            + $"Handles={settings.MaxHandleGrowth?.ToString(CultureInfo.InvariantCulture) ?? "UNSET"}; "
            + $"P95ms={settings.MaxP95Ms?.ToString(CultureInfo.InvariantCulture) ?? "UNSET"}");
        WriteInfo(settings.HasAllBudgets
            ? "Mode=budgeted sample regression (not field acceptance)."
            : "Mode=functional sample regression; incomplete budgets, NOT performance acceptance.");

        var durations = new double[settings.Iterations];
        for (int i = 0; i < 10; i++)
        {
            RunOnce(baseline);
        }
        CollectGarbage();
        process.Refresh();
        long initialPrivate = process.PrivateMemorySize64;
        int initialHandles = process.HandleCount;
        long peakPrivate = initialPrivate;
        int peakHandles = initialHandles;
        var elapsed = Stopwatch.StartNew();
        report.WriteLine("phase,iteration,elapsed_ms,private_bytes,working_set_bytes,managed_bytes,handles,last_run_ms");
        Sample("baseline", 0, 0);

        for (int i = 0; i < settings.Iterations; i++)
        {
            try
            {
                durations[i] = RunOnce(baseline);
            }
            catch (Xunit.Sdk.XunitException)
            {
                WriteInfo($"Baseline assertion failed at iteration {i + 1}.");
                throw;
            }
            if ((i + 1) % settings.SampleEvery == 0 || i + 1 == settings.Iterations)
            {
                Sample("running", i + 1, durations[i]);
            }
        }
        CollectGarbage();
        Sample("post_gc", settings.Iterations, durations[^1]);
        elapsed.Stop();

        double retainedMiB = (process.PrivateMemorySize64 - initialPrivate) / (1024d * 1024d);
        double peakGrowthMiB = (peakPrivate - initialPrivate) / (1024d * 1024d);
        int handleGrowth = peakHandles - initialHandles;
        Array.Sort(durations);
        double p95Ms = durations[(int)Math.Ceiling(durations.Length * 0.95) - 1];
        WriteInfo(FormattableString.Invariant(
            $"Summary: Completed={settings.Iterations}; ElapsedMs={elapsed.Elapsed.TotalMilliseconds:F3}; PeakPrivateGrowthMiB={peakGrowthMiB:F3}; RetainedPrivateGrowthMiB={retainedMiB:F3}; PeakHandleGrowth={handleGrowth}; P95ms={p95Ms:F3}; MaxMs={durations[^1]:F3}"));
        settings.AssertBudgets(peakGrowthMiB, handleGrowth, p95Ms);
        WriteInfo("Outcome=PASS; only configured budgets were evaluated.");

        void Sample(string phase, int iteration, double lastMs)
        {
            process.Refresh();
            peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
            peakHandles = Math.Max(peakHandles, process.HandleCount);
            report.WriteLine(FormattableString.Invariant(
                $"{phase},{iteration},{elapsed.Elapsed.TotalMilliseconds:F3},{process.PrivateMemorySize64},{process.WorkingSet64},{GC.GetTotalMemory(false)},{process.HandleCount},{lastMs:F3}"));
            report.Flush();
        }

        void WriteInfo(string message)
        {
            _output.WriteLine(message);
            report.WriteLine("# " + message);
            report.Flush();
        }
    }

    private static double RunOnce(ExampleImageBaseline baseline)
    {
        var elapsed = Stopwatch.StartNew();
        using (var context = new FlowContext())
        {
            baseline.AssertResult(baseline.Run(context), context);
        }
        Assert.True(baseline.InputImage.IsInitialized(), "Borrowed input was disposed by a run.");
        return elapsed.Elapsed.TotalMilliseconds;
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
