using System.Security.Cryptography;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Engine;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

internal sealed class ExampleImageBaseline : IDisposable
{
    internal const string RegionExample = "threshold-region.vflow.json";
    internal const string LineExample = "xld-line.vflow.json";

    private readonly FlowEngine _engine = new();
    private readonly SequenceNode _root;

    public string ExampleName { get; }
    public string FlowSha256 { get; }
    public string ImageSha256 { get; }
    public HObject InputImage { get; }

    public ExampleImageBaseline(string exampleName)
    {
        HalconRuntimeAvailabilityTests.RequireAvailable();
        if (exampleName != RegionExample && exampleName != LineExample)
        {
            throw new ArgumentException("Unknown image baseline: " + exampleName, nameof(exampleName));
        }
        ExampleName = exampleName;
        string flowPath = RepoPaths.Find(Path.Combine("examples", exampleName));
        string imagePath = RepoPaths.Find(Path.Combine("src", "Image", "razors1.png"));
        byte[] flowBytes = File.ReadAllBytes(flowPath);
        FlowSha256 = Convert.ToHexString(SHA256.HashData(flowBytes));
        ImageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imagePath)));
        using var reader = new StreamReader(new MemoryStream(flowBytes));
        _root = FlowSerializer.Load(reader.ReadToEnd());
        HOperatorSet.ReadImage(out HObject image, imagePath);
        InputImage = image;
    }

    public FlowRunResult Run(FlowContext context)
    {
        context.SetVariable(Variable.Object("Input", "Image", new HalconImage(InputImage), 1));
        return _engine.Run(_root, context);
    }

    public void AssertResult(FlowRunResult result, FlowContext context)
    {
        Assert.True(result.IsSuccess, result.Message);
        if (ExampleName == RegionExample)
        {
            Assert.Equal(1, context.GetVariable("流程输出1", "RegionCount").Value);
            double firstArea = Assert.IsType<double>(context.GetVariable("流程输出1", "FirstArea").Value);
            Assert.True(firstArea > 9027 && firstArea < 9977,
                $"FirstArea outside sample regression range (9027, 9977): {firstArea}");
            Assert.Equal(true, context.GetVariable("流程输出1", "Ok").Value);
        }
        else
        {
            int lineCount = Assert.IsType<int>(context.GetVariable("流程输出1", "LineCount").Value);
            Assert.InRange(lineCount, 180, 198);
            double row1 = Assert.IsType<double>(context.GetVariable("流程输出1", "Row1").Value);
            Assert.True(Math.Abs(row1 - 1.3229) < 0.05,
                $"Row1 outside sample regression range (1.3229 +/- 0.05): {row1}");
        }
    }

    public void Dispose()
    {
        InputImage.Dispose();
    }
}
