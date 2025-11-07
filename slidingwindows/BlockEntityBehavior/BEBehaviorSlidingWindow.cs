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
    public class BEBehaviorSlidingWindow : BEBehaviorAnimatable, IInteractable, IRotatable
    {
        public float RotateYRad;
        protected bool opened;
        protected MeshData mesh;
        protected Cuboidf[] boxesClosed, boxesOpened;
        public BlockFacing windowFacing { get { return BlockFacing.HorizontalFromYaw(RotateYRad); } }
        public BlockBehaviorSlidingWindow windowBh;
        public Cuboidf[] ColSelBoxes => opened ? boxesOpened : boxesClosed;
        public bool Opened => opened;
        protected bool pairable;

        protected bool mirrorTrack;
        protected Vec3i leftWindowOffset;
        protected Vec3i rightWindowOffset;

        public BEBehaviorSlidingWindow LeftWindow
        {
            get {
                if (leftWindowOffset == null) return null;
                var be = BlockBehaviorSlidingWindow.getSlidingWindowAt(Api.World, Pos.AddCopy(leftWindowOffset));
                if (be == null) leftWindowOffset = null;
                return be;
            }
            set {
                leftWindowOffset = value == null ? null : value.Pos.SubCopy(Pos).ToVec3i();
            }
        }

        public BEBehaviorSlidingWindow RightWindow
        {
            get {
                if (rightWindowOffset == null) return null;
                var be = BlockBehaviorSlidingWindow.getSlidingWindowAt(Api.World, Pos.AddCopy(rightWindowOffset));
                if (be == null) rightWindowOffset = null;
                return be;
            }
            set {
                rightWindowOffset = value == null ? null : value.Pos.SubCopy(Pos).ToVec3i();
            }
        }

        public static Vec3i getAdjacentOffset(int right, int back, int up, float rotateYRad)
        {
            return new Vec3i(
                right * (int)Math.Round(Math.Sin(rotateYRad + GameMath.PIHALF)) - back * (int)Math.Round(Math.Sin(rotateYRad)),
                up,
                right * (int)Math.Round(Math.Cos(rotateYRad + GameMath.PIHALF)) - back * (int)Math.Round(Math.Cos(rotateYRad))
            );
        }
        public BEBehaviorSlidingWindow(BlockEntity blockentity) : base(blockentity)
        {
            boxesClosed = Block.CollisionBoxes;

            windowBh = Block.GetBehavior<BlockBehaviorSlidingWindow>();
        }

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);

            pairable = Block.Attributes?["pairable"].AsBool(false) ?? false;
            mirrorTrack = Block.Attributes?["mirrorTrack"].AsBool(false) ?? false;

            SetupMeshAndBoxes();

            if (opened && animUtil != null && !animUtil.activeAnimationsByAnimCode.ContainsKey("opened"))
            {
                ToggleWindowSash(true);
            }
        }
        internal void SetupMeshAndBoxes()
        {
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
            // Base mesh for static tesselation
            mesh = windowBh.animatableOrigMesh.Clone();
           
            float rot = RotateYRad;
            // Vanilla doors: use a sign-flipped rotation angle when mirrored so the
            // animation "faces" the right way. Do the same for our sliding window.
            if (mirrorTrack)
            {
                rot = -rot;
            }

            if (rot != 0f)
            {
                // Rotate the static mesh around the block center
                mesh = mesh.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), 0, rot, 0);
            }

            // Mirror in local X (left/right) around block center, just like vanilla doors
            if (mirrorTrack)
            {
                // We need a full matrix transform for this to update normals as well
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


            // Make sure the animator uses the same rotation so the "opened" animation
            // slides along the block's local X, regardless of world facing
            if (Api.Side == EnumAppSide.Client && animUtil?.renderer != null)
            {
                animUtil.renderer.rotationDeg.Y = rot * GameMath.RAD2DEG;
            }

        }
                
        private Cuboidf MirrorBoxX(Cuboidf box)
        {
            // Mirror across X = 0.5 (block center), keeping Y/Z the same
            return new Cuboidf(
                1f - box.X2, box.Y1, box.Z1,
                1f - box.X1, box.Y2, box.Z2
            );
        }


        protected virtual void UpdateHitBoxes()
        {
            var all = Block.CollisionBoxes ?? Array.Empty<Cuboidf>();

            // Expecting the collisionboxes array to have three items:
            //   0 = stationary sash
            //   1 = moving sash (closed)
            //   2 = moving sash (open)
            // but be defensive in case JSON is missing something.
            Cuboidf stationary = all.Length > 0 ? all[0].Clone() : new Cuboidf(0, 0, 0, 0, 0, 0);
            Cuboidf movingClosed = all.Length > 1 ? all[1].Clone() : stationary.Clone();
            Cuboidf movingOpen = all.Length > 2 ? all[2].Clone() : movingClosed.Clone();

            // Closed: stationary + moving in closed position
            boxesClosed = new[] { stationary, movingClosed };

            // Open: stationary + moving in open position
            boxesOpened = new[] { stationary, movingOpen };


            // Use the same signed rotation as the mesh
            float rot = RotateYRad;
            if (mirrorTrack)
            {
                rot = -rot;
            }

            if (rot != 0f)
            {
                float degY = rot * GameMath.RAD2DEG;
                var origin = new Vec3d(0.5, 0.5, 0.5);

                for (int i = 0; i < boxesClosed.Length; i++)
                {
                    boxesClosed[i] = boxesClosed[i].RotatedCopy(0, degY, 0, origin);
                }

                for (int i = 0; i < boxesOpened.Length; i++)
                {
                    boxesOpened[i] = boxesOpened[i].RotatedCopy(0, degY, 0, origin);
                }
            }

            // Now mirror hitboxes in local X if this leaf is mirrored
            if (mirrorTrack)
            {
                for (int i = 0; i < boxesClosed.Length; i++)
                {
                    boxesClosed[i] = MirrorBoxX(boxesClosed[i]);
                }
                for (int i = 0; i < boxesOpened.Length; i++)
                {
                    boxesOpened[i] = MirrorBoxX(boxesOpened[i]);
                }
            }

        }

       void TryPairWithNeighbor()
        {
            if (!pairable) return;
            if (LeftWindow != null || RightWindow != null) return;

            int width = windowBh?.width ?? 1;   // defined in your block behavior JSON/attrs
            foreach (int dir in new int[] { 1, -1 })
            {
                
                Vec3i offset = getAdjacentOffset(dir * width, 0, 0, RotateYRad);
                BlockPos npos = Pos.AddCopy(offset.X, offset.Y, offset.Z);

                var nBeh = BlockBehaviorSlidingWindow.getSlidingWindowAt(Api.World, npos);
                if (nBeh == null) continue;
                if (!nBeh.pairable) continue;
                if (nBeh.LeftWindow != null || nBeh.RightWindow != null) continue;
                if (nBeh.windowFacing != this.windowFacing) continue;

                if (dir == 1)
                {
                    // Neighbor is to our right -> we are left leaf, neighbor is right leaf
                    RightWindow = nBeh;
                    nBeh.LeftWindow = this;

                    mirrorTrack = false;
                    nBeh.mirrorTrack = true;
                }
                else
                {
                    // Neighbor is to our left -> we are right leaf, neighbor is left leaf
                    LeftWindow = nBeh;
                    nBeh.RightWindow = this;

                    mirrorTrack = true;
                    nBeh.mirrorTrack = false;
                }

                // Rebuild both now that pairing + mirroring is known
                SetupMeshAndBoxes();
                nBeh.SetupMeshAndBoxes();

                Blockentity.MarkDirty();
                nBeh.Blockentity.MarkDirty();
                break;
            }
        }


        public virtual void OnBlockPlaced(ItemStack byItemStack, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (byItemStack == null) return; // Placed by worldgen

            RotateYRad = getRotateYRad(byPlayer, blockSel);
            pairable = Block.Attributes?["pairable"].AsBool(false) ?? false;

            SetupMeshAndBoxes();
            TryPairWithNeighbor();
        }

        public static float getRotateYRad(IPlayer byPlayer, BlockSelection blockSel)
        {
            BlockPos targetPos = blockSel.DidOffset ? blockSel.Position.AddCopy(blockSel.Face.Opposite) : blockSel.Position;
            double dx = byPlayer.Entity.Pos.X - (targetPos.X + blockSel.HitPosition.X);
            double dz = (float)byPlayer.Entity.Pos.Z - (targetPos.Z + blockSel.HitPosition.Z);
            float angleHor = (float)Math.Atan2(dx, dz);

            float deg90 = GameMath.PIHALF;
            return ((int)Math.Round(angleHor / deg90)) * deg90;
        }

        public bool IsSideSolid(BlockFacing facing)
        {
            return facing == windowFacing;
        }

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

        internal void ToggleWindowSashFromPartner(bool opened)
        {
            // Just do the visual + collision change, no sounds
            this.opened = opened;
            ToggleWindowSash(opened);
        }

        public void ToggleWindowSashState(IPlayer byPlayer, bool opened)
        {
            this.opened = opened;
            ToggleWindowSash(opened);
                    
            // Sync state to paired neighbors (no extra sounds)
            if (LeftWindow != null)
            {
                LeftWindow.ToggleWindowSashFromPartner(opened);
            }
            if (RightWindow != null)
            {
                RightWindow.ToggleWindowSashFromPartner(opened);
            }

            float pitch = opened ? 0.8f : 0.7f;

            var sound = opened ? windowBh?.OpenSound : windowBh?.CloseSound;
            var customSoundKey = Block.Attributes?["secondarySounds"]?[opened ? "open" : "close"]?.AsString(null);
            if (customSoundKey != null)
            {
                var customSound = new AssetLocation(customSoundKey);
                float customSoundPitch = opened ? 0.8f : 1f;
                Api.World.PlaySoundAt(customSound, Pos.X + 0.5f, Pos.InternalY + 0.5f, Pos.Z + 0.5f, byPlayer, EnumSoundType.Sound, customSoundPitch, 32f, 2f);

            }
            Api.World.PlaySoundAt(sound, Pos.X + 0.5f, Pos.InternalY + 0.5f, Pos.Z + 0.5f, byPlayer, EnumSoundType.Sound, pitch, 32f, 1f);
        }

        // updates movement and hitboxes, but no sound, because this is also done in initialize()
        private void ToggleWindowSash(bool opened)
        {
            float easeInSpeed = Block.Attributes?["openingSpeed"].AsFloat(10) ?? 10;
            float easeOutSpeed =Block.Attributes?["closingSpeed"].AsFloat(10) ?? 10;
            this.opened = opened;
            if (!opened)
            {
                animUtil.StopAnimation("opened");
            }
            else
            {
                animUtil.StartAnimation(new AnimationMetaData() { Animation = "opened", Code = "opened", EaseInSpeed = easeInSpeed, EaseOutSpeed = easeOutSpeed });
            }
            // Rebuild hitboxes for new state
            UpdateHitBoxes();

            // make mesh update with the new state
            if (Api?.Side == EnumAppSide.Client)
            {
                UpdateMeshAndAnimations();
            }

            // Push the change to client+server, and recache related blocks' selection/collision
            Api?.World?.BlockAccessor.MarkBlockDirty(Pos);
            Blockentity.MarkDirty();
            Api?.World?.BlockAccessor.ExchangeBlock(Block.Id, Pos);
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
            mirrorTrack = tree.GetBool("mirrorTrack");

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
            tree.SetBool("mirrorTrack", mirrorTrack);
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