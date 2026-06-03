using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace VsDune;


public class GenDesertShrubs : ModStdWorldGen
{
    private ICoreServerAPI sapi;
    private IWorldGenBlockAccessor wgba;
    private LCGRandom rnd;

    private ITreeGenerator pricklymoses;
    private int[] sandLikeBlockIds;
    private HashSet<int> rockBlockIds;

    // Per-chunk attempt count. Most skip on the sand-surface or
    // rock-shadow gate, so attempts can stay low.
    private const int AttemptsPerChunk = 2;

    // Match the brush's adjacency window. NeighborRadius 2 + VerticalReach
    // 1 means shrubs only place when literally at the foot of a cliff
    // face or against a boulder, not 4-5 blocks out.
    private const int NeighborRadius = 2;
    private const int VerticalReach = 1;

    private const int SubSeed = 332211;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override double ExecuteOrder() => 0.55;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);

        api.Event.InitWorldGenerator(InitWorldGen, "standard");
        api.Event.ChunkColumnGeneration(OnChunkColumnGen, EnumWorldGenPass.Vegetation, "standard");
        api.Event.GetWorldgenBlockAccessor(p => wgba = p.GetBlockAccessor(true));
    }

    private void InitWorldGen()
    {

        if (sapi.World.TreeGenerators != null)
        {
            sapi.World.TreeGenerators.TryGetValue(new AssetLocation("game", "pricklymoses"), out pricklymoses);
            if (pricklymoses == null)
            {
                sapi.World.TreeGenerators.TryGetValue(new AssetLocation("survival", "pricklymoses"), out pricklymoses);
            }
            if (pricklymoses == null)
            {
                sapi.World.TreeGenerators.TryGetValue(new AssetLocation("pricklymoses"), out pricklymoses);
            }
        }
        if (pricklymoses == null)
        {
            sapi.Logger.Warning("[VSDune] GenDesertShrubs: pricklymoses tree generator not registered. Shrubs will not spawn.");
        }

        var sandSet = new HashSet<int>();
        rockBlockIds = new HashSet<int>();
        var rockstrata = sapi.Assets.Get("worldgen/rockstrata.json").ToObject<RockStrataConfig>();
        foreach (var variant in rockstrata.Variants)
        {
            var rockBlock = sapi.World.GetBlock(variant.BlockCode);
            if (rockBlock != null) rockBlockIds.Add(rockBlock.BlockId);

            string rocktype = variant.BlockCode.Path.Split('-')[1];
            AddSearched(sandSet, "sand-" + rocktype);
            AddSearched(sandSet, "sandwavy-" + rocktype);
        }
        sandLikeBlockIds = new int[sandSet.Count];
        sandSet.CopyTo(sandLikeBlockIds);
    }

    private void AddSearched(HashSet<int> set, string code)
    {
        var found = sapi.World.SearchBlocks(new AssetLocation(code));
        if (found == null) return;
        foreach (var b in found)
        {
            if (b != null) set.Add(b.BlockId);
        }
    }

    private bool IsSandLike(int blockId)
    {
        for (int i = 0; i < sandLikeBlockIds.Length; i++)
        {
            if (sandLikeBlockIds[i] == blockId) return true;
        }
        return false;
    }

    private void OnChunkColumnGen(IChunkColumnGenerateRequest request)
    {
        if (wgba == null || pricklymoses == null) return;

        rnd.InitPositionSeed(request.ChunkX, request.ChunkZ);

        const int chunksize = GlobalConstants.ChunkSize;
        int chunkX = request.ChunkX;
        int chunkZ = request.ChunkZ;
        var heightmap = request.Chunks[0].MapChunk.RainHeightMap;
        int mapsizeY = sapi.WorldManager.MapSizeY;

        var pos = new BlockPos(Dimensions.NormalWorld);
        var npos = new BlockPos(Dimensions.NormalWorld);
        int radiusSq = NeighborRadius * NeighborRadius;

        var growParams = new TreeGenParams
        {
            size = 0.25f,
            skipForestFloor = true,
            vinesGrowthChance = 0f,
            mossGrowthChance = 0f,
            otherBlockChance = 1f,
        };

        for (int i = 0; i < AttemptsPerChunk; i++)
        {
            int dx = rnd.NextInt(chunksize);
            int dz = rnd.NextInt(chunksize);
            int worldX = chunkX * chunksize + dx;
            int worldZ = chunkZ * chunksize + dz;

            int y = heightmap[dz * chunksize + dx];
            if (y <= 1 || y >= mapsizeY - 16) continue;

            // Surface block must be sand or sandwavy (not gravel, not
            // exposed rock, not anything custom that happens to share Y).
            pos.Set(worldX, y, worldZ);
            var surf = wgba.GetBlock(pos);
            if (surf == null || !IsSandLike(surf.BlockId)) continue;

            bool nearRock = false;
            for (int rx = -NeighborRadius; rx <= NeighborRadius && !nearRock; rx++)
            {
                for (int rz = -NeighborRadius; rz <= NeighborRadius && !nearRock; rz++)
                {
                    if (rx == 0 && rz == 0) continue;
                    for (int ry = 0; ry <= VerticalReach && !nearRock; ry++)
                    {
                        if (rx * rx + rz * rz + ry * ry > radiusSq) continue;
                        npos.Set(worldX + rx, y + ry, worldZ + rz);
                        var nb = wgba.GetBlock(npos);
                        if (nb != null && rockBlockIds.Contains(nb.BlockId))
                        {
                            nearRock = true;
                        }
                    }
                }
            }
            if (!nearRock) continue;

            pos.Set(worldX, y + 1, worldZ);
            pricklymoses.GrowTree(wgba, pos, growParams, rnd);
        }
    }
}
