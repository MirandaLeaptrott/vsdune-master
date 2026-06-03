using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsDune;


public class GenDewSystem : ModSystem
{
    private ICoreServerAPI sapi;
    private LCGRandom rnd;

    private int dewdropBlockId;
    private HashSet<int> dewablePlantIds;

    // Per-day schedule tracking. trackedDay resets the step/clear flags
    // on calendar-day rollover; lastClearedDay is persisted.
    private int trackedDay = -1;
    private int stepsFiredMask = 0;
    private bool clearedToday = false;
    private int lastClearedDay = -1;

    // How often the calendar is polled (real seconds). Cheap.
    private const float PollIntervalSeconds = 5f;

    // Overnight buildup (in-game hour, per-column placement chance).
    // Coverage ramps sparse -> full as dawn approaches; tunable.
    private static readonly (float hour, float chance)[] DewSteps =
    {
        (2.0f,  0.15f),  // deep night: a few drops
        (4.0f,  0.45f),  // pre-dawn: building
        (6.5f,  1.00f),  // dawn: full coverage
    };

    // Morning sun burns the dew off. Clear pass runs once past this hour.
    private const float DewClearHour = 10.0f;

    // Player-radius scan bound. Placement and clear both use it.
    private const int RegenRadiusBlocks = 150;

    // How far above the surface heightmap to scan for canopy / tallgrass
    // tops. Pricklymoses leaves can sit a few blocks above the surface
    // sand; tallgrass sits at heightmap+1. 8 covers both with margin.
    private const int VerticalScanAboveSurface = 8;

    private const int SubSeed = 7711;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);

        api.Event.RegisterGameTickListener(PollDew, (int)(PollIntervalSeconds * 1000));

        // Load persisted lastClearedDay once save data is ready, so a
        // reload after the morning clear doesn't replay the night's dew.
        api.Event.SaveGameLoaded += OnSaveGameLoaded;

        // /dew: force an immediate full dew placement in the caller's
        // area. Testing aid; skips the calendar schedule.
        api.ChatCommands.Create("dew")
            .WithDescription("Force an immediate full dew placement in your area.")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(OnDewCommand);
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        base.AssetsFinalize(api);
        if (api.Side != EnumAppSide.Server) return;

        // AssetsFinalize runs BEFORE StartServerSide, so snapshot the
        // server api now so ResolveBlockIds can use it.
        sapi = (ICoreServerAPI)api;

        ResolveBlockIds();
    }

    // SaveGame key for the persisted "last day the morning clear ran".
    // Kept under the old name for save compatibility.
    private const string SaveKey = "vsdune.dewLastRegenDay";

    private void OnSaveGameLoaded()
    {
        try
        {
            byte[] data = sapi.WorldManager.SaveGame.GetData(SaveKey);
            if (data != null && data.Length == sizeof(int))
            {
                lastClearedDay = System.BitConverter.ToInt32(data, 0);
            }
        }
        catch (System.Exception ex)
        {
            sapi.Logger.Warning("[VSDune] GenDewSystem: failed to read persisted lastClearedDay: {0}. Leaving at -1.", ex.Message);
        }
    }

    private void PersistLastClearedDay()
    {
        try
        {
            sapi.WorldManager.SaveGame.StoreData(SaveKey, System.BitConverter.GetBytes(lastClearedDay));
        }
        catch (System.Exception ex)
        {
            sapi.Logger.Warning("[VSDune] GenDewSystem: failed to persist lastClearedDay: {0}.", ex.Message);
        }
    }

    private TextCommandResult OnDewCommand(TextCommandCallingArgs args)
    {
        var player = args.Caller.Player as IServerPlayer;
        if (player?.Entity == null)
        {
            return TextCommandResult.Error("Need a player caller.");
        }

        if (dewdropBlockId == 0)
        {
            return TextCommandResult.Error("dewdrop block not resolved. Check server log for AssetsFinalize warnings.");
        }
        if (dewablePlantIds == null || dewablePlantIds.Count == 0)
        {
            return TextCommandResult.Error("No dewable plant block ids resolved. Check server log.");
        }

        var diag = new DewDiagnostic();
        int placed = RegenForPlayer(player, 1.0f, diag);
        return TextCommandResult.Success(
            $"Dew: placed {placed} drops. " +
            $"Scanned {diag.ColumnsScanned} cols, {diag.ColumnsWithPlant} had plants, " +
            $"{diag.ColumnsBlockedAbove} blocked above. " +
            $"Eligible plant ids: {dewablePlantIds.Count}, dewdrop id: {dewdropBlockId}."
        );
    }

    private class DewDiagnostic
    {
        public int ColumnsScanned;
        public int ColumnsWithPlant;
        public int ColumnsBlockedAbove;
    }

    private void ResolveBlockIds()
    {
        var dewdrop = sapi.World.GetBlock(new AssetLocation("vsdune", "dewdrop"));
        dewdropBlockId = dewdrop?.BlockId ?? 0;
        if (dewdropBlockId == 0)
        {
            sapi.Logger.Warning("[VSDune] GenDewSystem: vsdune:dewdrop not registered. Dew regen disabled.");
        }

        dewablePlantIds = new HashSet<int>();
        // Tall plants only: dewdrop sits at plant_y + 1, just above the
        // visible top. Short grass is skipped (see header comment).
        AddSearched("tallgrass-medium-free");
        AddSearched("tallgrass-tall-free");
        AddSearched("tallgrass-verytall-free");
        AddSearched("leavesbranchy-grown-acacia");
        // Wild blackcurrant bushes (placed by GenDesertBrush).
        AddSearched("fruitingbush-wild-blackcurrant-free");
        // Pricklymoses (desert shrub from GenDesertShrubs).
        AddSearched("desertshrub-*");
    }

    private void AddSearched(string code)
    {
        var blocks = sapi.World.SearchBlocks(new AssetLocation(code));
        if (blocks == null) return;
        foreach (var b in blocks)
        {
            if (b == null) continue;
            dewablePlantIds.Add(b.BlockId);
        }
    }

    private void PollDew(float dt)
    {
        if (dewdropBlockId == 0) return;
        if (sapi.World.Calendar == null) return;

        int currentDay = (int)sapi.World.Calendar.TotalDays;
        float hour = sapi.World.Calendar.HourOfDay;

        // Day rollover: reset the per-day flags. If this day's cycle
        // already completed (clear ran, persisted), mark it done so a
        // reload doesn't replay the night's placements.
        if (currentDay != trackedDay)
        {
            trackedDay = currentDay;
            if (currentDay == lastClearedDay)
            {
                stepsFiredMask = (1 << DewSteps.Length) - 1;
                clearedToday = true;
            }
            else
            {
                stepsFiredMask = 0;
                clearedToday = false;
            }
        }

        if (clearedToday) return;

        // Morning clear: evaporate remaining dew once past the cutoff.
        if (hour >= DewClearHour)
        {
            int totalCleared = 0, playerCount = 0;
            foreach (var p in sapi.World.AllOnlinePlayers)
            {
                if (p is IServerPlayer sp) { totalCleared += ClearDewForPlayer(sp); playerCount++; }
            }
            if (playerCount > 0)
            {
                clearedToday = true;
                lastClearedDay = currentDay;
                PersistLastClearedDay();
                if (totalCleared > 0)
                {
                    sapi.Logger.Notification("[VSDune] Morning dew clear: removed {0} dewdrops across {1} player(s).", totalCleared, playerCount);
                }
            }
            return;
        }

        // Placement steps: fire each once its hour has arrived. Steps
        // whose hour already passed (late login) fire together so the
        // accumulated coverage lands correctly.
        for (int i = 0; i < DewSteps.Length; i++)
        {
            if ((stepsFiredMask & (1 << i)) != 0) continue;
            if (hour < DewSteps[i].hour) continue;

            int total = 0, players = 0;
            foreach (var p in sapi.World.AllOnlinePlayers)
            {
                if (p is IServerPlayer sp) { total += RegenForPlayer(sp, DewSteps[i].chance); players++; }
            }
            if (players > 0)
            {
                stepsFiredMask |= (1 << i);
                if (total > 0)
                {
                    sapi.Logger.Notification("[VSDune] Dew step {0:F1}h (chance {1:F2}): placed {2} dewdrops across {3} player(s).", DewSteps[i].hour, DewSteps[i].chance, total, players);
                }
            }
        }
    }

    private int RegenForPlayer(IServerPlayer player, float chance, DewDiagnostic diag = null)
    {
        if (player?.Entity == null) return 0;
        if (dewdropBlockId == 0 || dewablePlantIds == null || dewablePlantIds.Count == 0) return 0;

        int px = (int)player.Entity.Pos.X;
        int pz = (int)player.Entity.Pos.Z;

        var pos = new BlockPos(Dimensions.NormalWorld);
        var abovePos = new BlockPos(Dimensions.NormalWorld);
        int placed = 0;

        for (int dx = -RegenRadiusBlocks; dx <= RegenRadiusBlocks; dx++)
        {
            for (int dz = -RegenRadiusBlocks; dz <= RegenRadiusBlocks; dz++)
            {
                if (rnd.NextFloat() > chance) continue;
                if (diag != null) diag.ColumnsScanned++;

                int wx = px + dx;
                int wz = pz + dz;

                int? maybeY = sapi.WorldManager.GetSurfacePosY(wx, wz);
                if (maybeY == null) continue;
                int sy = maybeY.Value;
                if (sy <= 1) continue;

                // Scan top-down: find the highest dewable plant in this
                // column and place dew above it. Top-down so canopy gets
                // dew, not the leaves below the canopy.
                int top = System.Math.Min(sy + VerticalScanAboveSurface, sapi.WorldManager.MapSizeY - 2);
                int bottom = System.Math.Max(1, sy - 2);
                for (int y = top; y >= bottom; y--)
                {
                    pos.Set(wx, y, wz);
                    var b = sapi.World.BlockAccessor.GetBlock(pos);
                    if (b == null || !dewablePlantIds.Contains(b.BlockId)) continue;

                    if (diag != null) diag.ColumnsWithPlant++;

                    // Place the dewdrop in the air just above the plant top.
                    abovePos.Set(wx, y + 1, wz);
                    var above = sapi.World.BlockAccessor.GetBlock(abovePos);
                    if (above == null) break;
                    bool isAir = above.BlockId == 0;
                    bool isExistingDew = above.BlockId == dewdropBlockId;
                    if (!isAir && !isExistingDew)
                    {
                        if (diag != null) diag.ColumnsBlockedAbove++;
                        break;
                    }

                    sapi.World.BlockAccessor.SetBlock(dewdropBlockId, abovePos);
                    placed++;
                    break;
                }
            }
        }

        return placed;
    }

    // Morning evaporation: remove dewdrop blocks in the player radius
    private int ClearDewForPlayer(IServerPlayer player)
    {
        if (player?.Entity == null || dewdropBlockId == 0) return 0;

        int px = (int)player.Entity.Pos.X;
        int pz = (int)player.Entity.Pos.Z;
        var pos = new BlockPos(Dimensions.NormalWorld);
        int cleared = 0;

        for (int dx = -RegenRadiusBlocks; dx <= RegenRadiusBlocks; dx++)
        {
            for (int dz = -RegenRadiusBlocks; dz <= RegenRadiusBlocks; dz++)
            {
                int wx = px + dx;
                int wz = pz + dz;

                int? maybeY = sapi.WorldManager.GetSurfacePosY(wx, wz);
                if (maybeY == null) continue;
                int sy = maybeY.Value;
                if (sy <= 1) continue;

                int top = System.Math.Min(sy + VerticalScanAboveSurface, sapi.WorldManager.MapSizeY - 2);
                int bottom = System.Math.Max(1, sy - 2);
                for (int y = top; y >= bottom; y--)
                {
                    pos.Set(wx, y, wz);
                    var b = sapi.World.BlockAccessor.GetBlock(pos);
                    if (b != null && b.BlockId == dewdropBlockId)
                    {
                        sapi.World.BlockAccessor.SetBlock(0, pos);
                        cleared++;
                    }
                }
            }
        }

        return cleared;
    }
}
