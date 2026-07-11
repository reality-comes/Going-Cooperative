namespace GoingCooperative.Core
{
    public enum TransportMessageKind
    {
        Command = 1,
        Ack = 2,
        ReplicationHello = 20,
        ReplicationTransformSnapshot = 21,
        ReplicationIntent = 22,
        ReplicationCommandAck = 23,
        ReplicationRegionOrderState = 24,
        ReplicationWorldObjectDelta = 25,
        ReplicationWorldObjectDeltaAck = 26,
        ReplicationResyncControl = 27,
        ReplicationResourceContainerBatch = 28,
        Chunk = 100
    }

    public sealed class TransportEnvelope
    {
        public TransportEnvelope(TransportMessageKind kind, long tick, string senderId, string payload)
        {
            Kind = kind;
            Tick = tick;
            SenderId = senderId;
            Payload = payload;
        }

        public TransportMessageKind Kind { get; }
        public long Tick { get; }
        public string SenderId { get; }
        public string Payload { get; }
    }

    public interface INetworkTransport
    {
        bool IsConnected { get; }
        void StartHost(int port);
        void Connect(string host, int port);
        void Send(TransportEnvelope envelope);
        bool TryReceive(out TransportEnvelope envelope);
        void Stop();
    }
}
