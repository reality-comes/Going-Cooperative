using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GoingCooperative.Core
{
    public static class TransportChunkCodec
    {
        private const string Version = "GCOOP-CHUNK-1";

        public static IReadOnlyList<TransportEnvelope> CreateChunks(TransportEnvelope envelope, string chunkId, int maxChunkChars)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            if (string.IsNullOrWhiteSpace(chunkId))
            {
                throw new ArgumentException("Chunk id is required.", nameof(chunkId));
            }

            if (maxChunkChars <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxChunkChars), "Chunk size must be positive.");
            }

            var encodedEnvelope = TransportEnvelopeCodec.Encode(envelope);
            var total = Math.Max(1, (encodedEnvelope.Length + maxChunkChars - 1) / maxChunkChars);
            var chunks = new List<TransportEnvelope>(total);
            for (var index = 0; index < total; index++)
            {
                var offset = index * maxChunkChars;
                var length = Math.Min(maxChunkChars, encodedEnvelope.Length - offset);
                var chunkText = encodedEnvelope.Substring(offset, length);
                chunks.Add(new TransportEnvelope(
                    TransportMessageKind.Chunk,
                    envelope.Tick,
                    envelope.SenderId,
                    string.Join("|", new[]
                    {
                        Version,
                        EncodeText(chunkId),
                        index.ToString(CultureInfo.InvariantCulture),
                        total.ToString(CultureInfo.InvariantCulture),
                        EncodeText(chunkText)
                    })));
            }

            return chunks;
        }

        public static bool TryReadChunk(
            TransportEnvelope envelope,
            out string chunkId,
            out int index,
            out int total,
            out string chunkText,
            out string error)
        {
            chunkId = string.Empty;
            index = 0;
            total = 0;
            chunkText = string.Empty;
            error = string.Empty;

            if (envelope.Kind != TransportMessageKind.Chunk)
            {
                error = "envelope is not a chunk";
                return false;
            }

            var parts = envelope.Payload.Split('|');
            if (parts.Length != 5)
            {
                error = "expected chunk payload with 5 fields";
                return false;
            }

            if (!string.Equals(parts[0], Version, StringComparison.Ordinal)
                || !TryDecodeText(parts[1], out chunkId, out error)
                || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out index)
                || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out total)
                || !TryDecodeText(parts[4], out chunkText, out error))
            {
                if (string.IsNullOrEmpty(error))
                {
                    error = "invalid chunk payload";
                }

                return false;
            }

            if (index < 0 || total <= 0 || index >= total)
            {
                error = "invalid chunk index";
                return false;
            }

            return true;
        }

        private static string EncodeText(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? string.Empty));
        }

        private static bool TryDecodeText(string encoded, out string text, out string error)
        {
            try
            {
                text = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                error = string.Empty;
                return true;
            }
            catch (FormatException ex)
            {
                text = string.Empty;
                error = "invalid base64 text: " + ex.Message;
                return false;
            }
        }
    }

    public sealed class TransportChunkReassembler
    {
        private readonly Dictionary<string, PendingChunkSet> pending = new Dictionary<string, PendingChunkSet>(StringComparer.Ordinal);

        public void Clear()
        {
            pending.Clear();
        }

        public bool TryAddChunk(TransportEnvelope chunkEnvelope, out TransportEnvelope? reassembled, out string error)
        {
            reassembled = null;
            if (!TransportChunkCodec.TryReadChunk(chunkEnvelope, out var chunkId, out var index, out var total, out var chunkText, out error))
            {
                return false;
            }

            if (!pending.TryGetValue(chunkId, out var set))
            {
                set = new PendingChunkSet(total);
                pending[chunkId] = set;
            }

            if (set.Total != total)
            {
                error = "chunk total mismatch";
                pending.Remove(chunkId);
                return false;
            }

            set.Set(index, chunkText);
            if (!set.Complete)
            {
                return true;
            }

            pending.Remove(chunkId);
            if (!TransportEnvelopeCodec.TryDecode(set.Join(), out reassembled, out error) || reassembled == null)
            {
                return false;
            }

            return true;
        }

        private sealed class PendingChunkSet
        {
            private readonly string?[] chunks;
            private int received;

            public PendingChunkSet(int total)
            {
                Total = total;
                chunks = new string?[total];
            }

            public int Total { get; }

            public bool Complete
            {
                get { return received == Total; }
            }

            public void Set(int index, string text)
            {
                if (chunks[index] == null)
                {
                    received++;
                }

                chunks[index] = text;
            }

            public string Join()
            {
                var builder = new StringBuilder();
                for (var i = 0; i < chunks.Length; i++)
                {
                    builder.Append(chunks[i] ?? string.Empty);
                }

                return builder.ToString();
            }
        }
    }
}
