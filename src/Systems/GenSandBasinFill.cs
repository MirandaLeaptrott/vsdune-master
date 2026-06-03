using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace VsDune;

// Fill the dry basin pits (former vanilla lakes / oceans) with sand
public class GenSandBasinFill : ModStdWorldGen
{
    private ICoreServerAPI sapi;
    private IWorldGenBlockAccessor wgba;
    private int sealevel;

    // sandwavy-<rocktype> variants resolved at init, picked per column by underlying rock
    private readonly Dictionary<string, int> sandwavyByRock = new();
    private int fallbackSandBlockId;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override double ExecuteOrder() => 0.42;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.InitWorldGenerator(InitWorldGen, "standard");
        api.Event.ChunkColumnGeneration(OnChunkColumnGen, EnumWorldGenPass.Terrain, "standard");
        // Safety-net second pass. Runs in the Vegetation pass 
        api.Event.ChunkColumnGeneration(OnChunkColumnPostFill, EnumWorldGenPass.Vegetation, "standard");
        api.Event.GetWorldgenBlockAccessor(p => wgba = p.GetBlockAccessor(true));
    }

    private void InitWorldGen()
    {
        sealevel = sapi.World.SeaLevel;

        // Resolve every sandwavy-<rocktype> we can find so the second-pass
        // fill matches the column's underlying rock instead of slamming
        // granite next to neighbors of other rocktypes.
        var hits = sapi.World.SearchBlocks(new AssetLocation("game", "sandwavy-*"));
        if (hits != null)
        {
            foreach (var b in hits)
            {
                if (b == null) continue;
                string rock = b.Variant?["rock"];
                if (string.IsNullOrEmpty(rock)) continue;
                sandwavyByRock[rock] = b.BlockId;
            }
        }

        // Fallback if rocktype lookup fails or no neighbor sand is reachable.
        if (sandwavyByRock.TryGetValue("granite", out var graniteId))
        {
            fallbackSandBlockId = graniteId;
        }
        else
        {
            var fallback = sapi.World.GetBlock(new AssetLocation("game", "sandwavy-granite"));
            fallbackSandBlockId = fallback?.BlockId ?? 0;
        }

        if (fallbackSandBlockId == 0)
        {
            sapi.Logger.Warning("[VSDune] GenSandBasinFill could not resolve any sandwavy-<rocktype> variant. Second-pass hole-filling disabled.");
        }
    }

    private void OnChunkColumnGen(IChunkColumnGenerateRequest request)
    {
        if (wgba == null) return;

        var mapChunk = request.Chunks[0].MapChunk;
        var rainmap = mapChunk.RainHeightMap;
        var terrainmap = mapChunk.WorldGenTerrainHeightMap;
        const int chunksize = GlobalConstants.ChunkSize;
        var pos = new BlockPos(Dimensions.NormalWorld);

        int chunkX = request.ChunkX;
        int chunkZ = request.ChunkZ;

        for (int dx = 0; dx < chunksize; dx++)
        {
            for (int dz = 0; dz < chunksize; dz++)
            {
                int idx = dz * chunksize + dx;
                int y = rainmap[idx];
                if (y >= sealevel) continue;

                int worldX = chunkX * chunksize + dx;
                int worldZ = chunkZ * chunksize + dz;

                // Sample the bed block.
                pos.Set(worldX, y, worldZ);
                var bedBlock = wgba.GetBlock(pos);
                if (bedBlock == null || bedBlock.BlockId == 0) continue;
                if (bedBlock.BlockMaterial != EnumBlockMaterial.Sand) continue;
                int fillId = bedBlock.BlockId;

                // Cave-shaft discriminator
                int probeY = y + 1;
                int solidNeighbors = 0;
                var npos = new BlockPos(Dimensions.NormalWorld);
                int[][] deltas = { new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 } };
                foreach (var d in deltas)
                {
                    npos.Set(worldX + d[0], probeY, worldZ + d[1]);
                    var nb = wgba.GetBlock(npos);
                    if (nb == null) { solidNeighbors++; continue; }
                    // Replaceable >= 6000 == air-ish (air, dewdrop, tall
                    // grass). Anything below is solid (rock, sand,
                    // gravel, etc).
                    if (nb.Replaceable < 6000) solidNeighbors++;
                }
                if (solidNeighbors >= 4) continue;

                // Fill from bed+1 to sealevel. Stop at the first solid
                // block so cave roofs (carved earlier at order 0.3)
                // aren't plugged with sand.
                for (int fy = y + 1; fy <= sealevel; fy++)
                {
                    pos.Set(worldX, fy, worldZ);
                    var existing = wgba.GetBlock(pos);
                    if (existing == null) break;
                    if (existing.Replaceable < 6000) break;
                    wgba.SetBlock(fillId, pos);
                }

                // Update heightmaps so downstream gen passes 
                rainmap[idx] = (ushort)sealevel;
                if (terrainmap != null) terrainmap[idx] = (ushort)sealevel;
            }
        }
    }

    // Safety net for basin columns the first pass skipped
    private void OnChunkColumnPostFill(IChunkColumnGenerateRequest request)
    {
        if (wgba == null || fallbackSandBlockId == 0) return;

        var mapChunk = request.Chunks[0].MapChunk;
        var rainmap = mapChunk.RainHeightMap;
        var terrainmap = mapChunk.WorldGenTerrainHeightMap;
        if (terrainmap == null) return;

        const int chunksize = GlobalConstants.ChunkSize;
        var pos = new BlockPos(Dimensions.NormalWorld);

        int chunkX = request.ChunkX;
        int chunkZ = request.ChunkZ;

        for (int dx = 0; dx < chunksize; dx++)
        {
            for (int dz = 0; dz < chunksize; dz++)
            {
                int idx = dz * chunksize + dx;

                // Gate: only operate on basin columns
                int stoneTop = terrainmap[idx];
                if (stoneTop >= sealevel) continue;

                int worldX = chunkX * chunksize + dx;
                int worldZ = chunkZ * chunksize + dz;

                // Quick check: is the block at sealevel air?
                pos.Set(worldX, sealevel, worldZ);
                var atSeaLevel = wgba.GetBlock(pos);
                if (atSeaLevel == null) continue;
                if (atSeaLevel.Replaceable < 6000) continue;

                // Match fill to the column's bedrock so basins blend with neighbors instead of slamming granite into a mixed-rock area.
                int fillId = fallbackSandBlockId;
                for (int scan = 0; scan < 4; scan++)
                {
                    int py = stoneTop - scan;
                    if (py <= 0) break;
                    pos.Set(worldX, py, worldZ);
                    var rockBlock = wgba.GetBlock(pos);
                    string rock = rockBlock?.Variant?["rock"];
                    if (!string.IsNullOrEmpty(rock) && sandwavyByRock.TryGetValue(rock, out var matchId))
                    {
                        fillId = matchId;
                        break;
                    }
                }

                // Walk down from sealevel.
                int topFilled = -1;
                for (int fy = sealevel; fy > 0; fy--)
                {
                    pos.Set(worldX, fy, worldZ);
                    var existing = wgba.GetBlock(pos);
                    if (existing == null) break;
                    if (existing.Replaceable < 6000) break;
                    wgba.SetBlock(fillId, pos);
                    if (topFilled < 0) topFilled = fy;
                }

                // Update both heightmaps
                if (topFilled >= 0)
                {
                    if (rainmap[idx] < topFilled) rainmap[idx] = (ushort)topFilled;
                    if (terrainmap[idx] < topFilled) terrainmap[idx] = (ushort)topFilled;
                }
            }
        }
    }
}
