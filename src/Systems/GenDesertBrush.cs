using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace VsDune;


public class GenDesertBrush : ModStdWorldGen
{
    private ICoreServerAPI sapi;
    private IWorldGenBlockAccessor wgba;
    private LCGRandom rnd;

    private HashSet<int> rockBlockIds;
    private HashSet<int> sandLikeBlockIds;
    private int[] grassBlockIds;
    private int blackcurrantBushId = -1;


    private const int NeighborRadius = 2;
    private const int VerticalReach = 1;
    private const float BasePlaceChance = 0.006f;
    // Of rolled placements: 30% bushes, 70% tallgrass.
    private const float BushFraction = 0.30f;
    private const int SubSeed = 412791;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    // Run after vanilla GenVegetationAndPatches (0.5).
    public override double ExecuteOrder() => 0.6;

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
        // Cache rock block IDs from rockstrata so the adjacency check can
        // identify exposed rock at runtime without per-column lookups.
        var rockstrata = sapi.Assets.Get("worldgen/rockstrata.json").ToObject<RockStrataConfig>();
        rockBlockIds = new HashSet<int>();
        sandLikeBlockIds = new HashSet<int>();

        foreach (var variant in rockstrata.Variants)
        {
            var rockBlock = sapi.World.GetBlock(variant.BlockCode);
            if (rockBlock != null) rockBlockIds.Add(rockBlock.BlockId);

            // Sand and sandwavy variants for this rocktype. We only place
            // brush over these; gravel and exposed rock surfaces stay bare.
            string rocktype = variant.BlockCode.Path.Split('-')[1];
            AddSearchedBlocks(sandLikeBlockIds, "sand-" + rocktype);
            AddSearchedBlocks(sandLikeBlockIds, "sandwavy-" + rocktype);
        }

        // Pick uniformly from all six free-standing tallgrass heights.
        var grassList = new List<int>();
        AddResolvedBlocks(grassList, "tallgrass-veryshort-free");
        AddResolvedBlocks(grassList, "tallgrass-short-free");
        AddResolvedBlocks(grassList, "tallgrass-mediumshort-free");
        AddResolvedBlocks(grassList, "tallgrass-medium-free");
        AddResolvedBlocks(grassList, "tallgrass-tall-free");
        AddResolvedBlocks(grassList, "tallgrass-verytall-free");
        grassBlockIds = grassList.ToArray();

        if (grassBlockIds.Length == 0)
        {
            sapi.Logger.Warning("[VSDune] GenDesertBrush could not resolve any tallgrass blocks. Surface vegetation will not spawn.");
        }


        var bush = sapi.World.GetBlock(new AssetLocation("game", "fruitingbush-wild-blackcurrant-free"));
        blackcurrantBushId = bush?.BlockId ?? -1;
        if (blackcurrantBushId < 0)
        {
            sapi.Logger.Warning("[VSDune] GenDesertBrush could not resolve fruitingbush-wild-blackcurrant-free. Bushes will not spawn.");
        }
    }

    private void AddResolvedBlocks(List<int> list, string codePath)
    {
        var found = sapi.World.SearchBlocks(new AssetLocation(codePath));
        if (found == null) return;
        foreach (var b in found)
        {
            if (b != null) list.Add(b.BlockId);
        }
    }

    private void AddSearchedBlocks(HashSet<int> set, string code)
    {
        var blocks = sapi.World.SearchBlocks(new AssetLocation(code));
        if (blocks == null) return;
        foreach (var b in blocks)
        {
            if (b != null) set.Add(b.BlockId);
        }
    }

    private void OnChunkColumnGen(IChunkColumnGenerateRequest request)
    {
        if (grassBlockIds == null || grassBlockIds.Length == 0) return;
        if (wgba == null) return;

        int chunkX = request.ChunkX;
        int chunkZ = request.ChunkZ;
        var mapChunk = request.Chunks[0].MapChunk;
        var heightmap = mapChunk.RainHeightMap;

        rnd.InitPositionSeed(chunkX, chunkZ);

        var pos = new BlockPos(Dimensions.NormalWorld);
        var npos = new BlockPos(Dimensions.NormalWorld);

        const int chunksize = GlobalConstants.ChunkSize;
        int mapsizeY = sapi.WorldManager.MapSizeY;
        int radiusSq = NeighborRadius * NeighborRadius;

        for (int dx = 0; dx < chunksize; dx++)
        {
            for (int dz = 0; dz < chunksize; dz++)
            {
                if (rnd.NextFloat() > BasePlaceChance) continue;

                int worldX = chunkX * chunksize + dx;
                int worldZ = chunkZ * chunksize + dz;
                int y = heightmap[dz * chunksize + dx];
                if (y <= 1 || y >= mapsizeY - 2) continue;

                pos.Set(worldX, y, worldZ);
                var surf = wgba.GetBlock(pos);
                if (surf == null || !sandLikeBlockIds.Contains(surf.BlockId)) continue;

                bool nearRock = false;
                for (int rx = -NeighborRadius; rx <= NeighborRadius && !nearRock; rx++)
                {
                    for (int rz = -NeighborRadius; rz <= NeighborRadius && !nearRock; rz++)
                    {
                        if (rx == 0 && rz == 0) continue;
                        // Only sample at our Y level and one above. We want to
                        // detect the foot of an outcrop (rock at our feet or
                        // the first block of cliff face), not a high cliff
                        // overhead.
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

                // Place above the surface; skip if something non-replaceable
                // is already there (e.g. a structure block above the sand).
                pos.Set(worldX, y + 1, worldZ);
                var existing = wgba.GetBlock(pos);
                if (existing != null && existing.Replaceable < 6000) continue;

                // Roll bush vs grass. Bush only if it resolved at init,
                // otherwise fall through to grass.
                int placeId;
                bool placedBush = false;
                if (blackcurrantBushId >= 0 && rnd.NextFloat() < BushFraction)
                {
                    placeId = blackcurrantBushId;
                    placedBush = true;
                }
                else
                {
                    placeId = grassBlockIds[rnd.NextInt(grassBlockIds.Length)];
                }
                wgba.SetBlock(placeId, pos);

                if (placedBush)
                {
                    var be = wgba.GetBlockEntity(pos);
                    if (be != null)
                    {
                        foreach (var beb in be.Behaviors)
                        {
                            beb.OnBlockPlaced(null);
                        }
                    }
                }
            }
        }
    }
}
