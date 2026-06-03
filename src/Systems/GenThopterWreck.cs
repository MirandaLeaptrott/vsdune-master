using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace VsDune;

public class GenThopterWreck : ModStdWorldGen
{
    private ICoreServerAPI sapi;
    private IWorldGenBlockAccessor wgba;
    private LCGRandom rnd;
    private int wreckBlockId = 0;

    private const float WreckChancePerChunk = 0.005f;
    private const int SubSeed = 339922;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override double ExecuteOrder() => 0.65;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);

        api.Event.InitWorldGenerator(InitWorldGen, "standard");
        api.Event.ChunkColumnGeneration(OnChunkColumnGen, EnumWorldGenPass.Vegetation, "standard");
        api.Event.GetWorldgenBlockAccessor(p => wgba = p.GetBlockAccessor(true));

        api.ChatCommands.Create("thopterwreck")
            .WithDescription("Place a thopter wreck at your feet (admin).")
            .RequiresPrivilege(Privilege.controlserver)
            .RequiresPlayer()
            .HandleWith(OnPlaceCommand);
    }

    private void InitWorldGen()
    {
        var block = sapi.World.GetBlock(new AssetLocation("vsdune", "thopterwreck"));
        wreckBlockId = block?.BlockId ?? 0;
        if (wreckBlockId == 0)
        {
            sapi.Logger.Warning("[VSDune] GenThopterWreck: vsdune:thopterwreck block not registered. Worldgen wrecks disabled.");
        }
    }

    private void OnChunkColumnGen(IChunkColumnGenerateRequest request)
    {
        if (wgba == null || wreckBlockId == 0) return;

        rnd.InitPositionSeed(request.ChunkX, request.ChunkZ);
        if (rnd.NextFloat() > WreckChancePerChunk) return;

        const int chunksize = GlobalConstants.ChunkSize;
        int chunkX = request.ChunkX;
        int chunkZ = request.ChunkZ;
        var heightmap = request.Chunks[0].MapChunk.RainHeightMap;
        int sealevel = sapi.World.SeaLevel;
        int mapsizeY = sapi.WorldManager.MapSizeY;

        // Try a few candidates
        var pos = new BlockPos(Dimensions.NormalWorld);
        var abovePos = new BlockPos(Dimensions.NormalWorld);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            int dx = rnd.NextInt(chunksize);
            int dz = rnd.NextInt(chunksize);
            int worldX = chunkX * chunksize + dx;
            int worldZ = chunkZ * chunksize + dz;

            int y = heightmap[dz * chunksize + dx];
            if (y <= sealevel + 1) continue;
            if (y >= mapsizeY - 4) continue;

            pos.Set(worldX, y, worldZ);
            var surf = wgba.GetBlock(pos);
            if (surf == null) continue;
            if (surf.BlockMaterial != EnumBlockMaterial.Sand) continue;

            abovePos.Set(worldX, y + 1, worldZ);
            var above = wgba.GetBlock(abovePos);
            if (above == null) continue;
            if (above.BlockId != 0 && above.Replaceable < 9000) continue;

            wgba.SetBlock(wreckBlockId, abovePos);
            sapi.Logger.Notification("[VSDune] Thopter wreck placed at ({0}, {1}, {2}).", worldX, y + 1, worldZ);
            return;
        }
    }

    private TextCommandResult OnPlaceCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer sp) return TextCommandResult.Error("No caller.");
        if (wreckBlockId == 0) return TextCommandResult.Error("vsdune:thopterwreck not registered.");
        var pos = sp.Entity.Pos.AsBlockPos.AddCopy(0, 0, 0);
        sapi.World.BlockAccessor.SetBlock(wreckBlockId, pos);
        return TextCommandResult.Success($"Wreck placed at {pos}.");
    }
}
