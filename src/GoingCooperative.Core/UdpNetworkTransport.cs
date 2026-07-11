using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GoingCooperative.Core
{
    public sealed class UdpNetworkTransport : INetworkTransport
    {
        private readonly Queue<TransportEnvelope> inbox = new Queue<TransportEnvelope>();
        private readonly TransportChunkReassembler chunkReassembler = new TransportChunkReassembler();
        private UdpClient? udpClient;
        private IPEndPoint? remoteEndpoint;
        private bool isHostEndpoint;
        private long nextChunkId;
        private const int MaxUnchunkedDatagramBytes = 1100;
        private const int MaxChunkEnvelopeChars = 700;

        public bool IsConnected { get; private set; }

        /// <summary>Datagrams that failed envelope decode and were silently dropped.</summary>
        public long DecodeFailures { get; private set; }

        /// <summary>Chunk datagrams that failed reassembly (malformed or mismatched) and were dropped.</summary>
        public long ChunkFailures { get; private set; }

        public int LocalPort
        {
            get
            {
                if (udpClient == null || udpClient.Client.LocalEndPoint == null)
                {
                    return 0;
                }

                return ((IPEndPoint)udpClient.Client.LocalEndPoint).Port;
            }
        }

        public bool RemoteEndpointKnown
        {
            get { return remoteEndpoint != null; }
        }

        public void StartHost(int port)
        {
            Stop();
            udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            isHostEndpoint = true;
            IsConnected = true;
        }

        public void Connect(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Host is required.", nameof(host));
            }

            Stop();
            udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            remoteEndpoint = new IPEndPoint(ResolveHost(host), port);
            isHostEndpoint = false;
            IsConnected = true;
        }

        public void Send(TransportEnvelope envelope)
        {
            if (!IsConnected || udpClient == null)
            {
                throw new InvalidOperationException("Transport is not connected.");
            }

            var target = remoteEndpoint;
            if (target == null)
            {
                throw new InvalidOperationException(isHostEndpoint ? "Host has no remote endpoint yet." : "Client has no host endpoint.");
            }

            envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
            var encoded = TransportEnvelopeCodec.Encode(envelope);
            var bytes = Encoding.UTF8.GetBytes(encoded);
            if (envelope.Kind != TransportMessageKind.Chunk && bytes.Length > MaxUnchunkedDatagramBytes)
            {
                var chunkId = envelope.SenderId + "-" + (++nextChunkId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var chunks = TransportChunkCodec.CreateChunks(envelope, chunkId, MaxChunkEnvelopeChars);
                for (var i = 0; i < chunks.Count; i++)
                {
                    SendEncodedEnvelope(chunks[i], target);
                }

                return;
            }

            udpClient.Send(bytes, bytes.Length, target);
        }

        public bool TryReceive(out TransportEnvelope envelope)
        {
            PumpSocket();
            if (inbox.Count == 0)
            {
                envelope = new TransportEnvelope(TransportMessageKind.ReplicationHello, 0, string.Empty, string.Empty);
                return false;
            }

            envelope = inbox.Dequeue();
            return true;
        }

        public void Stop()
        {
            IsConnected = false;
            remoteEndpoint = null;
            inbox.Clear();
            chunkReassembler.Clear();
            if (udpClient != null)
            {
                udpClient.Close();
                udpClient = null;
            }
        }

        private void PumpSocket()
        {
            if (!IsConnected || udpClient == null)
            {
                return;
            }

            while (udpClient.Available > 0)
            {
                var sender = new IPEndPoint(IPAddress.Any, 0);
                var bytes = udpClient.Receive(ref sender);
                if (isHostEndpoint)
                {
                    remoteEndpoint = sender;
                }

                var line = Encoding.UTF8.GetString(bytes);
                if (TransportEnvelopeCodec.TryDecode(line, out var decoded, out _) && decoded != null)
                {
                    if (decoded.Kind == TransportMessageKind.Chunk)
                    {
                        if (chunkReassembler.TryAddChunk(decoded, out var reassembled, out var chunkError) && reassembled != null)
                        {
                            inbox.Enqueue(reassembled);
                        }
                        else if (!string.IsNullOrEmpty(chunkError))
                        {
                            ChunkFailures++;
                        }
                    }
                    else
                    {
                        inbox.Enqueue(decoded);
                    }
                }
                else
                {
                    DecodeFailures++;
                }
            }
        }

        private void SendEncodedEnvelope(TransportEnvelope envelope, IPEndPoint target)
        {
            var encoded = TransportEnvelopeCodec.Encode(envelope);
            var bytes = Encoding.UTF8.GetBytes(encoded);
            udpClient?.Send(bytes, bytes.Length, target);
        }

        private static IPAddress ResolveHost(string host)
        {
            if (IPAddress.TryParse(host, out var address))
            {
                return address;
            }

            var addresses = Dns.GetHostAddresses(host);
            for (var i = 0; i < addresses.Length; i++)
            {
                if (addresses[i].AddressFamily == AddressFamily.InterNetwork)
                {
                    return addresses[i];
                }
            }

            if (addresses.Length > 0)
            {
                return addresses[0];
            }

            throw new InvalidOperationException("Could not resolve host: " + host);
        }
    }
}
