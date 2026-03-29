using Nbn.Proto;
using Nbn.Runtime.Artifacts;
using Nbn.Shared;
using Nbn.Shared.Format;

namespace Nbn.Demos.Basics.Environment;

public static class BasicsArtifactStoreReader
{
    public static async Task<byte[]?> ReadArtifactBytesAsync(
        ArtifactRef artifactRef,
        string? defaultLocalStoreRootPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifactRef);

        if (!artifactRef.TryToSha256Bytes(out var hashBytes))
        {
            return null;
        }

        var resolver = new ArtifactStoreResolver(new ArtifactStoreResolverOptions(defaultLocalStoreRootPath));
        var store = resolver.Resolve(artifactRef.StoreUri);
        await using var stream = await store.TryOpenArtifactAsync(new Sha256Hash(hashBytes), cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public static async Task<BasicsDefinitionComplexitySummary?> TryReadDefinitionComplexityAsync(
        ArtifactRef artifactRef,
        string? defaultLocalStoreRootPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await ReadArtifactBytesAsync(artifactRef, defaultLocalStoreRootPath, cancellationToken).ConfigureAwait(false);
            return bytes is null ? null : ReadDefinitionComplexity(bytes);
        }
        catch
        {
            return null;
        }
    }

    public static BasicsDefinitionComplexitySummary ReadDefinitionComplexity(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var header = NbnBinary.ReadNbnHeader(bytes);
        var activeInternalRegionCount = 0;
        var internalNeuronCount = 0;
        var axonCount = 0;

        for (var regionId = 0; regionId < header.Regions.Length; regionId++)
        {
            var region = header.Regions[regionId];
            if (region.NeuronSpan <= 0 || region.Offset <= 0)
            {
                continue;
            }

            var section = NbnBinary.ReadNbnRegionSection(bytes, region.Offset);
            axonCount += section.AxonRecords.Length;
            if (regionId is NbnConstants.InputRegionId or NbnConstants.OutputRegionId)
            {
                continue;
            }

            activeInternalRegionCount++;
            internalNeuronCount += checked((int)region.NeuronSpan);
        }

        return new BasicsDefinitionComplexitySummary(
            ActiveInternalRegionCount: activeInternalRegionCount,
            InternalNeuronCount: internalNeuronCount,
            AxonCount: axonCount);
    }
}
