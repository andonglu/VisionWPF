using Xunit.Sdk;

namespace VisionFlow.Tests;

public class SoakSettingsTests
{
    [Fact]
    public void Defaults_AreExplicitlyFunctionalSmokeWithoutBudgets()
    {
        SoakSettings settings = SoakSettings.Read(_ => null);
        Assert.Equal(100, settings.Iterations);
        Assert.Equal(10, settings.SampleEvery);
        Assert.False(settings.HasAllBudgets);
        Assert.Null(settings.MaxMemoryGrowthMb);
        Assert.Null(settings.MaxHandleGrowth);
        Assert.Null(settings.MaxP95Ms);
    }

    [Theory]
    [InlineData("VISIONFLOW_SOAK_ITERATIONS", "0")]
    [InlineData("VISIONFLOW_SOAK_ITERATIONS", "-1")]
    [InlineData("VISIONFLOW_SOAK_ITERATIONS", "2147483648")]
    [InlineData("VISIONFLOW_SOAK_ITERATIONS", "")]
    [InlineData("VISIONFLOW_SOAK_SAMPLE_EVERY", "0")]
    [InlineData("VISIONFLOW_SOAK_MAX_MEMORY_MB", "-1")]
    [InlineData("VISIONFLOW_SOAK_MAX_MEMORY_MB", "NaN")]
    [InlineData("VISIONFLOW_SOAK_MAX_MEMORY_MB", "Infinity")]
    [InlineData("VISIONFLOW_SOAK_MAX_MEMORY_MB", "1,5")]
    [InlineData("VISIONFLOW_SOAK_MAX_HANDLES", "1.5")]
    [InlineData("VISIONFLOW_SOAK_MAX_HANDLES", "-1")]
    [InlineData("VISIONFLOW_SOAK_MAX_P95_MS", "0")]
    [InlineData("VISIONFLOW_SOAK_REQUIRE_BUDGETS", "yes")]
    public void InvalidConfiguration_FailsInsteadOfUsingDefaults(string key, string value)
    {
        Assert.Throws<FormatException>(() => SoakSettings.Read(name => name == key ? value : null));
    }

    [Fact]
    public void AcceptanceMode_RequiresEveryBudget()
    {
        Dictionary<string, string> values = BudgetValues();
        foreach (string key in new[] { "VISIONFLOW_SOAK_MAX_MEMORY_MB", "VISIONFLOW_SOAK_MAX_HANDLES",
            "VISIONFLOW_SOAK_MAX_P95_MS" })
        {
            Assert.Throws<InvalidOperationException>(() =>
                SoakSettings.Read(name => name == key ? null : values.GetValueOrDefault(name)));
        }
    }

    [Fact]
    public void ExplicitLimits_AcceptBoundaryValues()
    {
        Dictionary<string, string> values = BudgetValues();
        values["VISIONFLOW_SOAK_ITERATIONS"] = "1000";
        values["VISIONFLOW_SOAK_SAMPLE_EVERY"] = "25";
        SoakSettings settings = SoakSettings.Read(name => values.GetValueOrDefault(name));
        Assert.Equal(1000, settings.Iterations);
        Assert.Equal(25, settings.SampleEvery);
        Assert.True(settings.HasAllBudgets);
        settings.AssertBudgets(16.5, 4, 20);
    }

    [Theory]
    [InlineData(16.6, 4, 20)]
    [InlineData(16.5, 5, 20)]
    [InlineData(16.5, 4, 20.1)]
    public void ExceedingAnyLimit_Fails(double memoryMb, int handles, double p95Ms)
    {
        Dictionary<string, string> values = BudgetValues();
        SoakSettings settings = SoakSettings.Read(name => values.GetValueOrDefault(name));
        Assert.ThrowsAny<XunitException>(() => settings.AssertBudgets(memoryMb, handles, p95Ms));
    }

    private static Dictionary<string, string> BudgetValues()
    {
        return new Dictionary<string, string>
        {
            ["VISIONFLOW_SOAK_REQUIRE_BUDGETS"] = "true",
            ["VISIONFLOW_SOAK_MAX_MEMORY_MB"] = "16.5",
            ["VISIONFLOW_SOAK_MAX_HANDLES"] = "4",
            ["VISIONFLOW_SOAK_MAX_P95_MS"] = "20"
        };
    }
}
