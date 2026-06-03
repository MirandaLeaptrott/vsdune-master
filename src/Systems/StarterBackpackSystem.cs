using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace VsDune;


public class StarterBackpackSystem : ModSystem
{
    private ICoreServerAPI sapi;

    private const string IntentFlag = "vsdune.starterBackpackPending";

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.PlayerCreate += OnPlayerCreate;
        api.Event.PlayerNowPlaying += OnPlayerNowPlaying;

        // GetOrCreate so multiple systems can hang subcommands off the
        // same /vsdune parent without racing on Create.
        api.ChatCommands.GetOrCreate("vsdune")
            .WithDescription("VSDune admin commands.")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("resetbackpack")
                .WithDescription("Set the starter-backpack intent flag for the calling player and immediately re-run the grant.")
                .HandleWith(OnResetBackpackCommand)
            .EndSubCommand();
    }

    private TextCommandResult OnResetBackpackCommand(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer sp) return TextCommandResult.Error("No caller player.");
        sp.SetModdata(IntentFlag, SerializerUtil.Serialize(true));
        sapi.Logger.Notification("[VSDune] Set starter-backpack intent flag for {0}. Granting now.", sp.PlayerName);
        TryGrantIfPending(sp);
        return TextCommandResult.Success("Backpack intent flag set and grant re-run.");
    }

    private void OnPlayerCreate(IServerPlayer player)
    {
        if (player == null) return;
        // Fires exactly once per player UID, ever. Mark them as needing
        // a starter backpack; the actual grant happens in
        // OnPlayerNowPlaying when the client is ready.
        player.SetModdata(IntentFlag, SerializerUtil.Serialize(true));
        sapi.Logger.Notification("[VSDune] First-join for {0}: starter-backpack intent set, pending grant on ready.", player.PlayerName);
    }

    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        // PlayerNowPlaying fires after the client is fully in the
        // playing state: safe to mutate inventory without racing HUD
        // init. Idempotent: no-op if the intent flag isn't set.
        TryGrantIfPending(player);
    }

    private void TryGrantIfPending(IServerPlayer player)
    {
        if (player == null) return;

        bool pending = SerializerUtil.Deserialize(player.GetModdata(IntentFlag), false);
        if (!pending) return;

        // `game:` resolves vanilla content regardless of which asset
        // folder it ships in. If a future VS version moves the item
        // we'll see a logged warning rather than a silent miss.
        var backpackItem = sapi.World.GetItem(new AssetLocation("game", "backpack-normal"));
        if (backpackItem == null)
        {
            sapi.Logger.Warning("[VSDune] StarterBackpackSystem: game:backpack-normal item not found. Cannot grant starter backpack to {0}. Leaving intent flag set so we retry next login.", player.PlayerName);
            return;
        }

        var backpackInv = player.InventoryManager?.GetOwnInventory(GlobalConstants.backpackInvClassName);
        if (backpackInv == null)
        {
            sapi.Logger.Warning("[VSDune] StarterBackpackSystem: backpack inventory not available for {0}. Leaving intent flag set so we retry next login.", player.PlayerName);
            return;
        }
        if (backpackInv.Count == 0)
        {
            sapi.Logger.Warning("[VSDune] StarterBackpackSystem: backpack inventory has 0 slots for {0}. Leaving intent flag set so we retry next login.", player.PlayerName);
            return;
        }

        // Slots 0..3 of the "backpack" inventory class are the 4 body-
        // equipped bag slots. We target slot 0; if occupied, slot 1.
        bool granted = false;
        if (backpackInv[0].Empty)
        {
            backpackInv[0].Itemstack = new ItemStack(backpackItem);
            backpackInv[0].MarkDirty();
            sapi.Logger.Notification("[VSDune] Granted starter leather backpack to {0} (slot 0). Item resolved as {1}.", player.PlayerName, backpackItem.Code);
            granted = true;
        }
        else if (backpackInv.Count > 1 && backpackInv[1].Empty)
        {
            backpackInv[1].Itemstack = new ItemStack(backpackItem);
            backpackInv[1].MarkDirty();
            sapi.Logger.Notification("[VSDune] Granted starter leather backpack to {0} (slot 1). Item resolved as {1}.", player.PlayerName, backpackItem.Code);
            granted = true;
        }
        else
        {
            sapi.Logger.Notification("[VSDune] {0} already has bags in slots 0/1, clearing intent flag without grant.", player.PlayerName);
            granted = true; // count as resolved so we don't keep retrying
        }

        // Only clear the intent on success/resolved. On hard failures
        // (item missing, no inventory) leave the flag set so we retry
        // automatically on the next login.
        if (granted)
        {
            player.SetModdata(IntentFlag, SerializerUtil.Serialize(false));
        }
    }
}
