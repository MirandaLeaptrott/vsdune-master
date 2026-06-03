using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace VsDune;


public class GenArrakisRivulets : ModStdWorldGen
{
    private ICoreServerAPI sapi;
    private IWorldGenBlockAccessor wgba;
    private LCGRandom rnd;
    private int waterBlockId;
    private const int AttemptsPerChunk = 500;
    private const bool LogPlacements = false;

    private const int DeepBuffer = 40;

    private const int SubSeed = 778822;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override double ExecuteOrder() => 0.44;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);

        api.Event.InitWorldGenerator(InitWorldGen, "standard");
        api.Event.ChunkColumnGeneration(OnChunkColumnGen, EnumWorldGenPass.Terrain, "standard");
        api.Event.GetWorldgenBlockAccessor(p => wgba = p.GetBlockAccessor(true));
    }

    private void InitWorldGen()
    {
        var water = sapi.World.GetBlock(new AssetLocation("game", "water-still-7"))
                 ?? sapi.World.GetBlock(new AssetLocation("survival", "water-still-7"));
        if (water == null)
        {
            var found = sapi.World.SearchBlocks(new AssetLocation("water-still-7"));
            if (found != null && found.Length > 0) water = found[0];
        }
        waterBlockId = water?.BlockId ?? 0;
        if (waterBlockId == 0)
        {
            sapi.Logger.Warning("[VSDune] GenArrakisRivulets could not resolve water-still-7. Rivulets will not spawn.");
        }
    }

    private void OnChunkColumnGen(IChunkColumnGenerateRequest request)
    {
        if (wgba == null || waterBlockId == 0) return;

        rnd.InitPositionSeed(request.ChunkX, request.ChunkZ);

        const int chunksize = GlobalConstants.ChunkSize;
        int yMin = 8;
        int sealevel = sapi.World.SeaLevel;
        var heightmap = request.Chunks[0].MapChunk.WorldGenTerrainHeightMap;

        int chunkX = request.ChunkX;
        int chunkZ = request.ChunkZ;

        int placed = 0;
        for (int i = 0; i < AttemptsPerChunk; i++)
        {
            int dx = 1 + rnd.NextInt(chunksize - 2);
            int dz = 1 + rnd.NextInt(chunksize - 2);

            // Deep-only: rivulets are hidden moisture pockets in the
            // rockstrata, not near-surface puddles.
            int localSurface = heightmap[dz * chunksize + dx];
            int surfaceCap = localSurface - 5;
            int deepCap = sealevel - DeepBuffer;
            int yMax = System.Math.Min(surfaceCap, deepCap);
            if (yMax <= yMin) continue;

            int y = yMin + rnd.NextInt(yMax - yMin);

            int worldX = chunkX * chunksize + dx;
            int worldZ = chunkZ * chunksize + dz;

            if (TryPlaceRivulet(worldX, y, worldZ)) placed++;
        }

        if (LogPlacements && placed > 0)
        {
            sapi.Logger.Notification("[VSDune] GenArrakisRivulets: placed {0} rivulets in chunk ({1}, {2})", placed, chunkX, chunkZ);
        }
    }

    private static readonly int[][] FaceOffsets =
    {
        new[] {  1,  0,  0 },
        new[] { -1,  0,  0 },
        new[] {  0,  1,  0 }, // up (index 2)
        new[] {  0, -1,  0 }, // down (index 3)
        new[] {  0,  0,  1 },
        new[] {  0,  0, -1 },
    };

    private bool TryPlaceRivulet(int x, int y, int z)
    {
        var pos = new BlockPos(Dimensions.NormalWorld);

        // The center cell must be solid stone (we're punching a hole in
        // the rock and filling it with water).
        pos.Set(x, y, z);
        var center = wgba.GetBlock(pos);
        if (center == null || center.BlockMaterial != EnumBlockMaterial.Stone) return false;

        int solid = 0;
        int air = 0;

        for (int i = 0; i < 6; i++)
        {
            int fx = x + FaceOffsets[i][0];
            int fy = y + FaceOffsets[i][1];
            int fz = z + FaceOffsets[i][2];

            pos.Set(fx, fy, fz);
            var nb = wgba.GetBlock(pos);
            if (nb == null) return false;

            bool nbSolid = nb.BlockMaterial == EnumBlockMaterial.Stone;
            bool nbAir = nb.BlockMaterial == EnumBlockMaterial.Air;

            if (nbSolid) solid++;
            if (nbAir) air++;
            if (!nbSolid)
            {
                if (i == 2) return false; // up neighbor is air, skip
                if (i == 3)
                {
                    pos.Set(x, y + 1, z);
                    var above = wgba.GetBlock(pos);
                    if (above == null || above.BlockMaterial != EnumBlockMaterial.Stone) return false;
                }
            }
        }

        if (solid != 5 || air != 1) return false;

        pos.Set(x, y, z);
        wgba.SetBlock(0, pos);
        wgba.SetBlock(waterBlockId, pos, BlockLayersAccess.Fluid);
        wgba.ScheduleBlockUpdate(pos);
        return true;
    }
}
