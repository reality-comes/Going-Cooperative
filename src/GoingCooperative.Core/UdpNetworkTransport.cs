using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
        private readonly bool securityEnabled;
        private readonly byte[] securityKey;
        private byte[]? clientNonce;
        private byte[]? hostNonce;
        private byte[]? sessionId;
        private IPEndPoint? pendingEndpoint;
        private long sendSecuritySequence;
        private long highestReceiveSecuritySequence;
        private readonly HashSet<long> receivedSecuritySequences = new HashSet<long>();
        private DateTime nextClientHelloUtc;
        private int unauthenticatedDatagramsThisWindow;
        private DateTime unauthenticatedWindowUtc;
        private const int MaxUnchunkedDatagramBytes = 1100;
        private const int MaxChunkEnvelopeChars = 700;

        public bool IsConnected { get; private set; }

        public bool AuthenticationEstablished { get; private set; }

        public long AuthenticationFailures { get; private set; }

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

        public UdpNetworkTransport()
            : this(false, string.Empty)
        {
        }

        public UdpNetworkTransport(bool securityEnabled, string sessionCode)
        {
            this.securityEnabled = securityEnabled;
            if (securityEnabled)
            {
                if (!DirectTransportSecurity.TryDeriveKey(sessionCode, out securityKey, out var error))
                {
                    throw new ArgumentException(error, nameof(sessionCode));
                }
            }
            else
            {
                securityKey = new byte[0];
                AuthenticationEstablished = true;
            }
        }

        public void StartHost(int port)
        {
            Stop();
            udpClient = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            isHostEndpoint = true;
            IsConnected = true;
            AuthenticationEstablished = !securityEnabled;
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
            AuthenticationEstablished = !securityEnabled;
            if (securityEnabled)
            {
                clientNonce = DirectTransportSecurity.RandomBytes(16);
                nextClientHelloUtc = DateTime.MinValue;
                SendClientHelloIfDue();
            }
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
            var maxUnchunkedBytes = securityEnabled ? 850 : MaxUnchunkedDatagramBytes;
            if (envelope.Kind != TransportMessageKind.Chunk && bytes.Length > maxUnchunkedBytes)
            {
                var chunkId = envelope.SenderId + "-" + (++nextChunkId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var chunks = TransportChunkCodec.CreateChunks(envelope, chunkId, securityEnabled ? 450 : MaxChunkEnvelopeChars);
                for (var i = 0; i < chunks.Count; i++)
                {
                    SendEncodedEnvelope(chunks[i], target);
                }

                return;
            }

            SendPayload(bytes, target);
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
            AuthenticationEstablished = !securityEnabled;
            clientNonce = hostNonce = sessionId = null;
            pendingEndpoint = null;
            sendSecuritySequence = highestReceiveSecuritySequence = 0;
            receivedSecuritySequences.Clear();
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

            SendClientHelloIfDue();
            var processed = 0;
            while (udpClient.Available > 0 && processed++ < 2048)
            {
                var sender = new IPEndPoint(IPAddress.Any, 0);
                var bytes = udpClient.Receive(ref sender);
                if (!securityEnabled && isHostEndpoint)
                {
                    remoteEndpoint = sender;
                }

                if (securityEnabled)
                {
                    if (!TryUnwrapSecureDatagram(bytes, sender, out bytes)) continue;
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
            SendPayload(bytes, target);
        }

        private void SendPayload(byte[] payload, IPEndPoint target)
        {
            if (udpClient == null) return;
            if (!securityEnabled)
            {
                udpClient.Send(payload, payload.Length, target);
                return;
            }
            if (!AuthenticationEstablished || sessionId == null) return;
            var sequence = ++sendSecuritySequence;
            var sequenceBytes = BitConverter.GetBytes(sequence);
            var tag = DirectTransportSecurity.Mac(securityKey, "UDP-DATA", sessionId, sequenceBytes, payload);
            var packet = DirectTransportSecurity.UdpData + "\t"
                + Convert.ToBase64String(sessionId) + "\t"
                + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t"
                + Convert.ToBase64String(payload) + "\t"
                + Convert.ToBase64String(tag);
            var bytes = Encoding.UTF8.GetBytes(packet);
            udpClient.Send(bytes, bytes.Length, target);
        }

        private void SendClientHelloIfDue()
        {
            if (!securityEnabled || isHostEndpoint || AuthenticationEstablished || udpClient == null || remoteEndpoint == null || clientNonce == null) return;
            if (DateTime.UtcNow < nextClientHelloUtc) return;
            nextClientHelloUtc = DateTime.UtcNow.AddSeconds(1);
            var tag = DirectTransportSecurity.Mac(securityKey, "UDP-C1", clientNonce);
            SendRawSecurityPacket(DirectTransportSecurity.UdpClientHello + "\t" + Convert.ToBase64String(clientNonce) + "\t" + Convert.ToBase64String(tag), remoteEndpoint);
        }

        private bool TryUnwrapSecureDatagram(byte[] datagram, IPEndPoint sender, out byte[] payload)
        {
            payload = new byte[0];
            if (AuthenticationEstablished && remoteEndpoint != null && !EndpointEquals(sender, remoteEndpoint))
            {
                AuthenticationFailures++;
                return false;
            }
            var line = Encoding.UTF8.GetString(datagram);
            var fields = line.Split('\t');
            try
            {
                if (fields.Length == 3 && fields[0] == DirectTransportSecurity.UdpClientHello && isHostEndpoint && AllowUnauthenticatedDatagram())
                {
                    var nonce = Convert.FromBase64String(fields[1]);
                    var tag = Convert.FromBase64String(fields[2]);
                    if (nonce.Length != 16 || !DirectTransportSecurity.FixedTimeEquals(tag, DirectTransportSecurity.Mac(securityKey, "UDP-C1", nonce))) throw new InvalidDataException();
                    clientNonce = nonce;
                    hostNonce = DirectTransportSecurity.RandomBytes(16);
                    pendingEndpoint = sender;
                    var responseTag = DirectTransportSecurity.Mac(securityKey, "UDP-S1", clientNonce, hostNonce);
                    SendRawSecurityPacket(DirectTransportSecurity.UdpServerHello + "\t" + fields[1] + "\t" + Convert.ToBase64String(hostNonce) + "\t" + Convert.ToBase64String(responseTag), sender);
                    return false;
                }
                if (fields.Length == 4 && fields[0] == DirectTransportSecurity.UdpServerHello && !isHostEndpoint && clientNonce != null && remoteEndpoint != null && EndpointEquals(sender, remoteEndpoint))
                {
                    var echoedClient = Convert.FromBase64String(fields[1]);
                    var receivedHost = Convert.FromBase64String(fields[2]);
                    var tag = Convert.FromBase64String(fields[3]);
                    if (!DirectTransportSecurity.FixedTimeEquals(echoedClient, clientNonce) || receivedHost.Length != 16
                        || !DirectTransportSecurity.FixedTimeEquals(tag, DirectTransportSecurity.Mac(securityKey, "UDP-S1", clientNonce, receivedHost))) throw new InvalidDataException();
                    hostNonce = receivedHost;
                    sessionId = SessionId(clientNonce, hostNonce);
                    var finish = DirectTransportSecurity.Mac(securityKey, "UDP-C2", clientNonce, hostNonce);
                    SendRawSecurityPacket(DirectTransportSecurity.UdpClientFinish + "\t" + Convert.ToBase64String(clientNonce) + "\t" + Convert.ToBase64String(hostNonce) + "\t" + Convert.ToBase64String(finish), sender);
                    AuthenticationEstablished = true;
                    return false;
                }
                if (fields.Length == 4 && fields[0] == DirectTransportSecurity.UdpClientFinish && isHostEndpoint && pendingEndpoint != null && EndpointEquals(sender, pendingEndpoint) && clientNonce != null && hostNonce != null)
                {
                    var receivedClient = Convert.FromBase64String(fields[1]);
                    var receivedHost = Convert.FromBase64String(fields[2]);
                    var tag = Convert.FromBase64String(fields[3]);
                    if (!DirectTransportSecurity.FixedTimeEquals(receivedClient, clientNonce) || !DirectTransportSecurity.FixedTimeEquals(receivedHost, hostNonce)
                        || !DirectTransportSecurity.FixedTimeEquals(tag, DirectTransportSecurity.Mac(securityKey, "UDP-C2", clientNonce, hostNonce))) throw new InvalidDataException();
                    remoteEndpoint = sender;
                    sessionId = SessionId(clientNonce, hostNonce);
                    AuthenticationEstablished = true;
                    pendingEndpoint = null;
                    return false;
                }
                if (fields.Length == 5 && fields[0] == DirectTransportSecurity.UdpData && AuthenticationEstablished && sessionId != null)
                {
                    var receivedSession = Convert.FromBase64String(fields[1]);
                    if (!long.TryParse(fields[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var sequence)) throw new InvalidDataException();
                    var receivedPayload = Convert.FromBase64String(fields[3]);
                    var tag = Convert.FromBase64String(fields[4]);
                    if (!DirectTransportSecurity.FixedTimeEquals(receivedSession, sessionId)
                        || !DirectTransportSecurity.FixedTimeEquals(tag, DirectTransportSecurity.Mac(securityKey, "UDP-DATA", sessionId, BitConverter.GetBytes(sequence), receivedPayload))
                        || !AcceptReceiveSequence(sequence)) throw new InvalidDataException();
                    payload = receivedPayload;
                    return true;
                }
            }
            catch
            {
                AuthenticationFailures++;
                return false;
            }
            AuthenticationFailures++;
            return false;
        }

        private bool AcceptReceiveSequence(long sequence)
        {
            if (sequence <= 0 || sequence <= highestReceiveSecuritySequence - 2048 || receivedSecuritySequences.Contains(sequence)) return false;
            receivedSecuritySequences.Add(sequence);
            if (sequence > highestReceiveSecuritySequence) highestReceiveSecuritySequence = sequence;
            if (receivedSecuritySequences.Count > 4096)
            {
                receivedSecuritySequences.RemoveWhere(value => value <= highestReceiveSecuritySequence - 2048);
            }
            return true;
        }

        private bool AllowUnauthenticatedDatagram()
        {
            var now = DateTime.UtcNow;
            if ((now - unauthenticatedWindowUtc).TotalSeconds >= 1)
            {
                unauthenticatedWindowUtc = now;
                unauthenticatedDatagramsThisWindow = 0;
            }
            return ++unauthenticatedDatagramsThisWindow <= 128;
        }

        private void SendRawSecurityPacket(string packet, IPEndPoint target)
        {
            var bytes = Encoding.UTF8.GetBytes(packet);
            udpClient?.Send(bytes, bytes.Length, target);
        }

        private static byte[] SessionId(byte[] client, byte[] host)
        {
            using (var sha = SHA256.Create())
            {
                var combined = new byte[client.Length + host.Length];
                Buffer.BlockCopy(client, 0, combined, 0, client.Length);
                Buffer.BlockCopy(host, 0, combined, client.Length, host.Length);
                var full = sha.ComputeHash(combined);
                var result = new byte[16];
                Buffer.BlockCopy(full, 0, result, 0, result.Length);
                return result;
            }
        }

        private static bool EndpointEquals(IPEndPoint left, IPEndPoint right)
        {
            return left.Port == right.Port && left.Address.Equals(right.Address);
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
