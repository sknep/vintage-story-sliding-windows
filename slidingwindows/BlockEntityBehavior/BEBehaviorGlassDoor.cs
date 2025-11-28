using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using SlidingWindows.BlockBehaviors;

#nullable disable

namespace SlidingWindows.BlockEntityBehaviors
{

    public class BEBehaviorGlassDoor : BEBehaviorAnimatable, IInteractable, IRotatable
    {
        public float RotateYRad;
        protected bool isOpen;
        protected bool invertHandles;
        protected MeshData mesh;
        protected Cuboidf[] boxesClosed, boxesOpened;

        protected MeshData currentStaticMesh;
        class ScheduledCallback
        {
            public long Id;
            public Action Callback;
        }
        readonly List<ScheduledCallback> scheduledCallbacks = new List<ScheduledCallback>();
        bool renderCurrentMeshAnimation = false;
        bool tesselateCurrentStaticMesh = true;
        enum CurrentStaticMeshType { Closed, Opened }
        CurrentStaticMeshType currentStaticMeshType;
        bool animationStopsSoon = false;
        ICoreClientAPI capi;
        bool advancedAnimationAvailable = false;

        string OpeningAnimCode => Block.Attributes?["openingAnimationCode"].AsString("opening");
        string ClosingAnimCode => Block.Attributes?["closingAnimationCode"].AsString("closing");
        string LegacyOpenedAnimCode = "opened";
        bool UseAdvancedAnimation => advancedAnimationAvailable && Api?.Side == EnumAppSide.Client && animUtil != null;

        public BlockFacing facingWhenClosed { get { return BlockFacing.HorizontalFromYaw(RotateYRad); } }
        public BlockFacing facingWhenOpened { get { return invertHandles? facingWhenClosed.GetCCW() : facingWhenClosed.GetCW(); } }

        /// <summary>
        /// A rather counter-intuitive property, setting this actually sets up an internal Vec3i giving the offset to the Pos of the supplied door
        /// </summary>
        public BEBehaviorGlassDoor LeftDoor
        {
            get
            {
                if (leftDoorOffset != null)
                {
                    var door = BlockBehaviorGlassDoor.getDoorAt(Api.World, Pos.AddCopy(leftDoorOffset));
                    if (door == null) leftDoorOffset = null;

                    return door;
                }

                return null;
            }
            protected set { leftDoorOffset = value == null ? null : value.Pos.SubCopy(Pos).ToVec3i(); }
        }
        /// <summary>
        /// A rather counter-intuitive property, setting this actually sets up an internal Vec3i giving the offset to the Pos of the supplied door
        /// </summary>
        public BEBehaviorGlassDoor RightDoor
        {
            get
            {
                if (rightDoorOffset != null)
                {
                    var door = BlockBehaviorGlassDoor.getDoorAt(Api.World, Pos.AddCopy(rightDoorOffset));
                    if (door == null) rightDoorOffset = null;

                    return door;
                }

                return null;
            }
            protected set { rightDoorOffset = value == null ? null : value.Pos.SubCopy(Pos).ToVec3i(); }
        }

        protected Vec3i leftDoorOffset;
        protected Vec3i rightDoorOffset;

        public BlockBehaviorGlassDoor doorBh;

        public Cuboidf[] ColSelBoxes => isOpen ? boxesOpened : boxesClosed;
        public bool Opened => isOpen;
        public bool InvertHandles => invertHandles;
        public string StoryLockedCode;

        public BEBehaviorGlassDoor(BlockEntity blockentity) : base(blockentity)
        {
            boxesClosed = Block.CollisionBoxes;

            doorBh = Block.GetBehavior<BlockBehaviorGlassDoor>();
        }

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);
            capi = api as ICoreClientAPI;

            SetupRotationsAndColSelBoxes(false);

            if (Api.Side == EnumAppSide.Client)
            {
                PrepareStaticAndAnimMeshes();
                if (UseAdvancedAnimation)
                {
                    string primaryTrack = isOpen ? ClosingAnimCode : OpeningAnimCode;
                    EnsureAnimatorTrackReady(primaryTrack);
                    UpdateStaticMeshAndRotation(isOpen);
                    renderCurrentMeshAnimation = false;
                    updateShouldRenderFromCurrentAnim();
                    Api.World.BlockAccessor.MarkBlockDirty(Pos);
                }
                else
                {
                    UpdateLegacyMeshAndAnimations();
                    Api.World.BlockAccessor.MarkBlockDirty(Pos);
                }
            }

            if (isOpen && animUtil != null && !UseAdvancedAnimation && !animUtil.activeAnimationsByAnimCode.ContainsKey("opened"))
            {
                // Legacy path still expects vanilla animation behaviour
                ToggleDoorWing(true);
            }
        }

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

        internal void SetupRotationsAndColSelBoxes(bool initalSetup)
        {
            if (initalSetup)
            {

                if (BlockBehaviorGlassDoor.HasCombinableLeftDoor(Api.World, RotateYRad, Pos, doorBh.width, out BEBehaviorGlassDoor otherDoor, out int offset))
                {
                    if (otherDoor.LeftDoor == null && otherDoor.RightDoor == null && otherDoor.facingWhenClosed == facingWhenClosed)
                    {
                        if (otherDoor.invertHandles)
                        {
                            if (otherDoor.doorBh.width > 1)
                            {
                                Api.World.BlockAccessor.SetBlock(0, otherDoor.Pos);
                                BlockPos leftDoorPos = Pos.AddCopy(facingWhenClosed.GetCW(), (otherDoor.doorBh.width + doorBh.width - 1));
                                Api.World.BlockAccessor.SetBlock(otherDoor.Block.Id, leftDoorPos);
                                otherDoor = Block.GetBEBehavior<BEBehaviorGlassDoor>(leftDoorPos);
                                otherDoor.RotateYRad = RotateYRad;
                                otherDoor.doorBh.placeMultiblockParts(Api.World, leftDoorPos);
                                LeftDoor = otherDoor;
                                LeftDoor.RightDoor = this;
                                LeftDoor.SetupRotationsAndColSelBoxes(true);
                            }
                            else
                            {
                                otherDoor.invertHandles = false;
                                LeftDoor = otherDoor;
                                LeftDoor.RightDoor = this;
                                LeftDoor.Blockentity.MarkDirty(true);
                                LeftDoor.SetupRotationsAndColSelBoxes(false);
                            }
                        }
                        else
                        {
                            LeftDoor = otherDoor;
                            LeftDoor.RightDoor = this;
                        }

                        invertHandles = true;
                        Blockentity.MarkDirty(true);
                    }
                }

                if (BlockBehaviorGlassDoor.HasCombinableRightDoor(Api.World, RotateYRad, Pos, doorBh.width, out otherDoor, out offset))
                {
                    if (otherDoor.LeftDoor == null && otherDoor.RightDoor == null && otherDoor.facingWhenClosed == facingWhenClosed)
                    {
                        if (Api.Side == EnumAppSide.Server)
                        {
                            if (!otherDoor.invertHandles)
                            {
                                if (otherDoor.doorBh.width > 1)
                                {
                                    Api.World.BlockAccessor.SetBlock(0, otherDoor.Pos);
                                    BlockPos rightDoorPos = Pos.AddCopy(facingWhenClosed.GetCCW(), (otherDoor.doorBh.width + doorBh.width - 1));
                                    Api.World.BlockAccessor.SetBlock(otherDoor.Block.Id, rightDoorPos);
                                    otherDoor = Block.GetBEBehavior<BEBehaviorGlassDoor>(rightDoorPos);
                                    otherDoor.RotateYRad = RotateYRad;
                                    otherDoor.invertHandles = true;
                                    otherDoor.doorBh.placeMultiblockParts(Api.World, rightDoorPos);
                                    RightDoor = otherDoor;
                                    RightDoor.LeftDoor = this;
                                    otherDoor.SetupRotationsAndColSelBoxes(true);
                                }
                                else
                                {
                                    otherDoor.invertHandles = true;
                                    RightDoor = otherDoor;
                                    RightDoor.LeftDoor = this;
                                    RightDoor.Blockentity.MarkDirty(true);
                                    RightDoor.SetupRotationsAndColSelBoxes(false);
                                }
                            }
                            else
                            {
                                RightDoor = otherDoor;
                                RightDoor.LeftDoor = this;
                            }
                        }
                    }
                }
            }

            if (Api.Side == EnumAppSide.Client)
            {
                PrepareStaticAndAnimMeshes();
                UpdateStaticMeshAndRotation(isOpen);
            }

            UpdateHitBoxes();
        }

        private void PrepareStaticAndAnimMeshes()
        {
            if (Api.Side != EnumAppSide.Client) return;
            if (doorBh == null) return;
            if (capi == null) capi = Api as ICoreClientAPI;

            string closedAnimKey = Block.Shape?.ToString() ?? doorBh.animatableDictKey;
            if (doorBh.closedDictKey == null) doorBh.closedDictKey = closedAnimKey;

            if (animUtil != null && doorBh.closedAnimMesh == null)
            {
                doorBh.closedAnimMesh = animUtil.CreateMesh(closedAnimKey, null, out Shape animShape, null);
                if (doorBh.closedShape == null) doorBh.closedShape = animShape;
            }

            if (doorBh.animatableOrigMesh == null && doorBh.closedAnimMesh != null)
            {
                doorBh.animatableOrigMesh = doorBh.closedAnimMesh;
                doorBh.animatableShape = doorBh.closedShape;
                doorBh.animatableDictKey = doorBh.closedDictKey;
            }

            if (doorBh.closedShape == null && Block.Shape != null)
            {
                doorBh.closedShape = Shape.TryGet(Api, Block.Shape.Base);
            }

            if (doorBh.closedStaticMesh == null && doorBh.closedShape != null && capi != null)
            {
                capi.Tesselator.TesselateShape(Block, doorBh.closedShape, out MeshData closedStatic, null);
                doorBh.closedStaticMesh = closedStatic;
            }

            advancedAnimationAvailable = Block.Attributes?["openedShape"].Exists == true;

            if (!advancedAnimationAvailable) return;

            var openedShapeCode = Block.Attributes["openedShape"].AsString(null);
            var openedShapeLoc = AssetLocation.Create(openedShapeCode);
            string openedAnimKey = closedAnimKey + "-open";

            if (doorBh.openedDictKey == null) doorBh.openedDictKey = openedAnimKey;

            if (doorBh.openedShape == null) doorBh.openedShape = Shape.TryGet(Api, openedShapeLoc);

            if (animUtil != null && doorBh.openedAnimMesh == null && doorBh.openedShape != null)
            {
                doorBh.openedAnimMesh = animUtil.CreateMesh(openedAnimKey, doorBh.openedShape, out Shape animShape, null);
                doorBh.openedShape = animShape;
            }

            if (doorBh.openedStaticMesh == null && doorBh.openedShape != null && capi != null)
            {
                capi.Tesselator.TesselateShape(Block, doorBh.openedShape, out MeshData openedStatic, null);
                doorBh.openedStaticMesh = openedStatic;
            }
        }

        private void InitializeAnimatorTracks(string animCode)
        {
            if (Api.Side != EnumAppSide.Client || animUtil == null) return;

            MeshData animMesh = null;
            Shape animShape = null;
            string animKey = null;

            if (animCode == OpeningAnimCode)
            {
                animMesh = doorBh.closedAnimMesh ?? doorBh.animatableOrigMesh;
                animShape = doorBh.closedShape ?? doorBh.animatableShape;
                animKey = doorBh.closedDictKey ?? doorBh.animatableDictKey;
            }
            else if (animCode == ClosingAnimCode)
            {
                animMesh = doorBh.openedAnimMesh ?? doorBh.closedAnimMesh ?? doorBh.animatableOrigMesh;
                animShape = doorBh.openedShape ?? doorBh.closedShape ?? doorBh.animatableShape;
                animKey = doorBh.openedDictKey ?? doorBh.closedDictKey ?? doorBh.animatableDictKey;
            }
            else if (animCode == LegacyOpenedAnimCode)
            {
                animMesh = doorBh.closedAnimMesh ?? doorBh.animatableOrigMesh;
                animShape = doorBh.closedShape ?? doorBh.animatableShape;
                animKey = doorBh.closedDictKey ?? doorBh.animatableDictKey;
            }

            if (animMesh == null || animShape == null || animKey == null) return;

            animUtil.InitializeAnimator(animKey, animMesh, animShape, null);
        }

        protected virtual void UpdateStaticMeshAndRotation(bool useOpened = false)
        {
            if (Api.Side != EnumAppSide.Client) return;

            MeshData baseStaticMesh;
            if (useOpened && doorBh.openedStaticMesh != null)
            {
                baseStaticMesh = doorBh.openedStaticMesh;
                currentStaticMeshType = CurrentStaticMeshType.Opened;
            }
            else
            {
                baseStaticMesh = doorBh.closedStaticMesh ?? doorBh.closedAnimMesh ?? doorBh.animatableOrigMesh;
                currentStaticMeshType = CurrentStaticMeshType.Closed;
            }
            
            currentStaticMesh = baseStaticMesh?.Clone();
            if (currentStaticMesh == null) return;

            float rot = invertHandles ? -RotateYRad : RotateYRad;
            Matrixf mat = new Matrixf();
            mat.Translate(0.5f, 0.5f, 0.5f)
               .RotateY(rot)
               .Translate(-0.5f, -0.5f, -0.5f);
            currentStaticMesh.MatrixTransform(mat.Values);

            if (invertHandles)
            {
                Matrixf mirror = new Matrixf();
                mirror.Translate(0.5f, 0.5f, 0.5f).Scale(-1, 1, 1).Translate(-0.5f, -0.5f, -0.5f);
                currentStaticMesh.MatrixTransform(mirror.Values);
            }

            ApplyRendererRotation(rot);
            if (animUtil?.renderer != null)
            {
                animUtil.renderer.CustomTransform = mat.Values;
            }
        }

        protected virtual void UpdateLegacyMeshAndAnimations()
        {
            if (doorBh == null) return;

            MeshData sourceMesh = doorBh.closedAnimMesh ?? doorBh.animatableOrigMesh;
            if (sourceMesh == null) return;

            mesh = sourceMesh.Clone();
            if (RotateYRad != 0)
            {
                float rot = invertHandles ? -RotateYRad : RotateYRad;
                mesh = mesh.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), 0, rot, 0);
                if (animUtil?.renderer != null)
                {
                    animUtil.renderer.rotationDeg.Y = rot * GameMath.RAD2DEG;
                }
            }

            if (invertHandles)
            {
                Matrixf matf = new Matrixf();
                matf.Translate(0.5f, 0.5f, 0.5f).Scale(-1, 1, 1).Translate(-0.5f, -0.5f, -0.5f);
                mesh.MatrixTransform(matf.Values);

                if (animUtil?.renderer != null)
                {
                    animUtil.renderer.backfaceCulling = false;
                    animUtil.renderer.ScaleX = -1;
                }
            }
        }

        protected virtual void UpdateHitBoxes()
        {
            if (RotateYRad != 0)
            {
                boxesClosed = Block.CollisionBoxes;
                var boxes = new Cuboidf[boxesClosed.Length];
                for (int i = 0; i < boxesClosed.Length; i++)
                {
                    boxes[i] = boxesClosed[i].RotatedCopy(0, RotateYRad * GameMath.RAD2DEG, 0, new Vec3d(0.5, 0.5, 0.5));
                }

                boxesClosed = boxes;
            }

            var boxesopened = new Cuboidf[boxesClosed.Length];
            for (int i = 0; i < boxesClosed.Length; i++)
            {
                boxesopened[i] = boxesClosed[i].RotatedCopy(0, invertHandles ? 90 : -90, 0, new Vec3d(0.5, 0.5, 0.5));
            }

            this.boxesOpened = boxesopened;
        }

        public override void OnBlockRemoved()
        {
            base.OnBlockRemoved();
            CancelAnimationWork();
        }

        public override void OnBlockUnloaded()
        {
            base.OnBlockUnloaded();
            CancelAnimationWork();
        }

        public virtual void OnBlockPlaced(ItemStack byItemStack, IPlayer byPlayer, BlockSelection blockSel)
        {
            if (byItemStack == null) return; // Placed by worldgen

            RotateYRad = getRotateYRad(byPlayer, blockSel);
            SetupRotationsAndColSelBoxes(true);
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
            return (!isOpen && facing == facingWhenClosed) || (isOpen && facing == facingWhenOpened);
        }

        public bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
        {
            if (!doorBh.handopenable && byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
            {
                (Api as ICoreClientAPI).TriggerIngameError(this, "nothandopenable", Lang.Get("This door cannot be opened by hand."));
                return true;
            }

            ToggleDoorState(byPlayer, !isOpen);
            handling = EnumHandling.PreventDefault;
            return true;
        }

        public void ToggleDoorState(IPlayer byPlayer, bool isOpen)
        {
            this.isOpen = isOpen;
            ToggleDoorWing(isOpen);

            float pitch = isOpen ? 1.1f : 0.9f;

            var sound = isOpen ? doorBh?.OpenSound : doorBh?.CloseSound;

            Api.World.PlaySoundAt(sound, Pos.X + 0.5f, Pos.InternalY + 0.5f, Pos.Z + 0.5f, byPlayer, EnumSoundType.Sound, pitch);

            if (LeftDoor != null && invertHandles)
            {
                LeftDoor.ToggleDoorWing(isOpen);
                LeftDoor.UpdateNeighbors();
            }
            else if (RightDoor != null)
            {
                RightDoor.ToggleDoorWing(isOpen);
                RightDoor.UpdateNeighbors();
            }

            Blockentity.MarkDirty(true);

            UpdateNeighbors();
        }

        private void UpdateNeighbors()
        {
            if (Api.Side == EnumAppSide.Server)
            {
                BlockPos tempPos = new BlockPos(Pos.dimension);
                for (int y = 0; y < doorBh.height; y++)
                {
                    tempPos.Set(Pos).Add(0, y, 0);
                    BlockFacing sideMove = BlockFacing.ALLFACES[Opened ? facingWhenClosed.HorizontalAngleIndex : facingWhenOpened.HorizontalAngleIndex];

                    for (int x = 0; x < doorBh.width; x++)
                    {
                        Api.World.BlockAccessor.TriggerNeighbourBlockUpdate(tempPos);
                        tempPos.Add(sideMove);
                    }
                }
            }
        }

        private void ToggleDoorWing(bool isOpen)
        {
            this.isOpen = isOpen;

            if (!UseAdvancedAnimation)
            {
                if (animUtil == null)
                {
                    Blockentity.MarkDirty();
                    return;
                }

                if (!isOpen)
                {
                    animUtil.StopAnimation(LegacyOpenedAnimCode);
                }
                else
                {
                    float easingSpeed = Block.Attributes?["easingSpeed"].AsFloat(10) ?? 10;
                    animUtil.StartAnimation(new AnimationMetaData() { Animation = LegacyOpenedAnimCode, Code = LegacyOpenedAnimCode, EaseInSpeed = easingSpeed, EaseOutSpeed = easingSpeed });
                }
                Blockentity.MarkDirty();
                return;
            }

            string animCode = isOpen ? OpeningAnimCode : ClosingAnimCode;
            UpdateStaticMeshAndRotation(isOpen);
            ToggleDoorAnimation(animCode);
            Blockentity.MarkDirty();
        }

        private void RunAfterMs(int delayMs, Action callback)
        {
            if (capi == null || callback == null) return;

            var scheduled = new ScheduledCallback { Callback = callback };
            scheduled.Id = capi.Event.RegisterCallback(dt =>
            {
                scheduledCallbacks.Remove(scheduled);
                var cb = scheduled.Callback;
                scheduled.Callback = null;
                cb?.Invoke();
            }, delayMs);

            scheduledCallbacks.Add(scheduled);
        }

        private void RunBeforeNextFrameRender(Action callback)
        {
            if (capi == null || callback == null) return;
            capi.Event.EnqueueMainThreadTask(callback, "GlassDoorDeferred");
        }

        private bool ToggleDoorAnimation(string animCode)
        {
            if (!UseAdvancedAnimation) return false;

            EnsureAnimatorTrackReady(animCode);
            ApplyRendererRotation(invertHandles ? -RotateYRad : RotateYRad);
            return StartAnimationAndCallback(animCode);
        }

        private bool StartAnimationAndCallback(string animCode)
        {
            if (!UseAdvancedAnimation) return false;

            CompletePendingAnimationCallbacks(!renderCurrentMeshAnimation);

            float easeInSpeed = Block.Attributes?["openingSpeed"].AsFloat(Block.Attributes?["easingSpeed"].AsFloat(10) ?? 10) ?? 10;
            float easeOutSpeed = Block.Attributes?["closingSpeed"].AsFloat(Block.Attributes?["easingSpeed"].AsFloat(10) ?? 10) ?? 10;
            float easeSpeed = animCode == OpeningAnimCode ? easeInSpeed : easeOutSpeed;

            var meta = new AnimationMetaData
            {
                Animation = animCode,
                Code = animCode,
                EaseInSpeed = easeSpeed,
                EaseOutSpeed = easeSpeed,
                AnimationSpeed = 1f
            };

            double baseDurationMs = 1000.0 / easeSpeed;

            tesselateCurrentStaticMesh = true;
            renderCurrentMeshAnimation = true;
            updateShouldRenderFromCurrentAnim();

            animUtil.StartAnimation(meta);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);

            RunBeforeNextFrameRender(() =>
            {
                tesselateCurrentStaticMesh = false;
                RunBeforeNextFrameRender(() => Api.World.BlockAccessor.MarkBlockDirty(Pos));
            });

            string nextAnimCode = animCode == ClosingAnimCode ? OpeningAnimCode : ClosingAnimCode;

            RunAfterMs((int)(baseDurationMs * 6), () =>
            {
                UpdateStaticMeshAndRotation(isOpen);
                tesselateCurrentStaticMesh = true;
                RunBeforeNextFrameRender(() =>
                {
                    animationStopsSoon = true;
                    Api.World.BlockAccessor.MarkBlockDirty(Pos);
                    RunBeforeNextFrameRender(() => EnsureAnimatorTrackReady(nextAnimCode));
                });
            });

            return true;
        }

        private bool EnsureAnimatorTrackReady(string animCode)
        {
            if (!UseAdvancedAnimation) return false;

            bool missingClosedMeshes = doorBh?.closedStaticMesh == null || doorBh?.closedAnimMesh == null;
            bool missingOpenedMeshes = doorBh?.openedStaticMesh == null && doorBh?.openedAnimMesh == null && advancedAnimationAvailable;

            if (missingClosedMeshes || missingOpenedMeshes)
            {
                PrepareStaticAndAnimMeshes();
            }

            InitializeAnimatorTracks(animCode);
            return true;
        }

        private void CompletePendingAnimationCallbacks(bool invoke = true)
        {
            if (capi == null || scheduledCallbacks.Count == 0) return;

            var snapshot = scheduledCallbacks.ToArray();
            scheduledCallbacks.Clear();

            foreach (var scheduled in snapshot)
            {
                capi.Event.UnregisterCallback(scheduled.Id);

                if (invoke)
                {
                    var cb = scheduled.Callback;
                    scheduled.Callback = null;
                    cb?.Invoke();
                }
                else
                {
                    scheduled.Callback = null;
                }
            }
        }

        private void CancelAnimationWork()
        {
            if (Api?.Side != EnumAppSide.Client) return;

            CompletePendingAnimationCallbacks(false);
            animationStopsSoon = false;
            tesselateCurrentStaticMesh = true;
            renderCurrentMeshAnimation = false;
            updateShouldRenderFromCurrentAnim();
            if (animUtil == null) return;
            animUtil.StopAnimation(OpeningAnimCode);
            animUtil.StopAnimation(ClosingAnimCode);
            animUtil.StopAnimation(LegacyOpenedAnimCode);
        }

        private void ApplyRendererRotation(float rot)
        {
            if (animUtil?.renderer == null) return;

            animUtil.renderer.rotationDeg.Y = rot * GameMath.RAD2DEG;
            animUtil.renderer.ScaleX = invertHandles ? -1f : 1f;
            animUtil.renderer.backfaceCulling = !invertHandles;
        }

        private void updateShouldRenderFromCurrentAnim()
        {
            if (animUtil?.renderer == null || animUtil?.animator == null) return;
            animUtil.renderer.ShouldRender = renderCurrentMeshAnimation;
        }
        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {
            if (!UseAdvancedAnimation)
            {
                bool skipMesh = base.OnTesselation(mesher, tessThreadTesselator);
                if (!skipMesh && mesh != null)
                {
                    mesher.AddMeshData(mesh);
                }
                return true;
            }

            if (Api.Side != EnumAppSide.Client) return true;

            if (tesselateCurrentStaticMesh && currentStaticMesh != null && currentStaticMesh.VerticesCount > 0)
            {
                mesher.AddMeshData(currentStaticMesh);

                if (renderCurrentMeshAnimation && animationStopsSoon)
                {
                    animationStopsSoon = false;
                    RunBeforeNextFrameRender(() =>
                    {
                        Api.World.BlockAccessor.MarkBlockDirty(Pos);
                        RunAfterMs(30, () =>
                        {
                            if (animUtil == null) return;
                            tesselateCurrentStaticMesh = true;
                            renderCurrentMeshAnimation = false;
                            updateShouldRenderFromCurrentAnim();
                            animUtil.StopAnimation(OpeningAnimCode);
                            animUtil.StopAnimation(ClosingAnimCode);
                        });
                    });
                }
            }

            return true;
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);

            bool beforeOpened = isOpen;

            RotateYRad = tree.GetFloat("rotateYRad");
            isOpen = tree.GetBool("opened") || tree.GetBool("isOpen"); // old windows used 'opened' like doors
            invertHandles = tree.GetBool("invertHandles");
            leftDoorOffset = tree.GetVec3i("leftDoorPos");
            rightDoorOffset = tree.GetVec3i("rightDoorPos");
            StoryLockedCode = tree.GetString("storyLockedCode");

            if (isOpen != beforeOpened && animUtil != null)
            {
                if (UseAdvancedAnimation)
                {
                    ToggleDoorWing(isOpen);
                }
                else
                {
                    ToggleDoorWing(isOpen);
                }
            }

            if (Api != null && Api.Side is EnumAppSide.Client)
            {
                if (UseAdvancedAnimation)
                {
                    bool needPrep = doorBh?.closedStaticMesh == null || (advancedAnimationAvailable && doorBh?.openedStaticMesh == null);
                    if (needPrep)
                    {
                        PrepareStaticAndAnimMeshes();
                        string primaryTrack = isOpen ? ClosingAnimCode : OpeningAnimCode;
                        EnsureAnimatorTrackReady(primaryTrack);
                    }

                    if (!renderCurrentMeshAnimation)
                    {
                        ApplyRendererRotation(invertHandles ? -RotateYRad : RotateYRad);
                        UpdateStaticMeshAndRotation(isOpen);
                        Api.World.BlockAccessor.MarkBlockDirty(Pos);
                    }
                }
                else
                {
                    UpdateLegacyMeshAndAnimations();
                    Api.World.BlockAccessor.MarkBlockDirty(Pos);
                }

                UpdateHitBoxes();
            }
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);

            tree.SetFloat("rotateYRad", RotateYRad);
            tree.SetBool("isOpen", isOpen);
            tree.SetBool("invertHandles", invertHandles);
            if (StoryLockedCode != null)
            {
                tree.SetString("storyLockedCode", StoryLockedCode);
            }
            if (leftDoorOffset != null) tree.SetVec3i("leftDoorPos", leftDoorOffset);
            if (rightDoorOffset != null) tree.SetVec3i("rightDoorPos", rightDoorOffset);
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            if (Api is ICoreClientAPI capi)
            {
                if (capi.Settings.Bool["extendedDebugInfo"] == true)
                {
                    dsc.AppendLine("" + facingWhenClosed + (invertHandles ? "-inv " : " ") + (isOpen ? "open" : "closed"));
                    dsc.AppendLine("" + doorBh.height + "x" + doorBh.width + (leftDoorOffset != null ? " leftdoor at:" + leftDoorOffset : " ") + (rightDoorOffset != null ? " rightdoor at:" + rightDoorOffset : " "));
                    EnumHandling h = EnumHandling.PassThrough;
                    if (doorBh.GetLiquidBarrierHeightOnSide(BlockFacing.NORTH, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: North");
                    if (doorBh.GetLiquidBarrierHeightOnSide(BlockFacing.EAST, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: East");
                    if (doorBh.GetLiquidBarrierHeightOnSide(BlockFacing.SOUTH, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: South");
                    if (doorBh.GetLiquidBarrierHeightOnSide(BlockFacing.WEST, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: West");
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
