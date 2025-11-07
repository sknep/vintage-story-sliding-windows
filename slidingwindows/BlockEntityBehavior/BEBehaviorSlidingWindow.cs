using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using SlidingWindows.BlockBehaviors;
using Vintagestory.GameContent;

#nullable disable

namespace SlidingWindows.BlockEntityBehaviors
{
    /// <summary>
    /// Block entity behavior for sliding windows, structurally aligned with vanilla BEBehaviorDoor
    /// but using sliding animation instead of swing.
    /// </summary>
    public class BEBehaviorSlidingWindow : BEBehaviorAnimatable, IInteractable, IRotatable
    {
        public float RotateYRad;

        protected bool opened;
        protected bool invertHandles;
        protected MeshData mesh;

        // Open/closed collision + selection boxes for this controller.
        // For sliding windows we treat Block.CollisionBoxes (from the shape file)
        // as [frame, sashClosed, sashOpen] and build two explicit box sets
        // instead of rotating shapes like vanilla does, because our shapes are more complex.
        protected Cuboidf[] boxesClosed, boxesOpened;

        public BlockFacing windowFacing { get { return BlockFacing.HorizontalFromYaw(RotateYRad); } }

        public BlockBehaviorSlidingWindow windowBh;

        public Cuboidf[] ColSelBoxes => opened ? boxesOpened : boxesClosed;
        public bool Opened => opened;
        public bool InvertHandles => invertHandles;

        protected Vec3i leftWindowOffset;
        protected Vec3i rightWindowOffset;

        public BEBehaviorSlidingWindow LeftWindow
        {
            get
            {
                if (leftWindowOffset == null) return null;
                var be = BlockBehaviorSlidingWindow.getSlidingWindowAt(Api.World, Pos.AddCopy(leftWindowOffset));
                if (be == null) leftWindowOffset = null;
                return be;
            }
            protected set
            {
                leftWindowOffset = value == null ? null : value.Pos.SubCopy(Pos).ToVec3i();
            }
        }

        public BEBehaviorSlidingWindow RightWindow
        {
            get
            {
                if (rightWindowOffset == null) return null;
                var be = BlockBehaviorSlidingWindow.getSlidingWindowAt(Api.World, Pos.AddCopy(rightWindowOffset));
                if (be == null) rightWindowOffset = null;
                return be;
            }
            protected set
            {
                rightWindowOffset = value == null ? null : value.Pos.SubCopy(Pos).ToVec3i();
            }
        }

        public BEBehaviorSlidingWindow(BlockEntity blockentity) : base(blockentity)
        {
            windowBh = Block.GetBehavior<BlockBehaviorSlidingWindow>();
        }

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);

            SetupMeshAndBoxes(false);

            if (opened && animUtil != null && !animUtil.activeAnimationsByAnimCode.ContainsKey("opened"))
            {
                ToggleWindowSash(true);
            }
        }

        #region Adjacency helpers (ported from vanilla door)

        public Vec3i getAdjacentOffset(int right, int back = 0, int up = 0)
        {
            return getAdjacentOffset(right, back, up, RotateYRad, invertHandles);
        }

        public static Vec3i getAdjacentOffset(int right, int back, int up, float rotateYRad, bool invertHandles)
        {
            if (invertHandles) right = -right;
            return new Vec3i(
                right * (int)Math.Round(Math.Sin(rotateYRad + GameMath.PIHALF)) - back * (int)Math.Round(Math.Sin(rotateYRad)),
                up,
                right * (int)Math.Round(Math.Cos(rotateYRad + GameMath.PIHALF)) - back * (int)Math.Round(Math.Cos(rotateYRad))
            );
        }

        #endregion

        internal void SetupMeshAndBoxes(bool initialSetup)
        {
            if (LeftWindow == this || RightWindow == this) return;

            // Early out if we know this window is ace
            if (initialSetup && windowBh.pairable)
            {
                if (BlockBehaviorSlidingWindow.HasCombinableLeftWindow(Api.World, RotateYRad, Pos, windowBh.width, out BEBehaviorSlidingWindow otherWindow, out int offset))
                {
                    if (otherWindow.LeftWindow == null && otherWindow.RightWindow == null && otherWindow.windowFacing == windowFacing)
                    {
                        if (otherWindow.invertHandles)
                        {
                            if (otherWindow.windowBh.width > 1)
                            {
                                Api.World.BlockAccessor.SetBlock(0, otherWindow.Pos);
                                BlockPos leftWindowPos = Pos.AddCopy(windowFacing.GetCW(), (otherWindow.windowBh.width + windowBh.width - 1));
                                Api.World.BlockAccessor.SetBlock(otherWindow.Block.Id, leftWindowPos);
                                otherWindow = Block.GetBEBehavior<BEBehaviorSlidingWindow>(leftWindowPos);
                                otherWindow.RotateYRad = RotateYRad;
                                otherWindow.windowBh.placeMultiblockParts(Api.World, leftWindowPos);
                                LeftWindow = otherWindow;
                                LeftWindow.RightWindow = this;
                                LeftWindow.SetupMeshAndBoxes(true);
                            }
                            else
                            {
                                otherWindow.invertHandles = false;
                                LeftWindow = otherWindow;
                                LeftWindow.RightWindow = this;
                                LeftWindow.Blockentity.MarkDirty(true);
                                LeftWindow.SetupMeshAndBoxes(false);
                            }
                        }
                        else
                        {
                            LeftWindow = otherWindow;
                            LeftWindow.RightWindow = this;
                        }

                        invertHandles = true;
                        Blockentity.MarkDirty(true);
                    }
                }

                if (BlockBehaviorSlidingWindow.HasCombinableRightWindow(Api.World, RotateYRad, Pos, windowBh.width, out otherWindow, out offset))
                {
                    if (otherWindow.LeftWindow == null && otherWindow.RightWindow == null && otherWindow.windowFacing == windowFacing)
                    {
                        if (Api.Side == EnumAppSide.Server)
                        {
                            if (!otherWindow.invertHandles)
                            {
                                if (otherWindow.windowBh.width > 1)
                                {
                                    Api.World.BlockAccessor.SetBlock(0, otherWindow.Pos);
                                    BlockPos rightWindowPos = Pos.AddCopy(windowFacing.GetCCW(), (otherWindow.windowBh.width + windowBh.width - 1));
                                    Api.World.BlockAccessor.SetBlock(otherWindow.Block.Id, rightWindowPos);
                                    otherWindow = Block.GetBEBehavior<BEBehaviorSlidingWindow>(rightWindowPos);
                                    otherWindow.RotateYRad = RotateYRad;
                                    otherWindow.invertHandles = true;
                                    otherWindow.windowBh.placeMultiblockParts(Api.World, rightWindowPos);
                                    RightWindow = otherWindow;
                                    RightWindow.LeftWindow = this;
                                    otherWindow.SetupMeshAndBoxes(true);
                                }
                                else
                                {
                                    otherWindow.invertHandles = true;
                                    RightWindow = otherWindow;
                                    RightWindow.LeftWindow = this;
                                    RightWindow.Blockentity.MarkDirty(true);
                                    RightWindow.SetupMeshAndBoxes(false);
                                }
                            }
                            else
                            {
                                RightWindow = otherWindow;
                                RightWindow.LeftWindow = this;
                            }
                        }
                    }
                }
            }
            else if (!windowBh.pairable && initialSetup)
            {
                // get over being dumped by the other window
                LeftWindow = null;
                RightWindow = null;
                invertHandles = false;
                Blockentity.MarkDirty(true);
            }

            if (Api.Side == EnumAppSide.Client)
            {
                if (windowBh.animatableOrigMesh == null)
                {
                    string animkey = Block.Shape.ToString();
                    windowBh.animatableOrigMesh = animUtil.CreateMesh(animkey, null, out Shape shape, null);

                    windowBh.animatableShape = shape;
                    windowBh.animatableDictKey = animkey;
                }

                if (windowBh.animatableOrigMesh != null)
                {
                    animUtil.InitializeAnimator(windowBh.animatableDictKey, windowBh.animatableOrigMesh, windowBh.animatableShape, null);
                    UpdateMeshAndAnimations();
                }
            }

            UpdateHitBoxes();
        }

        protected virtual void UpdateMeshAndAnimations()
        {
            mesh = windowBh.animatableOrigMesh.Clone();

            float rot = RotateYRad;

            if (invertHandles) rot = -rot;

            if (rot != 0f)
            {
                mesh = mesh.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), 0, rot, 0);
            }

            if (invertHandles)
            {
                Matrixf matf = new Matrixf();
                matf.Translate(0.5f, 0.5f, 0.5f)
                    .Scale(-1f, 1f, 1f)
                    .Translate(-0.5f, -0.5f, -0.5f);

                mesh.MatrixTransform(matf.Values);

                if (Api.Side == EnumAppSide.Client && animUtil?.renderer != null)
                {
                    animUtil.renderer.backfaceCulling = false;
                    animUtil.renderer.ScaleX = -1f;
                }
            }
            else
            {
                if (Api.Side == EnumAppSide.Client && animUtil?.renderer != null)
                {
                    animUtil.renderer.backfaceCulling = true;
                    animUtil.renderer.ScaleX = 1f;
                }
            }

            // Make animator follow the same yaw so the "opened" animation always slides
            // along the local X axis, regardless of world orientation.
            if (Api.Side == EnumAppSide.Client && animUtil?.renderer != null)
            {
                animUtil.renderer.rotationDeg.Y = rot * GameMath.RAD2DEG;
            }
        }
        protected virtual void UpdateHitBoxes()
        {
            // Expecting Block.CollisionBoxes to contain:
            // [0] = stationary frame
            // [1] = moving sash (closed)
            // [2] = moving sash (open)
            var all = Block.CollisionBoxes ?? Array.Empty<Cuboidf>();

            Cuboidf stationary   = all.Length > 0 ? all[0].Clone() : new Cuboidf(0, 0, 0, 0, 0, 0);
            Cuboidf movingClosed = all.Length > 1 ? all[1].Clone() : stationary.Clone();
            Cuboidf movingOpen   = all.Length > 2 ? all[2].Clone() : movingClosed.Clone();

            var localClosed = new[] { stationary.Clone(), movingClosed.Clone() };
            var localOpened = new[] { stationary.Clone(), movingOpen.Clone() };

            // X center stays at the controller block center (0.5) so the mesh and boxes
            // always pivot around the controller column. This expects the JSON shapes
            // and collision to also start from the controller block as origin.
            float mirrorCenterX = 0.5f;

            if (invertHandles)
            {
                MirrorAroundCenterX(localClosed, mirrorCenterX);
                MirrorAroundCenterX(localOpened, mirrorCenterX);
            }

            // For yaw rotation, only X/Z of the origin matter. We keep X=0.5 so
            // the shape stays anchored on the controller column. Y is set to the
            // vertical center for symmetry; it does not affect yaw.
            double centerX = 0.5;
            double centerY = (windowBh?.height > 0 ? windowBh.height : 1) / 2.0;

            var origin = new Vec3d(centerX, centerY, 0.5);
            float degY = RotateYRad * GameMath.RAD2DEG;

            boxesClosed = new Cuboidf[localClosed.Length];
            boxesOpened = new Cuboidf[localOpened.Length];

            for (int i = 0; i < localClosed.Length; i++)
            {
                boxesClosed[i] = localClosed[i].RotatedCopy(0, degY, 0, origin);
                boxesOpened[i] = localOpened[i].RotatedCopy(0, degY, 0, origin);
            }

            // - Vanilla implicitly stacks its 1×2 column per block; we do not.
            // - Instead, JSON collisionboxes already describe the full multi-block
            //   footprint (0..2 in the case of a height=2 block), and here we only
            //   mirror + rotate that union into world space.
        }

        private static void MirrorAroundCenterX(Cuboidf[] boxes, float centerX)
        {
            for (int i = 0; i < boxes.Length; i++)
            {
                var b = boxes[i];
                float x1 = b.X1;
                float x2 = b.X2;

                b.X1 = 2f * centerX - x2;
                b.X2 = 2f * centerX - x1;

                boxes[i] = b;
            }
        }

        public virtual void OnBlockPlaced(ItemStack byItemStack, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (byItemStack == null) return; // Placed by worldgen

            RotateYRad = getRotateYRad(byPlayer, blockSel);
            SetupMeshAndBoxes(true);
        }

        public static float getRotateYRad(IPlayer byPlayer, BlockSelection blockSel)
        {
            BlockPos targetPos = blockSel.DidOffset ? blockSel.Position.AddCopy(blockSel.Face.Opposite) : blockSel.Position;
            double dx = byPlayer.Entity.Pos.X - (targetPos.X + blockSel.HitPosition.X);
            double dz = byPlayer.Entity.Pos.Z - (targetPos.Z + blockSel.HitPosition.Z);
            float angleHor = (float)Math.Atan2(dx, dz);

            float deg90 = GameMath.PIHALF;
            return ((int)Math.Round(angleHor / deg90)) * deg90;
        }

        public bool IsSideSolid(BlockFacing facing)
        {
            // Facing never changes, just make sure that face isn't open
            return !opened && facing == windowFacing;
        }

        #region IInteractable

        public bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
        {
            if (!windowBh.handopenable && byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
            {
                (Api as ICoreClientAPI).TriggerIngameError(this, "nothandopenable", Lang.Get("This window cannot be opened by hand."));
                return true;
            }

            ToggleWindowSashState(byPlayer, !opened);
            handling = EnumHandling.PreventDefault;
            return true;
        }

        #endregion

        internal void ToggleWindowSashFromPartner(bool opened)
        {
            // Just do visuals + hitboxes, no sound. Mirrors vanilla "sync the other leaf".
            this.opened = opened;
            ToggleWindowSash(opened);
            // Vanilla also updates neighbors; we stick to MarkBlockDirty in ToggleWindowSash.
        }

        public void ToggleWindowSashState(IPlayer byPlayer, bool opened)
        {
            this.opened = opened;
            ToggleWindowSash(opened);

            // Sync state to paired neighbors (no extra sounds)
            if (LeftWindow != null && invertHandles)
            {
                LeftWindow.ToggleWindowSashFromPartner(opened);
            }
            if (RightWindow != null)
            {
                RightWindow.ToggleWindowSashFromPartner(opened);
            }

            float pitch = opened ? 0.8f : 0.7f;

            var sound = opened ? windowBh?.OpenSound : windowBh?.CloseSound;


            // We use some extra sounds from "secondarySounds" attributes on top.
            var customSoundKey = Block.Attributes?["secondarySounds"]?[opened ? "open" : "close"]?.AsString(null);
            if (customSoundKey != null)
            {
                var customSound = new AssetLocation(customSoundKey);
                float customSoundPitch = opened ? 0.8f : 1f;
                Api.World.PlaySoundAt(customSound, Pos.X + 0.5f, Pos.InternalY + 0.5f, Pos.Z + 0.5f, byPlayer, EnumSoundType.Sound, customSoundPitch, 32f, 2f);
            }

            Api.World.PlaySoundAt(sound, Pos.X + 0.5f, Pos.InternalY + 0.5f, Pos.Z + 0.5f, byPlayer, EnumSoundType.Sound, pitch, 32f, 1f);
        }

        // Updates movement and hitboxes, but no sounds
        private void ToggleWindowSash(bool opened)
        {
            float easeInSpeed = Block.Attributes?["openingSpeed"].AsFloat(10) ?? 10;
            float easeOutSpeed = Block.Attributes?["closingSpeed"].AsFloat(10) ?? 10;

            this.opened = opened;

            if (!opened)
            {
                animUtil.StopAnimation("opened");
            }
            else
            {
                animUtil.StartAnimation(new AnimationMetaData()
                {
                    Animation = "opened",
                    Code = "opened",
                    EaseInSpeed = easeInSpeed,
                    EaseOutSpeed = easeOutSpeed
                });
            }

            Api?.World?.BlockAccessor.MarkBlockDirty(Pos);
            Blockentity.MarkDirty();
        }

        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {
            bool skipMesh = base.OnTesselation(mesher, tessThreadTesselator);
            if (!skipMesh)
            {
                mesher.AddMeshData(mesh);
            }
            return true;
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);

            bool beforeOpened = opened;

            RotateYRad = tree.GetFloat("rotateYRad");
            opened = tree.GetBool("opened");
            invertHandles = tree.GetBool("invertHandles");
            leftWindowOffset = tree.GetVec3i("leftWindowPos");
            rightWindowOffset = tree.GetVec3i("rightWindowPos");

            if (opened != beforeOpened && animUtil != null) ToggleWindowSash(opened);

            if (Api != null && Api.Side is EnumAppSide.Client)
            {
                if (animUtil?.renderer != null)
                {
                    animUtil.renderer.rotationDeg.Y = RotateYRad * GameMath.RAD2DEG;
                }

                UpdateMeshAndAnimations();

                if (opened && !beforeOpened && animUtil != null && !animUtil.activeAnimationsByAnimCode.ContainsKey("opened"))
                {
                    ToggleWindowSash(true);
                }

                UpdateHitBoxes();
                Api.World.BlockAccessor.MarkBlockDirty(Pos);
            }
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat("rotateYRad", RotateYRad);
            tree.SetBool("opened", opened);
            tree.SetBool("invertHandles", invertHandles);
            if (leftWindowOffset != null) tree.SetVec3i("leftWindowPos", leftWindowOffset);
            if (rightWindowOffset != null) tree.SetVec3i("rightWindowPos", rightWindowOffset);
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            if (Api is ICoreClientAPI capi)
            {
                if (capi.Settings.Bool["extendedDebugInfo"] == true)
                {
                    dsc.AppendLine("" + windowFacing + " " + (opened ? "open" : "closed"));
                    dsc.AppendLine("" + windowBh.height + "x" + windowBh.width);
                    EnumHandling h = EnumHandling.PassThrough;
                    if (windowBh.GetLiquidBarrierHeightOnSide(BlockFacing.NORTH, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: North");
                    if (windowBh.GetLiquidBarrierHeightOnSide(BlockFacing.EAST, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: East");
                    if (windowBh.GetLiquidBarrierHeightOnSide(BlockFacing.SOUTH, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: South");
                    if (windowBh.GetLiquidBarrierHeightOnSide(BlockFacing.WEST, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: West");
                }
            }
        }

        public void OnTransformed(IWorldAccessor worldAccessor, ITreeAttribute tree, int degreeRotation, Dictionary<int, AssetLocation> oldBlockIdMapping, Dictionary<int, AssetLocation> oldItemIdMapping, EnumAxis? flipAxis)
        {
            RotateYRad = tree.GetFloat("rotateYRad");
            RotateYRad = (RotateYRad - degreeRotation * GameMath.DEG2RAD) % GameMath.TWOPI;
            tree.SetFloat("rotateYRad", RotateYRad);
        }
    }
}
