using System;
using System.Collections.Generic;

namespace GoingCooperative.Core
{
    public sealed class InMemoryTransportHub
    {
        private readonly List<InMemoryNetworkTransport> endpoints = new List<InMemoryNetworkTransport>();

        public InMemoryNetworkTransport CreateEndpoint(string endpointId)
        {
            var endpoint = new InMemoryNetworkTransport(this, endpointId);
            endpoints.Add(endpoint);
            return endpoint;
        }

        internal void Send(InMemoryNetworkTransport sender, TransportEnvelope envelope)
        {
            foreach (var endpoint in endpoints)
            {
                if (ReferenceEquals(endpoint, sender) || !endpoint.IsConnected)
                {
                    continue;
                }

                endpoint.Enqueue(envelope);
            }
        }
    }

    public sealed class InMemoryNetworkTransport : INetworkTransport
    {
        private readonly InMemoryTransportHub hub;
        private readonly Queue<TransportEnvelope> inbox = new Queue<TransportEnvelope>();

        internal InMemoryNetworkTransport(InMemoryTransportHub hub, string endpointId)
        {
            this.hub = hub ?? throw new ArgumentNullException(nameof(hub));
            EndpointId = string.IsNullOrWhiteSpace(endpointId) ? "endpoint" : endpointId;
        }

        public string EndpointId { get; }
        public bool IsHostEndpoint { get; private set; }
        public bool IsConnected { get; private set; }

        public void StartHost(int port)
        {
            IsHostEndpoint = true;
            IsConnected = true;
        }

        public void Connect(string host, int port)
        {
            IsHostEndpoint = false;
            IsConnected = true;
        }

        public void Send(TransportEnvelope envelope)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Transport is not connected.");
            }

            hub.Send(this, envelope ?? throw new ArgumentNullException(nameof(envelope)));
        }

        public bool TryReceive(out TransportEnvelope envelope)
        {
            if (inbox.Count == 0)
            {
                envelope = new TransportEnvelope(TransportMessageKind.ReplicationHello, 0, EndpointId, string.Empty);
                return false;
            }

            envelope = inbox.Dequeue();
            return true;
        }

        public void Stop()
        {
            IsConnected = false;
            inbox.Clear();
        }

        internal void Enqueue(TransportEnvelope envelope)
        {
            inbox.Enqueue(envelope);
        }
    }
}
