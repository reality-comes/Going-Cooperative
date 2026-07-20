using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GoingCooperative.Core;
using NSMedieval;

namespace GoingCooperative.Plugin.BepInEx
{
    internal sealed class MultiplayerSaveTransfer : IDisposable
    {
        private const string Magic = "GOING_COOPERATIVE_CONTROL_V3";
        private readonly object stateLock = new object();
        private readonly object writeLock = new object();
        private TcpListener? listener;
        private TcpClient? client;
        private Stream? controlStream;
        private Thread? worker;
        private string saveRoot = string.Empty;
        private volatile bool stopping;
        private volatile bool hostMode;
        private volatile bool localReady;
        private volatile bool remoteReady;
        private volatile bool localLoaded;
        private volatile bool remoteLoaded;
        private volatile bool resyncCaptureRequested;
        private volatile int loadGeneration;
        private volatile int resumeGeneration;
        private volatile int epoch;
        private string phase = "Idle";
        private string detail = "Select a save to host.";
        private float progress;
        private string receivedSavePath = string.Empty;
        private string receivedVillageName = string.Empty;
        private Exception? failure;
        private bool directSecurityEnabled;
        private byte[] directSecurityKey = new byte[0];

        public string Phase { get { lock (stateLock) return phase; } }
        public string Detail { get { lock (stateLock) return detail; } }
        public float Progress { get { lock (stateLock) return progress; } }
        public bool TransferComplete { get { return Phase == "Connected" || Phase == "Playing"; } }
        public int LoadGeneration { get { return loadGeneration; } }
        public int ResumeGeneration { get { return resumeGeneration; } }
        public int Epoch { get { return epoch; } }
        public bool ResyncCaptureRequested { get { return resyncCaptureRequested; } }
        public string ReceivedSavePath { get { lock (stateLock) return receivedSavePath; } }
        public string ReceivedVillageName { get { lock (stateLock) return receivedVillageName; } }
        public Exception? Failure { get { return failure; } }

        public void StartHost(int port, VillageSaveInfo save, bool securityEnabled = false, string sessionCode = "")
        {
            if (save == null) throw new ArgumentNullException(nameof(save));
            Stop();
            hostMode = true;
            stopping = false;
            ConfigureSecurity(securityEnabled, sessionCode);
            SetState("Waiting for Connections", "Listening for a client on TCP port " + port.ToString(CultureInfo.InvariantCulture) + ".", 0f);
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start(1);
            worker = new Thread(() => HostWorker(save)) { IsBackground = true, Name = "Going Cooperative Control Host" };
            worker.Start();
        }

        public void StartClient(string host, int port, string clientSaveRoot, bool securityEnabled = false, string sessionCode = "")
        {
            Stop();
            hostMode = false;
            saveRoot = clientSaveRoot;
            stopping = false;
            ConfigureSecurity(securityEnabled, sessionCode);
            SetState("Connecting", "Opening the persistent multiplayer control channel.", 0f);
            worker = new Thread(() => ClientWorker(host, port)) { IsBackground = true, Name = "Going Cooperative Control Client" };
            worker.Start();
        }

        public void MarkReadyToPlay()
        {
            if (!TransferComplete || client == null) return;
            localReady = true;
            SendCommand("READY", epoch);
            SetDetail(hostMode ? "Host ready. Waiting for the client to press Play." : "Ready. Waiting for the host to start the game.");
            if (hostMode && remoteReady) SendLoadCommand("LOAD");
        }

        public bool RequestFullResync(out string error)
        {
            if (hostMode) { error = "Only the client can request a full resync."; return false; }
            if (client == null || !client.Connected || resumeGeneration == 0) { error = "The multiplayer control channel is not ready."; return false; }
            if (Phase != "Playing") { error = "A load or resync operation is already in progress."; return false; }
            SetState("Requesting Resync", "Asking the host for a fresh checkpoint.", 0f);
            SendCommand("RESYNC_REQUEST", epoch);
            error = string.Empty;
            return true;
        }

        public void QueueResyncCheckpoint(VillageSaveInfo save)
        {
            if (!hostMode || save == null) throw new InvalidOperationException("Only the host can send a resync checkpoint.");
            resyncCaptureRequested = false;
            var sendThread = new Thread(() =>
            {
                try { SendBundle(save, true); }
                catch (Exception ex) { Fail(ex); }
            }) { IsBackground = true, Name = "Going Cooperative Resync Sender" };
            sendThread.Start();
        }

        public void RejectResync(string reason)
        {
            if (!hostMode) return;
            resyncCaptureRequested = false;
            SendCommand("RESYNC_FAILED", epoch, reason);
            resumeGeneration++;
            SetState("Playing", "Resync failed; continuing the existing session. " + reason, 1f);
        }

        public void NotifyNativeLoadFinished()
        {
            localLoaded = true;
            SendCommand("LOADED", epoch);
            SetState("Waiting for Peer", "Native loading complete. Waiting for the other player.", 1f);
            if (hostMode && remoteLoaded) SendResume();
        }

        public void ReportLoadFailure(string reason)
        {
            try { SendCommand("LOAD_FAILED", epoch, reason); } catch { }
            SetState("Failed", "Native checkpoint load failed. " + reason, 0f);
        }

        public void Stop()
        {
            stopping = true;
            try { client?.Close(); } catch { }
            try { listener?.Stop(); } catch { }
            client = null;
            controlStream = null;
            listener = null;
            localReady = remoteReady = localLoaded = remoteLoaded = resyncCaptureRequested = false;
            loadGeneration = resumeGeneration = epoch = 0;
            receivedSavePath = receivedVillageName = string.Empty;
            failure = null;
            SetState("Idle", "Select a save to host.", 0f);
        }

        public void Dispose() { Stop(); }

        private void ConfigureSecurity(bool enabled, string sessionCode)
        {
            directSecurityEnabled = enabled;
            if (!enabled)
            {
                directSecurityKey = new byte[0];
                return;
            }
            if (!DirectTransportSecurity.TryDeriveKey(sessionCode, out directSecurityKey, out var error))
            {
                throw new ArgumentException(error, nameof(sessionCode));
            }
        }

        private void HostWorker(VillageSaveInfo initialSave)
        {
            try
            {
                while (!stopping && controlStream == null)
                {
                    client = listener!.AcceptTcpClient();
                    client.NoDelay = true;
                    var raw = client.GetStream();
                    if (!directSecurityEnabled)
                    {
                        controlStream = raw;
                        break;
                    }
                    try
                    {
                        client.ReceiveTimeout = 5000;
                        client.SendTimeout = 5000;
                        controlStream = DirectTransportSecurity.AuthenticateTcpHost(raw, directSecurityKey);
                        client.ReceiveTimeout = 0;
                        client.SendTimeout = 0;
                    }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is SocketException)
                    {
                        try { client.Close(); } catch { }
                        client = null;
                        controlStream = null;
                        SetState("Waiting for Connections", "Rejected an unauthenticated connection; still waiting for the player with the session code.", 0f);
                    }
                }
                if (stopping || client == null || controlStream == null) return;
                SendCommand("HELLO", 0, Magic);
                SendBundle(initialSave, false);
                ReadHostCommands(new BinaryReader(controlStream, Encoding.UTF8, true));
            }
            catch (Exception ex) { Fail(ex); }
        }

        private void ClientWorker(string host, int port)
        {
            try
            {
                client = new TcpClient { NoDelay = true };
                if (directSecurityEnabled)
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                }
                client.Connect(host, port);
                var raw = client.GetStream();
                if (directSecurityEnabled)
                {
                    try { controlStream = DirectTransportSecurity.AuthenticateTcpClient(raw, directSecurityKey); }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is SocketException)
                    {
                        throw new InvalidDataException("Direct connection authentication failed. Confirm the host address and session code, then try again.", ex);
                    }
                }
                else
                {
                    controlStream = raw;
                }
                client.ReceiveTimeout = 0;
                client.SendTimeout = 0;
                var reader = new BinaryReader(controlStream, Encoding.UTF8, true);
                if (reader.ReadString() != "HELLO" || reader.ReadInt32() != 0 || reader.ReadString() != Magic)
                    throw new InvalidDataException("The host control protocol is incompatible.");
                ReadClientCommands(reader);
            }
            catch (Exception ex) { Fail(ex); }
        }

        private void ReadHostCommands(BinaryReader reader)
        {
            while (!stopping)
            {
                var command = reader.ReadString();
                var commandEpoch = reader.ReadInt32();
                if (command == "VERIFIED")
                {
                    if (commandEpoch != epoch) continue;
                    SetState("Connected", commandEpoch == 0 ? "Save verified. Press Play when ready." : "Resync checkpoint verified.", 1f);
                    if (commandEpoch > 0) SendLoadCommand("RESYNC_LOAD");
                }
                else if (command == "READY")
                {
                    remoteReady = true;
                    SetDetail(localReady ? "Both players ready. Starting." : "Client ready. Press Play when ready.");
                    if (localReady) SendLoadCommand("LOAD");
                }
                else if (command == "RESYNC_REQUEST" && commandEpoch == epoch)
                {
                    if (Phase != "Playing") continue;
                    resyncCaptureRequested = true;
                    SetState("Capturing Checkpoint", "Client requested a full save reload.", 0f);
                }
                else if (command == "LOADED" && commandEpoch == epoch)
                {
                    remoteLoaded = true;
                    if (localLoaded) SendResume();
                }
                else if (command == "LOAD_FAILED" && commandEpoch == epoch)
                {
                    SetState("Failed", "Client failed to load the checkpoint. " + reader.ReadString(), 0f);
                }
            }
        }

        private void ReadClientCommands(BinaryReader reader)
        {
            while (!stopping)
            {
                var command = reader.ReadString();
                var commandEpoch = reader.ReadInt32();
                if (command == "BUNDLE") ReceiveBundle(reader, commandEpoch);
                else if ((command == "LOAD" || command == "RESYNC_LOAD") && commandEpoch == epoch) BeginLoad();
                else if (command == "RESUME" && commandEpoch == epoch)
                {
                    resumeGeneration++;
                    SetState("Playing", "Resync complete. Replication resumed.", 1f);
                }
                else if (command == "RESYNC_FAILED" && commandEpoch == epoch)
                {
                    var reason = reader.ReadString();
                    resumeGeneration++;
                    SetState("Playing", "Host could not create a resync checkpoint. " + reason, 1f);
                }
                else if (command == "LOAD_FAILED" && commandEpoch == epoch)
                {
                    SetState("Failed", "Host failed to load the checkpoint. " + reader.ReadString(), 0f);
                }
            }
        }

        private void SendBundle(VillageSaveInfo save, bool isResync)
        {
            if (client == null) throw new IOException("Control channel is not connected.");
            WaitForSaveBundleReady(save.FilePath, TimeSpan.FromSeconds(10));
            if (isResync)
            {
                epoch++;
                localLoaded = remoteLoaded = false;
            }
            var bundleEpoch = epoch;
            var files = GetSaveBundle(save.FilePath);
            lock (writeLock)
            {
                if (controlStream == null) throw new InvalidOperationException("The control channel is not ready.");
                var writer = new BinaryWriter(controlStream, Encoding.UTF8, true);
                writer.Write("BUNDLE"); writer.Write(bundleEpoch);
                writer.Write(Path.GetFileName(save.FilePath));
                writer.Write(save.VillageName ?? string.Empty);
                writer.Write(files.Count);
                long total = 0; foreach (var path in files) total += new FileInfo(path).Length;
                writer.Write(total);
                long sent = 0;
                var buffer = new byte[64 * 1024];
                foreach (var path in files)
                {
                    var info = new FileInfo(path);
                    writer.Write(Path.GetFileName(path)); writer.Write(info.Length); writer.Write(ComputeSha256(path));
                    using (var input = File.OpenRead(path))
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            writer.Write(buffer, 0, read); sent += read;
                            SetState(isResync ? "Transferring Resync" : "Transfer Load", Path.GetFileName(path), (float)sent / total);
                        }
                    }
                }
                writer.Flush();
            }
        }

        private void ReceiveBundle(BinaryReader reader, int bundleEpoch)
        {
            if (bundleEpoch < epoch) throw new InvalidDataException("Received a stale checkpoint epoch.");
            epoch = bundleEpoch;
            var primaryName = SafeFileName(reader.ReadString());
            var villageName = SafeFileName(reader.ReadString());
            var count = reader.ReadInt32();
            var total = reader.ReadInt64();
            if (count < 1 || count > 16 || total < 1 || total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Invalid save manifest.");
            var stem = "GoingCooperative_" + (bundleEpoch == 0 ? "Start_" : "Resync_" + bundleEpoch + "_") + DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture);
            var targetPrimary = stem + ".sav";
            var root = Path.Combine(saveRoot, villageName);
            Directory.CreateDirectory(root);
            long received = 0;
            var buffer = new byte[64 * 1024];
            for (var i = 0; i < count; i++)
            {
                var sourceName = SafeFileName(reader.ReadString());
                var targetName = MapBundleName(sourceName, primaryName, targetPrimary);
                var length = reader.ReadInt64();
                var hash = reader.ReadString();
                if (length < 0 || length > total) throw new InvalidDataException("Invalid file length.");
                var path = Path.Combine(root, targetName);
                var partialPath = path + ".part";
                using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    long remaining = length;
                    while (remaining > 0)
                    {
                        var read = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read <= 0) throw new EndOfStreamException("Save transfer ended early.");
                        output.Write(buffer, 0, read); remaining -= read; received += read;
                        SetState(bundleEpoch == 0 ? "Receive Load" : "Receiving Resync", sourceName, (float)received / total);
                    }
                }
                if (ComputeSha256(partialPath) != hash) throw new InvalidDataException("Checksum mismatch for " + sourceName + ".");
                File.Move(partialPath, path);
            }
            lock (stateLock)
            {
                receivedSavePath = Path.Combine(root, targetPrimary);
                receivedVillageName = villageName;
            }
            SendCommand("VERIFIED", bundleEpoch);
            SetState("Connected", bundleEpoch == 0 ? "Save verified. Press Play when ready." : "Resync verified. Waiting for synchronized reload.", 1f);
        }

        private void SendLoadCommand(string command)
        {
            SendCommand(command, epoch);
            BeginLoad();
        }

        private void BeginLoad()
        {
            localLoaded = remoteLoaded = false;
            loadGeneration++;
            SetState("Loading", "Loading authoritative checkpoint.", 1f);
        }

        private void SendResume()
        {
            SendCommand("RESUME", epoch);
            resumeGeneration++;
            SetState("Playing", "Both players loaded. Replication resumed.", 1f);
        }

        private void SendCommand(string command, int commandEpoch, string? payload = null)
        {
            if (client == null) throw new IOException("Control channel is not connected.");
            lock (writeLock)
            {
                if (controlStream == null) throw new InvalidOperationException("The control channel is not ready.");
                var writer = new BinaryWriter(controlStream, Encoding.UTF8, true);
                writer.Write(command); writer.Write(commandEpoch);
                if (payload != null) writer.Write(payload);
                writer.Flush();
            }
        }

        private static string MapBundleName(string source, string primary, string targetPrimary)
        {
            if (source.Equals(primary, StringComparison.OrdinalIgnoreCase)) return targetPrimary;
            if (source.Equals(primary + ".meta", StringComparison.OrdinalIgnoreCase)) return targetPrimary + ".meta";
            if (source.Equals(Path.ChangeExtension(primary, ".gmevents"), StringComparison.OrdinalIgnoreCase)) return Path.ChangeExtension(targetPrimary, ".gmevents");
            throw new InvalidDataException("Unexpected save companion " + source + ".");
        }

        private static List<string> GetSaveBundle(string primary)
        {
            if (!File.Exists(primary)) throw new FileNotFoundException("Selected save is missing.", primary);
            var files = new List<string> { primary };
            if (!File.Exists(primary + ".meta")) throw new FileNotFoundException("Save metadata is missing.", primary + ".meta");
            files.Add(primary + ".meta");
            var eventsFile = Path.ChangeExtension(primary, ".gmevents");
            if (File.Exists(eventsFile)) files.Add(eventsFile);
            return files;
        }

        private static void WaitForSaveBundleReady(string primary, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            Exception? lastError = null;
            var eventsPath = Path.ChangeExtension(primary, ".gmevents");
            DateTime? fingerprintStableSince = null;
            string lastFingerprint = string.Empty;
            var stableSamples = 0;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(primary) && File.Exists(primary + ".meta"))
                    {
                        using (File.Open(primary, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                        using (File.Open(primary + ".meta", FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                        var eventsExist = File.Exists(eventsPath);
                        if (eventsExist)
                        {
                            using (File.Open(eventsPath, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                        }

                        var primaryInfo = new FileInfo(primary);
                        var metaInfo = new FileInfo(primary + ".meta");
                        var eventsInfo = eventsExist ? new FileInfo(eventsPath) : null;
                        var fingerprint = primaryInfo.Length.ToString(CultureInfo.InvariantCulture)
                            + ":" + primaryInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                            + "|" + metaInfo.Length.ToString(CultureInfo.InvariantCulture)
                            + ":" + metaInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                            + "|" + (eventsInfo == null
                                ? "absent"
                                : eventsInfo.Length.ToString(CultureInfo.InvariantCulture)
                                    + ":" + eventsInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
                        if (string.Equals(fingerprint, lastFingerprint, StringComparison.Ordinal))
                        {
                            stableSamples++;
                        }
                        else
                        {
                            lastFingerprint = fingerprint;
                            stableSamples = 1;
                            fingerprintStableSince = DateTime.UtcNow;
                        }

                        // Native save companions are written separately. Require a short
                        // quiet window so an optional .gmevents file cannot appear just
                        // after the primary and metadata files were accepted.
                        if (stableSamples >= 2
                            && fingerprintStableSince.HasValue
                            && DateTime.UtcNow - fingerprintStableSince.Value >= TimeSpan.FromMilliseconds(500))
                        {
                            return;
                        }
                    }
                    else
                    {
                        fingerprintStableSince = null;
                        lastFingerprint = string.Empty;
                        stableSamples = 0;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    fingerprintStableSince = null;
                    lastFingerprint = string.Empty;
                    stableSamples = 0;
                }
                Thread.Sleep(50);
            }
            throw new IOException("Timed out waiting for the native save checkpoint to finish writing.", lastError);
        }

        private static string SafeFileName(string value)
        {
            var name = Path.GetFileName(value ?? string.Empty);
            if (name.Length == 0 || name != value || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("Invalid save name.");
            return name;
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(stream); var builder = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        private void SetState(string value, string message, float valueProgress) { lock (stateLock) { phase = value; detail = message; progress = valueProgress; } }
        private void SetDetail(string message) { lock (stateLock) detail = message; }
        private void Fail(Exception ex)
        {
            if (stopping) return;
            failure = ex;
            SetState("Failed", ex.GetType().Name + ": " + ex.Message, 0f);
            try { client?.Close(); } catch { }
        }
    }
}
