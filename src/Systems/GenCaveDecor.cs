using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace VsDune;

public class GenCaveDecor : ModStdWorldGen
{
    private ICoreServerAPI sapi;
    private IWorldGenBlockAccessor wgba;
    private LCGRandom rnd;

    private int[] mushroomBlockIds;
    private int sealevel;
    private int mapsizeY;

    // Per-cell roll for mushroom placement on a qualifying cave-floor
    // cell. Mushrooms feel like a find rather than a carpet, so the
    // chance is small.
    private const float MushroomChance = 0.04f;

    // Y range to scan per column. Capped to keep deep-bedrock columns from
    // walking the full mapheight when there's nothing to find down there.
    private const int MaxBelowSealevel = 100;
    private const int MinAboveBedrock = 4;

    private const int SubSeed = 614392;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override double ExecuteOrder() => 0.43;

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
        sealevel = sapi.World.SeaLevel;
        mapsizeY = sapi.WorldManager.MapSizeY;

        // Mushrooms
        var mushroomFound = sapi.World.SearchBlocks(new AssetLocation("mushroom-*-normal"));
        var mushroomIds = new List<int>();
        if (mushroomFound != null)
        {
            foreach (var b in mushroomFound)
            {
                if (b != null) mushroomIds.Add(b.BlockId);
            }
        }
        mushroomBlockIds = mushroomIds.ToArray();

        if (mushroomBlockIds.Length == 0)
        {
            sapi.Logger.Warning("[VSDune] GenCaveDecor: no mushroom-*-normal blocks resolved. Cave mushrooms will not generate.");
        }
    }

    private void OnChunkColumnGen(IChunkColumnGenerateRequest request)
    {
        if (wgba == null) return;
        if (mushroomBlockIds.Length == 0) return;

        rnd.InitPositionSeed(request.ChunkX, request.ChunkZ);

        const int chunksize = GlobalConstants.ChunkSize;
        int yMax = sealevel - 5;
        int yMin = Math.Max(MinAboveBedrock, sealevel - MaxBelowSealevel);

        var pos = new BlockPos(Dimensions.NormalWorld);
        var below = new BlockPos(Dimensions.NormalWorld);

        int chunkX = request.ChunkX;
        int chunkZ = request.ChunkZ;

        for (int dx = 0; dx < chunksize; dx++)
        {
            for (int dz = 0; dz < chunksize; dz++)
            {
                int worldX = chunkX * chunksize + dx;
                int worldZ = chunkZ * chunksize + dz;

                // Walk down looking for the first cave floor
                for (int y = yMax; y >= yMin; y--)
                {
                    pos.Set(worldX, y, worldZ);
                    var blockHere = wgba.GetBlock(pos);
                    if (blockHere == null || blockHere.Replaceable < 6000) continue;

                    below.Set(worldX, y - 1, worldZ);
                    var blockBelow = wgba.GetBlock(below);
                    if (blockBelow == null || blockBelow.Replaceable >= 6000) continue;

                    // Cave floor confirmed. Roll for mushroom placement.
                    if (rnd.NextFloat() < MushroomChance)
                    {
                        int mushId = mushroomBlockIds[rnd.NextInt(mushroomBlockIds.Length)];
                        wgba.SetBlock(mushId, pos);
                    }

                    // One placement attempt per column, regardless of result,
                    // so the loop stays bounded.
                    break;
                }
            }
        }
    }
}
