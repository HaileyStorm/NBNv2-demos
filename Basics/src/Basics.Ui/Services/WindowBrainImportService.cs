using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Text.Json;

namespace Nbn.Demos.Basics.Ui.Services;

public sealed record BasicsImportedBrainFile(
    string DisplayName,
    string? LocalPath,
    byte[] DefinitionBytes,
    string? SnapshotLocalPath,
    byte[]? SnapshotBytes);

public interface IBasicsBrainImportService
{
    Task<IReadOnlyList<BasicsImportedBrainFile>> ImportAsync(CancellationToken cancellationToken = default);
}

public sealed class WindowBrainImportService : IBasicsBrainImportService
{
    private readonly TopLevel _topLevel;

    public WindowBrainImportService(TopLevel topLevel)
    {
        _topLevel = topLevel ?? throw new ArgumentNullException(nameof(topLevel));
    }

    public async Task<IReadOnlyList<BasicsImportedBrainFile>> ImportAsync(CancellationToken cancellationToken = default)
    {
        if (!_topLevel.StorageProvider.CanOpen)
        {
            throw new InvalidOperationException("This platform does not support file-open dialogs for initial brain import.");
        }

        var startLocation = await _topLevel.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents).ConfigureAwait(false)
                            ?? await _topLevel.StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads).ConfigureAwait(false);
        var files = await _topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add initial brains",
            AllowMultiple = true,
            SuggestedStartLocation = startLocation,
            FileTypeFilter =
            [
                new FilePickerFileType("NBN definition")
                {
                    Patterns = ["*.nbn"],
                    MimeTypes = ["application/x-nbn"]
                }
            ]
        }).ConfigureAwait(false);
        if (files.Count == 0)
        {
            return Array.Empty<BasicsImportedBrainFile>();
        }

        var imported = new List<BasicsImportedBrainFile>(files.Count);
        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync().ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            imported.Add(new BasicsImportedBrainFile(
                DisplayName: file.Name,
                LocalPath: file.TryGetLocalPath(),
                DefinitionBytes: buffer.ToArray(),
                SnapshotLocalPath: TryResolveSnapshotPath(file.TryGetLocalPath()),
                SnapshotBytes: await TryReadSnapshotBytesAsync(file.TryGetLocalPath(), cancellationToken).ConfigureAwait(false)));
        }

        return imported;
    }

    private static async Task<byte[]?> TryReadSnapshotBytesAsync(string? definitionPath, CancellationToken cancellationToken)
    {
        var snapshotPath = TryResolveSnapshotPath(definitionPath);
        if (string.IsNullOrWhiteSpace(snapshotPath) || !File.Exists(snapshotPath))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
    }

    private static string? TryResolveSnapshotPath(string? definitionPath)
    {
        if (string.IsNullOrWhiteSpace(definitionPath))
        {
            return null;
        }

        var tracePath = $"{definitionPath}.trace.json";
        if (File.Exists(tracePath))
        {
            try
            {
                using var stream = File.OpenRead(tracePath);
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.TryGetProperty("snapshotPath", out var snapshotPathProperty)
                    && snapshotPathProperty.ValueKind == JsonValueKind.String)
                {
                    var snapshotPath = snapshotPathProperty.GetString();
                    if (!string.IsNullOrWhiteSpace(snapshotPath) && File.Exists(snapshotPath))
                    {
                        return snapshotPath;
                    }
                }
            }
            catch
            {
                // Fall through to adjacent-file heuristics.
            }
        }

        var adjacentSnapshotPath = Path.ChangeExtension(definitionPath, ".nbs");
        return File.Exists(adjacentSnapshotPath) ? adjacentSnapshotPath : null;
    }
}
