using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace VsDune;

public class GenSurfaceFlint : ModStdWorldGen
{
    private ICoreServerAPI sapi;
    private IWorldGenBlockAccessor wgba;
    private LCGRandom rnd;

    // sandBlockId -> looseflintsBlockId for the matching rock type
    private Dictionary<int, int> sandToFlint;

    // Per-chunk attempt count
    private const int AttemptsPerChunk = 3;

    private const int SubSeed = 71717;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override double ExecuteOrder() => 0.62;

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
        sandToFlint = new Dictionary<int, int>();
        var rockstrata = sapi.Assets.Get("worldgen/rockstrata.json").ToObject<RockStrataConfig>();

        foreach (var variant in rockstrata.Variants)
        {
            // BlockCode here is rock-{rocktype}
            string rocktype = variant.BlockCode.Path.Split('-')[1];

            var flintBlock = sapi.World.GetBlock(new AssetLocation("looseflints-" + rocktype + "-free"));
            if (flintBlock == null) continue;
            int flintId = flintBlock.BlockId;

            // Map every sand and sandwavy variant for this rocktype
            AddSandMapping("sand-" + rocktype, flintId);
            AddSandMapping("sandwavy-" + rocktype, flintId);
        }

        if (sandToFlint.Count == 0)
        {
            sapi.Logger.Warning("[VSDune] GenSurfaceFlint resolved no sand->flint mappings. Surface flint will not spawn.");
        }
    }

    private void AddSandMapping(string sandCode, int flintBlockId)
    {
        var found = sapi.World.SearchBlocks(new AssetLocation(sandCode));
        if (found == null) return;
        foreach (var b in found)
        {
            if (b == null) continue;
            sandToFlint[b.BlockId] = flintBlockId;
        }
    }

    private void OnChunkColumnGen(IChunkColumnGenerateRequest request)
    {
        if (wgba == null || sandToFlint == null || sandToFlint.Count == 0) return;

        rnd.InitPositionSeed(request.ChunkX, request.ChunkZ);

        const int chunksize = GlobalConstants.ChunkSize;
        int chunkX = request.ChunkX;
        int chunkZ = request.ChunkZ;
        var heightmap = request.Chunks[0].MapChunk.RainHeightMap;
        int mapsizeY = sapi.WorldManager.MapSizeY;

        var pos = new BlockPos(Dimensions.NormalWorld);
        var abovePos = new BlockPos(Dimensions.NormalWorld);

        for (int i = 0; i < AttemptsPerChunk; i++)
        {
            int dx = rnd.NextInt(chunksize);
            int dz = rnd.NextInt(chunksize);
            int worldX = chunkX * chunksize + dx;
            int worldZ = chunkZ * chunksize + dz;

            int y = heightmap[dz * chunksize + dx];
            if (y <= 1 || y >= mapsizeY - 2) continue;

            pos.Set(worldX, y, worldZ);
            var surf = wgba.GetBlock(pos);
            if (surf == null) continue;

            if (!sandToFlint.TryGetValue(surf.BlockId, out int flintId)) continue;

            // Air-above check
            abovePos.Set(worldX, y + 1, worldZ);
            var above = wgba.GetBlock(abovePos);
            if (above == null) continue;
            if (above.BlockId != 0 && above.Replaceable < 9000) continue;

            wgba.SetBlock(flintId, abovePos);
        }
    }
}
