using System;
using System.Globalization;
using GoingCooperative.Core.Replication;
using HarmonyLib;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private const float ReplicationGameTimeSnapshotSeconds = 1f;
        private const int ReplicationGameTimeMinuteSnapThreshold = 1;
        private const float ReplicationGameTimeUnitySnapThreshold = 2f;
        private static object? replicationWorldTimeManagerInstance;

        private void SendHostReplicationGameTimeSnapshotIfDue()
        {
            if (!replicationConfigEnabled
                || !replicationConfigHostMode
                || IsReplicationWorldObjectDeltaModeOff()
                || !replicationRuntimeStarted
                || !replicationRemoteHelloReceived
                || replicationTransport == null)
            {
                return;
            }

            var now = Time.realtimeSinceStartup;
            if (now < replicationNextGameTimeSnapshotRealtime)
            {
                return;
            }

            replicationNextGameTimeSnapshotRealtime = now + ReplicationGameTimeSnapshotSeconds;
            if (!TryReadReplicationGameTimeState(out var minutesTotal, out var minuteFract, out var unityTime, out var detail))
            {
                replicationLastGameTimeSummary = "collect-failed " + detail;
                return;
            }

            var delta = new ReplicationWorldObjectDelta(
                ++replicationWorldObjectDeltaSequence,
                now,
                "GameTimeSnapshot",
                ++replicationGameTimeSnapshotSequence,
                "world-time",
                0,
                0,
                0,
                "minutes="
                    + minutesTotal.ToString(CultureInfo.InvariantCulture)
                    + " minuteFract="
                    + minuteFract.ToString("0.###", CultureInfo.InvariantCulture)
                    + " unityTime="
                    + unityTime.ToString("0.###", CultureInfo.InvariantCulture));

            TrySendReplicationWorldObjectDelta(delta, isRetry: false);
            replicationLastGameTimeSummary = "sent minutes="
                + minutesTotal.ToString(CultureInfo.InvariantCulture)
                + " unityTime="
                + unityTime.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static bool TryApplyReplicationGameTimeSnapshot(ReplicationWorldObjectDelta delta, out string detail)
        {
            if (!TryReadReplicationWorldObjectDetailInt(delta.Detail, "minutes", out var hostMinutes))
            {
                detail = "game-time-minutes-missing";
                return false;
            }

            var hostMinuteFract = TryReadReplicationWorldObjectDetailFloat(delta.Detail, "minuteFract", out var parsedMinuteFract)
                ? parsedMinuteFract
                : 0f;
            var hostUnityTime = TryReadReplicationWorldObjectDetailFloat(delta.Detail, "unityTime", out var parsedUnityTime)
                ? parsedUnityTime
                : 0f;

            if (!TryReadReplicationGameTimeState(out var localMinutes, out var localMinuteFract, out var localUnityTime, out var readDetail))
            {
                detail = "game-time-local-read-failed " + readDetail;
                return false;
            }

            var minuteDrift = hostMinutes - localMinutes;
            var unityDrift = hostUnityTime - localUnityTime;
            if (Math.Abs(minuteDrift) < ReplicationGameTimeMinuteSnapThreshold
                && Math.Abs(unityDrift) < ReplicationGameTimeUnitySnapThreshold)
            {
                detail = "game-time-in-sync hostMinutes="
                    + hostMinutes.ToString(CultureInfo.InvariantCulture)
                    + " localMinutes="
                    + localMinutes.ToString(CultureInfo.InvariantCulture)
                    + " localMinuteFract="
                    + localMinuteFract.ToString("0.###", CultureInfo.InvariantCulture);
                replicationLastGameTimeSummary = detail;
                return true;
            }

            if (!TryWriteReplicationGameTimeState(hostMinutes, hostMinuteFract, hostUnityTime, out var writeDetail))
            {
                detail = "game-time-apply-failed driftMinutes="
                    + minuteDrift.ToString(CultureInfo.InvariantCulture)
                    + " driftUnity="
                    + unityDrift.ToString("0.###", CultureInfo.InvariantCulture)
                    + " "
                    + writeDetail;
                replicationLastGameTimeSummary = detail;
                return false;
            }

            detail = "game-time-corrected driftMinutes="
                + minuteDrift.ToString(CultureInfo.InvariantCulture)
                + " driftUnity="
                + unityDrift.ToString("0.###", CultureInfo.InvariantCulture)
                + " "
                + writeDetail;
            replicationLastGameTimeSummary = detail;
            instance?.LogReplicationInfo("Going Cooperative replication game time corrected " + detail);
            return true;
        }

        private static bool TryReadReplicationGameTimeState(out int minutesTotal, out float minuteFract, out float unityTime, out string detail)
        {
            minutesTotal = 0;
            minuteFract = 0f;
            unityTime = 0f;
            if (!TryGetReplicationWorldTimeManager(out var manager, out detail) || manager == null)
            {
                return false;
            }

            if (!TryReadReplicationFloatMember(manager, "UnityTime", "unityTime", out unityTime)
                && !TryReadReplicationStaticFloatMember(manager.GetType(), "UnityTime", "unityTime", out unityTime))
            {
                detail = "unity-time-missing type=" + (manager.GetType().FullName ?? manager.GetType().Name);
                return false;
            }

            if ((!TryReadInstanceMemberValue(manager, "dateAndTime", out var worldDate) || worldDate == null)
                && (!TryReadInstanceMemberValue(manager, "DateAndTime", out worldDate) || worldDate == null))
            {
                detail = "world-date-missing type=" + (manager.GetType().FullName ?? manager.GetType().Name);
                return false;
            }

            if (!TryReadReplicationIntMember(worldDate, "MinutesTotal", "minutesTotal", out minutesTotal))
            {
                detail = "minutes-total-missing type=" + (worldDate.GetType().FullName ?? worldDate.GetType().Name);
                return false;
            }

            TryReadReplicationFloatMember(worldDate, "MinuteFract", "minuteFract", out minuteFract);
            detail = "ok";
            return true;
        }

        private static bool TryWriteReplicationGameTimeState(int minutesTotal, float minuteFract, float unityTime, out string detail)
        {
            if (!TryGetReplicationWorldTimeManager(out var manager, out detail) || manager == null)
            {
                return false;
            }

            if ((!TryReadInstanceMemberValue(manager, "dateAndTime", out var worldDate) || worldDate == null)
                && (!TryReadInstanceMemberValue(manager, "DateAndTime", out worldDate) || worldDate == null))
            {
                detail = "world-date-missing";
                return false;
            }

            var setMinutes = TryInvokeReplicationObjectMethod(
                worldDate,
                "SetMinutesTotal",
                new object[] { Convert.ToUInt32(Math.Max(0, minutesTotal), CultureInfo.InvariantCulture) },
                out _);
            var setFract = TryInvokeReplicationObjectMethod(
                worldDate,
                "SetMinuteFractionalPart",
                new object[] { Math.Max(0f, Math.Min(0.999f, minuteFract)) },
                out _);
            var setUnity = TrySetInstanceMemberValue(manager, "UnityTime", unityTime)
                || TrySetInstanceMemberValue(manager, "unityTime", unityTime)
                || TrySetReplicationStaticFloatMember(manager.GetType(), "UnityTime", "unityTime", unityTime);

            detail = "setMinutes="
                + (setMinutes ? "yes" : "no")
                + " setMinuteFract="
                + (setFract ? "yes" : "no")
                + " setUnityTime="
                + (setUnity ? "yes" : "no");
            return setMinutes && setUnity;
        }

        private static bool TryGetReplicationWorldTimeManager(out object? manager, out string detail)
        {
            if (replicationWorldTimeManagerInstance != null)
            {
                manager = replicationWorldTimeManagerInstance;
                detail = "cached";
                return true;
            }

            var type = AccessTools.TypeByName("NSMedieval.WorldTimeManager");
            if (type == null)
            {
                manager = null;
                detail = "world-time-type-missing";
                return false;
            }

            try
            {
                var objects = UnityEngine.Object.FindObjectsOfType(type) ?? Array.Empty<UnityEngine.Object>();
                if (objects.Length == 0)
                {
                    manager = null;
                    detail = "world-time-instance-missing";
                    return false;
                }

                Array.Sort(objects, CompareUnityObjectsForFixedStep);
                replicationWorldTimeManagerInstance = objects[0];
                manager = replicationWorldTimeManagerInstance;
                detail = "found";
                return true;
            }
            catch (Exception ex)
            {
                manager = null;
                detail = "world-time-find-error " + FormatReflectionExceptionDetail(ex);
                return false;
            }
        }

        private static bool TryReadReplicationFloatMember(object value, string upperName, string lowerName, out float number)
        {
            number = 0f;
            if ((!TryReadInstanceMemberValue(value, upperName, out var memberValue) || memberValue == null)
                && (!TryReadInstanceMemberValue(value, lowerName, out memberValue) || memberValue == null))
            {
                return false;
            }

            try
            {
                number = Convert.ToSingle(memberValue, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadReplicationStaticFloatMember(Type type, string upperName, string lowerName, out float number)
        {
            number = 0f;
            try
            {
                var property = AccessTools.Property(type, upperName) ?? AccessTools.Property(type, lowerName);
                if (property != null)
                {
                    number = Convert.ToSingle(property.GetValue(null, null), CultureInfo.InvariantCulture);
                    return true;
                }

                var field = AccessTools.Field(type, upperName) ?? AccessTools.Field(type, lowerName);
                if (field != null)
                {
                    number = Convert.ToSingle(field.GetValue(null), CultureInfo.InvariantCulture);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TrySetReplicationStaticFloatMember(Type type, string upperName, string lowerName, float value)
        {
            try
            {
                var property = AccessTools.Property(type, upperName) ?? AccessTools.Property(type, lowerName);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(null, value, null);
                    return true;
                }

                var field = AccessTools.Field(type, upperName) ?? AccessTools.Field(type, lowerName);
                if (field != null)
                {
                    field.SetValue(null, value);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void ClearReplicationGameTimeSyncCache()
        {
            replicationWorldTimeManagerInstance = null;
        }
    }
}
