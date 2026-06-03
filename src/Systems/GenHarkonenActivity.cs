using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace VsDune;

public class GenHarkonenActivity : ModSystem
{
    private ICoreServerAPI sapi;
    private LCGRandom rnd;

    // Decay rate is per second. Score units are abstract; the per-event
    // bumps and threshold below are all in the same currency.
    private const float DecayPerSecond = 0.4f;
    private const float BackupThreshold = 60f;
    private const float BackupCooldownSeconds = 600f;

    public const float BumpFromDetection = 6f;
    public const float BumpFromHarkAttacked = 18f;
    public const float BumpFromHarkKilled = 35f;

    private readonly Dictionary<string, double> scoreByUid = new();
    private readonly Dictionary<string, double> lastBackupAtByUid = new();
    private double lastDecayMs = 0;

    private const int SubSeed = 6677299;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        rnd = new LCGRandom();
        rnd.SetWorldSeed(api.WorldManager.Seed - SubSeed);
        lastDecayMs = api.World.ElapsedMilliseconds;

        api.Event.RegisterGameTickListener(OnDecayTick, 1000);

        api.ChatCommands.Create("harkscore")
            .WithDescription("Show or modify your Harkonen activity score.")
            .RequiresPrivilege(Privilege.controlserver)
            .RequiresPlayer()
            .HandleWith(OnScoreCommand)
            .BeginSubCommand("bump")
                .WithArgs(api.ChatCommands.Parsers.OptionalFloat("amount", 30f))
                .HandleWith(OnBumpCommand)
            .EndSubCommand()
            .BeginSubCommand("clear").HandleWith(OnClearCommand).EndSubCommand();
    }

    public void Bump(IServerPlayer player, float amount)
    {
        if (player?.PlayerUID == null) return;
        string uid = player.PlayerUID;
        scoreByUid.TryGetValue(uid, out double s);
        s += amount;
        scoreByUid[uid] = s;
        if (s >= BackupThreshold) TryFireBackup(player);
    }

    // Attribute a Hark-attacked bump to the attacker if they're a
    // player, otherwise to the nearest online player (Fremen NPC kills
    // attribute to whoever's around).
    public void BumpFromHarkAttackedBy(Entity attacker, float amount)
    {
        IServerPlayer attribute = null;
        if (attacker is EntityPlayer ep) attribute = ep.Player as IServerPlayer;
        if (attribute == null)
        {
            double bestSq = double.MaxValue;
            foreach (var p in sapi.World.AllOnlinePlayers)
            {
                if (p?.Entity == null) continue;
                double dx = p.Entity.Pos.X - attacker.Pos.X;
                double dz = p.Entity.Pos.Z - attacker.Pos.Z;
                double distSq = dx * dx + dz * dz;
                if (distSq < bestSq) { bestSq = distSq; attribute = p as IServerPlayer; }
            }
        }
        if (attribute != null) Bump(attribute, amount);
    }

    public float GetScore(IServerPlayer player)
    {
        if (player?.PlayerUID == null) return 0;
        scoreByUid.TryGetValue(player.PlayerUID, out double s);
        return (float)s;
    }

    private void OnDecayTick(float dt)
    {
        double nowMs = sapi.World.ElapsedMilliseconds;
        double elapsedS = (nowMs - lastDecayMs) / 1000.0;
        lastDecayMs = nowMs;
        if (elapsedS <= 0) return;

        var keys = new List<string>(scoreByUid.Keys);
        foreach (var uid in keys)
        {
            double s = scoreByUid[uid] - DecayPerSecond * elapsedS;
            if (s <= 0) scoreByUid.Remove(uid);
            else scoreByUid[uid] = s;
        }
    }

    private void TryFireBackup(IServerPlayer player)
    {
        double nowMs = sapi.World.ElapsedMilliseconds;
        lastBackupAtByUid.TryGetValue(player.PlayerUID, out double lastAt);
        if (nowMs - lastAt < BackupCooldownSeconds * 1000.0) return;
        lastBackupAtByUid[player.PlayerUID] = nowMs;

        // Drain score so we don't retrigger while it decays past the line.
        scoreByUid[player.PlayerUID] = 0;

        FactionChannels.Harkonen(sapi, "Activity threshold exceeded. Dispatching response.");
        sapi.Logger.Notification("[VSDune] Hark activity backup raid for {0}.", player.PlayerName);

        var airPatrol = sapi.ModLoader.GetModSystem<GenHarkonenAirPatrol>();
        airPatrol?.DispatchBackupRaid(player);
    }

    private TextCommandResult OnScoreCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer sp) return TextCommandResult.Error("No caller player.");
        float score = GetScore(sp);
        return TextCommandResult.Success($"Harkonen activity for {sp.PlayerName}: {score:F1} / {BackupThreshold} (decay {DecayPerSecond}/s).");
    }

    private TextCommandResult OnBumpCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer sp) return TextCommandResult.Error("No caller player.");
        float amount = (float)args.Parsers[0].GetValue();
        Bump(sp, amount);
        return TextCommandResult.Success($"Bumped {sp.PlayerName} by {amount}, new score {GetScore(sp):F1}.");
    }

    private TextCommandResult OnClearCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer sp) return TextCommandResult.Error("No caller player.");
        scoreByUid.Remove(sp.PlayerUID);
        lastBackupAtByUid.Remove(sp.PlayerUID);
        return TextCommandResult.Success($"Cleared Hark activity for {sp.PlayerName}.");
    }
}
