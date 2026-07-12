namespace GoingCooperative.Core
{
    public enum CoordinateTargetKind
    {
        Unknown = 0,
        Building = 1,
        MapResource = 2,
        ResourcePile = 3,
        Stockpile = 4
    }

    public static class CoordinateResolverPolicy
    {
        public static bool UsesAuthoritativeGrid(CoordinateTargetKind kind)
        {
            return kind == CoordinateTargetKind.Building;
        }

        public static int ResolveY(CoordinateTargetKind kind, bool hasGrid, int gridY, int worldY)
        {
            if (UsesAuthoritativeGrid(kind) && hasGrid)
            {
                return gridY;
            }

            if (kind == CoordinateTargetKind.MapResource
                || kind == CoordinateTargetKind.ResourcePile
                || kind == CoordinateTargetKind.Stockpile)
            {
                return worldY;
            }

            return hasGrid ? gridY : worldY;
        }
    }
}
