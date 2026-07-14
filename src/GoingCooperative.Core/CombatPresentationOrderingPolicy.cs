using System;

namespace GoingCooperative.Core
{
    public enum CombatPresentationEndDisposition
    {
        WaitForStart = 0,
        ApplyActive = 1,
        ApplyCompleted = 2,
        SupersedeActive = 3
    }

    public static class CombatPresentationOrderingPolicy
    {
        public static CombatPresentationEndDisposition ResolveEnd(
            string? activeChargeId,
            long activeLatestEventTick,
            string? completedChargeId,
            string incomingChargeId,
            long incomingEventTick)
        {
            if (!string.IsNullOrEmpty(activeChargeId)
                && string.Equals(activeChargeId, incomingChargeId, StringComparison.Ordinal))
            {
                return CombatPresentationEndDisposition.ApplyActive;
            }

            if (!string.IsNullOrEmpty(completedChargeId)
                && string.Equals(completedChargeId, incomingChargeId, StringComparison.Ordinal))
            {
                return CombatPresentationEndDisposition.ApplyCompleted;
            }

            if (!string.IsNullOrEmpty(activeChargeId) && incomingEventTick > activeLatestEventTick)
            {
                return CombatPresentationEndDisposition.SupersedeActive;
            }

            return CombatPresentationEndDisposition.WaitForStart;
        }
    }
}
