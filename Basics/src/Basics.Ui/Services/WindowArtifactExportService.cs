using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nbn.Demos.Basics.Environment;
using Nbn.Proto;
using Nbn.Shared;

namespace Nbn.Demos.Basics.Ui.Services;

public interface IBasicsArtifactExportService
{
    Task<string?> ExportAsync(
        ArtifactRef artifact,
        string title,
        string suggestedFileName,
        CancellationToken cancellationToken = default);
}

public sealed class WindowArtifactExportService : IBasicsArtifactExportService
{
    private readonly TopLevel _topLevel;

    public WindowArtifactExportService(TopLevel topLevel)
    {
        _topLevel = topLevel ?? throw new ArgumentNullException(nameof(topLevel));
    }

    public async Task<string?> ExportAsync(
        ArtifactRef artifact,
        string title,
        string suggestedFileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (!_topLevel.StorageProvider.CanSave)
        {
            throw new InvalidOperationException("This platform does not support save dialogs for artifact export.");
        }

        var extension = ResolveExtension(artifact.MediaType);
        var fileType = CreateFileType(artifact.MediaType, extension);
        var startLocation = await _topLevel.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads).ConfigureAwait(false)
                            ?? await _topLevel.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents).ConfigureAwait(false);
        var file = await _topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = EnsureFileNameHasExtension(suggestedFileName, extension),
            DefaultExtension = extension,
            SuggestedStartLocation = startLocation,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { fileType },
            SuggestedFileType = fileType
        }).ConfigureAwait(false);
        if (file is null)
        {
            return null;
        }

        var bytes = await BasicsArtifactStoreReader.ReadArtifactBytesAsync(artifact, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            throw new FileNotFoundException(
                $"Artifact {artifact.ToSha256Hex()} could not be read from {artifact.StoreUri ?? "the configured artifact store"}.");
        }

        await using var stream = await file.OpenWriteAsync().ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        return file.TryGetLocalPath() ?? file.Path.LocalPath;
    }

    private static FilePickerFileType CreateFileType(string? mediaType, string extension)
        => new(mediaType == "application/x-nbs" ? "NBN snapshot" : "NBN definition")
        {
            Patterns = new[] { $"*.{extension}" },
            MimeTypes = string.IsNullOrWhiteSpace(mediaType) ? null : new[] { mediaType }
        };

    private static string ResolveExtension(string? mediaType)
        => string.Equals(mediaType, "application/x-nbs", StringComparison.OrdinalIgnoreCase) ? "nbs" : "nbn";

    private static string EnsureFileNameHasExtension(string suggestedFileName, string extension)
    {
        var trimmed = string.IsNullOrWhiteSpace(suggestedFileName) ? "nbn-artifact" : suggestedFileName.Trim();
        return trimmed.EndsWith($".{extension}", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}.{extension}";
    }
}
