using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace VsDune;


public class EntityBehaviorRandomOutfit : EntityBehavior
{
    private HumanoidOutfits humanoidOutfits;

    public EntityBehaviorRandomOutfit(Entity entity) : base(entity) { }

    public AssetLocation OutfitConfigFileName
    {
        get
        {
            // Faction-aware outfit picking
            string factionCode = entity.Properties.Attributes?["factionCode"].AsString(null);
            if (factionCode != null && factionCode.Length > 0)
            {
                return new AssetLocation("vsdune", factionCode + "outfits");
            }
            string configName = entity.Properties.Attributes?["outfitConfigFileName"].AsString("traderaccessories");
            return AssetLocation.Create(configName, entity.Code.Domain);
        }
    }

    public string[] OutfitSlots
    {
        get => (entity.WatchedAttributes["outfitslots"] as StringArrayAttribute)?.value;
        set
        {
            if (value == null) entity.WatchedAttributes.RemoveAttribute("outfitslots");
            else entity.WatchedAttributes["outfitslots"] = new StringArrayAttribute(value);
            entity.WatchedAttributes.MarkPathDirty("outfitslots");
        }
    }

    public string[] OutfitCodes
    {
        get => (entity.WatchedAttributes["outfitcodes"] as StringArrayAttribute)?.value;
        set
        {
            if (value == null) entity.WatchedAttributes.RemoveAttribute("outfitcodes");
            else
            {
                for (int i = 0; i < value.Length; i++) if (value[i] == null) value[i] = "";
                entity.WatchedAttributes["outfitcodes"] = new StringArrayAttribute(value);
            }
            entity.WatchedAttributes.MarkPathDirty("outfitcodes");
        }
    }

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);

        humanoidOutfits = entity.Api.ModLoader.GetModSystem<HumanoidOutfits>();
        if (humanoidOutfits == null)
        {
            entity.Api.Logger.Warning("[VSDune] EntityBehaviorRandomOutfit: HumanoidOutfits modsystem missing. Outfit will not render.");
            return;
        }

        if (entity.Api.Side == EnumAppSide.Server)
        {
            int wantedVersion = entity.Properties.Attributes?["outfitversion"].AsInt(0) ?? 0;
            int currentVersion = entity.WatchedAttributes.GetInt("outfitversion", -1);
            if (OutfitCodes == null || currentVersion != wantedVersion)
            {
                LoadOutfitCodes();
                entity.WatchedAttributes.SetInt("outfitversion", wantedVersion);
            }
        }
        else
        {
            entity.WatchedAttributes.RegisterModifiedListener("outfitcodes", () => entity.MarkShapeModified());
        }
    }

    private void LoadOutfitCodes()
    {
        Dictionary<string, WeightedCode[]> partialRandom = null;
        var attr = entity.Properties.Attributes?["partialRandomOutfits"];
        if (attr != null && attr.Exists)
        {
            partialRandom = attr.AsObject<Dictionary<string, WeightedCode[]>>();
        }

        var outfit = humanoidOutfits.GetRandomOutfit(OutfitConfigFileName, partialRandom);
        OutfitSlots = outfit.Keys.ToArray();
        OutfitCodes = outfit.Values.ToArray();
    }

    public override void OnTesselation(ref Shape entityShape, string shapePathForLogging, ref bool shapeIsCloned, ref string[] willDeleteElements)
    {
        if (entity.Api.Side != EnumAppSide.Client) return;
        if (humanoidOutfits == null) return;

        string[] slots = OutfitSlots;
        string[] codes = OutfitCodes;
        if (slots == null || codes == null) return;
        if (slots.Length == 0 || codes.Length == 0) return;

        // Make sure the shape is cloned before we mutate it
        if (!shapeIsCloned)
        {
            entityShape = entityShape.Clone();
            shapeIsCloned = true;
        }

        var capi = entity.Api as ICoreClientAPI;
        var cshapes = humanoidOutfits.Outfit2Shapes(OutfitConfigFileName, codes);


        for (int i = 0; i < slots.Length && i < cshapes.Length; i++)
        {
            var twcshape = cshapes[i];
            if (twcshape == null || twcshape.Base == null) continue;
            AddGearToShape(slots[i], twcshape, entityShape, shapePathForLogging, twcshape.Textures);
        }


        for (int i = 0; i < codes.Length; i++)
        {
            var twcshape = cshapes[i];
            if (twcshape == null) continue;

            if (twcshape.DisableElements != null)
            {
                entityShape.RemoveElements(twcshape.DisableElements);
            }

            if (twcshape.OverrideTextures != null)
            {
                foreach (var val in twcshape.OverrideTextures)
                {
                    entityShape.Textures[val.Key] = val.Value;
                    var cmpt = new CompositeTexture(val.Value);
                    cmpt.Bake(capi.Assets);
                    entity.Properties.Client.Textures[val.Key] = cmpt;
                }
            }
        }
    }

    private void AddGearToShape(string prefixcode, CompositeShape cshape, Shape entityShape, string shapePathForLogging, Dictionary<string, AssetLocation> textureOverrides)
    {
        AssetLocation shapePath = cshape.Base.CopyWithPathPrefixAndAppendixOnce("shapes/", ".json");
        Shape gearshape = Shape.TryGet(entity.Api, shapePath);
        if (gearshape == null)
        {
            entity.Api.Logger.Warning("[VSDune] outfit shape {0} not found, gear piece will be invisible.", shapePath);
            return;
        }


        WalkAndClearEmptyStepParents(gearshape.Elements);

        if (prefixcode != null && prefixcode.Length > 0) prefixcode += "-";

        if (textureOverrides != null)
        {
            foreach (var val in textureOverrides)
            {
                gearshape.Textures[val.Key] = val.Value;
            }
        }

        if (gearshape.Textures != null)
        {
            foreach (var val in gearshape.Textures)
            {
                entityShape.TextureSizes[prefixcode + val.Key] = new int[] { gearshape.TextureWidth, gearshape.TextureHeight };
            }
        }

        var capi = entity.Api as ICoreClientAPI;
        var clientTextures = entity.Properties.Client.Textures;

        gearshape.SubclassForStepParenting(prefixcode, 0);
        gearshape.ResolveReferences(entity.Api.Logger, shapePath);
        entityShape.StepParentShape(gearshape, shapePath.ToShortString(), shapePathForLogging, entity.Api.Logger, (texcode, loc) =>
        {
            string texName = prefixcode + texcode;
            if (!clientTextures.ContainsKey(texName))
            {
                var cmpt = new CompositeTexture(loc);
                cmpt.Bake(capi.Assets);
                capi.EntityTextureAtlas.GetOrInsertTexture(
                    new AssetLocationAndSource(cmpt.Baked.TextureFilenames[0], new SourceStringComponents("VSDune random outfit, shape {0}", shapePath)),
                    out int subId, out _
                );
                cmpt.Baked.TextureSubId = subId;
                clientTextures[texName] = cmpt;
            }
        });
    }

    public override string PropertyName() => "vsdune.randomoutfit";

    // Recursively null out any empty-string StepParentName so the
    // engine's GetElementByName recursion skips them silently.
    private static void WalkAndClearEmptyStepParents(ShapeElement[] elements)
    {
        if (elements == null) return;
        for (int i = 0; i < elements.Length; i++)
        {
            var el = elements[i];
            if (el == null) continue;
            if (el.StepParentName != null && el.StepParentName.Length == 0)
            {
                el.StepParentName = null;
            }
            if (el.Children != null && el.Children.Length > 0)
            {
                WalkAndClearEmptyStepParents(el.Children);
            }
        }
    }
}
