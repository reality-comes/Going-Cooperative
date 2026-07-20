using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GoingCooperative.Core
{
    public static class DirectTransportSecurity
    {
        public const string UdpClientHello = "GCOOP-AUTH-C1";
        public const string UdpServerHello = "GCOOP-AUTH-S1";
        public const string UdpClientFinish = "GCOOP-AUTH-C2";
        public const string UdpData = "GCOOP-AUTH-D1";
        private static readonly byte[] TcpMagic = Encoding.ASCII.GetBytes("GCOOP-TCP-AUTH-1");

        public static string GenerateSessionCode()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return ToHex(bytes);
        }

        public static bool TryDeriveKey(string code, out byte[] key, out string error)
        {
            var normalized = NormalizeCode(code);
            if (normalized.Length != 32)
            {
                key = new byte[0];
                error = "Session code must be the 32-character code shown by the host.";
                return false;
            }
            for (var i = 0; i < normalized.Length; i++)
            {
                var c = normalized[i];
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')))
                {
                    key = new byte[0];
                    error = "Session code contains an invalid character.";
                    return false;
                }
            }
            using (var sha = SHA256.Create())
            {
                key = sha.ComputeHash(Encoding.UTF8.GetBytes("GOING-COOPERATIVE-DIRECT-SECURITY-V1\0" + normalized));
            }
            error = string.Empty;
            return true;
        }

        public static string NormalizeCode(string code)
        {
            return (code ?? string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).Trim().ToUpperInvariant();
        }

        public static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return bytes;
        }

        public static byte[] Mac(byte[] key, string domain, params byte[][] parts)
        {
            using (var hmac = new HMACSHA256(key))
            using (var memory = new MemoryStream())
            {
                var prefix = Encoding.UTF8.GetBytes(domain);
                memory.Write(prefix, 0, prefix.Length);
                for (var i = 0; i < parts.Length; i++)
                {
                    var part = parts[i] ?? new byte[0];
                    WriteInt32(memory, part.Length);
                    memory.Write(part, 0, part.Length);
                }
                return hmac.ComputeHash(memory.ToArray());
            }
        }

        public static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            var difference = 0;
            for (var i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        public static Stream AuthenticateTcpHost(Stream raw, byte[] key)
        {
            var clientNonce = ReadHandshake(raw, TcpMagic, 16, 32);
            var expectedC1 = Mac(key, "TCP-C1", clientNonce.Item1);
            if (!FixedTimeEquals(clientNonce.Item2, expectedC1)) throw new InvalidDataException("Client session-code authentication failed.");
            var hostNonce = RandomBytes(16);
            var s1 = Mac(key, "TCP-S1", clientNonce.Item1, hostNonce);
            WriteHandshake(raw, TcpMagic, hostNonce, s1);
            var finish = ReadExact(raw, 32);
            var expectedC2 = Mac(key, "TCP-C2", clientNonce.Item1, hostNonce);
            if (!FixedTimeEquals(finish, expectedC2)) throw new InvalidDataException("Client authentication finish was invalid.");
            var sessionKey = Mac(key, "TCP-SESSION", clientNonce.Item1, hostNonce);
            return new AuthenticatedRecordStream(raw, sessionKey, "H2C", "C2H");
        }

        public static Stream AuthenticateTcpClient(Stream raw, byte[] key)
        {
            var clientNonce = RandomBytes(16);
            var c1 = Mac(key, "TCP-C1", clientNonce);
            WriteHandshake(raw, TcpMagic, clientNonce, c1);
            var response = ReadHandshake(raw, TcpMagic, 16, 32);
            var expectedS1 = Mac(key, "TCP-S1", clientNonce, response.Item1);
            if (!FixedTimeEquals(response.Item2, expectedS1)) throw new InvalidDataException("Host session-code authentication failed.");
            var finish = Mac(key, "TCP-C2", clientNonce, response.Item1);
            raw.Write(finish, 0, finish.Length);
            raw.Flush();
            var sessionKey = Mac(key, "TCP-SESSION", clientNonce, response.Item1);
            return new AuthenticatedRecordStream(raw, sessionKey, "C2H", "H2C");
        }

        private static Tuple<byte[], byte[]> ReadHandshake(Stream stream, byte[] magic, int nonceLength, int macLength)
        {
            var receivedMagic = ReadExact(stream, magic.Length);
            if (!FixedTimeEquals(receivedMagic, magic)) throw new InvalidDataException("Direct security handshake is incompatible.");
            return Tuple.Create(ReadExact(stream, nonceLength), ReadExact(stream, macLength));
        }

        private static void WriteHandshake(Stream stream, byte[] magic, byte[] nonce, byte[] mac)
        {
            stream.Write(magic, 0, magic.Length);
            stream.Write(nonce, 0, nonce.Length);
            stream.Write(mac, 0, mac.Length);
            stream.Flush();
        }

        internal static byte[] ReadExact(Stream stream, int count)
        {
            var result = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(result, offset, count - offset);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
            }
            return result;
        }

        internal static void WriteInt32(Stream stream, int value)
        {
            var bytes = BitConverter.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        internal static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (var i = 0; i < bytes.Length; i++) builder.Append(bytes[i].ToString("X2"));
            return builder.ToString();
        }
    }

    internal sealed class AuthenticatedRecordStream : Stream
    {
        private const int MaxRecordBytes = 1024 * 1024;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("GCR1");
        private readonly Stream inner;
        private readonly byte[] key;
        private readonly string writeDomain;
        private readonly string readDomain;
        private long writeSequence;
        private long readSequence;
        private byte[] readBuffer = new byte[0];
        private int readOffset;

        public AuthenticatedRecordStream(Stream inner, byte[] key, string writeDomain, string readDomain)
        {
            this.inner = inner;
            this.key = key;
            this.writeDomain = writeDomain;
            this.readDomain = readDomain;
        }

        public override bool CanRead { get { return inner.CanRead; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return inner.CanWrite; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { inner.Flush(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            if (readOffset >= readBuffer.Length) ReadNextRecord();
            var available = readBuffer.Length - readOffset;
            var take = Math.Min(available, count);
            Buffer.BlockCopy(readBuffer, readOffset, buffer, offset, take);
            readOffset += take;
            return take;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var take = Math.Min(count, MaxRecordBytes);
                WriteRecord(buffer, offset, take);
                offset += take;
                count -= take;
            }
        }

        private void WriteRecord(byte[] buffer, int offset, int count)
        {
            var sequenceBytes = BitConverter.GetBytes(++writeSequence);
            var lengthBytes = BitConverter.GetBytes(count);
            var payload = new byte[count];
            Buffer.BlockCopy(buffer, offset, payload, 0, count);
            var tag = DirectTransportSecurity.Mac(key, "TCP-RECORD-" + writeDomain, sequenceBytes, lengthBytes, payload);
            inner.Write(Magic, 0, Magic.Length);
            inner.Write(sequenceBytes, 0, sequenceBytes.Length);
            inner.Write(lengthBytes, 0, lengthBytes.Length);
            inner.Write(payload, 0, payload.Length);
            inner.Write(tag, 0, tag.Length);
        }

        private void ReadNextRecord()
        {
            var magic = DirectTransportSecurity.ReadExact(inner, Magic.Length);
            if (!DirectTransportSecurity.FixedTimeEquals(magic, Magic)) throw new InvalidDataException("Authenticated control record marker was invalid.");
            var sequenceBytes = DirectTransportSecurity.ReadExact(inner, 8);
            var lengthBytes = DirectTransportSecurity.ReadExact(inner, 4);
            var sequence = BitConverter.ToInt64(sequenceBytes, 0);
            var length = BitConverter.ToInt32(lengthBytes, 0);
            if (sequence != readSequence + 1) throw new InvalidDataException("Authenticated control record sequence was invalid.");
            if (length < 1 || length > MaxRecordBytes) throw new InvalidDataException("Authenticated control record length was invalid.");
            var payload = DirectTransportSecurity.ReadExact(inner, length);
            var tag = DirectTransportSecurity.ReadExact(inner, 32);
            var expected = DirectTransportSecurity.Mac(key, "TCP-RECORD-" + readDomain, sequenceBytes, lengthBytes, payload);
            if (!DirectTransportSecurity.FixedTimeEquals(tag, expected)) throw new InvalidDataException("Authenticated control record was modified or forged.");
            readSequence = sequence;
            readBuffer = payload;
            readOffset = 0;
        }
    }
}
