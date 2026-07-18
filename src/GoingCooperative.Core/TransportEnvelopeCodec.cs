using System;
using System.Globalization;
using System.Text;

namespace GoingCooperative.Core
{
    public static class TransportEnvelopeCodec
    {
        private const string Version = "GCOOP-ENV-1";

        public static string Encode(TransportEnvelope envelope)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            return string.Join("\t", new[]
            {
                Version,
                ((int)envelope.Kind).ToString(CultureInfo.InvariantCulture),
                envelope.Tick.ToString(CultureInfo.InvariantCulture),
                EncodeText(envelope.SenderId),
                EncodeText(envelope.Payload)
            });
        }

        public static bool TryDecode(string line, out TransportEnvelope? envelope, out string error)
        {
            envelope = null;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(line))
            {
                error = "line is empty";
                return false;
            }

            // Force the legacy char[] overload used by the game's Mono runtime.
            // Newer compilers otherwise bind Split(char), which is absent there.
            var parts = line.Split(new[] { '\t' }, StringSplitOptions.None);
            if (parts.Length != 5)
            {
                error = "expected 5 tab-delimited fields";
                return false;
            }

            if (!string.Equals(parts[0], Version, StringComparison.Ordinal))
            {
                error = "unsupported envelope version";
                return false;
            }

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kindValue))
            {
                error = "invalid message kind";
                return false;
            }

            if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tick))
            {
                error = "invalid tick";
                return false;
            }

            if (!TryDecodeText(parts[3], out var senderId, out error)
                || !TryDecodeText(parts[4], out var payload, out error))
            {
                return false;
            }

            envelope = new TransportEnvelope((TransportMessageKind)kindValue, tick, senderId, payload);
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
}
