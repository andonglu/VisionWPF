using System.Globalization;

namespace VisionFlow.Tests;

internal sealed class SoakSettings
{
    public int Iterations { get; private init; }
    public int SampleEvery { get; private init; }
    public double? MaxMemoryGrowthMb { get; private init; }
    public int? MaxHandleGrowth { get; private init; }
    public double? MaxP95Ms { get; private init; }
    public bool HasAllBudgets => MaxMemoryGrowthMb.HasValue && MaxHandleGrowth.HasValue && MaxP95Ms.HasValue;

    public static SoakSettings Read(Func<string, string?> getVariable)
    {
        var settings = new SoakSettings
        {
            Iterations = ReadInteger(getVariable, "VISIONFLOW_SOAK_ITERATIONS", 1) ?? 100,
            SampleEvery = ReadInteger(getVariable, "VISIONFLOW_SOAK_SAMPLE_EVERY", 1) ?? 10,
            MaxMemoryGrowthMb = ReadNumber(getVariable, "VISIONFLOW_SOAK_MAX_MEMORY_MB"),
            MaxHandleGrowth = ReadInteger(getVariable, "VISIONFLOW_SOAK_MAX_HANDLES", 0),
            MaxP95Ms = ReadNumber(getVariable, "VISIONFLOW_SOAK_MAX_P95_MS", positive: true)
        };
        string? requireText = getVariable("VISIONFLOW_SOAK_REQUIRE_BUDGETS");
        if (requireText != null)
        {
            if (!bool.TryParse(requireText, out bool require))
            {
                throw new FormatException("VISIONFLOW_SOAK_REQUIRE_BUDGETS must be true or false.");
            }
            if (require && !settings.HasAllBudgets)
            {
                throw new InvalidOperationException("Budgeted acceptance requires memory, handle and P95 limits.");
            }
        }
        return settings;
    }

    public void AssertBudgets(double memoryGrowthMb, int handleGrowth, double p95Ms)
    {
        if (MaxMemoryGrowthMb is double memoryLimit)
        {
            Assert.True(memoryGrowthMb <= memoryLimit,
                $"Sampled private-memory growth {memoryGrowthMb:F3} MiB exceeds {memoryLimit:F3} MiB.");
        }
        if (MaxHandleGrowth is int handleLimit)
        {
            Assert.True(handleGrowth <= handleLimit,
                $"Sampled handle growth {handleGrowth} exceeds {handleLimit}.");
        }
        if (MaxP95Ms is double timeLimit)
        {
            Assert.True(p95Ms <= timeLimit, $"P95 {p95Ms:F3} ms exceeds {timeLimit:F3} ms.");
        }
    }

    private static int? ReadInteger(Func<string, string?> getVariable, string name, int minimum)
    {
        string? text = getVariable(name);
        if (text == null) return null;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < minimum)
        {
            throw new FormatException($"{name} must be an integer >= {minimum}.");
        }
        return value;
    }

    private static double? ReadNumber(Func<string, string?> getVariable, string name, bool positive = false)
    {
        string? text = getVariable(name);
        if (text == null) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value) || (positive ? value <= 0 : value < 0))
        {
            throw new FormatException($"{name} must be a finite number {(positive ? "> 0" : ">= 0")}.");
        }
        return value;
    }
}
