using GoingCooperative.Core;

namespace GoingCooperative.Plugin.BepInEx
{
    internal interface IRuntimeCommandActions
    {
        bool ApplyPause(bool paused, out string detail);

        bool ApplySpeedIndex(int speedIndex, string action, out string detail);

        bool ApplyDig(int startX, int startY, int startZ, int endX, int endY, int endZ, out string detail);

        bool ApplyBuild(string blueprintId, int x, int y, int z, int angleY, string buildingType, string factionOwnership, bool afterLoading, out string detail);

        bool ApplyCutPlant(int uniqueId, string blueprintId, int x, int y, int z, int worldX, int worldY, int worldZ, out string detail);

        bool ApplyRegionOrder(string orderType, int startX, int startY, int startZ, int endX, int endY, int endZ, string allowType, string areaType, string subType, out string detail);

        bool ApplyCustom(string payloadJson, out string detail);
    }

    internal sealed class RuntimeCommandResult
    {
        public RuntimeCommandResult(bool invoked, string detail)
        {
            Invoked = invoked;
            Detail = string.IsNullOrWhiteSpace(detail) ? "no-detail" : detail;
        }

        public bool Invoked { get; }

        public string Detail { get; }
    }

    internal sealed class RuntimeCommandExecutor
    {
        private readonly IRuntimeCommandActions actions;

        public RuntimeCommandExecutor(IRuntimeCommandActions actions)
        {
            this.actions = actions;
        }

        public RuntimeCommandResult Apply(LockstepCommand command)
        {
            if (command == null)
            {
                return new RuntimeCommandResult(false, "command-null");
            }

            switch (command.Kind)
            {
                case CommandKind.Pause:
                    if (!LockstepCommandPayloads.TryReadPausePayload(command.PayloadJson, out var paused))
                    {
                        return new RuntimeCommandResult(false, "pause-payload-invalid");
                    }

                    return ApplyAction((out string detail) => actions.ApplyPause(paused, out detail));

                case CommandKind.Speed:
                    if (!LockstepCommandPayloads.TryReadSpeedPayload(command.PayloadJson, out var speedIndex, out var speedAction))
                    {
                        return new RuntimeCommandResult(false, "speed-payload-invalid");
                    }

                    return ApplyAction((out string detail) => actions.ApplySpeedIndex(speedIndex, speedAction, out detail));

                case CommandKind.Dig:
                    if (!LockstepCommandPayloads.TryReadDigPayload(command.PayloadJson, out var startX, out var startY, out var startZ, out var endX, out var endY, out var endZ))
                    {
                        return new RuntimeCommandResult(false, "dig-payload-invalid");
                    }

                    return ApplyAction((out string detail) => actions.ApplyDig(startX, startY, startZ, endX, endY, endZ, out detail));

                case CommandKind.Build:
                    if (!LockstepCommandPayloads.TryReadBuildPayload(command.PayloadJson, out var blueprintId, out var x, out var y, out var z, out var angleY, out var buildingType, out var factionOwnership, out var afterLoading))
                    {
                        return new RuntimeCommandResult(false, "build-payload-invalid");
                    }

                    return ApplyAction((out string detail) => actions.ApplyBuild(blueprintId, x, y, z, angleY, buildingType, factionOwnership, afterLoading, out detail));

                case CommandKind.Cut:
                    if (!LockstepCommandPayloads.TryReadCutPlantPayload(command.PayloadJson, out var uniqueId, out var cutBlueprintId, out var cutX, out var cutY, out var cutZ, out var worldX, out var worldY, out var worldZ))
                    {
                        return new RuntimeCommandResult(false, "cut-plant-payload-invalid");
                    }

                    return ApplyAction((out string detail) => actions.ApplyCutPlant(uniqueId, cutBlueprintId, cutX, cutY, cutZ, worldX, worldY, worldZ, out detail));

                case CommandKind.RegionOrder:
                    if (!LockstepCommandPayloads.TryReadRegionOrderPayload(
                        command.PayloadJson,
                        out var orderType,
                        out var regionStartX,
                        out var regionStartY,
                        out var regionStartZ,
                        out var regionEndX,
                        out var regionEndY,
                        out var regionEndZ,
                        out var allowType,
                        out var areaType,
                        out var subType))
                    {
                        return new RuntimeCommandResult(false, "region-order-payload-invalid");
                    }

                    return ApplyAction((out string detail) => actions.ApplyRegionOrder(
                        orderType,
                        regionStartX,
                        regionStartY,
                        regionStartZ,
                        regionEndX,
                        regionEndY,
                        regionEndZ,
                        allowType,
                        areaType,
                        subType,
                        out detail));

                case CommandKind.Custom:
                    return ApplyAction((out string detail) => actions.ApplyCustom(command.PayloadJson, out detail));

                default:
                    return new RuntimeCommandResult(false, "unsupported-kind=" + command.Kind);
            }
        }

        private static RuntimeCommandResult ApplyAction(RuntimeCommandAction action)
        {
            var invoked = action(out var detail);
            return new RuntimeCommandResult(invoked, detail);
        }

        private delegate bool RuntimeCommandAction(out string detail);
    }
}
