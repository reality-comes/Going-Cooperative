using System;
using System.Globalization;

namespace GoingCooperative.Plugin.BepInEx
{
    internal enum MultiplayerTransferTestKind
    {
        SaveTransfer,
        Resync
    }

    internal sealed class MultiplayerTransferTestState
    {
        private const long TestBytes = 32L * 1024L * 1024L;
        private float startedRealtime;

        public bool IsActive { get; private set; }

        public bool IsComplete { get; private set; }

        public MultiplayerTransferTestKind Kind { get; private set; }

        public float Progress { get; private set; }

        public string Phase { get; private set; } = "Idle";

        public string Detail { get; private set; } = "Ready";

        public void Start(MultiplayerTransferTestKind kind, float realtime)
        {
            Kind = kind;
            startedRealtime = realtime;
            IsActive = true;
            IsComplete = false;
            Progress = 0f;
            Phase = kind == MultiplayerTransferTestKind.Resync ? "Requesting checkpoint" : "Preparing save";
            Detail = "TEST MODE · No user save is modified";
        }

        public void Update(float realtime)
        {
            if (!IsActive)
            {
                return;
            }

            var elapsed = Math.Max(0f, realtime - startedRealtime);
            if (elapsed < 1.2f)
            {
                SetPhase("Preparing", elapsed / 12f, "Creating safe test manifest");
            }
            else if (elapsed < 2.2f)
            {
                SetPhase("Manifest ready", 0.1f + ((elapsed - 1.2f) / 10f), "32.0 MiB · 128 chunks · SHA-256 queued");
            }
            else if (elapsed < 9.2f)
            {
                var transfer = (elapsed - 2.2f) / 7f;
                Progress = 0.2f + (transfer * 0.62f);
                Phase = Kind == MultiplayerTransferTestKind.Resync ? "Transferring checkpoint" : "Transferring save";
                var transferred = (long)(TestBytes * transfer);
                Detail = FormatBytes(transferred) + " / " + FormatBytes(TestBytes)
                    + " · " + Math.Min(128, (int)(128 * transfer)).ToString(CultureInfo.InvariantCulture) + "/128 chunks";
            }
            else if (elapsed < 10.4f)
            {
                SetPhase("Verifying", 0.88f, "Checking chunk hashes and final SHA-256");
            }
            else if (elapsed < 11.6f)
            {
                SetPhase("Staging", 0.95f, "Quarantine path ready · existing saves untouched");
            }
            else
            {
                Progress = 1f;
                Phase = Kind == MultiplayerTransferTestKind.Resync ? "Reload required" : "Transfer test complete";
                Detail = "TEST COMPLETE · Save-load API is not invoked";
                IsActive = false;
                IsComplete = true;
            }
        }

        private void SetPhase(string phase, float progress, string detail)
        {
            Phase = phase;
            Progress = Math.Max(Progress, Math.Min(1f, progress));
            Detail = detail;
        }

        private static string FormatBytes(long bytes)
        {
            return (bytes / 1048576d).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
        }

        public static bool RunSmokeTest(out string detail)
        {
            var state = new MultiplayerTransferTestState();
            state.Start(MultiplayerTransferTestKind.SaveTransfer, 100f);
            state.Update(106f);
            if (!state.IsActive || state.Progress <= 0.2f || state.Progress >= 0.82f || state.Phase != "Transferring save")
            {
                detail = "transfer-midpoint-invalid";
                return false;
            }

            state.Update(112f);
            if (state.IsActive || !state.IsComplete || state.Progress != 1f || state.Phase != "Transfer test complete")
            {
                detail = "transfer-completion-invalid";
                return false;
            }

            state.Start(MultiplayerTransferTestKind.Resync, 200f);
            state.Update(212f);
            if (!state.IsComplete || state.Phase != "Reload required")
            {
                detail = "resync-completion-invalid";
                return false;
            }

            detail = "ok";
            return true;
        }
    }
}
