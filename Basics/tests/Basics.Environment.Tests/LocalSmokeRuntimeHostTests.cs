using System.Net;
using System.Net.Sockets;
using Nbn.Demos.Basics.Harness;

namespace Nbn.Demos.Basics.Environment.Tests;

public sealed class LocalSmokeRuntimeHostTests
{
    [Fact]
    public async Task StartAsync_WhenReadinessIsCanceled_ReleasesPortAndRuntimeDirectory()
    {
        var port = GetFreeTcpPort();
        var runtimeParent = Path.Combine(Path.GetTempPath(), "nbn-basics-local-smoke");
        var directoriesBefore = GetRuntimeDirectories(runtimeParent);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalSmokeRuntimeHost.StartAsync(
            new LocalSmokeRuntimeHostOptions
            {
                IoPort = port,
                ReadinessTimeout = TimeSpan.FromSeconds(5)
            },
            cancellation.Token));

        Assert.Equal(directoriesBefore, GetRuntimeDirectories(runtimeParent));
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
    }

    private static string[] GetRuntimeDirectories(string runtimeParent)
        => Directory.Exists(runtimeParent)
            ? Directory.GetDirectories(runtimeParent).Order(StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
