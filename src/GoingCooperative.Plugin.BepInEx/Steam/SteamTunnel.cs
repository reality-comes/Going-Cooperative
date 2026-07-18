using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Steamworks;

namespace GoingCooperative.Plugin.BepInEx.Steam
{
    /// <summary>
    /// Bridges the plugin's existing loopback networking over one Steam P2P
    /// session so no port forwarding or VPN is required. Two lanes are carried
    /// per peer: the TCP save/control stream (reliable, ordered messages on
    /// channel 0) and UDP replication datagrams (unreliable messages on
    /// channel 1). Channel 2 carries tunnel control (TCP open/close).
    ///
    /// The host bridges to its real listeners on 127.0.0.1:gamePort. The client
    /// exposes a local 127.0.0.1 port pair the unchanged game networking
    /// connects to. Steam API calls happen only on the Unity main thread via
    /// Pump(); local socket I/O runs on background threads through queues.
    /// </summary>
    internal sealed class SteamTunnel : IDisposable
    {
        private const int ChannelTcp = 0;
        private const int ChannelUdp = 1;
        private const int ChannelControl = 2;
        private const byte ControlTcpOpen = 1;
        private const byte ControlTcpClose = 2;
        private const int TcpChunkBytes = 16 * 1024;
        private const int ReceiveBatch = 64;

        private readonly Action<string> log;
        private readonly ConcurrentQueue<KeyValuePair<int, byte[]>> steamSendQueue = new ConcurrentQueue<KeyValuePair<int, byte[]>>();
        private readonly ConcurrentQueue<byte[]> tcpWriteQueue = new ConcurrentQueue<byte[]>();
        private readonly AutoResetEvent tcpWriteSignal = new AutoResetEvent(false);
        private readonly IntPtr[] receiveBuffer = new IntPtr[ReceiveBatch];

        private Callback<SteamNetworkingMessagesSessionRequest_t>? sessionRequestCallback;
        private Func<ulong, bool>? acceptPredicate;
        private SteamNetworkingIdentity remoteIdentity;
        private bool remoteKnown;
        private bool hostMode;
        private bool running;
        private int gamePort;
        private int localBridgePort;

        private TcpListener? clientTcpListener;
        private TcpClient? tcpConnection;
        private NetworkStream? tcpStream;
        private Thread? tcpReadThread;
        private Thread? tcpWriteThread;
        private Thread? tcpAcceptThread;
        private volatile bool tcpOpen;

        private UdpClient? udpLocal;
        private Thread? udpReadThread;
        private IPEndPoint? clientGameEndpoint;

        private long udpForwardedToSteam;
        private long udpForwardedFromSteam;
        private long tcpBytesToSteam;
        private long tcpBytesFromSteam;
        private long steamSendFailures;
        private long nextSendFailureLog = 1L;
        private int pendingReliableChunks;
        private KeyValuePair<int, byte[]>? reliableRetry;
        private const int MaxPendingReliableChunks = 192;

        public SteamTunnel(Action<string> logSink)
        {
            log = logSink;
        }

        public bool IsRunning => running;

        public bool RemoteKnown => remoteKnown;

        public int LocalBridgePort => localBridgePort;

        public string Detail { get; private set; } = "Tunnel idle.";

        public ulong RemoteSteamId => remoteKnown ? remoteIdentity.GetSteamID64() : 0UL;

        /// <summary>Host: accept one Steam peer and bridge it to the local game
        /// listeners on 127.0.0.1:port.</summary>
        public void StartHost(int port, Func<ulong, bool> accept)
        {
            Stop();
            hostMode = true;
            gamePort = port;
            acceptPredicate = accept;
            running = true;
            remoteKnown = false;
            sessionRequestCallback ??= Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            StartUdpBridge(bindPort: 0);
            Detail = "Waiting for a Steam peer.";
            log("Going Cooperative Steam tunnel hosting gamePort=" + port.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Client: open a session to the host and expose a loopback
        /// port pair for the unchanged game networking to connect to.</summary>
        public bool StartClient(ulong hostSteamId, out int localPort, out string detail)
        {
            Stop();
            hostMode = false;
            running = true;
            remoteIdentity = new SteamNetworkingIdentity();
            remoteIdentity.SetSteamID64(hostSteamId);
            remoteKnown = true;

            localPort = 0;
            if (!TryBindClientLoopbackPair(out detail))
            {
                running = false;
                Detail = detail;
                return false;
            }

            localPort = localBridgePort;
            StartUdpBridge(bindPort: localBridgePort);
            tcpAcceptThread = new Thread(ClientTcpAcceptLoop) { IsBackground = true, Name = "GC Steam Tunnel Accept" };
            tcpAcceptThread.Start();
            Detail = "Tunnel ready on 127.0.0.1:" + localBridgePort.ToString(CultureInfo.InvariantCulture);
            detail = Detail;
            log("Going Cooperative Steam tunnel client host=" + hostSteamId.ToString(CultureInfo.InvariantCulture)
                + " bridgePort=" + localBridgePort.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>Main-thread pump: drains outgoing queues into Steam and
        /// dispatches received Steam messages. Call once per frame.</summary>
        public void Pump()
        {
            if (!running)
            {
                return;
            }

            if (reliableRetry.HasValue)
            {
                if (TrySendReliable(reliableRetry.Value))
                {
                    reliableRetry = null;
                }
            }

            if (!reliableRetry.HasValue)
            {
                while (steamSendQueue.TryDequeue(out var pending))
                {
                    if (pending.Key == ChannelUdp)
                    {
                        SendUnreliable(pending.Value);
                        continue;
                    }

                    if (!TrySendReliable(pending))
                    {
                        // The Steam send buffer is full (large save transfer).
                        // Hold this chunk and let the local reader thread block
                        // on the pending counter until the buffer drains.
                        reliableRetry = pending;
                        break;
                    }
                }
            }

            ReceiveChannel(ChannelControl);
            ReceiveChannel(ChannelTcp);
            ReceiveChannel(ChannelUdp);
        }

        public string BuildStatusSummary()
        {
            return "steamTunnel remote=" + (remoteKnown ? remoteIdentity.GetSteamID64().ToString(CultureInfo.InvariantCulture) : "<none>")
                + " tcpOpen=" + tcpOpen
                + " tcpBytes tx=" + tcpBytesToSteam.ToString(CultureInfo.InvariantCulture)
                + " rx=" + tcpBytesFromSteam.ToString(CultureInfo.InvariantCulture)
                + " udp tx=" + udpForwardedToSteam.ToString(CultureInfo.InvariantCulture)
                + " rx=" + udpForwardedFromSteam.ToString(CultureInfo.InvariantCulture)
                + " sendFailures=" + steamSendFailures.ToString(CultureInfo.InvariantCulture);
        }

        public void Stop()
        {
            running = false;
            tcpOpen = false;
            try { tcpConnection?.Close(); } catch { }
            try { clientTcpListener?.Stop(); } catch { }
            try { udpLocal?.Close(); } catch { }
            tcpConnection = null;
            tcpStream = null;
            clientTcpListener = null;
            udpLocal = null;
            clientGameEndpoint = null;
            tcpWriteSignal.Set();
            while (steamSendQueue.TryDequeue(out _)) { }
            while (tcpWriteQueue.TryDequeue(out _)) { }
            reliableRetry = null;
            Interlocked.Exchange(ref pendingReliableChunks, 0);
            if (remoteKnown)
            {
                try
                {
                    SteamNetworkingMessages.CloseSessionWithUser(ref remoteIdentity);
                }
                catch { }
            }

            remoteKnown = false;
            localBridgePort = 0;
            Detail = "Tunnel stopped.";
        }

        public void Dispose()
        {
            Stop();
            sessionRequestCallback?.Dispose();
            sessionRequestCallback = null;
        }

        private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request)
        {
            if (!running || !hostMode)
            {
                return;
            }

            var requester = request.m_identityRemote;
            var steamId = requester.GetSteamID64();
            if (remoteKnown && remoteIdentity.GetSteamID64() != steamId)
            {
                log("Going Cooperative Steam tunnel refused extra peer steamId=" + steamId.ToString(CultureInfo.InvariantCulture));
                SteamNetworkingMessages.CloseSessionWithUser(ref requester);
                return;
            }

            var accept = acceptPredicate;
            if (accept != null && !accept(steamId))
            {
                log("Going Cooperative Steam tunnel rejected peer steamId=" + steamId.ToString(CultureInfo.InvariantCulture));
                SteamNetworkingMessages.CloseSessionWithUser(ref requester);
                return;
            }

            SteamNetworkingMessages.AcceptSessionWithUser(ref requester);
            remoteIdentity = requester;
            remoteKnown = true;
            Detail = "Steam peer connected (" + steamId.ToString(CultureInfo.InvariantCulture) + ").";
            log("Going Cooperative Steam tunnel accepted peer steamId=" + steamId.ToString(CultureInfo.InvariantCulture));
        }

        private void ReceiveChannel(int channel)
        {
            int count;
            try
            {
                count = SteamNetworkingMessages.ReceiveMessagesOnChannel(channel, receiveBuffer, ReceiveBatch);
            }
            catch (Exception ex)
            {
                Fail("Steam receive failed: " + ex.GetType().Name);
                return;
            }

            for (var i = 0; i < count; i++)
            {
                var pointer = receiveBuffer[i];
                try
                {
                    var message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                    var sender = message.m_identityPeer.GetSteamID64();
                    if (!remoteKnown || sender != remoteIdentity.GetSteamID64())
                    {
                        continue;
                    }

                    var data = new byte[message.m_cbSize];
                    Marshal.Copy(message.m_pData, data, 0, message.m_cbSize);
                    DispatchIncoming(channel, data);
                }
                finally
                {
                    SteamNetworkingMessage_t.Release(pointer);
                }
            }
        }

        private void DispatchIncoming(int channel, byte[] data)
        {
            switch (channel)
            {
                case ChannelControl:
                    HandleControl(data);
                    break;
                case ChannelTcp:
                    tcpBytesFromSteam += data.Length;
                    tcpWriteQueue.Enqueue(data);
                    tcpWriteSignal.Set();
                    EnsureHostTcpBridge();
                    break;
                case ChannelUdp:
                    ForwardUdpToLocal(data);
                    break;
            }
        }

        private void HandleControl(byte[] data)
        {
            if (data.Length < 1)
            {
                return;
            }

            if (data[0] == ControlTcpOpen && hostMode)
            {
                EnsureHostTcpBridge();
            }
            else if (data[0] == ControlTcpClose)
            {
                CloseTcpBridge(notifyPeer: false);
            }
        }

        private void EnsureHostTcpBridge()
        {
            if (!hostMode || tcpOpen || !running)
            {
                return;
            }

            try
            {
                var connection = new TcpClient { NoDelay = true };
                connection.Connect(IPAddress.Loopback, gamePort);
                AttachTcpConnection(connection);
                log("Going Cooperative Steam tunnel host TCP bridge opened port=" + gamePort.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Fail("Host TCP bridge connect failed: " + ex.GetType().Name + ":" + ex.Message);
            }
        }

        private void ClientTcpAcceptLoop()
        {
            var listener = clientTcpListener;
            if (listener == null)
            {
                return;
            }

            while (running)
            {
                TcpClient accepted;
                try
                {
                    accepted = listener.AcceptTcpClient();
                }
                catch
                {
                    return;
                }

                if (!running)
                {
                    try { accepted.Close(); } catch { }
                    return;
                }

                CloseTcpBridge(notifyPeer: true);
                accepted.NoDelay = true;
                AttachTcpConnection(accepted);
                steamSendQueue.Enqueue(new KeyValuePair<int, byte[]>(ChannelControl, new[] { ControlTcpOpen }));
                log("Going Cooperative Steam tunnel client TCP bridge accepted local connection.");
            }
        }

        private void AttachTcpConnection(TcpClient connection)
        {
            tcpConnection = connection;
            tcpStream = connection.GetStream();
            tcpOpen = true;
            tcpReadThread = new Thread(TcpReadLoop) { IsBackground = true, Name = "GC Steam Tunnel TCP Read" };
            tcpWriteThread = new Thread(TcpWriteLoop) { IsBackground = true, Name = "GC Steam Tunnel TCP Write" };
            tcpReadThread.Start();
            tcpWriteThread.Start();
        }

        private void TcpReadLoop()
        {
            var stream = tcpStream;
            if (stream == null)
            {
                return;
            }

            var buffer = new byte[TcpChunkBytes];
            try
            {
                while (running && tcpOpen)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    var chunk = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                    tcpBytesToSteam += read;
                    while (running && tcpOpen && System.Threading.Volatile.Read(ref pendingReliableChunks) > MaxPendingReliableChunks)
                    {
                        Thread.Sleep(5);
                    }

                    Interlocked.Increment(ref pendingReliableChunks);
                    steamSendQueue.Enqueue(new KeyValuePair<int, byte[]>(ChannelTcp, chunk));
                }
            }
            catch
            {
                // Socket teardown paths surface as read exceptions; fall through.
            }

            if (running && tcpOpen)
            {
                CloseTcpBridge(notifyPeer: true);
            }
        }

        private void TcpWriteLoop()
        {
            var stream = tcpStream;
            if (stream == null)
            {
                return;
            }

            try
            {
                while (running && tcpOpen)
                {
                    while (tcpWriteQueue.TryDequeue(out var data))
                    {
                        stream.Write(data, 0, data.Length);
                    }

                    stream.Flush();
                    tcpWriteSignal.WaitOne(50);
                }
            }
            catch
            {
                if (running && tcpOpen)
                {
                    CloseTcpBridge(notifyPeer: true);
                }
            }
        }

        private void CloseTcpBridge(bool notifyPeer)
        {
            if (!tcpOpen)
            {
                return;
            }

            tcpOpen = false;
            try { tcpConnection?.Close(); } catch { }
            tcpConnection = null;
            tcpStream = null;
            tcpWriteSignal.Set();
            while (tcpWriteQueue.TryDequeue(out _)) { }
            if (notifyPeer)
            {
                steamSendQueue.Enqueue(new KeyValuePair<int, byte[]>(ChannelControl, new[] { ControlTcpClose }));
            }

            log("Going Cooperative Steam tunnel TCP bridge closed notifyPeer=" + notifyPeer);
        }

        private bool TryBindClientLoopbackPair(out string detail)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                TcpListener? listener = null;
                try
                {
                    listener = new TcpListener(IPAddress.Loopback, 0);
                    listener.Start(1);
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                    clientTcpListener = listener;
                    udpLocal = udp;
                    localBridgePort = port;
                    detail = string.Empty;
                    return true;
                }
                catch
                {
                    try { listener?.Stop(); } catch { }
                }
            }

            detail = "Could not reserve a loopback port pair for the Steam tunnel.";
            return false;
        }

        private void StartUdpBridge(int bindPort)
        {
            if (udpLocal == null)
            {
                udpLocal = new UdpClient(new IPEndPoint(IPAddress.Loopback, bindPort));
            }

            udpReadThread = new Thread(UdpReadLoop) { IsBackground = true, Name = "GC Steam Tunnel UDP" };
            udpReadThread.Start();
        }

        private void UdpReadLoop()
        {
            var socket = udpLocal;
            if (socket == null)
            {
                return;
            }

            while (running)
            {
                try
                {
                    var sender = new IPEndPoint(IPAddress.Any, 0);
                    var data = socket.Receive(ref sender);
                    if (!hostMode)
                    {
                        clientGameEndpoint = sender;
                    }

                    udpForwardedToSteam++;
                    steamSendQueue.Enqueue(new KeyValuePair<int, byte[]>(ChannelUdp, data));
                }
                catch
                {
                    if (!running)
                    {
                        return;
                    }
                }
            }
        }

        private void ForwardUdpToLocal(byte[] data)
        {
            var socket = udpLocal;
            if (socket == null)
            {
                return;
            }

            try
            {
                if (hostMode)
                {
                    socket.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, gamePort));
                    udpForwardedFromSteam++;
                }
                else
                {
                    var target = clientGameEndpoint;
                    if (target != null)
                    {
                        socket.Send(data, data.Length, target);
                        udpForwardedFromSteam++;
                    }
                }
            }
            catch
            {
                // Unreliable lane: drops are tolerated by the replication protocol.
            }
        }

        private void SendUnreliable(byte[] data)
        {
            if (!remoteKnown)
            {
                return;
            }

            try
            {
                SendMessage(ChannelUdp, data, Constants.k_nSteamNetworkingSend_UnreliableNoNagle);
            }
            catch (Exception ex)
            {
                Fail("Steam send failed: " + ex.GetType().Name);
            }
        }

        private bool TrySendReliable(KeyValuePair<int, byte[]> pending)
        {
            if (!remoteKnown)
            {
                // Drop reliable traffic queued before the peer is known; the
                // save-transfer protocol only starts after the session exists.
                if (pending.Key == ChannelTcp)
                {
                    Interlocked.Decrement(ref pendingReliableChunks);
                }

                return true;
            }

            EResult result;
            try
            {
                result = SendMessage(
                    pending.Key,
                    pending.Value,
                    Constants.k_nSteamNetworkingSend_ReliableNoNagle | Constants.k_nSteamNetworkingSend_AutoRestartBrokenSession);
            }
            catch (Exception ex)
            {
                Fail("Steam send failed: " + ex.GetType().Name);
                return true;
            }

            if (result == EResult.k_EResultOK)
            {
                if (pending.Key == ChannelTcp)
                {
                    Interlocked.Decrement(ref pendingReliableChunks);
                }

                return true;
            }

            steamSendFailures++;
            if (steamSendFailures >= nextSendFailureLog)
            {
                nextSendFailureLog = steamSendFailures * 2L;
                log("Going Cooperative Steam tunnel reliable send deferred result=" + result
                    + " failures=" + steamSendFailures.ToString(CultureInfo.InvariantCulture));
            }

            return false;
        }

        private EResult SendMessage(int channel, byte[] data, int flags)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                return SteamNetworkingMessages.SendMessageToUser(
                    ref remoteIdentity,
                    handle.AddrOfPinnedObject(),
                    (uint)data.Length,
                    flags,
                    channel);
            }
            finally
            {
                handle.Free();
            }
        }

        private void Fail(string reason)
        {
            Detail = reason;
            log("Going Cooperative Steam tunnel failure: " + reason);
            Stop();
        }
    }
}
