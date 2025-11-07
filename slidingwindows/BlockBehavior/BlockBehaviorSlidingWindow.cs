using System;
using System.Text;
using Vintagestory.API;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using SlidingWindows.BlockEntityBehaviors;
using Vintagestory.GameContent;

#nullable disable

namespace SlidingWindows.BlockBehaviors
{
    /// <summary>
    /// A block behavior for a sliding window, based on the vanilla door behavior. 
    /// Also requires the "BEBehaviorSlidingWindow" block entity behavior type on a block to work.
    /// Defined with the "SlidingWindow" code.
    /// </summary>
    /// <example><code lang="json">
    ///"behaviors": [
    ///	{ "name": "SlidingWindow" }
    ///]
    ///</code><code lang="json">
    ///"attributes": {
    ///	"widthByType": {
    ///		"*": 1
    ///	},
    ///	"heightByType": {
    ///		"*": 2
    ///	},
    ///	"closingSpeed": 10,
    ///	"openingSpeed" 8,
    ///	"openSound": "sounds/block/cokeovendoor-open",
    ///	"closeSound" "sounds/block/cokeovendoor-close",
    ///	"secondarySounds": {
    ///		"open": "sounds/block/cokeovendoor-open",
    ///		"close": "sounds/block/cokeovendoor-close"
    ///	}
    ///}
    ///</code></example>
    [DocumentAsJson]
    [AddDocumentationProperty("TriggerSound", "Sets both OpenSound & CloseSound.", "Vintagestory.API.Common.AssetLocation", "Optional", "sounds/block/door", true)]
    public class BlockBehaviorSlidingWindow : StrongBlockBehavior, IMultiBlockColSelBoxes, IMultiBlockBlockProperties, IClaimTraverseable
    {
        /// <summary>
        /// The sound to play when the window is opened. This is overridden by the sounds in attributes.
        /// </summary>
        [DocumentAsJson("Optional", "sounds/block/door", true)]
        public AssetLocation OpenSound;

        /// <summary>
        /// The sound to play when the window is closed. This is overridden by the sounds in attributes.
        /// </summary>
        [DocumentAsJson("Optional", "sounds/block/door", true)]
        public AssetLocation CloseSound;

        /// <summary>
        /// The width of the multiblock instance for the window.
        /// </summary>
        [DocumentAsJson("Optional", "1", true)]
        public int width;

        /// <summary>
        /// The height of the multiblock instance for the window.
        /// </summary>
        [DocumentAsJson("Optional", "1", true)]
        public int height;

        /// <summary>
        /// Should this sliding block try to auto-pair with neighbors?
        /// </summary>
        [DocumentAsJson("Optional", "false", true)]
        public bool pairable;

        /// <summary>
        /// If true, this sliding element moves in the opposite direction of its paired neighbor.
        /// </summary>
        [DocumentAsJson("Optional", "true", true)]
        public bool mirrorTrack;
        /// <summary>
        /// Can this window be opened by hand?
        /// </summary>
        [DocumentAsJson("Optional", "True", true)]
        public bool handopenable;

        /// <summary>
        /// Is this window airtight?
        /// </summary>
        [DocumentAsJson("Optional", "True", true)]
        public bool airtight;

        ICoreAPI api;
        public MeshData animatableOrigMesh;
        public Shape animatableShape;
        public string animatableDictKey;

        public BlockBehaviorSlidingWindow(Block block) : base(block)
        {
            airtight = block.Attributes["airtight"].AsBool(true);
            width = block.Attributes["width"].AsInt(1);
            height = block.Attributes["height"].AsInt(1);
            pairable = block.Attributes["pairable"].AsBool(false);
            mirrorTrack = block.Attributes["mirrorTrack"].AsBool(false);
            handopenable = block.Attributes["handopenable"].AsBool(true);
        }

        public override void OnLoaded(ICoreAPI api)
        {
            this.api = api;
            OpenSound = CloseSound = AssetLocation.Create(block.Attributes["triggerSound"].AsString("sounds/block/door"));

            JsonObject soundAttribute = block.Attributes["openSound"];
            if (soundAttribute.Exists) OpenSound = AssetLocation.Create(soundAttribute.AsString("sounds/block/door"));
            soundAttribute = block.Attributes["closeSound"];
            if (soundAttribute.Exists) CloseSound = AssetLocation.Create(soundAttribute.AsString("sounds/block/door"));

            base.OnLoaded(api);
        }


        public override void Activate(IWorldAccessor world, Caller caller, BlockSelection blockSel, ITreeAttribute activationArgs, ref EnumHandling handled)
        {  // only bottom sash can activate with this line
           // var beh = world.BlockAccessor.GetBlockEntity(blockSel.Position)?.GetBehavior<BEBehaviorSlidingWindow>();

           // let any sash activate
            var beh = getSlidingWindowAt(world, blockSel.Position);
            if (beh == null)
            {
                base.Activate(world, caller, blockSel, activationArgs, ref handled);
                return;
            }
                
            bool opened = !beh.Opened;
            if (activationArgs != null)
            {
                opened = activationArgs.GetBool("opened", opened);
            }

            if (beh.Opened != opened)
            {
                beh.ToggleWindowSashState(null, opened);
            }
        }

        public override void OnDecalTesselation(IWorldAccessor world, MeshData decalMesh, BlockPos pos, ref EnumHandling handled)
        {
            handled = EnumHandling.PreventDefault;

            var beh = world.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorSlidingWindow>();
            if (beh != null)
            {
                decalMesh.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), 0, beh.RotateYRad, 0);
            }
        }

        public static BEBehaviorSlidingWindow getSlidingWindowAt(IWorldAccessor world, BlockPos pos)
        {
            var window = world.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorSlidingWindow>();
            if (window != null) return window;

            var blockMb = world.BlockAccessor.GetBlock(pos) as BlockMultiblock;
            if (blockMb != null)
            {
                window = world.BlockAccessor.GetBlockEntity(pos.AddCopy(blockMb.OffsetInv))?.GetBehavior<BEBehaviorSlidingWindow>();
                return window;
            }

            return null;
        }

        public override bool CanPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling, ref string failureCode)
        {
            BlockPos pos = blockSel.Position.Copy();
            var rotRad = BEBehaviorSlidingWindow.getRotateYRad(byPlayer, blockSel);
            bool blocked = false;

            IterateOverEach(pos, rotRad, (mpos) =>
            {
                Block mblock = world.BlockAccessor.GetBlock(mpos, BlockLayersAccess.Solid);
                if (!mblock.IsReplacableBy(block))
                {
                    blocked = true;
                    return false;
                }

                return true;
            });

            if (blocked)
            {
                handling = EnumHandling.PreventDefault;
                failureCode = "notenoughspace";
                return false;
            }

            return base.CanPlaceBlock(world, byPlayer, blockSel, ref handling, ref failureCode);
        }

        public override bool TryPlaceBlock(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, ref EnumHandling handling, ref string failureCode)
        {
            handling = EnumHandling.PreventDefault;
            BlockPos pos = blockSel.Position.Copy();
            IBlockAccessor ba = world.BlockAccessor;

            if (block.CanPlaceBlock(world, byPlayer, blockSel, ref failureCode))
            {
                var rotRad = BEBehaviorSlidingWindow.getRotateYRad(byPlayer, blockSel);                
                return placeSlidingWindow(world, byPlayer, itemstack, blockSel, pos, ba);
            }

            return false;
        }

        public bool placeSlidingWindow(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, BlockPos pos, IBlockAccessor ba)
        {
            ba.SetBlock(block.BlockId, pos);
            var bh = ba.GetBlockEntity(pos)?.GetBehavior<BEBehaviorSlidingWindow>();
            bh.OnBlockPlaced(itemstack, byPlayer, blockSel);

            if (world.Side == EnumAppSide.Server)
            {
                placeMultiblockParts(world, pos);
            }

            return true;
        }

        public void placeMultiblockParts(IWorldAccessor world, BlockPos pos)
        {
            var beh = world.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorSlidingWindow>();
            float rotRad = beh?.RotateYRad ?? 0;

            IterateOverEach(pos, rotRad, (mpos) =>
            {
                if (mpos == pos) return true;
                int dx = mpos.X - pos.X;
                int dy = mpos.Y - pos.Y;
                int dz = mpos.Z - pos.Z;

                string sdx = (dx < 0 ? "n" : (dx > 0 ? "p" : "")) + Math.Abs(dx);
                string sdy = (dy < 0 ? "n" : (dy > 0 ? "p" : "")) + Math.Abs(dy);
                string sdz = (dz < 0 ? "n" : (dz > 0 ? "p" : "")) + Math.Abs(dz);

                AssetLocation loc = new AssetLocation("multiblock-monolithic-" + sdx + "-" + sdy + "-" + sdz);
                Block block = world.GetBlock(loc);
                world.BlockAccessor.SetBlock(block.Id, mpos);
                if (world.Side == EnumAppSide.Server) world.BlockAccessor.TriggerNeighbourBlockUpdate(mpos);
                return true;
            });
        }

        public override void OnBlockRemoved(IWorldAccessor world, BlockPos pos, ref EnumHandling handling)
        {
            if (world.Side == EnumAppSide.Client) return;
            var beh = world.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorSlidingWindow>();

            var rotRad = beh?.RotateYRad ?? 0;

            IterateOverEach(pos, rotRad, (mpos) =>
            {
                if (mpos == pos) return true;

                Block mblock = world.BlockAccessor.GetBlock(mpos);
                if (mblock is BlockMultiblock)
                {
                    world.BlockAccessor.SetBlock(0, mpos);
                    if (world.Side == EnumAppSide.Server) world.BlockAccessor.TriggerNeighbourBlockUpdate(mpos);
                }

                return true;
            });

            base.OnBlockRemoved(world, pos, ref handling);
        }

        public void IterateOverEach(BlockPos pos, float yRotRad, ActionConsumable<BlockPos> onBlock)
        {
            BlockPos tmpPos = new BlockPos(pos.dimension);

            for (int dx = 0; dx < width; dx++)
            {
                for (int dy = 0; dy < height; dy++)
                {
                    // Door/window is only 1 block thick: back = 0
                    var offset = BEBehaviorSlidingWindow.getAdjacentOffset(dx, 0, dy, yRotRad);
                    tmpPos.Set(pos.X + offset.X, pos.Y + offset.Y, pos.Z + offset.Z);

                    if (!onBlock(tmpPos)) return;
                }
            }
        }

        
        // simpler, maybe? might only work for 2x1
        // public void IterateOverEach(BlockPos pos, ActionConsumable<BlockPos> onBlock)
        // {
        //     BlockPos tmpPos = new BlockPos(pos.dimension);

        //     for (int dy = 0; dy < height; dy++)
        //     {
        //         tmpPos.Set(pos.X, pos.Y + dy, pos.Z);
        //         if (!onBlock(tmpPos)) return;
        //     }
        // }

        private static Cuboidf[] GetMultiblockBoxes(IBlockAccessor blockAccessor, BlockPos pos, Vec3i offset)
        {
            // For multiblock callbacks, `pos` is the filler position.
            // The controller (with the BE) is at pos + offset.
            var controllerPos = pos.AddCopy(offset.X, offset.Y, offset.Z);

            var beh = blockAccessor.GetBlockEntity(controllerPos)?.GetBehavior<BEBehaviorSlidingWindow>();
            var baseBoxes = beh?.ColSelBoxes;

            if (baseBoxes == null || baseBoxes.Length == 0)
            {
                return Array.Empty<Cuboidf>();
            }

            var adjusted = new Cuboidf[baseBoxes.Length];
            for (int i = 0; i < baseBoxes.Length; i++)
            {
                var box = baseBoxes[i].Clone();
                box.Translate(offset.X, offset.Y, offset.Z);
                adjusted[i] = box;
            }

            return adjusted;
        }

        public Cuboidf[] MBGetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos, Vec3i offset)
        {
            return GetMultiblockBoxes(blockAccessor, pos, offset);
        }

        public Cuboidf[] MBGetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos, Vec3i offset)
        {
            return GetMultiblockBoxes(blockAccessor, pos, offset);
        }

        public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos, ref EnumHandling handled)
        {
            handled = EnumHandling.PreventSubsequent;
            return blockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorSlidingWindow>()?.ColSelBoxes ?? null;
        }

        public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos, ref EnumHandling handled)
        {
            handled = EnumHandling.PreventSubsequent;
            return blockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorSlidingWindow>()?.ColSelBoxes ?? null;
        }

        public override Cuboidf[] GetParticleCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos, ref EnumHandling handled)
        {
            handled = EnumHandling.PreventSubsequent;
            return blockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorSlidingWindow>()?.ColSelBoxes ?? null;
        }

        public override Cuboidf GetParticleBreakBox(IBlockAccessor blockAccess, BlockPos pos, BlockFacing facing, ref EnumHandling handled)
        {
            return base.GetParticleBreakBox(blockAccess, pos, facing, ref handled);
        }

        public override void GetHeldItemName(StringBuilder sb, ItemStack itemStack)
        {
            windowNameWithMaterial(sb);
        }

        public override void GetPlacedBlockName(StringBuilder sb, IWorldAccessor world, BlockPos pos)
        {
            // Already set in Block.GetPlacedBlockName()
        }

        private void windowNameWithMaterial(StringBuilder sb)
        {
            if (block.Variant.TryGetValue("wood", out string wood))
            {
                string windowname = sb.ToString();
                sb.Clear();
                sb.Append($"{windowname} ({Lang.Get("material-" + wood)})");
            }
        }

        public override float GetLiquidBarrierHeightOnSide(BlockFacing face, BlockPos pos, ref EnumHandling handled)
        {
            handled = EnumHandling.PreventDefault;
            var beh = block.GetBEBehavior<BEBehaviorSlidingWindow>(pos);
            if (beh == null) return 0f;

            if (!beh.IsSideSolid(face)) return 0f;
            if (!airtight) return 0f;

            return 1f;
        }

        public float MBGetLiquidBarrierHeightOnSide(BlockFacing face, BlockPos pos, Vec3i offset)
        {
            var beh = block.GetBEBehavior<BEBehaviorSlidingWindow>(pos.AddCopy(offset.X, offset.Y, offset.Z));
            if (beh == null) return 0f;
            if (!beh.IsSideSolid(face)) return 0f;
            if (!airtight) return 0f;

            return 1f;
        }

        public override int GetRetention(BlockPos pos, BlockFacing facing, EnumRetentionType type, ref EnumHandling handled)
        {
            handled = EnumHandling.PreventDefault;
            var beh = block.GetBEBehavior<BEBehaviorSlidingWindow>(pos);
            if (beh == null) return 0;

            if (type == EnumRetentionType.Sound) return beh.IsSideSolid(facing) ? 3 : 0;

            if (!airtight) return 0;
            if (api.World.Config.GetBool("openDoorsNotSolid", false)) return beh.IsSideSolid(facing) ? getInsulation(pos) : 0;
            return (beh.IsSideSolid(facing) || beh.IsSideSolid(facing.Opposite)) ? getInsulation(pos) : 3; // Also check opposite so the window can be facing inwards or outwards.
        }


        public int MBGetRetention(BlockPos pos, BlockFacing facing, EnumRetentionType type, Vec3i offset)
        {
            var beh = block.GetBEBehavior< BEBehaviorSlidingWindow>(pos.AddCopy(offset.X, offset.Y, offset.Z));
            if (beh == null) return 0;
            if (type == EnumRetentionType.Sound) return beh.IsSideSolid(facing) ? 3 : 0;

            if (!airtight) return 0;
            if (api.World.Config.GetBool("openDoorsNotSolid", false)) return beh.IsSideSolid(facing) ? getInsulation(pos) : 0;
            return (beh.IsSideSolid(facing) || beh.IsSideSolid(facing.Opposite)) ? getInsulation(pos) : 3; // Also check opposite so the window can be facing inwards or outwards.
        }

        private int getInsulation(BlockPos pos)
        {
            var mat = block.GetBlockMaterial(api.World.BlockAccessor, pos);
            if (mat == EnumBlockMaterial.Ore || mat == EnumBlockMaterial.Stone || mat == EnumBlockMaterial.Soil || mat == EnumBlockMaterial.Ceramic)
            {
                return -1;
            }
            return 1;
        }

        public bool MBCanAttachBlockAt(IBlockAccessor blockAccessor, Block block, BlockPos pos, BlockFacing blockFace, Cuboidi attachmentArea, Vec3i offsetInv)
        {
            return false;
        }

        public JsonObject MBGetAttributes(IBlockAccessor blockAccessor, BlockPos pos)
        {
            return null;
        }
    }
}