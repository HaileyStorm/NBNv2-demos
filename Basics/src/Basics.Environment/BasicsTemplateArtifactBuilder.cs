using Nbn.Shared;
using Nbn.Shared.Format;
using Nbn.Shared.Packing;
using Nbn.Shared.Quantization;
using Nbn.Shared.Validation;

namespace Nbn.Demos.Basics.Environment;

public static class BasicsTemplateArtifactBuilder
{
    private const uint DefaultStride = 1024u;
    private const byte IdentityActivation = 1;
    private const byte SumAccumulation = 0;
    private const byte ZeroReset = 0;
    private const byte MaxStrengthCode = 31;
    private static readonly byte[] PreferredActivationFunctionIds = { 1, 5, 6, 7, 8, 9, 11, 18, 28 };
    private static readonly byte[] PreferredResetFunctionIds = { 0, 1, 3, 17, 30, 43, 44, 45, 47, 48, 49, 58 };
    private static readonly byte[] PreferredAccumulationFunctionIds = { 0, 1, 2 };

    public static BasicsTemplateBuildResult Build(BasicsSeedTemplateContract template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var validation = template.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join("; ", validation.Errors));
        }

        var shape = ResolveShape(template.InitialSeedShapeConstraints);
        var internalRegionNeuronCounts = DistributeInternalNeurons(
            shape.ActiveInternalRegionCount,
            shape.InternalNeuronCount);
        var minimumViableAxons = ResolveMinimumViableAxonCount(internalRegionNeuronCounts);
        if (template.InitialSeedShapeConstraints.MaxAxonCount is int maxAxons && maxAxons < minimumViableAxons)
        {
            throw new InvalidOperationException(
                $"Seed-shape max axons {maxAxons} is below the minimum viable topology requirement {minimumViableAxons}.");
        }

        if (shape.AxonCount < minimumViableAxons)
        {
            shape = shape with { AxonCount = minimumViableAxons };
        }

        var regionIds = Enumerable.Range(1, shape.ActiveInternalRegionCount).Select(static id => (byte)id).ToArray();

        var axonsByRegion = new Dictionary<byte, List<List<AxonRecord>>>();
        axonsByRegion[(byte)NbnConstants.InputRegionId] = CreateNeuronAxonBuckets((int)BasicsIoGeometry.InputWidth);
        for (var i = 0; i < regionIds.Length; i++)
        {
            axonsByRegion[regionIds[i]] = CreateNeuronAxonBuckets(internalRegionNeuronCounts[i]);
        }

        axonsByRegion[(byte)NbnConstants.OutputRegionId] = CreateNeuronAxonBuckets((int)BasicsIoGeometry.OutputWidth);

        AddBaseConnectivity(axonsByRegion, regionIds, internalRegionNeuronCounts);
        AddExtraConnectivity(axonsByRegion, regionIds, internalRegionNeuronCounts, shape.AxonCount);

        var sections = new List<NbnRegionSection>();
        var directory = new NbnRegionDirectoryEntry[NbnConstants.RegionCount];
        ulong offset = NbnBinary.NbnHeaderBytes;

        offset = AddRegionSection(
            (byte)NbnConstants.InputRegionId,
            (int)BasicsIoGeometry.InputWidth,
            DefaultStride,
            directory,
            sections,
            offset,
            axonsByRegion[(byte)NbnConstants.InputRegionId],
            isInternal: false);

        for (var i = 0; i < regionIds.Length; i++)
        {
            offset = AddRegionSection(
                regionIds[i],
                internalRegionNeuronCounts[i],
                DefaultStride,
                directory,
                sections,
                offset,
                axonsByRegion[regionIds[i]],
                isInternal: true);
        }

        offset = AddRegionSection(
            (byte)NbnConstants.OutputRegionId,
            (int)BasicsIoGeometry.OutputWidth,
            DefaultStride,
            directory,
            sections,
            offset,
            axonsByRegion[(byte)NbnConstants.OutputRegionId],
            isInternal: false);

        var header = new NbnHeaderV2(
            "NBN2",
            2,
            1,
            10,
            brainSeed: 1,
            axonStride: DefaultStride,
            flags: 0,
            quantization: QuantizationSchemas.DefaultNbn,
            regions: directory);

        var formatValidation = NbnBinaryValidator.ValidateNbn(header, sections);
        if (!formatValidation.IsValid)
        {
            throw new InvalidOperationException(
                $"Generated Basics template is invalid: {string.Join("; ", formatValidation.Issues.Select(static issue => issue.ToString()))}");
        }

        return new BasicsTemplateBuildResult(NbnBinary.WriteNbn(header, sections), shape);
    }

    private static BasicsResolvedSeedShape ResolveShape(BasicsSeedShapeConstraints constraints)
    {
        var validation = constraints.Validate();
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join("; ", validation.Errors));
        }

        var maxRegions = constraints.MaxActiveInternalRegionCount ?? int.MaxValue;
        var maxInternalNeurons = constraints.MaxInternalNeuronCount ?? int.MaxValue;
        var minRegions = constraints.MinActiveInternalRegionCount ?? 0;
        var minInternalNeurons = constraints.MinInternalNeuronCount ?? 0;

        var activeInternalRegions = maxRegions <= 0 || maxInternalNeurons <= 0
            ? 0
            : Math.Clamp(1, minRegions, maxRegions);
        var internalNeurons = activeInternalRegions == 0
            ? 0
            : Math.Clamp(Math.Max(1, activeInternalRegions), minInternalNeurons, maxInternalNeurons);

        if (activeInternalRegions > 0 && internalNeurons < activeInternalRegions)
        {
            internalNeurons = activeInternalRegions;
        }

        if (activeInternalRegions == 0 && minInternalNeurons > 0)
        {
            activeInternalRegions = Math.Clamp(1, 1, Math.Max(1, maxRegions));
            internalNeurons = Math.Clamp(Math.Max(1, activeInternalRegions), minInternalNeurons, Math.Max(minInternalNeurons, maxInternalNeurons));
        }

        var baseAxons = activeInternalRegions == 0
            ? (int)BasicsIoGeometry.InputWidth
            : (int)BasicsIoGeometry.InputWidth + internalNeurons;
        var resolvedAxons = Math.Max(baseAxons, constraints.MinAxonCount ?? baseAxons);
        if (constraints.MaxAxonCount is int maxAxons && maxAxons < baseAxons)
        {
            throw new InvalidOperationException(
                $"Seed-shape max axons {maxAxons} is below the minimum viable topology requirement {baseAxons}.");
        }

        if (constraints.MaxAxonCount is int boundedMaxAxons)
        {
            resolvedAxons = Math.Min(resolvedAxons, boundedMaxAxons);
        }

        return new BasicsResolvedSeedShape(
            ActiveInternalRegionCount: activeInternalRegions,
            InternalNeuronCount: internalNeurons,
            AxonCount: resolvedAxons);
    }

    private static int[] DistributeInternalNeurons(int activeInternalRegions, int internalNeurons)
    {
        if (activeInternalRegions <= 0 || internalNeurons <= 0)
        {
            return Array.Empty<int>();
        }

        var counts = new int[activeInternalRegions];
        var baseCount = internalNeurons / activeInternalRegions;
        var remainder = internalNeurons % activeInternalRegions;
        for (var i = 0; i < counts.Length; i++)
        {
            counts[i] = baseCount + (i < remainder ? 1 : 0);
        }

        return counts;
    }

    private static List<List<AxonRecord>> CreateNeuronAxonBuckets(int neuronCount)
    {
        var result = new List<List<AxonRecord>>(neuronCount);
        for (var i = 0; i < neuronCount; i++)
        {
            result.Add(new List<AxonRecord>());
        }

        return result;
    }

    private static void AddBaseConnectivity(
        IDictionary<byte, List<List<AxonRecord>>> axonsByRegion,
        IReadOnlyList<byte> internalRegionIds,
        IReadOnlyList<int> internalRegionNeuronCounts)
    {
        var inputBuckets = axonsByRegion[(byte)NbnConstants.InputRegionId];
        if (internalRegionIds.Count == 0)
        {
            for (var inputNeuronId = 0; inputNeuronId < inputBuckets.Count; inputNeuronId++)
            {
                var targetNeuronId = inputNeuronId % Math.Max(1, (int)BasicsIoGeometry.OutputWidth);
                inputBuckets[inputNeuronId].Add(new AxonRecord(MaxStrengthCode, targetNeuronId, (byte)NbnConstants.OutputRegionId));
            }

            EnsureOutputCoverage(inputBuckets, (byte)NbnConstants.OutputRegionId, (int)BasicsIoGeometry.OutputWidth);
            return;
        }

        var firstInternalRegionId = internalRegionIds[0];
        var firstInternalCount = internalRegionNeuronCounts[0];
        for (var inputNeuronId = 0; inputNeuronId < inputBuckets.Count; inputNeuronId++)
        {
            var targetNeuronId = inputNeuronId % Math.Max(1, firstInternalCount);
            inputBuckets[inputNeuronId].Add(new AxonRecord(MaxStrengthCode, targetNeuronId, firstInternalRegionId));
        }

        for (var regionIndex = 0; regionIndex < internalRegionIds.Count; regionIndex++)
        {
            var sourceRegionId = internalRegionIds[regionIndex];
            var sourceBuckets = axonsByRegion[sourceRegionId];
            var targetIsOutput = regionIndex == internalRegionIds.Count - 1;
            var targetRegionId = targetIsOutput
                ? (byte)NbnConstants.OutputRegionId
                : internalRegionIds[regionIndex + 1];
            var targetNeuronCount = targetIsOutput
                ? (int)BasicsIoGeometry.OutputWidth
                : internalRegionNeuronCounts[regionIndex + 1];

            for (var neuronId = 0; neuronId < sourceBuckets.Count; neuronId++)
            {
                var targetNeuronId = neuronId % Math.Max(1, targetNeuronCount);
                sourceBuckets[neuronId].Add(new AxonRecord(MaxStrengthCode, targetNeuronId, targetRegionId));
            }

            if (targetIsOutput)
            {
                EnsureOutputCoverage(sourceBuckets, targetRegionId, targetNeuronCount);
            }
        }
    }

    private static void AddExtraConnectivity(
        IDictionary<byte, List<List<AxonRecord>>> axonsByRegion,
        IReadOnlyList<byte> internalRegionIds,
        IReadOnlyList<int> internalRegionNeuronCounts,
        int targetAxonCount)
    {
        var currentAxonCount = axonsByRegion.Values.SelectMany(static neurons => neurons).Sum(static axons => axons.Count);
        if (currentAxonCount > targetAxonCount)
        {
            throw new InvalidOperationException(
                $"Resolved target axon count {targetAxonCount} is below the required base topology count {currentAxonCount}.");
        }

        if (currentAxonCount == targetAxonCount)
        {
            return;
        }

        var candidates = new List<(byte RegionId, int NeuronId, AxonRecord Axon)>();
        BuildExtraCandidates(candidates, (byte)NbnConstants.InputRegionId, (int)BasicsIoGeometry.InputWidth, internalRegionIds, internalRegionNeuronCounts);
        for (var regionIndex = 0; regionIndex < internalRegionIds.Count; regionIndex++)
        {
            BuildExtraCandidates(
                candidates,
                internalRegionIds[regionIndex],
                internalRegionNeuronCounts[regionIndex],
                internalRegionIds.Skip(regionIndex + 1).ToArray(),
                internalRegionNeuronCounts.Skip(regionIndex + 1).ToArray());
        }

        foreach (var candidate in candidates)
        {
            if (currentAxonCount >= targetAxonCount)
            {
                break;
            }

            var neuronAxons = axonsByRegion[candidate.RegionId][candidate.NeuronId];
            if (neuronAxons.Any(existing =>
                    existing.TargetRegionId == candidate.Axon.TargetRegionId
                    && existing.TargetNeuronId == candidate.Axon.TargetNeuronId))
            {
                continue;
            }

            neuronAxons.Add(candidate.Axon);
            currentAxonCount++;
        }

        if (currentAxonCount < targetAxonCount)
        {
            throw new InvalidOperationException(
                $"Seed-shape requested {targetAxonCount} axons but the 2->2 template can only realize {currentAxonCount} unique forward edges.");
        }
    }

    private static void BuildExtraCandidates(
        ICollection<(byte RegionId, int NeuronId, AxonRecord Axon)> candidates,
        byte sourceRegionId,
        int sourceNeuronCount,
        IReadOnlyList<byte> downstreamInternalRegions,
        IReadOnlyList<int> downstreamInternalNeuronCounts)
    {
        for (var neuronId = 0; neuronId < sourceNeuronCount; neuronId++)
        {
            for (var downstreamIndex = 0; downstreamIndex < downstreamInternalRegions.Count; downstreamIndex++)
            {
                for (var targetNeuronId = 0; targetNeuronId < downstreamInternalNeuronCounts[downstreamIndex]; targetNeuronId++)
                {
                    candidates.Add((
                        sourceRegionId,
                        neuronId,
                        new AxonRecord(MaxStrengthCode, targetNeuronId, downstreamInternalRegions[downstreamIndex])));
                }
            }

            for (var targetNeuronId = 0; targetNeuronId < BasicsIoGeometry.OutputWidth; targetNeuronId++)
            {
                candidates.Add((
                    sourceRegionId,
                    neuronId,
                    new AxonRecord(MaxStrengthCode, targetNeuronId, (byte)NbnConstants.OutputRegionId)));
            }
        }
    }

    private static void EnsureOutputCoverage(
        IReadOnlyList<List<AxonRecord>> sourceBuckets,
        byte targetRegionId,
        int targetNeuronCount)
    {
        if (sourceBuckets.Count == 0 || targetNeuronCount <= 0)
        {
            return;
        }

        var coveredTargets = new HashSet<int>();
        foreach (var bucket in sourceBuckets)
        {
            foreach (var axon in bucket)
            {
                if (axon.TargetRegionId == targetRegionId)
                {
                    coveredTargets.Add(axon.TargetNeuronId);
                }
            }
        }

        for (var targetNeuronId = 0; targetNeuronId < targetNeuronCount; targetNeuronId++)
        {
            if (coveredTargets.Contains(targetNeuronId))
            {
                continue;
            }

            var sourceNeuronId = targetNeuronId % sourceBuckets.Count;
            if (!sourceBuckets[sourceNeuronId].Any(existing =>
                    existing.TargetRegionId == targetRegionId
                    && existing.TargetNeuronId == targetNeuronId))
            {
                sourceBuckets[sourceNeuronId].Add(new AxonRecord(MaxStrengthCode, targetNeuronId, targetRegionId));
            }
        }
    }

    private static int ResolveMinimumViableAxonCount(IReadOnlyList<int> internalRegionNeuronCounts)
    {
        if (internalRegionNeuronCounts.Count == 0)
        {
            return Math.Max((int)BasicsIoGeometry.InputWidth, (int)BasicsIoGeometry.OutputWidth);
        }

        var lastInternalCount = internalRegionNeuronCounts[^1];
        return (int)BasicsIoGeometry.InputWidth
               + internalRegionNeuronCounts.Sum()
               + Math.Max(0, (int)BasicsIoGeometry.OutputWidth - lastInternalCount);
    }

    private static ulong AddRegionSection(
        byte regionId,
        int neuronCount,
        uint stride,
        NbnRegionDirectoryEntry[] directory,
        ICollection<NbnRegionSection> sections,
        ulong offset,
        IReadOnlyList<List<AxonRecord>> neuronAxons,
        bool isInternal)
    {
        var neurons = new NeuronRecord[neuronCount];
        var axons = new List<AxonRecord>();
        for (var neuronId = 0; neuronId < neuronCount; neuronId++)
        {
            var outgoing = neuronAxons[neuronId]
                .OrderBy(static axon => axon.TargetRegionId)
                .ThenBy(static axon => axon.TargetNeuronId)
                .ThenBy(static axon => axon.StrengthCode)
                .ToArray();
            var activationFunctionId = isInternal
                ? SelectPreferredFunction(PreferredActivationFunctionIds, regionId, neuronId)
                : IdentityActivation;
            var resetFunctionId = isInternal
                ? SelectPreferredFunction(PreferredResetFunctionIds, regionId, neuronId)
                : ZeroReset;
            var accumulationFunctionId = isInternal
                ? SelectPreferredFunction(PreferredAccumulationFunctionIds, regionId, neuronId)
                : SumAccumulation;
            neurons[neuronId] = new NeuronRecord(
                axonCount: (ushort)outgoing.Length,
                paramBCode: 0,
                paramACode: isInternal ? (byte)8 : (byte)0,
                activationThresholdCode: 0,
                preActivationThresholdCode: 0,
                resetFunctionId: resetFunctionId,
                activationFunctionId: activationFunctionId,
                accumulationFunctionId: accumulationFunctionId,
                exists: true);
            axons.AddRange(outgoing);
        }

        var totalAxons = (ulong)axons.Count;
        var checkpointCount = (uint)((Math.Max(1, neuronCount) + stride - 1) / stride + 1);
        var checkpoints = new ulong[checkpointCount];
        checkpoints[0] = 0;
        checkpoints[^1] = totalAxons;
        if (checkpointCount > 2)
        {
            ulong running = 0;
            uint nextBoundary = stride;
            var checkpointIndex = 1;
            for (var neuronId = 0; neuronId < neurons.Length && checkpointIndex < checkpoints.Length - 1; neuronId++)
            {
                running += neurons[neuronId].AxonCount;
                if ((uint)(neuronId + 1) == nextBoundary)
                {
                    checkpoints[checkpointIndex++] = running;
                    nextBoundary += stride;
                }
            }
        }

        var section = new NbnRegionSection(
            regionId,
            (uint)neuronCount,
            totalAxons,
            stride,
            checkpointCount,
            checkpoints,
            neurons,
            axons.ToArray());
        directory[regionId] = new NbnRegionDirectoryEntry((uint)neuronCount, totalAxons, offset, 0);
        sections.Add(section);
        return offset + (ulong)section.ByteLength;
    }

    private static byte SelectPreferredFunction(IReadOnlyList<byte> preferredCodes, byte regionId, int neuronId)
    {
        if (preferredCodes.Count == 0)
        {
            return 0;
        }

        var index = Math.Abs((regionId * 17) + neuronId) % preferredCodes.Count;
        return preferredCodes[index];
    }
}
