using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsDune;

public class GenSpiceHarvest : ModSystem
{
    private ICoreServerAPI sapi;

    private const float HarkonenShareChance = 0.80f;
    // 150 frames at 30fps = 5s. Timer matches animation exactly.
    private const int DeployAnimationDurationMs = 5000;
    private const int PickupAnimationDurationMs = 5000;
    // Client needs time to tessellate the freshly spawned entity before animation starts.
    private const int AnimStartDelayMs = 750;
    private const double SpawnAltitudeAboveGround = 30.0;
    private const double PickupHoverAboveGround = 8.0;
    // TODO: drive pickup off worm-threat timing (3 min out) instead of a fixed delay.
    private const int PickupDelayMs = 30_000;

    private const int SubSeed = 7711893;
    private LCGRandom rnd;

    // Active sessions so multiple /vsdune harvester runs don't trip
    // each other up.
    private readonly List<HarvestSession> sessions = new();

    private class HarvestSession
    {
        public bool IsHarkonen;
        public Vec3d GroundPos;
        public long CrawlerId;
    }

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);

        api.ChatCommands.GetOrCreate("vsdune")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("harvester")
                .WithDescription("Run the harvester choreography: deploy + (30s later) pickup at caller's column.")
                .RequiresPlayer()
                .HandleWith(OnHarvesterCommand)
            .EndSubCommand();
    }

    private TextCommandResult OnHarvesterCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer sp || sp.Entity == null)
            return TextCommandResult.Error("No caller entity.");

        bool isHark = rnd.NextFloat() < HarkonenShareChance;

        var pos = sp.Entity.Pos;
        int groundY = sapi.World.BlockAccessor.GetRainMapHeightAt(new BlockPos((int)pos.X, 0, (int)pos.Z, Dimensions.NormalWorld));
        var spawnPos = new Vec3d(pos.X, groundY + SpawnAltitudeAboveGround, pos.Z);
        float yaw = (float)(rnd.NextDouble() * Math.PI * 2);

        if (!SpawnCarrierWithDeploy(spawnPos, yaw, isHark, out long carrierId, out string err))
            return TextCommandResult.Error(err);

        sapi.Event.RegisterCallback(_ => CompleteDeploy(carrierId, isHark), DeployAnimationDurationMs);

        sapi.Logger.Notification("[VSDune] Harvester sequence triggered ({0}) at ({1:F0}, {2:F0}, {3:F0}).",
            isHark ? "Harkonen" : "Smuggler", spawnPos.X, spawnPos.Y, spawnPos.Z);

        return TextCommandResult.Success(string.Format(
            "Harvester ({0}): deploy ~{1}s, then pickup ~{2}s later. Spawn at ({3:F0}, {4:F0}, {5:F0}).",
            isHark ? "Harkonen" : "Smuggler",
            DeployAnimationDurationMs / 1000,
            (DeployAnimationDurationMs + PickupDelayMs) / 1000,
            spawnPos.X, spawnPos.Y, spawnPos.Z));
    }

    private bool SpawnCarrierWithDeploy(Vec3d at, float yaw, bool isHark, out long carrierId, out string err)
    {
        carrierId = 0;
        string code = isHark ? "harkcarrierwithcrawler" : "carrierwithcrawler";
        var type = sapi.World.GetEntityType(new AssetLocation("vsdune", code));
        if (type == null) { err = $"Entity vsdune:{code} not registered."; return false; }

        var carrier = sapi.World.ClassRegistry.CreateEntity(type);
        if (carrier == null) { err = "Failed to create carrier entity."; return false; }

        carrier.Pos.SetPos(at.X, at.Y, at.Z);
        carrier.Pos.Yaw = yaw;
        sapi.World.SpawnEntity(carrier);

        long capturedId = carrier.EntityId;
        sapi.Event.RegisterCallback(_ =>
        {
            var e = sapi.World.GetEntityById(capturedId);
            if (e == null || !e.Alive) return;
            e.AnimManager.StartAnimation(new AnimationMetaData { Code = "deploycarrier", Animation = "deploycarrier", AnimationSpeed = 1f }.Init());
        }, AnimStartDelayMs);

        carrierId = carrier.EntityId;
        err = null;
        return true;
    }

    private void CompleteDeploy(long carrierId, bool isHark)
    {
        var carrier = sapi.World.GetEntityById(carrierId);
        if (carrier == null || !carrier.Alive) return;

        var cPos = carrier.Pos.XYZ;
        var session = new HarvestSession { IsHarkonen = isHark };

        string carryallCode = isHark ? "harkcarryall" : "carryall";
        var carryallType = sapi.World.GetEntityType(new AssetLocation("vsdune", carryallCode));
        if (carryallType != null)
        {
            var carryall = sapi.World.ClassRegistry.CreateEntity(carryallType);
            if (carryall != null)
            {
                carryall.Pos.SetPos(cPos.X, cPos.Y, cPos.Z);
                carryall.Pos.Yaw = carrier.Pos.Yaw;
                sapi.World.SpawnEntity(carryall);
                // Depart nose-first along the carrier heading. Forward = (sin yaw, cos yaw).
                float cy = carrier.Pos.Yaw;
                var fleeDir = new Vec3d(Math.Sin(cy) * 0.7, 0.35, Math.Cos(cy) * 0.7);
                EntityBehaviorOrnithopterFlight.SetFleeing(carryall, fleeDir, 12f);
            }
        }

        string crawlerCode = isHark ? "harkcrawler" : "sandcrawler";
        var crawlerType = sapi.World.GetEntityType(new AssetLocation("vsdune", crawlerCode));
        if (crawlerType != null)
        {
            var crawler = sapi.World.ClassRegistry.CreateEntity(crawlerType);
            if (crawler != null)
            {
                int gy = sapi.World.BlockAccessor.GetRainMapHeightAt(new BlockPos((int)cPos.X, 0, (int)cPos.Z, Dimensions.NormalWorld));
                var groundPos = new Vec3d(cPos.X, gy + 1, cPos.Z);
                crawler.Pos.SetPos(groundPos.X, groundPos.Y, groundPos.Z);
                crawler.Pos.Yaw = carrier.Pos.Yaw;
                sapi.World.SpawnEntity(crawler);
                session.CrawlerId = crawler.EntityId;
                session.GroundPos = groundPos;
            }
        }

        carrier.Die(EnumDespawnReason.Removed);

        if (session.CrawlerId != 0)
        {
            sessions.Add(session);
            sapi.Event.RegisterCallback(_ => BeginPickup(session), PickupDelayMs);
        }
    }

    private void BeginPickup(HarvestSession session)
    {
        if (!sessions.Contains(session)) return;
        var crawler = sapi.World.GetEntityById(session.CrawlerId);
        if (crawler == null || !crawler.Alive)
        {
            sessions.Remove(session);
            return;
        }

        var hoverPos = new Vec3d(session.GroundPos.X, session.GroundPos.Y + PickupHoverAboveGround, session.GroundPos.Z);
        float yaw = crawler.Pos.Yaw;

        // Despawn the crawler at the moment of pickup so it visually
        // gets "lifted" into the carrier's bay during the animation.
        crawler.Die(EnumDespawnReason.Removed);
        session.CrawlerId = 0;

        if (!SpawnCarrierForPickup(hoverPos, yaw, session.IsHarkonen, out long carrierId, out string err))
        {
            sapi.Logger.Warning("[VSDune] Pickup spawn failed: {0}", err);
            sessions.Remove(session);
            return;
        }

        sapi.Event.RegisterCallback(_ => CompletePickup(carrierId, session), PickupAnimationDurationMs);
    }

    private bool SpawnCarrierForPickup(Vec3d at, float yaw, bool isHark, out long carrierId, out string err)
    {
        carrierId = 0;
        string code = isHark ? "harkcarrierwithcrawler" : "carrierwithcrawler";
        var type = sapi.World.GetEntityType(new AssetLocation("vsdune", code));
        if (type == null) { err = $"Entity vsdune:{code} not registered."; return false; }

        var carrier = sapi.World.ClassRegistry.CreateEntity(type);
        if (carrier == null) { err = "Failed to create pickup carrier."; return false; }

        carrier.Pos.SetPos(at.X, at.Y, at.Z);
        carrier.Pos.Yaw = yaw;
        sapi.World.SpawnEntity(carrier);

        long capturedId = carrier.EntityId;
        sapi.Event.RegisterCallback(_ =>
        {
            var e = sapi.World.GetEntityById(capturedId);
            if (e == null || !e.Alive) return;
            e.AnimManager.StartAnimation(new AnimationMetaData { Code = "pickupcarrier", Animation = "pickupcarrier", AnimationSpeed = 1f }.Init());
        }, AnimStartDelayMs);

        carrierId = carrier.EntityId;
        err = null;
        return true;
    }

    private void CompletePickup(long carrierId, HarvestSession session)
    {
        var carrier = sapi.World.GetEntityById(carrierId);
        sessions.Remove(session);
        if (carrier == null || !carrier.Alive) return;

        // carrierwithcrawler has no ornithopterflight behavior so SetFleeing
        // would set WatchedAttributes nobody reads. Spawn a fresh carryall
        // (which does have the behavior) and flee that instead.
        string carryallCode = session.IsHarkonen ? "harkcarryall" : "carryall";
        var carryallType = sapi.World.GetEntityType(new AssetLocation("vsdune", carryallCode));
        if (carryallType != null)
        {
            var carryall = sapi.World.ClassRegistry.CreateEntity(carryallType);
            if (carryall != null)
            {
                carryall.Pos.SetPos(carrier.Pos.X, carrier.Pos.Y, carrier.Pos.Z);
                carryall.Pos.Yaw = carrier.Pos.Yaw;
                sapi.World.SpawnEntity(carryall);
                float cy = carrier.Pos.Yaw;
                var fleeDir = new Vec3d(Math.Sin(cy) * 0.7, 0.35, Math.Cos(cy) * 0.7);
                EntityBehaviorOrnithopterFlight.SetFleeing(carryall, fleeDir, 12f);
            }
        }

        carrier.Die(EnumDespawnReason.Removed);
    }
}
