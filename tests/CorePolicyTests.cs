using System;
using GoingCooperative.Core;

internal static class CorePolicyTests
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

    public static int Main()
    {
        Equal(5, CoordinateResolverPolicy.ResolveY(CoordinateTargetKind.Building, true, 5, 15), "building uses map Y");
        Equal(15, CoordinateResolverPolicy.ResolveY(CoordinateTargetKind.MapResource, true, 5, 15), "map resource uses selection Y");
        Equal(15, CoordinateResolverPolicy.ResolveY(CoordinateTargetKind.ResourcePile, true, 5, 15), "pile contextual action uses selection Y");
        Equal(15, CoordinateResolverPolicy.ResolveY(CoordinateTargetKind.Stockpile, true, 5, 15), "stockpile region uses selection Y");
        Equal(15, CoordinateResolverPolicy.ResolveY(CoordinateTargetKind.Building, false, 0, 15), "building safely falls back");

        var research = LockstepCommandPayloads.CreateResearchActivatePayload("research_\"one");
        Equal(true, LockstepCommandPayloads.TryReadResearchActivatePayload(research, out var node), "research payload parses");
        Equal("research_\"one", node, "research payload roundtrip");

        var production = LockstepCommandPayloads.CreateProductionQueuePayload("SetCount", 10, 5, 12, 2, "meal_stew", 20);
        Equal(true, LockstepCommandPayloads.TryReadProductionQueuePayload(production, out var operation, out var x, out var y, out var z, out var index, out var blueprint, out var value), "production payload parses");
        Equal("SetCount", operation, "production operation");
        Equal(5, y, "production map Y");
        Equal(2, index, "production ticket index");
        Equal("meal_stew", blueprint, "production blueprint");
        Equal(20, value, "production value");

        Console.WriteLine(failures == 0 ? "PASS CorePolicyTests" : "FAILED " + failures);
        return failures == 0 ? 0 : 1;
    }
}
