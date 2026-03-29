using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Nbn.Demos.Basics.Environment;
using Nbn.Proto.Io;
using Nbn.Shared;
using Proto;

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

    [Fact]
    public async Task WaitForOutputVectorAsync_AssemblesOutputVectorSegments()
    {
        var port = GetFreeTcpPort();
        var brainId = Guid.NewGuid();
        var options = new BasicsRuntimeClientOptions
        {
            BindHost = "127.0.0.1",
            Port = port
        };

        await using var client = await BasicsRuntimeClient.StartAsync(options);
        var outputWidths = GetPrivateField<ConcurrentDictionary<Guid, int>>(client, "_outputWidths");
        outputWidths[brainId] = 2;

        var system = GetPrivateField<ActorSystem>(client, "_system");
        var receiverPid = GetPrivateField<PID>(client, "_receiverPid");
        system.Root.Send(receiverPid, new OutputVectorSegment
        {
            BrainId = brainId.ToProtoUuid(),
            TickId = 7,
            OutputIndexStart = 1,
            Values = { 20f }
        });
        system.Root.Send(receiverPid, new OutputVectorSegment
        {
            BrainId = brainId.ToProtoUuid(),
            TickId = 7,
            OutputIndexStart = 0,
            Values = { 10f }
        });

        var output = await client.WaitForOutputVectorAsync(brainId, afterTickExclusive: 0, TimeSpan.FromSeconds(5));
        Assert.NotNull(output);
        Assert.Equal((ulong)7, output!.TickId);
        Assert.Collection(
            output.Values,
            value => Assert.Equal(10f, value),
            value => Assert.Equal(20f, value));
    }

    private static T GetPrivateField<T>(object instance, string fieldName) where T : class
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var value = field!.GetValue(instance);
        var typed = Assert.IsType<T>(value);
        return typed;
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
