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
    /// Block behavior for a sliding window, based closely on vanilla BlockBehaviorDoor.
    /// Requires BEBehaviorSlidingWindow on the block to function.
    /// </summary>
    [DocumentAsJson]
    [AddDocumentationProperty("TriggerSound", "Sets both OpenSound & CloseSound.", "Vintagestory.API.Common.AssetLocation", "Optional", "sounds/block/door", true)]
    public class BlockBehaviorSlidingWindow : StrongBlockBehavior, IMultiBlockColSelBoxes, IMultiBlockBlockProperties, IClaimTraverseable
    {
        [DocumentAsJson("Optional", "sounds/block/door", true)]
        public AssetLocation OpenSound;

        [DocumentAsJson("Optional", "sounds/block/door", true)]
        public AssetLocation CloseSound;

        [DocumentAsJson("Optional", "1", true)]
        public int width;

        [DocumentAsJson("Optional", "1", true)]
        public int height;

        // DEVIATION from vanilla door:
        // Doors always try to combine; sliding windows gate that via this attribute so
        // we don't run expensive “find neighbor” logic for single, non-pairable windows.
        [DocumentAsJson("Optional", "false", true)]
        public bool pairable;

        [DocumentAsJson("Optional", "True", true)]
        public bool handopenable;

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
            handopenable = block.Attributes["handopenable"].AsBool(true);
        }

        public override void OnLoaded(ICoreAPI api)
        {
            this.api = api;

            // Vanilla-style TriggerSound/OpenSound/CloseSound cascade
            OpenSound = CloseSound = AssetLocation.Create(block.Attributes["triggerSound"].AsString("sounds/block/door"));

            JsonObject soundAttribute = block.Attributes["openSound"];
            if (soundAttribute.Exists) OpenSound = AssetLocation.Create(soundAttribute.AsString("sounds/block/door"));

            soundAttribute = block.Attributes["closeSound"];
            if (soundAttribute.Exists) CloseSound = AssetLocation.Create(soundAttribute.AsString("sounds/block/door"));

            base.OnLoaded(api);
        }

        public override void Activate(IWorldAccessor world, Caller caller, BlockSelection blockSel, ITreeAttribute activationArgs, ref EnumHandling handled)
        {
            // Let any sash (controller or filler) activate via the BE lookup helper
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

        public static bool HasCombinableLeftWindow(IWorldAccessor world, float rotateYRad, BlockPos pos, int width, out BEBehaviorSlidingWindow leftWindow, out int leftOffset)
        {
            leftOffset = 0;
            leftWindow = null;

            BlockFacing leftFacing = BlockFacing.HorizontalFromYaw(rotateYRad).GetCW();

            BlockPos leftPos = pos.AddCopy(leftFacing);
            leftWindow = getSlidingWindowAt(world, leftPos);

            if (width > 1)
            {
                if (leftWindow == null)
                {
                    for (int i = 2; i <= width; i++)
                    {
                        leftPos = pos.AddCopy(leftFacing, i);
                        leftWindow = getSlidingWindowAt(world, leftPos);
                        if (leftWindow != null) break;
                    }
                }

                if (leftWindow != null)
                {
                    // Controllers should end up separated by (width + otherWidth - 1) blocks,
                    // so a 2-wide + 2-wide gives C..C pattern (controllers 3 blocks apart).
                    BlockPos offsetPos = leftWindow.Pos.AddCopy(leftFacing.Opposite, leftWindow.InvertHandles ? width : (width + leftWindow.windowBh.width - 1));
                    leftOffset = (int)pos.DistanceTo(offsetPos);

                    // Must be in same "column" along the window's facing axis
                    if ((leftWindow.windowFacing.Axis == EnumAxis.X && leftPos.X != leftWindow.Pos.X) ||
                        (leftWindow.windowFacing.Axis == EnumAxis.Z && leftPos.Z != leftWindow.Pos.Z))
                    {
                        leftWindow = null;
                        leftOffset = 0;
                    }
                }
            }

            if (leftWindow != null &&
                leftWindow.windowBh.pairable &&   // DEVIATION: vanilla has no pairable flag.
                leftWindow.LeftWindow == null &&
                leftWindow.RightWindow == null &&
                leftWindow.windowFacing == BlockFacing.HorizontalFromYaw(rotateYRad))
            {
                return true;
            }

            return false;
        }

        public static bool HasCombinableRightWindow(IWorldAccessor world, float rotateYRad, BlockPos pos, int width, out BEBehaviorSlidingWindow rightWindow, out int rightOffset)
        {
            rightOffset = 0;
            rightWindow = null;

            BlockFacing rightFacing = BlockFacing.HorizontalFromYaw(rotateYRad).GetCCW();

            BlockPos rightPos = pos.AddCopy(rightFacing);
            rightWindow = getSlidingWindowAt(world, rightPos);

            if (width > 1)
            {
                if (rightWindow == null)
                {
                    for (int i = 2; i <= width; i++)
                    {
                        rightPos = pos.AddCopy(rightFacing, i);
                        rightWindow = getSlidingWindowAt(world, rightPos);
                        if (rightWindow != null) break;
                    }
                }

                if (rightWindow != null)
                {
                    BlockPos offsetPos = rightWindow.Pos.AddCopy(rightFacing.Opposite, !rightWindow.InvertHandles ? width : width + rightWindow.windowBh.width - 1);
                    rightOffset = (int)pos.DistanceTo(offsetPos);

                    if ((rightWindow.windowFacing.Axis == EnumAxis.X && rightPos.X != rightWindow.Pos.X) ||
                        (rightWindow.windowFacing.Axis == EnumAxis.Z && rightPos.Z != rightWindow.Pos.Z))
                    {
                        rightWindow = null;
                        rightOffset = 0;
                    }
                }
            }

            if (rightWindow != null &&
                rightWindow.windowBh.pairable &&  // DEVIATION: gated by pairable.
                rightWindow.RightWindow == null &&
                rightWindow.LeftWindow == null &&
                rightWindow.windowFacing == BlockFacing.HorizontalFromYaw(rotateYRad))
            {
                return true;
            }

            return false;
        }


        public override bool CanPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling, ref string failureCode)
        {
            BlockPos pos = blockSel.Position.Copy();
            var rotRad = BEBehaviorSlidingWindow.getRotateYRad(byPlayer, blockSel);

            bool blocked = false;

            // let nonpairables go home single
            if (!pairable)
            {
                IterateOverEach(pos, rotRad, false, (mpos) =>
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

            // Vanilla door logic:
            // 1. See if we can combine with a left neighbor; if yes, we might have to
            //    shift our controller so the two multiwidth windows form a clean span.
            BlockFacing facing = BlockFacing.HorizontalFromYaw(rotRad);

            bool placeAsRightOfLeft = HasCombinableLeftWindow(world, rotRad, blockSel.Position, width, out BEBehaviorSlidingWindow otherWindow, out int offset);

            if (placeAsRightOfLeft && width > 1 && offset != 0)
            {
                // Shift our origin CCW (left) by the computed offset
                pos.Add(facing.GetCCW(), offset);
            }

            // 2. If not combining to the left, try combining to the right.
            if (!placeAsRightOfLeft &&
                HasCombinableRightWindow(world, rotRad, blockSel.Position, width, out otherWindow, out offset) &&
                width > 1 && offset != 0)
            {
                pos.Add(facing.GetCW(), offset);
            }

            // 3. Now test the actual multiblock footprint at the (possibly shifted) pos.
            IterateOverEach(pos, rotRad, placeAsRightOfLeft, (mpos) =>
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
                BlockFacing facing = BlockFacing.HorizontalFromYaw(rotRad);

                if (HasCombinableLeftWindow(world, rotRad, blockSel.Position, width, out BEBehaviorSlidingWindow otherWindow, out int offset))
                {
                    if (width > 1 && offset != 0)
                    {
                        pos.Add(facing.GetCCW(), offset);
                    }
                }
                else if (HasCombinableRightWindow(world, rotRad, blockSel.Position, width, out otherWindow, out offset))
                {
                    if (width > 1 && offset != 0)
                    {
                        pos.Add(facing.GetCW(), offset);
                    }
                }

                return placeSlidingWindow(world, byPlayer, itemstack, blockSel, pos, ba);
            }

            return false;
        }

        public bool placeSlidingWindow(IWorldAccessor world, IPlayer byPlayer, ItemStack itemstack, BlockSelection blockSel, BlockPos pos, IBlockAccessor ba)
        {
            ba.SetBlock(block.BlockId, pos);
            var bh = ba.GetBlockEntity(pos)?.GetBehavior<BEBehaviorSlidingWindow>();
            bh?.OnBlockPlaced(itemstack, byPlayer, blockSel);

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

            IterateOverEach(pos, rotRad, beh?.InvertHandles ?? false, (mpos) =>
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

            IterateOverEach(pos, rotRad, beh?.InvertHandles ?? false, (mpos) =>
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

        public void IterateOverEach(BlockPos pos, float yRotRad, bool invertHandle, ActionConsumable<BlockPos> onBlock)
        {
            BlockPos tmpPos = new BlockPos(pos.dimension);

            // Vanilla doors loop dz < width, which effectively treated the blockentity
            // as width × height × width (a 2×2×2 cube when width=2,height=2).
            // Sliding windows are always 1 block deep, so the true footprint is
            // width × height × 1.  We therefore keep "depth" (dz) at 0.
            for (int dx = 0; dx < width; dx++)
            {
                for (int dy = 0; dy < height; dy++)
                {
                    // no extra depth, just the front/sliding plane
                    var offset = BEBehaviorSlidingWindow.getAdjacentOffset(dx, 0, dy, yRotRad, invertHandle);
                    tmpPos.Set(pos.X + offset.X, pos.Y + offset.Y, pos.Z + offset.Z);

                    if (!onBlock(tmpPos)) return;
                }
            }
        }

        public Cuboidf[] MBGetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos, Vec3i offset)
        {
            return getColSelBoxes(blockAccessor, pos, offset);
        }

        public Cuboidf[] MBGetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos, Vec3i offset)
        {
            return getColSelBoxes(blockAccessor, pos, offset);
        }

        private static Cuboidf[] getColSelBoxes(IBlockAccessor blockAccessor, BlockPos pos, Vec3i offset)
        {
            // For multiblock calls, pos is the stub position and `offset`
            // points from stub -> controller (BlockMultiblock.OffsetInv).
            var controllerPos = pos.AddCopy(offset.X, offset.Y, offset.Z);
            var beh = blockAccessor.GetBlockEntity(controllerPos)?.GetBehavior<BEBehaviorSlidingWindow>();
            if (beh == null) return Array.Empty<Cuboidf>();

            var baseBoxes = beh.ColSelBoxes;
            if (baseBoxes == null || baseBoxes.Length == 0) return Array.Empty<Cuboidf>();

            // The engine expects MBGetCollisionBoxes/SelectionBoxes to return
            // boxes local to the stub block.  Our ColSelBoxes are authored in
            // the controller's local space (0..2 in X/Y for the full multiblock).
            //
            // We want every stub to contribute the same world-space union, i.e.:
            //   world = controllerPos + baseBox
            //
            // But the engine will compute:
            //   world = stubPos + adjustedBox
            //
            // So we solve for adjustedBox:
            //   stubPos + adjusted = controllerPos + base
            //   adjusted = base + (controllerPos - stubPos)
            //   controllerPos - stubPos == offset
            //
            // => adjusted = base + offset
            //
            // That "offset" is BlockMultiblock.OffsetInv (usually negative), so
            // adding it cancels the stub's displacement and pins the boxes back
            // to the controller’s world location.
            var adjusted = new Cuboidf[baseBoxes.Length];
            for (int i = 0; i < baseBoxes.Length; i++)
            {
                adjusted[i] = baseBoxes[i].Clone().Translate(offset.X, offset.Y, offset.Z);
            }

            return adjusted;
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
            // Already set in Block.GetPlacedBlockName(), 
        }

        private void windowNameWithMaterial(StringBuilder sb)
        {
            if (block.Variant.ContainsKey("wood"))
            {
                string doorname = sb.ToString();
                sb.Clear();
                sb.Append(Lang.Get("doorname-with-material", doorname, Lang.Get("material-" + block.Variant["wood"])));
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
            return (beh.IsSideSolid(facing) || beh.IsSideSolid(facing.Opposite)) ? getInsulation(pos) : 3;
        }

        public int MBGetRetention(BlockPos pos, BlockFacing facing, EnumRetentionType type, Vec3i offset)
        {
            var beh = block.GetBEBehavior<BEBehaviorSlidingWindow>(pos.AddCopy(offset.X, offset.Y, offset.Z));
            if (beh == null) return 0;
            if (type == EnumRetentionType.Sound) return beh.IsSideSolid(facing) ? 3 : 0;

            if (!airtight) return 0;
            if (api.World.Config.GetBool("openDoorsNotSolid", false)) return beh.IsSideSolid(facing) ? getInsulation(pos) : 0;
            return (beh.IsSideSolid(facing) || beh.IsSideSolid(facing.Opposite)) ? getInsulation(pos) : 3;
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
