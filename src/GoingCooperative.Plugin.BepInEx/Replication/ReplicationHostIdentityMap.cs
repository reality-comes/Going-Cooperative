using System;
using System.Collections.Generic;
using System.Globalization;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private static readonly Dictionary<long, object> ReplicationLocalObjectByHostId = new Dictionary<long, object>();
        private static readonly Dictionary<int, long> ReplicationHostIdByLocalObject = new Dictionary<int, long>();
        private static readonly Dictionary<string, object> ReplicationResourcePileByLocationKey = new Dictionary<string, object>(StringComparer.Ordinal);
        private static readonly Dictionary<int, string> ReplicationResourcePileLocationKeyByLocalObject = new Dictionary<int, string>();
        private static long replicationHostIdentityRegistrations;
        private static long replicationHostIdentityRemovals;
        private static long replicationHostIdentityHits;
        private static long replicationHostIdentityMisses;
        private static string replicationLastHostIdentitySummary = string.Empty;
        private static bool replicationResourcePileLocationIndexComplete;
        private static int replicationResourcePileLocationIndexNextIndex;
        private static float replicationNextResourcePileLocationIndexRealtime;

        private static bool TryGetReplicationLocalObjectByHostId(long hostId, out object? localObject, out string detail)
        {
            localObject = null;
            if (hostId <= 0)
            {
                detail = "host-id-invalid";
                return false;
            }

            lock (ReplicationWorldObjectDeltaLock)
            {
                if (ReplicationLocalObjectByHostId.TryGetValue(hostId, out var mapped) && mapped != null)
                {
                    replicationHostIdentityHits++;
                    localObject = mapped;
                    detail = "host-id-hit hostId=" + hostId.ToString(CultureInfo.InvariantCulture);
                    return true;
                }
            }

            replicationHostIdentityMisses++;
            detail = "host-id-miss hostId=" + hostId.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        private static void RegisterReplicationHostIdentity(long hostId, object? localObject, string source)
        {
            if (hostId <= 0 || localObject == null)
            {
                return;
            }

            var localKey = GetReplicationLocalObjectKey(localObject);
            lock (ReplicationWorldObjectDeltaLock)
            {
                ReplicationLocalObjectByHostId[hostId] = localObject;
                ReplicationHostIdByLocalObject[localKey] = hostId;
                replicationHostIdentityRegistrations++;
                replicationLastHostIdentitySummary = "register hostId="
                    + hostId.ToString(CultureInfo.InvariantCulture)
                    + " localKey="
                    + localKey.ToString(CultureInfo.InvariantCulture)
                    + " source="
                    + source;
            }
        }

        private static void RemoveReplicationHostIdentity(long hostId, object? localObject, string source)
        {
            if (hostId <= 0 && localObject == null)
            {
                return;
            }

            lock (ReplicationWorldObjectDeltaLock)
            {
                if (hostId <= 0 && localObject != null)
                {
                    ReplicationHostIdByLocalObject.TryGetValue(GetReplicationLocalObjectKey(localObject), out hostId);
                }

                if (hostId > 0)
                {
                    ReplicationLocalObjectByHostId.Remove(hostId);
                }

                if (localObject != null)
                {
                    ReplicationHostIdByLocalObject.Remove(GetReplicationLocalObjectKey(localObject));
                }

                replicationHostIdentityRemovals++;
                replicationLastHostIdentitySummary = "remove hostId="
                    + hostId.ToString(CultureInfo.InvariantCulture)
                    + " source="
                    + source;
            }
        }

        private static bool TryGetReplicationResourcePileByLocationKey(string locationKey, out object? localObject, out string detail)
        {
            localObject = null;
            if (string.IsNullOrWhiteSpace(locationKey))
            {
                detail = "pile-location-key-empty";
                return false;
            }

            lock (ReplicationWorldObjectDeltaLock)
            {
                if (ReplicationResourcePileByLocationKey.TryGetValue(locationKey, out var mapped) && mapped != null)
                {
                    localObject = mapped;
                    detail = "pile-location-hit key=" + locationKey;
                    return true;
                }
            }

            detail = "pile-location-miss key=" + locationKey;
            return false;
        }

        private static void RegisterReplicationResourcePileLocation(string locationKey, object? localObject, string source)
        {
            if (string.IsNullOrWhiteSpace(locationKey) || localObject == null)
            {
                return;
            }

            var localKey = GetReplicationLocalObjectKey(localObject);
            lock (ReplicationWorldObjectDeltaLock)
            {
                ReplicationResourcePileByLocationKey[locationKey] = localObject;
                ReplicationResourcePileLocationKeyByLocalObject[localKey] = locationKey;
                replicationLastHostIdentitySummary = "register-pile-location key="
                    + locationKey
                    + " localKey="
                    + localKey.ToString(CultureInfo.InvariantCulture)
                    + " source="
                    + source;
            }
        }

        private static void RemoveReplicationResourcePileLocation(string locationKey, object? localObject, string source)
        {
            if (string.IsNullOrWhiteSpace(locationKey) && localObject == null)
            {
                return;
            }

            lock (ReplicationWorldObjectDeltaLock)
            {
                if (string.IsNullOrWhiteSpace(locationKey) && localObject != null)
                {
                    ReplicationResourcePileLocationKeyByLocalObject.TryGetValue(GetReplicationLocalObjectKey(localObject), out locationKey);
                }

                if (!string.IsNullOrWhiteSpace(locationKey))
                {
                    ReplicationResourcePileByLocationKey.Remove(locationKey);
                }

                if (localObject != null)
                {
                    ReplicationResourcePileLocationKeyByLocalObject.Remove(GetReplicationLocalObjectKey(localObject));
                }

                replicationLastHostIdentitySummary = "remove-pile-location key="
                    + (locationKey ?? string.Empty)
                    + " source="
                    + source;
            }
        }

        private static void ClearReplicationHostIdentityMap()
        {
            lock (ReplicationWorldObjectDeltaLock)
            {
                ReplicationLocalObjectByHostId.Clear();
                ReplicationHostIdByLocalObject.Clear();
                ReplicationResourcePileByLocationKey.Clear();
                ReplicationResourcePileLocationKeyByLocalObject.Clear();
                replicationResourcePileLocationIndexComplete = false;
                replicationResourcePileLocationIndexNextIndex = 0;
                replicationNextResourcePileLocationIndexRealtime = 0f;
                replicationHostIdentityRegistrations = 0;
                replicationHostIdentityRemovals = 0;
                replicationHostIdentityHits = 0;
                replicationHostIdentityMisses = 0;
                replicationLastHostIdentitySummary = string.Empty;
            }
        }

        private static int GetReplicationLocalObjectKey(object localObject)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(localObject);
        }
    }
}
