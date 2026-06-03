using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsDune;

// /vsdune cinematic: scripted showcase that converges the headline systems near the caller.
public class GenCinematic : ModSystem
{
    private ICoreServerAPI sapi;
    private LCGRandom rnd;
    private bool active;
    private BlockPos spicePos;
    private Vec3d wormPos;
    private Vec3d fremenPos;
    private long particleTickListenerId;
    private float particleElapsed;

    private const float CinematicTotalSeconds = 120f;
    // 80-block square = +/-40 from caller. Inner ring stays clear so
    // the camera framing has the caller in the middle of the action.
    private const int SquareHalfWidth = 40;
    private const int InnerClear = 12;

    private const float CountdownSeconds = 10f;
    private const float SpiceBlowBeatSeconds = 10f;
    private const float HarkonenInboundBeatSeconds = 15f;
    private const float SmugglerInboundBeatSeconds = 30f;
    private const float FremenBurstBeatSeconds = 55f;
    private const float WormEmergeBeatSeconds = 70f;

    private const float SpiceBlowMagnitude = 1.2f;
    private const int FremenBurstCount = 4;
    private const string FremenArcherCode = "fremen-archer";
    private static readonly string[] FremenMeleeCodes = new[]
    {
        "fremen-warrior-axe",
        "fremen-warrior-knife",
        "fremen-warrior-spear"
    };

    private const int SubSeed = 88811;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);

        // GetOrCreate so we share the /vsdune parent with other systems
        // (StarterBackpackSystem, etc.) without racing on Create.
        api.ChatCommands.GetOrCreate("vsdune")
            .WithDescription("VSDune admin commands.")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("cinematic")
                .WithDescription("Spawn 3 markers and run the headline convergence sequence at your position.")
                .RequiresPlayer()
                .HandleWith(OnCinematicCommand)
            .EndSubCommand();
    }

    private TextCommandResult OnCinematicCommand(TextCommandCallingArgs args)
    {
        if (active) return TextCommandResult.Error("Cinematic already running. Wait for it to finish.");
        if (args.Caller.Player is not IServerPlayer sp || sp.Entity == null)
            return TextCommandResult.Error("No caller entity.");

        var center = sp.Entity.Pos.XYZ;

        // Worm: basin sand column inside the square (sealevel +/- a few).
        wormPos = PickBasinSpot(center) ?? center.AddCopy(20, 0, 0);
        // Spice: any flat sandy surface inside the square, distinct from worm.
        spicePos = PickFlatSandSurface(center, exclude: wormPos);
        if (spicePos == null) return TextCommandResult.Error("No flat sand surface found inside the cinematic square. Stand on dunes near a basin and retry.");
        // Fremen: dune sand above sealevel, distinct from both.
        fremenPos = PickDuneSpot(center, exclude1: wormPos, exclude2: new Vec3d(spicePos.X, spicePos.Y, spicePos.Z));

        active = true;
        particleElapsed = 0f;
        particleTickListenerId = sapi.Event.RegisterGameTickListener(OnMarkerParticleTick, 250);

        FactionChannels.Observation(sapi, string.Format(
            "[Cinematic] Markers placed. Spice blow at ({0:F0}, {1:F0}, {2:F0}). Position camera. T-{3:F0}s.",
            spicePos.X, spicePos.Y, spicePos.Z, CountdownSeconds
        ));

        // Schedule all beats relative to t=0 (now). Each beat just
        // checks `active` so a future /vsdune cinematicstop could
        // short-circuit them.
        Schedule(SpiceBlowBeatSeconds, BeatSpiceBlow, "SpiceBlow");
        Schedule(HarkonenInboundBeatSeconds, BeatHarkonenInbound, "HarkonenInbound");
        Schedule(SmugglerInboundBeatSeconds, BeatSmugglerInbound, "SmugglerInbound");
        Schedule(FremenBurstBeatSeconds, BeatFremenBurst, "FremenBurst");
        Schedule(WormEmergeBeatSeconds, BeatWormEmerge, "WormEmerge");
        Schedule(CinematicTotalSeconds, BeatEnd, "End");

        return TextCommandResult.Success(string.Format(
            "Cinematic started. Spice at ({0}, {1}, {2}). Worm at ({3:F0}, _, {4:F0}). Fremen at ({5:F0}, _, {6:F0}). Duration {7:F0}s.",
            spicePos.X, spicePos.Y, spicePos.Z,
            wormPos.X, wormPos.Z,
            fremenPos.X, fremenPos.Z,
            CinematicTotalSeconds
        ));
    }

    private void Schedule(float t, Action beat, string name)
    {
        sapi.Event.RegisterCallback(_ =>
        {
            if (!active) return;
            try { beat(); }
            catch (Exception ex)
            {
                // Don't let one beat permanently jam future /vsdune
                // cinematic runs by leaving active=true. Log and let
                // the rest of the schedule continue.
                sapi.Logger.Error("[VSDune] Cinematic beat '{0}' threw: {1}", name, ex);
            }
        }, (int)(t * 1000));
    }

    private void BeatSpiceBlow()
    {
        FactionChannels.Observation(sapi, "[Cinematic] Spice blow imminent.");
        var spice = sapi.ModLoader.GetModSystem<GenSpiceBlow>();
        if (spice == null)
        {
            sapi.Logger.Warning("[VSDune] Cinematic: GenSpiceBlow not loaded, skipping blow beat.");
            return;
        }
        // fireEvent=false: we own all faction arrivals from here on.
        spice.TriggerBlowAt(spicePos, SpiceBlowMagnitude, fireEvent: false);
    }

    private void BeatHarkonenInbound()
    {
        FactionChannels.Observation(sapi, "[Cinematic] Harkonen vector inbound.");
        var raid = sapi.ModLoader.GetModSystem<GenOrnithopterRaid>();
        if (raid == null)
        {
            sapi.Logger.Warning("[VSDune] Cinematic: GenOrnithopterRaid not loaded, skipping Hark beat.");
            return;
        }
        // Lands close so Hark grabs the spice first. delaySeconds=0
        // because the beat is itself scheduled.
        raid.DispatchScriptedRaid(SpiceCenterVec(), isHarkonen: true, landFar: false, delaySeconds: 0f, announce: false);
    }

    private void BeatSmugglerInbound()
    {
        FactionChannels.Observation(sapi, "[Cinematic] Unidentified second vector. Closing on the blow site.");
        var raid = sapi.ModLoader.GetModSystem<GenOrnithopterRaid>();
        if (raid == null)
        {
            sapi.Logger.Warning("[VSDune] Cinematic: GenOrnithopterRaid not loaded, skipping Smuggler beat.");
            return;
        }
        raid.DispatchScriptedRaid(SpiceCenterVec(), isHarkonen: false, landFar: true, delaySeconds: 0f, announce: false);
    }

    private void BeatFremenBurst()
    {
        FactionChannels.Observation(sapi, "[Cinematic] Heat signatures rising under the dunes.");
        SpawnFremenBurst(fremenPos);
    }

    private void BeatWormEmerge()
    {
        FactionChannels.Observation(sapi, "[Cinematic] Vibration anomaly. Pull back.");
        SpawnVertworm(wormPos);
    }

    private void BeatEnd()
    {
        // Always clears active even if a prior beat threw, so the
        // /vsdune cinematic command stays runnable.
        try { FactionChannels.Observation(sapi, "[Cinematic] Sequence complete. Free combat."); }
        finally
        {
            StopMarkerParticles();
            active = false;
        }
    }

    private void OnMarkerParticleTick(float dt)
    {
        particleElapsed += dt;
        if (particleElapsed >= CinematicTotalSeconds)
        {
            StopMarkerParticles();
            return;
        }
        EmitMarkerColumn(new Vec3d(spicePos.X + 0.5, spicePos.Y + 1, spicePos.Z + 0.5), ColorUtil.ToRgba(230, 130, 50, 220));
        EmitMarkerColumn(wormPos, ColorUtil.ToRgba(230, 200, 160, 110));
        EmitMarkerColumn(fremenPos, ColorUtil.ToRgba(230, 80, 200, 230));
    }

    private void StopMarkerParticles()
    {
        if (particleTickListenerId == 0) return;
        sapi.Event.UnregisterGameTickListener(particleTickListenerId);
        particleTickListenerId = 0;
    }

    private void EmitMarkerColumn(Vec3d basePos, int rgba)
    {
        // Tall thin column rising 25 blocks, slow vertical motion so it
        // reads as a beacon from far away.
        var p = new SimpleParticleProperties(
            6, 10,
            rgba,
            new Vec3d(basePos.X - 0.4, basePos.Y, basePos.Z - 0.4),
            new Vec3d(basePos.X + 0.4, basePos.Y + 1.0, basePos.Z + 0.4),
            new Vec3f(-0.02f, 1.5f, -0.02f),
            new Vec3f(0.02f, 3.0f, 0.02f),
            4.0f,
            -0.01f,
            0.4f, 0.9f,
            EnumParticleModel.Quad
        );
        p.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, -25);
        p.SizeEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, 0.3f);
        p.MinSize = 0.4f;
        p.MaxSize = 0.9f;
        sapi.World.SpawnParticles(p);
    }

    private Vec3d SpiceCenterVec() => new(spicePos.X + 0.5, spicePos.Y + 0.5, spicePos.Z + 0.5);

    private BlockPos PickFlatSandSurface(Vec3d center, Vec3d exclude)
    {
        var ba = sapi.World.BlockAccessor;
        var probe = new BlockPos(Dimensions.NormalWorld);
        for (int tries = 0; tries < 40; tries++)
        {
            int dx = rnd.NextInt(SquareHalfWidth * 2) - SquareHalfWidth;
            int dz = rnd.NextInt(SquareHalfWidth * 2) - SquareHalfWidth;
            if (Math.Abs(dx) < InnerClear && Math.Abs(dz) < InnerClear) continue;
            int x = (int)center.X + dx;
            int z = (int)center.Z + dz;
            if (exclude != null && Math.Abs(x - exclude.X) < 12 && Math.Abs(z - exclude.Z) < 12) continue;

            probe.Set(x, 0, z);
            int y = ba.GetRainMapHeightAt(probe);
            probe.Set(x, y, z);
            var b = ba.GetBlock(probe);
            if (b == null || b.BlockMaterial != EnumBlockMaterial.Sand) continue;
            return new BlockPos(x, y, z, Dimensions.NormalWorld);
        }
        return null;
    }

    private Vec3d PickBasinSpot(Vec3d center)
    {
        for (int tries = 0; tries < 40; tries++)
        {
            int dx = rnd.NextInt(SquareHalfWidth * 2) - SquareHalfWidth;
            int dz = rnd.NextInt(SquareHalfWidth * 2) - SquareHalfWidth;
            if (Math.Abs(dx) < InnerClear && Math.Abs(dz) < InnerClear) continue;
            int x = (int)center.X + dx;
            int z = (int)center.Z + dz;
            if (ColumnIsBasinSand(x, z))
            {
                return new Vec3d(x + 0.5, sapi.World.SeaLevel, z + 0.5);
            }
        }
        return null;
    }

    private Vec3d PickDuneSpot(Vec3d center, Vec3d exclude1, Vec3d exclude2)
    {
        var ba = sapi.World.BlockAccessor;
        var probe = new BlockPos(Dimensions.NormalWorld);
        for (int tries = 0; tries < 40; tries++)
        {
            int dx = rnd.NextInt(SquareHalfWidth * 2) - SquareHalfWidth;
            int dz = rnd.NextInt(SquareHalfWidth * 2) - SquareHalfWidth;
            if (Math.Abs(dx) < InnerClear && Math.Abs(dz) < InnerClear) continue;
            int x = (int)center.X + dx;
            int z = (int)center.Z + dz;
            if (exclude1 != null && Math.Abs(x - exclude1.X) < 12 && Math.Abs(z - exclude1.Z) < 12) continue;
            if (exclude2 != null && Math.Abs(x - exclude2.X) < 12 && Math.Abs(z - exclude2.Z) < 12) continue;

            probe.Set(x, 0, z);
            int y = ba.GetRainMapHeightAt(probe);
            if (y <= sapi.World.SeaLevel + 1) continue;
            probe.Set(x, y, z);
            var b = ba.GetBlock(probe);
            if (b == null || b.BlockMaterial != EnumBlockMaterial.Sand) continue;
            return new Vec3d(x + 0.5, y + 1, z + 0.5);
        }
        return center.AddCopy(15, 1, 15);
    }

    private bool ColumnIsBasinSand(int x, int z)
    {
        int sealevel = sapi.World.SeaLevel;
        var probe = new BlockPos(Dimensions.NormalWorld);
        int depth = 0;
        for (int y = sealevel; y > sealevel - 30; y--)
        {
            probe.Set(x, y, z);
            var b = sapi.World.BlockAccessor.GetBlock(probe);
            if (b == null) break;
            if (b.BlockMaterial != EnumBlockMaterial.Sand) break;
            depth++;
            if (depth >= 8) return true;
        }
        return false;
    }

    private void SpawnFremenBurst(Vec3d at)
    {
        // Burst of sand particles + sound, then spawn fremen at the
        // surface. They walk toward the spice site under their normal
        // engage AI once enemies are in range.
        EmitSandBurst(at);
        sapi.World.PlaySoundAt(
            new AssetLocation("game:sounds/effect/woodbreak"),
            at.X, at.Y, at.Z, null, false, 100f, 0.8f
        );

        long herdId = sapi.WorldManager.GetNextUniqueId();
        for (int i = 0; i < FremenBurstCount; i++)
        {
            string code = (i == 0)
                ? FremenArcherCode
                : FremenMeleeCodes[rnd.NextInt(FremenMeleeCodes.Length)];
            var et = sapi.World.GetEntityType(new AssetLocation("vsdune", code));
            if (et == null) continue;
            var ent = sapi.World.ClassRegistry.CreateEntity(et);
            if (ent == null) continue;

            double angle = rnd.NextDouble() * Math.PI * 2;
            double dist = rnd.NextDouble() * 3;
            double ux = at.X + Math.Cos(angle) * dist;
            double uz = at.Z + Math.Sin(angle) * dist;
            int uy = sapi.World.BlockAccessor.GetRainMapHeightAt(new BlockPos((int)ux, 0, (int)uz));
            ent.Pos.SetPos(ux, uy + 1, uz);
            ent.WatchedAttributes.SetBool(EntityBehaviorOutlawArrakis.AttrScriptedSpawn, true);
            if (ent is EntityAgent ea) ea.HerdId = herdId;
            sapi.World.SpawnEntity(ent);
        }
        sapi.Logger.Notification("[VSDune] Cinematic: Fremen burst of {0} at ({1:F0}, _, {2:F0}).", FremenBurstCount, at.X, at.Z);
    }

    private void EmitSandBurst(Vec3d at)
    {
        var p = new SimpleParticleProperties(
            80, 120,
            ColorUtil.ToRgba(220, 230, 200, 150),
            new Vec3d(at.X - 1, at.Y, at.Z - 1),
            new Vec3d(at.X + 1, at.Y + 0.6, at.Z + 1),
            new Vec3f(-1.5f, 2.0f, -1.5f),
            new Vec3f(1.5f, 4.5f, 1.5f),
            1.5f,
            0.8f,
            0.3f, 0.7f,
            EnumParticleModel.Quad
        );
        p.OpacityEvolve = new EvolvingNatFloat(EnumTransformFunction.LINEAR, -120);
        sapi.World.SpawnParticles(p);
    }

    private void SpawnVertworm(Vec3d at)
    {
        var et = sapi.World.GetEntityType(new AssetLocation("vsdune", "vertworm"));
        if (et == null)
        {
            sapi.Logger.Warning("[VSDune] Cinematic: vertworm entity type not registered, skipping worm beat.");
            return;
        }
        var ent = sapi.World.ClassRegistry.CreateEntity(et);
        if (ent == null) return;
        double sy = sapi.World.SeaLevel - 5;
        ent.Pos.SetPos(at.X, sy, at.Z);
        sapi.World.SpawnEntity(ent);
        sapi.Logger.Notification("[VSDune] Cinematic: vertworm spawned at ({0:F0}, {1:F1}, {2:F0}).", at.X, sy, at.Z);
    }
}
