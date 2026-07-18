using System;
using System.Collections.Generic;
using GoingCooperative.Core;
using GoingCooperative.Core.Replication;

internal static class ReplicationMotionCodecTests
{
    private static int failures;

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!Equals(expected, actual))
        {
            Console.Error.WriteLine("FAIL " + name + " expected=" + expected + " actual=" + actual);
            failures++;
        }
    }

    private static void True(bool value, string name)
    {
        if (!value)
        {
            Console.Error.WriteLine("FAIL " + name);
            failures++;
        }
    }

    public static int Main()
    {
        TestLegacyGoldenAndRoundTrip();
        TestSemanticAndMixedRoundTrip();
        TestMalformedSemanticMetadata();
        TestIncrementalPayloadBudget();
        Console.WriteLine(failures == 0 ? "PASS ReplicationMotionCodecTests" : "FAILED " + failures);
        return failures == 0 ? 0 : 1;
    }

    private static void TestLegacyGoldenAndRoundTrip()
    {
        var entity = Legacy("uid:1", 1, 2, 3);
        var envelope = ReplicationPayloadCodec.ForTransformSnapshot(
            "host",
            new ReplicationTransformSnapshot(5, 12.5f, new[] { entity }));
        Equal(
            ReplicationPayloadCodec.ProtocolVersion + "|5|12.5|1|dWlkOjE=,d29ya2Vy,1,2,3,0,0,0,1",
            envelope.Payload,
            "legacy transform layout remains byte exact");
        True(ReplicationPayloadCodec.TryReadTransformSnapshot(envelope, out var decoded, out _), "legacy decodes");
        True(decoded != null && decoded.Entities.Count == 1, "legacy count");
        True(decoded != null && !decoded.Entities[0].Motion.HasValue, "legacy motion remains absent");
    }

    private static void TestSemanticAndMixedRoundTrip()
    {
        var motion = new ReplicationEntityMotionMetadata(
            1.25f,
            -0.5f,
            2.75f,
            3.1f,
            ReplicationAgentLocomotionGait.Climb,
            true,
            true,
            true,
            true,
            -1,
            1.35f,
            42);
        var semantic = new ReplicationEntityTransform("uid:2", "animal", 4, 5, 6, 0, 0, 0, 1, motion);
        var envelope = ReplicationPayloadCodec.ForTransformSnapshot(
            "host",
            new ReplicationTransformSnapshot(6, 20f, new[] { Legacy("uid:1", 1, 2, 3), semantic }));
        True(ReplicationPayloadCodec.TryReadTransformSnapshot(envelope, out var decoded, out var error), "mixed snapshot decodes " + error);
        True(decoded != null && decoded.Entities.Count == 2, "mixed count");
        True(decoded != null && !decoded.Entities[0].Motion.HasValue, "mixed legacy entity absent motion");
        var decodedMotion = decoded != null ? decoded.Entities[1].Motion : null;
        True(decodedMotion.HasValue, "mixed semantic entity has motion");
        if (decodedMotion.HasValue)
        {
            var actual = decodedMotion.GetValueOrDefault();
            Equal(ReplicationAgentLocomotionGait.Climb, actual.Gait, "semantic gait");
            Equal(true, actual.IsMoving, "semantic moving flag");
            Equal(true, actual.IsRunning, "semantic running flag");
            Equal(true, actual.IsSwimming, "semantic swimming flag");
            Equal(true, actual.IsClimbing, "semantic climbing flag");
            Equal(-1, actual.ClimbDirection, "semantic climb direction");
            Equal(42L, actual.PathRevision, "semantic revision");
            Equal(1.35f, actual.AnimatorSpeed, "semantic animator speed");
        }
    }

    private static void TestMalformedSemanticMetadata()
    {
        Reject("dWlkOjE=,d29ya2Vy,1,2,3,0,0,0,1,m1", "short semantic fields");
        Reject("dWlkOjE=,d29ya2Vy,1,2,3,0,0,0,1,m2,1,0,0,1,2,1,0,1,1", "unknown semantic marker");
        Reject("dWlkOjE=,d29ya2Vy,1,2,3,0,0,0,1,m1,NaN,0,0,1,2,1,0,1,1", "NaN velocity");
        Reject("dWlkOjE=,d29ya2Vy,1,2,3,0,0,0,1,m1,1,0,0,-1,2,1,0,1,1", "negative movement speed");
        Reject("dWlkOjE=,d29ya2Vy,1,2,3,0,0,0,1,m1,1,0,0,1,99,1,0,1,1", "unknown gait");
        Reject("dWlkOjE=,d29ya2Vy,1,2,3,0,0,0,1,m1,1,0,0,1,2,16,0,1,1", "unknown flag bit");
        Reject("dWlkOjE=,d29ya2Vy,1,2,3,0,0,0,1,m1,1,0,0,1,2,1,0,-1,1", "negative animator speed");
        Reject("dWlkOjE=,d29ya2Vy,1,2,3,0,0,0,1,m1,1,0,0,1,2,1,0,1,-1", "negative revision");
    }

    private static void TestIncrementalPayloadBudget()
    {
        const int count = 128;
        var legacy = new List<ReplicationEntityTransform>(count);
        var semantic = new List<ReplicationEntityTransform>(count);
        var motion = new ReplicationEntityMotionMetadata(
            1.25f, 0, -2.5f, 2.8f, ReplicationAgentLocomotionGait.Run,
            true, true, false, false, 0, 1.1f, 7);
        for (var i = 0; i < count; i++)
        {
            legacy.Add(Legacy("uid:" + i, i, 2, 3));
            semantic.Add(new ReplicationEntityTransform("uid:" + i, "worker", i, 2, 3, 0, 0, 0, 1, motion));
        }

        var legacyLength = ReplicationPayloadCodec.ForTransformSnapshot(
            "host", new ReplicationTransformSnapshot(1, 1, legacy)).Payload.Length;
        var semanticLength = ReplicationPayloadCodec.ForTransformSnapshot(
            "host", new ReplicationTransformSnapshot(1, 1, semantic)).Payload.Length;
        var incrementalPerEntity = (semanticLength - legacyLength) / count;
        True(incrementalPerEntity <= 64, "semantic metadata incremental budget <=64 chars/entity actual=" + incrementalPerEntity);
        Console.WriteLine("Motion payload budget entities=" + count
            + " legacyChars=" + legacyLength
            + " semanticChars=" + semanticLength
            + " incrementalPerEntity=" + incrementalPerEntity);
    }

    private static ReplicationEntityTransform Legacy(string id, float x, float y, float z)
    {
        return new ReplicationEntityTransform(id, "worker", x, y, z, 0, 0, 0, 1);
    }

    private static void Reject(string encodedEntity, string name)
    {
        var envelope = new TransportEnvelope(
            TransportMessageKind.ReplicationTransformSnapshot,
            1,
            "host",
            ReplicationPayloadCodec.ProtocolVersion + "|1|1|1|" + encodedEntity);
        True(!ReplicationPayloadCodec.TryReadTransformSnapshot(envelope, out _, out _), "rejects " + name);
    }
}
