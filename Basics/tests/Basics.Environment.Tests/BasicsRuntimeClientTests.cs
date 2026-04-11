using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Channels;
using Nbn.Demos.Basics.Environment;
using Nbn.Proto.Io;
using Nbn.Proto.Repro;
using Nbn.Proto.Speciation;
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

    [Fact]
    public async Task OutputVectorBuffer_DropsOldestEntries_WhenProducerOutrunsConsumer()
    {
        var port = GetFreeTcpPort();
        var brainId = Guid.NewGuid();
        var options = new BasicsRuntimeClientOptions
        {
            BindHost = "127.0.0.1",
            Port = port
        };

        await using var client = await BasicsRuntimeClient.StartAsync(options);
        Assert.Null(await client.WaitForOutputVectorAsync(brainId, afterTickExclusive: 0, TimeSpan.FromMilliseconds(1)));

        var system = GetPrivateField<ActorSystem>(client, "_system");
        var receiverPid = GetPrivateField<PID>(client, "_receiverPid");
        for (var tick = 1UL; tick <= 300UL; tick++)
        {
            system.Root.Send(receiverPid, new OutputVectorEvent
            {
                BrainId = brainId.ToProtoUuid(),
                TickId = tick,
                Values = { (float)tick, 1f }
            });
        }

        var buffers = GetPrivateField<ConcurrentDictionary<Guid, Channel<BasicsRuntimeOutputVector>>>(client, "_outputBuffers");
        Assert.True(buffers.TryGetValue(brainId, out var channel));
        await Task.Delay(TimeSpan.FromSeconds(1));

        var drained = new List<BasicsRuntimeOutputVector>();
        while (channel!.Reader.TryRead(out var output))
        {
            drained.Add(output);
        }

        Assert.Equal(256, drained.Count);
        Assert.Equal(45UL, drained[0].TickId);
        Assert.Equal(300UL, drained[^1].TickId);
    }

    [Fact]
    public async Task SubscribeOutputsVectorAsync_RetriesQueuedAckUntilSubscriptionIsActive()
    {
        var port = GetFreeTcpPort();
        var brainId = Guid.NewGuid();
        var options = new BasicsRuntimeClientOptions
        {
            IoAddress = $"127.0.0.1:{port}",
            IoGatewayName = "io-gateway",
            BindHost = "127.0.0.1",
            Port = port
        };

        await using var client = await BasicsRuntimeClient.StartAsync(options);
        var system = GetPrivateField<ActorSystem>(client, "_system");
        var receiverPid = GetPrivateField<PID>(client, "_receiverPid");
        var observedSubscriptions = new TaskCompletionSource<IReadOnlyList<SubscribeOutputsVector>>(TaskCreationOptions.RunContinuationsAsynchronously);
        system.Root.SpawnNamed(
            Props.FromProducer(() => new OutputSubscriptionProbeActor(observedSubscriptions)),
            "io-gateway");

        await client.SubscribeOutputsVectorAsync(brainId);

        var messages = await observedSubscriptions.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, messages.Count);
        Assert.All(
            messages,
            message =>
            {
                Assert.True(message.BrainId?.TryToGuid(out var observedBrainId) == true && observedBrainId == brainId);
                Assert.False(string.IsNullOrWhiteSpace(message.SubscriberActor));
                Assert.Contains(receiverPid.Id, message.SubscriberActor, StringComparison.Ordinal);
                Assert.Equal(OutputSubscriptionDeliveryMode.LatestOnly, message.DeliveryMode);
            });
    }

    [Fact]
    public async Task ReproduceByArtifactsAsync_PropagatesCallerCancellation()
    {
        var port = GetFreeTcpPort();
        var options = new BasicsRuntimeClientOptions
        {
            IoAddress = $"127.0.0.1:{port}",
            IoGatewayName = "io-gateway",
            BindHost = "127.0.0.1",
            Port = port
        };

        await using var client = await BasicsRuntimeClient.StartAsync(options);
        var system = GetPrivateField<ActorSystem>(client, "_system");
        system.Root.SpawnNamed(
            Props.FromProducer(static () => new SilentIoGatewayActor()),
            "io-gateway");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReproduceByArtifactsAsync(
            new ReproduceByArtifactsRequest(),
            cancellation.Token));
    }

    [Fact]
    public async Task AssignSpeciationAsync_PropagatesCallerCancellation()
    {
        var port = GetFreeTcpPort();
        var options = new BasicsRuntimeClientOptions
        {
            IoAddress = $"127.0.0.1:{port}",
            IoGatewayName = "io-gateway",
            BindHost = "127.0.0.1",
            Port = port
        };

        await using var client = await BasicsRuntimeClient.StartAsync(options);
        var system = GetPrivateField<ActorSystem>(client, "_system");
        system.Root.SpawnNamed(
            Props.FromProducer(static () => new SilentIoGatewayActor()),
            "io-gateway");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AssignSpeciationAsync(
            new SpeciationAssignRequest(),
            cancellation.Token));
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

    private sealed class OutputSubscriptionProbeActor : IActor
    {
        private readonly TaskCompletionSource<IReadOnlyList<SubscribeOutputsVector>> _observedSubscriptions;
        private readonly List<SubscribeOutputsVector> _messages = new();

        public OutputSubscriptionProbeActor(TaskCompletionSource<IReadOnlyList<SubscribeOutputsVector>> observedSubscriptions)
        {
            _observedSubscriptions = observedSubscriptions;
        }

        public Task ReceiveAsync(IContext context)
        {
            if (context.Message is not SubscribeOutputsVector subscribe)
            {
                return Task.CompletedTask;
            }

            _messages.Add(subscribe.Clone());
            context.Respond(new IoCommandAck
            {
                BrainId = subscribe.BrainId?.Clone(),
                Command = "subscribe_outputs_vector",
                Success = true,
                Message = _messages.Count == 1 ? "queued" : "applied"
            });

            if (_messages.Count >= 2)
            {
                _observedSubscriptions.TrySetResult(_messages.ToArray());
            }

            return Task.CompletedTask;
        }
    }

    private sealed class SilentIoGatewayActor : IActor
    {
        public Task ReceiveAsync(IContext context) => Task.CompletedTask;
    }
}
