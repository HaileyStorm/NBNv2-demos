using System.Net;
using System.Net.Sockets;
using Nbn.Demos.Basics.Environment;

namespace Nbn.Demos.Basics.Environment.Tests;

public sealed class BasicsRuntimeClientTests
{
    [Fact]
    public async Task DisposeAsync_ReleasesBoundPort_ForImmediateReuse()
    {
        var port = GetFreeTcpPort();
        var options = new BasicsRuntimeClientOptions
        {
            BindHost = "127.0.0.1",
            Port = port
        };

        await using (var first = await BasicsRuntimeClient.StartAsync(options))
        {
        }

        await using var second = await BasicsRuntimeClient.StartAsync(options);
        Assert.NotNull(second);
    }

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
