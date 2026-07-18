using System;

namespace GoingCooperative.Core
{
    public readonly struct AgentWorkPresentationDescriptor
    {
        internal AgentWorkPresentationDescriptor(
            string goalId,
            string actionId,
            string workKind,
            string defaultAnimationToken)
        {
            GoalId = goalId;
            ActionId = actionId;
            WorkKind = workKind;
            DefaultAnimationToken = defaultAnimationToken;
        }

        public string GoalId { get; }
        public string ActionId { get; }
        public string WorkKind { get; }
        public string DefaultAnimationToken { get; }
    }

    public static class AgentWorkPresentationPolicy
    {
        public static bool IsCandidateAction(string actionId)
        {
            return string.Equals(actionId, "StartObtaining", StringComparison.Ordinal)
                || string.Equals(actionId, "EnemyCutPlant", StringComparison.Ordinal)
                || string.Equals(actionId, "ConstructAction", StringComparison.Ordinal)
                || string.Equals(actionId, "DeconstructAction", StringComparison.Ordinal)
                || string.Equals(actionId, "RepairAction", StringComparison.Ordinal)
                || string.Equals(actionId, "UninstallAction", StringComparison.Ordinal)
                || string.Equals(actionId, "PlantCropsAction", StringComparison.Ordinal);
        }

        public static bool IsMigratedGoal(string goalId)
        {
            return string.Equals(goalId, "ChopTreeGoal", StringComparison.Ordinal)
                || string.Equals(goalId, "ConstructBuildingGoal", StringComparison.Ordinal)
                || string.Equals(goalId, "DigGoal", StringComparison.Ordinal)
                || string.Equals(goalId, "HarvestGoal", StringComparison.Ordinal)
                || string.Equals(goalId, "FollowCutPlantOrderGoal", StringComparison.Ordinal)
                || string.Equals(goalId, "DeconstructGoal", StringComparison.Ordinal)
                || string.Equals(goalId, "RepairBuildingGoal", StringComparison.Ordinal)
                || string.Equals(goalId, "UninstallBuildingGoal", StringComparison.Ordinal)
                || string.Equals(goalId, "PlantCropsGoal", StringComparison.Ordinal);
        }

        public static bool TryResolve(
            string goalId,
            string actionId,
            out AgentWorkPresentationDescriptor descriptor)
        {
            if (Matches(goalId, actionId, "ChopTreeGoal", "StartObtaining"))
            {
                descriptor = Create(goalId, actionId, "Chop", "Mining");
                return true;
            }

            if (Matches(goalId, actionId, "ConstructBuildingGoal", "ConstructAction"))
            {
                descriptor = Create(goalId, actionId, "Build", "Build");
                return true;
            }

            if (Matches(goalId, actionId, "DigGoal", "StartObtaining"))
            {
                descriptor = Create(goalId, actionId, "Dig", "Mining");
                return true;
            }

            if (Matches(goalId, actionId, "HarvestGoal", "StartObtaining"))
            {
                descriptor = Create(goalId, actionId, "Harvest", "Harvest");
                return true;
            }

            if (Matches(goalId, actionId, "FollowCutPlantOrderGoal", "EnemyCutPlant"))
            {
                descriptor = Create(goalId, actionId, "CutPlant", "Mining");
                return true;
            }

            if (Matches(goalId, actionId, "DeconstructGoal", "DeconstructAction"))
            {
                descriptor = Create(goalId, actionId, "Deconstruct", "Build");
                return true;
            }

            if (Matches(goalId, actionId, "RepairBuildingGoal", "RepairAction"))
            {
                descriptor = Create(goalId, actionId, "Repair", "Build");
                return true;
            }

            if (Matches(goalId, actionId, "UninstallBuildingGoal", "UninstallAction"))
            {
                descriptor = Create(goalId, actionId, "Uninstall", "Build");
                return true;
            }

            if (Matches(goalId, actionId, "PlantCropsGoal", "PlantCropsAction"))
            {
                descriptor = Create(goalId, actionId, "Plant", "Planting");
                return true;
            }

            descriptor = default;
            return false;
        }

        public static string GetDefaultAnimationToken(string workKind)
        {
            if (string.Equals(workKind, "Harvest", StringComparison.Ordinal))
            {
                return "Harvest";
            }

            if (string.Equals(workKind, "Plant", StringComparison.Ordinal))
            {
                return "Planting";
            }

            if (string.Equals(workKind, "Chop", StringComparison.Ordinal)
                || string.Equals(workKind, "Dig", StringComparison.Ordinal)
                || string.Equals(workKind, "CutPlant", StringComparison.Ordinal))
            {
                return "Mining";
            }

            return "Build";
        }

        public static bool ShouldReplayAnimatorLayer(
            bool initialSample,
            int authoritativeStateHash,
            int clientStateHash)
        {
            if (authoritativeStateHash == 0)
            {
                return false;
            }

            return initialSample || authoritativeStateHash != clientStateHash;
        }

        public static bool UsesClientTriggerMaintenance(string workKind)
        {
            return string.Equals(workKind, "Harvest", StringComparison.Ordinal);
        }

        public static bool UsesHostCyclePulse(string workKind)
        {
            return string.Equals(workKind, "Dig", StringComparison.Ordinal);
        }

        public static bool UsesClientStartConfirmation(string workKind)
        {
            // Construction is the only work family currently observed to
            // occasionally consume its first client-side hammer trigger before
            // the animator has entered the host's sampled state. Keep this
            // deliberately narrow until another action has equivalent evidence.
            return string.Equals(workKind, "Build", StringComparison.Ordinal);
        }

        public static bool IsCycleDriverScopedWork(string workKind)
        {
            return UsesClientTriggerMaintenance(workKind) || UsesHostCyclePulse(workKind);
        }

        private static bool Matches(
            string goalId,
            string actionId,
            string expectedGoalId,
            string expectedActionId)
        {
            return string.Equals(goalId, expectedGoalId, StringComparison.Ordinal)
                && string.Equals(actionId, expectedActionId, StringComparison.Ordinal);
        }

        private static AgentWorkPresentationDescriptor Create(
            string goalId,
            string actionId,
            string workKind,
            string defaultAnimationToken)
        {
            return new AgentWorkPresentationDescriptor(
                goalId,
                actionId,
                workKind,
                defaultAnimationToken);
        }
    }
}
