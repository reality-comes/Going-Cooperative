using System;
using System.Globalization;

namespace GoingCooperative.Core
{
    public static class LockstepCommandPayloads
    {
        public const int NormalSpeedIndex = 1;
        public const string SetPausedAction = "SetPaused";
        public const string SetSpeedIndexAction = "SetSpeedIndex";
        public const string SetSpeedNormalAction = "SetSpeedNormal";
        public const string DigVoxelAction = "DigVoxel";
        public const string PlaceBlueprintAction = "PlaceBlueprint";
        public const string CutPlantAction = "CutPlant";
        public const string RegionOrderAction = "RegionOrder";
        public const string EquipOrderAction = "EquipOrder";
        public const string ResearchActivateAction = "ResearchActivate";
        public const string ProductionQueueAction = "ProductionQueue";

        public static string CreateResearchActivatePayload(string nodeId)
        {
            return "{\"action\":\"" + ResearchActivateAction + "\",\"nodeId\":\"" + EscapeJsonString(nodeId) + "\"}";
        }

        public static bool TryReadResearchActivatePayload(string payloadJson, out string nodeId)
        {
            var normalized = Normalize(payloadJson);
            nodeId = string.Empty;
            return normalized.IndexOf("\"action\":\"" + ResearchActivateAction + "\"", StringComparison.Ordinal) >= 0
                && TryReadStringProperty(normalized, "nodeId", out nodeId)
                && !string.IsNullOrWhiteSpace(nodeId);
        }

        public static string CreateProductionQueuePayload(
            string operation,
            int buildingX,
            int buildingY,
            int buildingZ,
            int ticketIndex,
            string blueprintId,
            int value)
        {
            return "{\"action\":\"" + ProductionQueueAction
                + "\",\"operation\":\"" + EscapeJsonString(operation)
                + "\",\"buildingX\":" + buildingX.ToString(CultureInfo.InvariantCulture)
                + ",\"buildingY\":" + buildingY.ToString(CultureInfo.InvariantCulture)
                + ",\"buildingZ\":" + buildingZ.ToString(CultureInfo.InvariantCulture)
                + ",\"ticketIndex\":" + ticketIndex.ToString(CultureInfo.InvariantCulture)
                + ",\"blueprintId\":\"" + EscapeJsonString(blueprintId)
                + "\",\"value\":" + value.ToString(CultureInfo.InvariantCulture)
                + "}";
        }

        public static bool TryReadProductionQueuePayload(
            string payloadJson,
            out string operation,
            out int buildingX,
            out int buildingY,
            out int buildingZ,
            out int ticketIndex,
            out string blueprintId,
            out int value)
        {
            var normalized = Normalize(payloadJson);
            operation = string.Empty;
            buildingX = 0;
            buildingY = 0;
            buildingZ = 0;
            ticketIndex = -1;
            blueprintId = string.Empty;
            value = 0;
            return normalized.IndexOf("\"action\":\"" + ProductionQueueAction + "\"", StringComparison.Ordinal) >= 0
                && TryReadStringProperty(normalized, "operation", out operation)
                && TryReadIntProperty(normalized, "buildingX", out buildingX)
                && TryReadIntProperty(normalized, "buildingY", out buildingY)
                && TryReadIntProperty(normalized, "buildingZ", out buildingZ)
                && TryReadIntProperty(normalized, "ticketIndex", out ticketIndex)
                && TryReadStringProperty(normalized, "blueprintId", out blueprintId)
                && TryReadIntProperty(normalized, "value", out value);
        }

        public static string CreatePausePayload(bool paused)
        {
            return "{\"action\":\"" + SetPausedAction + "\",\"paused\":" + (paused ? "true" : "false") + "}";
        }

        public static string CreateSpeedIndexPayload(int speedIndex)
        {
            return "{\"action\":\"" + SetSpeedIndexAction + "\",\"speedIndex\":" + speedIndex.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public static string CreateSetSpeedNormalPayload()
        {
            return "{\"action\":\"" + SetSpeedNormalAction + "\",\"speedIndex\":" + NormalSpeedIndex.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public static string CreateDigPayload(int startX, int startY, int startZ, int endX, int endY, int endZ)
        {
            return "{\"action\":\"" + DigVoxelAction + "\",\"startX\":" + startX.ToString(CultureInfo.InvariantCulture)
                + ",\"startY\":" + startY.ToString(CultureInfo.InvariantCulture)
                + ",\"startZ\":" + startZ.ToString(CultureInfo.InvariantCulture)
                + ",\"endX\":" + endX.ToString(CultureInfo.InvariantCulture)
                + ",\"endY\":" + endY.ToString(CultureInfo.InvariantCulture)
                + ",\"endZ\":" + endZ.ToString(CultureInfo.InvariantCulture)
                + "}";
        }

        public static string CreateBuildPayload(string blueprintId, int x, int y, int z, int angleY, string buildingType, string factionOwnership, bool afterLoading)
        {
            return "{\"action\":\"" + PlaceBlueprintAction + "\",\"blueprintId\":\"" + EscapeJsonString(blueprintId)
                + "\",\"x\":" + x.ToString(CultureInfo.InvariantCulture)
                + ",\"y\":" + y.ToString(CultureInfo.InvariantCulture)
                + ",\"z\":" + z.ToString(CultureInfo.InvariantCulture)
                + ",\"angleY\":" + angleY.ToString(CultureInfo.InvariantCulture)
                + ",\"buildingType\":\"" + EscapeJsonString(buildingType)
                + "\",\"faction\":\"" + EscapeJsonString(factionOwnership)
                + "\",\"afterLoading\":" + (afterLoading ? "true" : "false")
                + "}";
        }

        public static string CreateCutPlantPayload(int uniqueId, string blueprintId, int x, int y, int z, int worldX, int worldY, int worldZ)
        {
            return "{\"action\":\"" + CutPlantAction + "\",\"uniqueId\":" + uniqueId.ToString(CultureInfo.InvariantCulture)
                + ",\"blueprintId\":\"" + EscapeJsonString(blueprintId)
                + "\",\"x\":" + x.ToString(CultureInfo.InvariantCulture)
                + ",\"y\":" + y.ToString(CultureInfo.InvariantCulture)
                + ",\"z\":" + z.ToString(CultureInfo.InvariantCulture)
                + ",\"worldX\":" + worldX.ToString(CultureInfo.InvariantCulture)
                + ",\"worldY\":" + worldY.ToString(CultureInfo.InvariantCulture)
                + ",\"worldZ\":" + worldZ.ToString(CultureInfo.InvariantCulture)
                + "}";
        }

        public static string CreateRegionOrderPayload(
            string orderType,
            int startX,
            int startY,
            int startZ,
            int endX,
            int endY,
            int endZ,
            string allowType,
            string areaType,
            string subType)
        {
            return "{\"action\":\"" + RegionOrderAction
                + "\",\"orderType\":\"" + EscapeJsonString(orderType)
                + "\",\"startX\":" + startX.ToString(CultureInfo.InvariantCulture)
                + ",\"startY\":" + startY.ToString(CultureInfo.InvariantCulture)
                + ",\"startZ\":" + startZ.ToString(CultureInfo.InvariantCulture)
                + ",\"endX\":" + endX.ToString(CultureInfo.InvariantCulture)
                + ",\"endY\":" + endY.ToString(CultureInfo.InvariantCulture)
                + ",\"endZ\":" + endZ.ToString(CultureInfo.InvariantCulture)
                + ",\"allowType\":\"" + EscapeJsonString(allowType)
                + "\",\"areaType\":\"" + EscapeJsonString(areaType)
                + "\",\"subType\":\"" + EscapeJsonString(subType)
                + "\"}";
        }

        public static string CreateEquipOrderPayload(string entityId, string blueprintId, int x, int y, int z)
        {
            return "{\"action\":\"" + EquipOrderAction
                + "\",\"entityId\":\"" + EscapeJsonString(entityId)
                + "\",\"blueprintId\":\"" + EscapeJsonString(blueprintId)
                + "\",\"x\":" + x.ToString(CultureInfo.InvariantCulture)
                + ",\"y\":" + y.ToString(CultureInfo.InvariantCulture)
                + ",\"z\":" + z.ToString(CultureInfo.InvariantCulture)
                + "}";
        }

        public static bool TryReadPausePayload(string payloadJson, out bool paused)
        {
            paused = false;
            var normalized = Normalize(payloadJson);
            if (normalized.Contains("\"paused\":true", StringComparison.Ordinal))
            {
                paused = true;
                return true;
            }

            if (normalized.Contains("\"paused\":false", StringComparison.Ordinal))
            {
                paused = false;
                return true;
            }

            return false;
        }

        public static bool TryReadSpeedPayload(string payloadJson, out int speedIndex, out string action)
        {
            speedIndex = NormalSpeedIndex;
            action = string.Empty;
            var normalized = Normalize(payloadJson);

            if (normalized.Contains("\"action\":\"" + SetSpeedNormalAction + "\"", StringComparison.Ordinal))
            {
                action = SetSpeedNormalAction;
                speedIndex = NormalSpeedIndex;
                return true;
            }

            if (TryReadIntProperty(normalized, "speedIndex", out speedIndex)
                || TryReadIntProperty(normalized, "speed", out speedIndex))
            {
                action = SetSpeedIndexAction;
                return true;
            }

            return false;
        }

        public static bool TryReadDigPayload(string payloadJson, out int startX, out int startY, out int startZ, out int endX, out int endY, out int endZ)
        {
            startX = 0;
            startY = 0;
            startZ = 0;
            endX = 0;
            endY = 0;
            endZ = 0;

            var normalized = Normalize(payloadJson);
            if (!normalized.Contains("\"action\":\"" + DigVoxelAction + "\"", StringComparison.Ordinal))
            {
                return false;
            }

            return TryReadIntProperty(normalized, "startX", out startX)
                && TryReadIntProperty(normalized, "startY", out startY)
                && TryReadIntProperty(normalized, "startZ", out startZ)
                && TryReadIntProperty(normalized, "endX", out endX)
                && TryReadIntProperty(normalized, "endY", out endY)
                && TryReadIntProperty(normalized, "endZ", out endZ);
        }

        public static bool TryReadBuildPayload(
            string payloadJson,
            out string blueprintId,
            out int x,
            out int y,
            out int z,
            out int angleY,
            out string buildingType,
            out string factionOwnership,
            out bool afterLoading)
        {
            blueprintId = string.Empty;
            x = 0;
            y = 0;
            z = 0;
            angleY = 0;
            buildingType = string.Empty;
            factionOwnership = string.Empty;
            afterLoading = false;

            var normalized = Normalize(payloadJson);
            if (!normalized.Contains("\"action\":\"" + PlaceBlueprintAction + "\"", StringComparison.Ordinal))
            {
                return false;
            }

            return TryReadStringProperty(normalized, "blueprintId", out blueprintId)
                && TryReadIntProperty(normalized, "x", out x)
                && TryReadIntProperty(normalized, "y", out y)
                && TryReadIntProperty(normalized, "z", out z)
                && TryReadIntProperty(normalized, "angleY", out angleY)
                && TryReadStringProperty(normalized, "buildingType", out buildingType)
                && TryReadStringProperty(normalized, "faction", out factionOwnership)
                && TryReadBoolProperty(normalized, "afterLoading", out afterLoading);
        }

        public static bool TryReadCutPlantPayload(
            string payloadJson,
            out int uniqueId,
            out string blueprintId,
            out int x,
            out int y,
            out int z,
            out int worldX,
            out int worldY,
            out int worldZ)
        {
            uniqueId = 0;
            blueprintId = string.Empty;
            x = 0;
            y = 0;
            z = 0;
            worldX = 0;
            worldY = 0;
            worldZ = 0;

            var normalized = Normalize(payloadJson);
            if (!normalized.Contains("\"action\":\"" + CutPlantAction + "\"", StringComparison.Ordinal))
            {
                return false;
            }

            return TryReadIntProperty(normalized, "uniqueId", out uniqueId)
                && TryReadStringProperty(normalized, "blueprintId", out blueprintId)
                && TryReadIntProperty(normalized, "x", out x)
                && TryReadIntProperty(normalized, "y", out y)
                && TryReadIntProperty(normalized, "z", out z)
                && TryReadIntProperty(normalized, "worldX", out worldX)
                && TryReadIntProperty(normalized, "worldY", out worldY)
                && TryReadIntProperty(normalized, "worldZ", out worldZ);
        }

        public static bool TryReadRegionOrderPayload(
            string payloadJson,
            out string orderType,
            out int startX,
            out int startY,
            out int startZ,
            out int endX,
            out int endY,
            out int endZ,
            out string allowType,
            out string areaType,
            out string subType)
        {
            orderType = string.Empty;
            startX = 0;
            startY = 0;
            startZ = 0;
            endX = 0;
            endY = 0;
            endZ = 0;
            allowType = string.Empty;
            areaType = string.Empty;
            subType = string.Empty;

            var normalized = Normalize(payloadJson);
            if (!normalized.Contains("\"action\":\"" + RegionOrderAction + "\"", StringComparison.Ordinal))
            {
                return false;
            }

            return TryReadStringProperty(normalized, "orderType", out orderType)
                && TryReadIntProperty(normalized, "startX", out startX)
                && TryReadIntProperty(normalized, "startY", out startY)
                && TryReadIntProperty(normalized, "startZ", out startZ)
                && TryReadIntProperty(normalized, "endX", out endX)
                && TryReadIntProperty(normalized, "endY", out endY)
                && TryReadIntProperty(normalized, "endZ", out endZ)
                && TryReadStringProperty(normalized, "allowType", out allowType)
                && TryReadStringProperty(normalized, "areaType", out areaType)
                && TryReadStringProperty(normalized, "subType", out subType);
        }

        public static bool TryReadEquipOrderPayload(
            string payloadJson,
            out string entityId,
            out string blueprintId,
            out int x,
            out int y,
            out int z)
        {
            entityId = string.Empty;
            blueprintId = string.Empty;
            x = 0;
            y = 0;
            z = 0;

            var normalized = Normalize(payloadJson);
            if (!normalized.Contains("\"action\":\"" + EquipOrderAction + "\"", StringComparison.Ordinal))
            {
                return false;
            }

            return TryReadStringProperty(normalized, "entityId", out entityId)
                && TryReadStringProperty(normalized, "blueprintId", out blueprintId)
                && TryReadIntProperty(normalized, "x", out x)
                && TryReadIntProperty(normalized, "y", out y)
                && TryReadIntProperty(normalized, "z", out z);
        }

        private static string EscapeJsonString(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string Normalize(string payloadJson)
        {
            return (payloadJson ?? string.Empty)
                .Replace(" ", string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty)
                .Replace("\t", string.Empty);
        }

        private static bool TryReadIntProperty(string normalizedJson, string propertyName, out int value)
        {
            value = 0;
            var marker = "\"" + propertyName + "\":";
            var start = normalizedJson.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return false;
            }

            start += marker.Length;
            var end = start;
            while (end < normalizedJson.Length && (char.IsDigit(normalizedJson[end]) || normalizedJson[end] == '-'))
            {
                end++;
            }

            return end > start
                && int.TryParse(normalizedJson.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadBoolProperty(string normalizedJson, string propertyName, out bool value)
        {
            value = false;
            var trueMarker = "\"" + propertyName + "\":true";
            if (normalizedJson.Contains(trueMarker, StringComparison.Ordinal))
            {
                value = true;
                return true;
            }

            var falseMarker = "\"" + propertyName + "\":false";
            if (normalizedJson.Contains(falseMarker, StringComparison.Ordinal))
            {
                value = false;
                return true;
            }

            return false;
        }

        private static bool TryReadStringProperty(string normalizedJson, string propertyName, out string value)
        {
            value = string.Empty;
            var marker = "\"" + propertyName + "\":\"";
            var start = normalizedJson.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return false;
            }

            start += marker.Length;
            var end = start;
            var escaped = false;
            while (end < normalizedJson.Length)
            {
                var ch = normalizedJson[end];
                if (escaped)
                {
                    escaped = false;
                    end++;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    end++;
                    continue;
                }

                if (ch == '"')
                {
                    value = normalizedJson.Substring(start, end - start).Replace("\\\"", "\"").Replace("\\\\", "\\");
                    return true;
                }

                end++;
            }

            return false;
        }
    }
}
