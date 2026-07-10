using Nbn.Demos.Basics.Environment;
using Nbn.Demos.Basics.Ui.Services;
using Nbn.Demos.Basics.Ui.ViewModels;
using Nbn.Proto;
using Nbn.Shared;
using Nbn.Shared.Format;
using Nbn.Shared.Packing;

namespace Nbn.Demos.Basics.Ui.Tests;

public sealed class MainWindowViewModelImportTests
{
    [Fact]
    public async Task AddInitialBrainsCommand_SurfacesSemanticNbnValidationFailure()
    {
        var invalidBytes = BuildDefinitionTargetingInputRegion();
        var importService = new StubBrainImportService(new BasicsImportedBrainFile(
            "invalid-input-target.nbn",
            LocalPath: null,
            invalidBytes,
            SnapshotLocalPath: null,
            SnapshotBytes: null));
        var viewModel = new MainWindowViewModel(
            new UiDispatcher(),
            new StubArtifactExportService(),
            importService,
            new StubWorkerProcessService());

        viewModel.AddInitialBrainsCommand.Execute(null);

        await WaitForAsync(() => viewModel.InitialBrainSeedStatus.Contains("import rejected", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(viewModel.InitialBrainSeeds);
        Assert.Contains("canonical NBN validation", viewModel.InitialBrainSeedStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("may not target the input region", viewModel.InitialBrainSeedStatus, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildDefinitionTargetingInputRegion()
    {
        var build = BasicsTemplateArtifactBuilder.Build(BasicsSeedTemplateContract.CreateDefault());
        var header = NbnBinary.ReadNbnHeader(build.Bytes);
        var sections = header.Regions
            .Where(static entry => entry.NeuronSpan > 0)
            .Select(entry => NbnBinary.ReadNbnRegionSection(build.Bytes, entry.Offset))
            .ToArray();
        var inputIndex = Array.FindIndex(sections, static section => section.RegionId == NbnConstants.InputRegionId);
        var input = sections[inputIndex];
        var invalidAxons = input.AxonRecords.ToArray();
        invalidAxons[0] = new AxonRecord(
            invalidAxons[0].StrengthCode,
            targetNeuronId: 0,
            targetRegionId: (byte)NbnConstants.InputRegionId);
        sections[inputIndex] = new NbnRegionSection(
            input.RegionId,
            input.NeuronSpan,
            input.TotalAxons,
            input.Stride,
            input.CheckpointCount,
            input.Checkpoints,
            input.NeuronRecords,
            invalidAxons);
        return NbnBinary.WriteNbn(header, sections);
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(predicate(), "Timed out waiting for import validation result.");
    }

    private sealed class StubBrainImportService : IBasicsBrainImportService
    {
        private readonly IReadOnlyList<BasicsImportedBrainFile> _files;

        public StubBrainImportService(params BasicsImportedBrainFile[] files)
        {
            _files = files;
        }

        public Task<IReadOnlyList<BasicsImportedBrainFile>> ImportAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_files);
    }

    private sealed class StubArtifactExportService : IBasicsArtifactExportService
    {
        public Task<string?> ExportAsync(
            ArtifactRef artifact,
            string title,
            string suggestedFileName,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class StubWorkerProcessService : IBasicsLocalWorkerProcessService
    {
        public int LaunchedWorkerCount => 0;

        public Task<BasicsLocalWorkerLaunchResult> StartWorkersAsync(
            BasicsLocalWorkerLaunchRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<BasicsLocalWorkerStopResult> StopLaunchedWorkersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new BasicsLocalWorkerStopResult(0, "none", "none"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
