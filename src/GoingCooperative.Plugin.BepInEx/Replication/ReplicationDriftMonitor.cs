using System;
using System.Globalization;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static long replicationDriftSignaturesMatched;
        private static long replicationDriftSignaturesMismatched;
        private static string replicationLastDriftSummary = string.Empty;

        private static void RecordReplicationDriftSignature(
            string channel,
            long snapshotId,
            int expectedCount,
            int observedCount,
            bool hostSignatureValid,
            ulong hostSignature,
            ulong localSignature,
            string detail)
        {
            var matched = hostSignatureValid && hostSignature == localSignature;
            if (matched)
            {
                replicationDriftSignaturesMatched++;
            }
            else
            {
                replicationDriftSignaturesMismatched++;
            }

            replicationLastDriftSummary = "channel="
                + channel
                + " snapshotId="
                + snapshotId.ToString(CultureInfo.InvariantCulture)
                + " expected="
                + expectedCount.ToString(CultureInfo.InvariantCulture)
                + " observed="
                + observedCount.ToString(CultureInfo.InvariantCulture)
                + " hostSignature="
                + (hostSignatureValid ? FormatReplicationDriftSignature(hostSignature) : "<missing>")
                + " localSignature="
                + FormatReplicationDriftSignature(localSignature)
                + " matched="
                + (matched ? "yes" : "no")
                + " "
                + detail;

            var current = instance;
            if (current == null)
            {
                return;
            }

            if (matched)
            {
                current.LogReplicationInfo("Going Cooperative replication drift signature " + replicationLastDriftSummary);
            }
            else
            {
                current.LogReplicationWarning("Going Cooperative replication drift mismatch " + replicationLastDriftSummary);
            }
        }

        private static string FormatReplicationDriftSignature(ulong signature)
        {
            return "0x" + signature.ToString("x16", CultureInfo.InvariantCulture);
        }
    }
}
