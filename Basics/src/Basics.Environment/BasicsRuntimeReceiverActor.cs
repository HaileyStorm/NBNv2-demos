using Nbn.Proto.Io;
using Nbn.Proto.Control;
using Nbn.Shared;
using Proto;

namespace Nbn.Demos.Basics.Environment;

internal interface IBasicsRuntimeEventSink
{
    void OnConnectAck(ConnectAck ack);

    void OnOutputEvent(OutputEvent output);

    void OnOutputVectorEvent(OutputVectorEvent output);

    void OnOutputVectorSegment(OutputVectorSegment output);

    void OnBrainTerminated(BrainTerminated terminated);
}

internal sealed class BasicsRuntimeReceiverActor : IActor
{
    private readonly IBasicsRuntimeEventSink _sink;
    private PID? _ioGateway;

    public BasicsRuntimeReceiverActor(IBasicsRuntimeEventSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    public Task ReceiveAsync(IContext context)
    {
        switch (context.Message)
        {
            case BasicsSetIoGatewayPid setIo:
                _ioGateway = setIo.Pid;
                break;
            case BasicsConnectCommand connect:
                RequestToIo(context, new Connect
                {
                    ClientName = connect.ClientName
                });
                break;
            case BasicsSubscribeOutputsVectorCommand subscribe:
                SendToIo(context, new SubscribeOutputsVector
                {
                    BrainId = subscribe.BrainId.ToProtoUuid(),
                    SubscriberActor = PidLabel(context.Self, context.System.Address)
                });
                break;
            case BasicsSubscribeOutputsCommand subscribe:
                SendToIo(context, new SubscribeOutputs
                {
                    BrainId = subscribe.BrainId.ToProtoUuid(),
                    SubscriberActor = PidLabel(context.Self, context.System.Address)
                });
                break;
            case BasicsUnsubscribeOutputsVectorCommand unsubscribe:
                SendToIo(context, new UnsubscribeOutputsVector
                {
                    BrainId = unsubscribe.BrainId.ToProtoUuid(),
                    SubscriberActor = PidLabel(context.Self, context.System.Address)
                });
                break;
            case BasicsUnsubscribeOutputsCommand unsubscribe:
                SendToIo(context, new UnsubscribeOutputs
                {
                    BrainId = unsubscribe.BrainId.ToProtoUuid(),
                    SubscriberActor = PidLabel(context.Self, context.System.Address)
                });
                break;
            case BasicsInputVectorCommand vector:
                var message = new InputVector
                {
                    BrainId = vector.BrainId.ToProtoUuid()
                };
                message.Values.Add(vector.Values);
                SendToIo(context, message);
                break;
            case OutputEvent outputEvent:
                _sink.OnOutputEvent(outputEvent.Clone());
                break;
            case ConnectAck connectAck:
                _sink.OnConnectAck(connectAck.Clone());
                break;
            case OutputVectorEvent outputVector:
                _sink.OnOutputVectorEvent(outputVector.Clone());
                break;
            case OutputVectorSegment outputVectorSegment:
                _sink.OnOutputVectorSegment(outputVectorSegment.Clone());
                break;
            case BrainTerminated terminated:
                _sink.OnBrainTerminated(terminated.Clone());
                break;
        }

        return Task.CompletedTask;
    }

    private void SendToIo(IContext context, object message)
    {
        if (_ioGateway is null)
        {
            return;
        }

        context.Send(_ioGateway, message);
    }

    private void RequestToIo(IContext context, object message)
    {
        if (_ioGateway is null)
        {
            return;
        }

        context.Request(_ioGateway, message);
    }

    private static string PidLabel(PID pid, string? fallbackAddress = null)
    {
        var address = string.IsNullOrWhiteSpace(pid.Address) ? fallbackAddress : pid.Address;
        return string.IsNullOrWhiteSpace(address) ? pid.Id : $"{address}/{pid.Id}";
    }
}

internal sealed record BasicsSetIoGatewayPid(PID? Pid);

internal sealed record BasicsConnectCommand(string ClientName);

internal sealed record BasicsSubscribeOutputsVectorCommand(Guid BrainId);

internal sealed record BasicsSubscribeOutputsCommand(Guid BrainId);

internal sealed record BasicsUnsubscribeOutputsVectorCommand(Guid BrainId);

internal sealed record BasicsUnsubscribeOutputsCommand(Guid BrainId);

internal sealed record BasicsInputVectorCommand(Guid BrainId, IReadOnlyList<float> Values);
