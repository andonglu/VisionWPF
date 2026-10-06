# Copilot instructions for VisionFlow

## Build and validation

Work from the repository root (`VisionWPF`). The solution file is `VisionFlow.slnx`, not a classic `.sln`.

Prerequisites: Windows, a .NET SDK that can build `net9.0-windows`, and HALCON's .NET assembly at `lib\halcon\halcondotnet.dll`. All projects reference `..\lib\halcon\halcondotnet.dll`; DLLs are ignored by git, so a fresh clone without that file fails with `MSB3245` / `CS0246` for `HalconDotNet`.

```powershell
dotnet restore .\VisionFlow.slnx
dotnet build .\VisionFlow.slnx --configuration Debug --no-restore
dotnet build .\VisionFlow.WpfApp\VisionFlow.WpfApp.csproj --configuration Debug --no-restore
dotnet run --project .\VisionFlow.WpfApp\VisionFlow.WpfApp.csproj --configuration Debug
```

There are currently no automated test projects or lint configs. Use the smallest affected project build as the primary validation check. Manual regression flows live in `examples\*.vflow.json` and expect `Input.Image`; the README example image is `src\Image\razors1.png`.

## Architecture

VisionFlow is a visual-flow editor/runtime component, not the full equipment application. Keep recipe management, camera acquisition, PLC/IO/motion control, MES/reporting, production cadence, and upper-level HMI concerns outside this repo unless adding an explicit integration boundary.

`VisionFlow.Base` is the core runtime: `FlowNode` is the synchronous execution base, with `SequenceNode`, `ToolNode`, `IfElseNode`, `ForLoopNode`, and `FlowOutputNode` forming the flow tree. `FlowEngine` executes the root, stops on the first failed `NodeResult`, and records logs, trace IDs, node reports, and variables in `FlowContext`. `VisionFlowRuntime` wraps the engine for sync/async callers. `FlowSerializer` persists `.vflow.json` structure and tool parameters, while `FlowValidator` and `RefCandidateService` validate/offer upstream references without running tools.

`VisionFlow.Tools` contains built-in HALCON tools. Tools derive from `ToolBase`, read upstream values through `[InputRef]` string properties and `Input<T>()`, and write outputs with `SetOutput(ctx, Variable.*(ModuleName, ...))`. `[ToolOutput]` metadata must match the actual output names so editors, validation, and reference dropdowns see the same contract. Tools whose output names come from user configuration implement `IDynamicOutputTool`; read outputs through `ToolMetadata.GetOutputs(ToolBase)` (static plus dynamic) rather than `GetOutputs(Type)` wherever a tool instance is available. Tools with expressions in their parameters implement `IExpressionTool` (expressions use `VisionFlow.Base\Expressions`, references in braces like `{测量1.Row}`), and tools with line-based configuration can report format problems through `IToolConfigurationCheck`; `FlowValidator` checks all three. HALCON values are wrapped with types from `VisionFlow.Base\Variables\HalconTypes.cs`.

`VisionFlow.EditorCore` is UI-independent editor state and registration. `FlowEditModel` owns add/move/remove/nesting operations; `ToolboxRegistry.RegisterDefaults()` creates built-in toolbox nodes and unique module names; `VisionFlowPluginLoader` scans `AppContext.BaseDirectory\plugins` for plugin DLLs.

`VisionFlow.WpfApp` is the only editor application; the older WinForms editor (`VisionFlow.App`) and WinForms tool edit forms (`VisionFlow.ToolEditors`) have been removed. The WPF app reuses the core model, runtime, serializer, validator, tools, and the `VisionFlow.Controls` HALCON image/ROI controls (embedded via `WindowsFormsHost`, so `UseWindowsForms` stays enabled in the WPF projects). WPF-specific edit windows live in `VisionFlow.WpfToolEditors`.

`VisionFlowPluginLoader` in `VisionFlow.EditorCore` scans `AppContext.BaseDirectory\plugins` for third-party plugin DLLs; no first-party plugin ships with this repo.

## Project-specific conventions

User-facing names, logs, validation messages, toolbox categories, and flow output defaults are primarily Chinese; keep new UI/runtime strings consistent with the existing Chinese labels.

Variable references use `Module.Variable`, optional array/member access (`Module.Items[0]`, `Module.BestMatch.Row`), and loop pseudo-variables (`Loop.Index`, `Loop.Count`, `Loop.Current`, `Loop.Current.Member`). `FlowContext` stores variables globally by module/name after a node runs; `RefCandidateService` limits editor choices to upstream outputs and the innermost loop context.

Tool parameter persistence is intentionally narrow. `FlowSerializer.SerializableProperties()` only saves public get/set tool properties of type `string`, `int`, `double`, `bool`, `byte[]`, or enum, excluding `ToolBase.ModuleName`. If a new parameter type must persist, update serialization/deserialization and the relevant editor UI together.

Any tool loaded from JSON or registered as a plugin must provide `.ctor(string moduleName)`. Built-in tools are added in `ToolboxRegistry.RegisterDefaults()`. Plugin tools use `[ToolboxTool(..., Id = "...", DefaultModuleName = "...")]`; toolbox registration rejects duplicate IDs and duplicate category/display-name pairs, so a plugin tool that duplicates a built-in display name is intentionally hidden.

When adding a built-in visual tool, wire every surface that applies: tool class and `[ToolOutput]` / `[InputRef]` metadata in `VisionFlow.Tools`, toolbox entry in `ToolboxRegistry`, WPF editor routing in `MainWindow.OpenToolEditor()` or `IsVisualPreviewTool()`, and README/examples for user-visible flow capability changes.

WPF tool editor layout conventions are documented in `VisionFlow.WpfToolEditors\WPF_EDITOR_DESIGN.md`: use two-column editor layouts, card styling, and semantic resources from `VisionFlow.WpfApp\Themes\Tokens.xaml` such as `LayerFillColorDefaultBrush`, `TextFillColorPrimaryBrush`, and `AccentFillColorDefaultBrush`.

Project files disable implicit usings and nullable annotations and use block-scoped namespaces. Follow that style in touched files unless changing project-wide settings deliberately.
