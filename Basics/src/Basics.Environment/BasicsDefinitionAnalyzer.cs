using Nbn.Shared;
using Nbn.Shared.Format;

namespace Nbn.Demos.Basics.Environment;

public sealed record BasicsDefinitionAnalysis(
    BasicsBrainGeometryValidation Geometry,
    BasicsDefinitionComplexitySummary Complexity,
    bool HasInputToOutputPath);

public static class BasicsDefinitionAnalyzer
{
    public static BasicsDefinitionAnalysis Analyze(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var header = NbnBinary.ReadNbnHeader(bytes);
        var geometry = new BasicsBrainGeometryValidation(
            IsValid: header.Regions[NbnConstants.InputRegionId].NeuronSpan == BasicsIoGeometry.InputWidth
                     && header.Regions[NbnConstants.OutputRegionId].NeuronSpan == BasicsIoGeometry.OutputWidth,
            ExpectedInputWidth: BasicsIoGeometry.InputWidth,
            ExpectedOutputWidth: BasicsIoGeometry.OutputWidth,
            ActualInputWidth: header.Regions[NbnConstants.InputRegionId].NeuronSpan,
            ActualOutputWidth: header.Regions[NbnConstants.OutputRegionId].NeuronSpan,
            FailureReason: header.Regions[NbnConstants.InputRegionId].NeuronSpan == BasicsIoGeometry.InputWidth
                           && header.Regions[NbnConstants.OutputRegionId].NeuronSpan == BasicsIoGeometry.OutputWidth
                ? string.Empty
                : $"expected_{BasicsIoGeometry.InputWidth}x{BasicsIoGeometry.OutputWidth}_got_{header.Regions[NbnConstants.InputRegionId].NeuronSpan}x{header.Regions[NbnConstants.OutputRegionId].NeuronSpan}");
        var complexity = BasicsArtifactStoreReader.ReadDefinitionComplexity(bytes);
        var hasInputToOutputPath = HasInputToOutputPath(bytes, header);
        return new BasicsDefinitionAnalysis(geometry, complexity, hasInputToOutputPath);
    }

    public static bool FitsSeedShapeBounds(
        BasicsDefinitionComplexitySummary complexity,
        BasicsSeedShapeConstraints bounds)
    {
        ArgumentNullException.ThrowIfNull(complexity);
        ArgumentNullException.ThrowIfNull(bounds);

        return FitsOptionalRange(complexity.ActiveInternalRegionCount, bounds.MinActiveInternalRegionCount, bounds.MaxActiveInternalRegionCount)
               && FitsOptionalRange(complexity.InternalNeuronCount, bounds.MinInternalNeuronCount, bounds.MaxInternalNeuronCount)
               && FitsOptionalRange(complexity.AxonCount, bounds.MinAxonCount, bounds.MaxAxonCount);
    }

    private static bool FitsOptionalRange(int value, int? min, int? max)
    {
        if (min.HasValue && value < min.Value)
        {
            return false;
        }

        if (max.HasValue && value > max.Value)
        {
            return false;
        }

        return true;
    }

    private static bool HasInputToOutputPath(byte[] bytes, NbnHeaderV2 header)
    {
        var adjacency = new Dictionary<(byte RegionId, int NeuronId), List<(byte RegionId, int NeuronId)>>();
        for (var regionId = 0; regionId < header.Regions.Length; regionId++)
        {
            var region = header.Regions[regionId];
            if (region.NeuronSpan <= 0 || region.Offset <= 0)
            {
                continue;
            }

            var section = NbnBinary.ReadNbnRegionSection(bytes, region.Offset);
            var axonOffset = 0;
            for (var neuronId = 0; neuronId < section.NeuronRecords.Length; neuronId++)
            {
                var neuron = section.NeuronRecords[neuronId];
                var outgoing = new List<(byte RegionId, int NeuronId)>((int)neuron.AxonCount);
                for (var i = 0; i < neuron.AxonCount; i++)
                {
                    var axon = section.AxonRecords[axonOffset + i];
                    outgoing.Add((axon.TargetRegionId, axon.TargetNeuronId));
                }

                adjacency[((byte)regionId, neuronId)] = outgoing;
                axonOffset += (int)neuron.AxonCount;
            }
        }

        var visited = new HashSet<(byte RegionId, int NeuronId)>();
        var frontier = new Queue<(byte RegionId, int NeuronId)>();
        for (var inputNeuronId = 0; inputNeuronId < BasicsIoGeometry.InputWidth; inputNeuronId++)
        {
            frontier.Enqueue(((byte)NbnConstants.InputRegionId, inputNeuronId));
        }

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current.RegionId == NbnConstants.OutputRegionId)
            {
                return true;
            }

            if (!adjacency.TryGetValue(current, out var outgoing))
            {
                continue;
            }

            foreach (var next in outgoing)
            {
                frontier.Enqueue(next);
            }
        }

        return false;
    }
}
