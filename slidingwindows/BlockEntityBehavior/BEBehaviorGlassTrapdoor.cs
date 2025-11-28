using System;
using System.Collections.Generic;
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
    public class BEBehaviorGlassTrapdoor : BEBehaviorAnimatable, IInteractable, IRotatable
    {
        protected bool opened;
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

        public int AttachedFace;
        public int RotDeg; 

        public float RotRad => RotDeg * GameMath.DEG2RAD;
        string OpeningAnimCode => Block.Attributes?["openingAnimationCode"].AsString("opening");
        string ClosingAnimCode => Block.Attributes?["closingAnimationCode"].AsString("closing");
        string LegacyOpenedAnimCode = "opened";
        bool UseAdvancedAnimation => advancedAnimationAvailable && Api?.Side == EnumAppSide.Client && animUtil != null;

        public BlockFacing facingWhenClosed
        {
            get
            {
                if (BlockFacing.ALLFACES[AttachedFace].IsVertical) return BlockFacing.ALLFACES[AttachedFace].Opposite;
                else return BlockFacing.DOWN.FaceWhenRotatedBy(0f, (float)BlockFacing.ALLFACES[AttachedFace].HorizontalAngleIndex * 90f * GameMath.DEG2RAD + 90f * GameMath.DEG2RAD, RotRad);
            }
        }
        public BlockFacing facingWhenOpened
        {
            get
            {
                if (BlockFacing.ALLFACES[AttachedFace].IsVertical) return BlockFacing.ALLFACES[AttachedFace].Opposite.FaceWhenRotatedBy((BlockFacing.ALLFACES[AttachedFace].Negative ? -90f : 90f) * GameMath.DEG2RAD, 0f, 0).FaceWhenRotatedBy(0f, RotRad, 0);
                else return BlockFacing.ALLFACES[AttachedFace].Opposite;
            }
        }

        protected BlockBehaviorGlassTrapdoor doorBh;

        public Cuboidf[] ColSelBoxes => opened ? boxesOpened : boxesClosed;
        public bool Opened => opened;

        public BEBehaviorGlassTrapdoor(BlockEntity blockentity) : base(blockentity)
        {
            boxesClosed = blockentity.Block.CollisionBoxes;

            doorBh = blockentity.Block.GetBehavior<BlockBehaviorGlassTrapdoor>();
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
                    string primaryTrack = opened ? ClosingAnimCode : OpeningAnimCode;
                    EnsureAnimatorTrackReady(primaryTrack);
                    UpdateStaticMeshAndRotation(opened);
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

            if (opened && animUtil != null && !UseAdvancedAnimation && !animUtil.activeAnimationsByAnimCode.ContainsKey(LegacyOpenedAnimCode))
            {
                ToggleDoorWing(true);
            }
        }

        protected void SetupRotationsAndColSelBoxes(bool initalSetup)
        {
            if (Api.Side == EnumAppSide.Client)
            {
                PrepareStaticAndAnimMeshes();
                if (UseAdvancedAnimation)
                {
                    UpdateStaticMeshAndRotation(opened);
                }
                else
                {
                    UpdateLegacyMeshAndAnimations();
                }
            }

            UpdateHitBoxes();
        }

        private void PrepareStaticAndAnimMeshes()
        {
            if (Api.Side != EnumAppSide.Client) return;
            if (doorBh == null) return;
            if (capi == null) capi = Api as ICoreClientAPI;

            string styleCode = Blockentity.Block?.Variant?["style"] ?? Block.Code.Path;
            string closedAnimKey = $"trapdoor-{styleCode}";
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

            Matrixf mat = getTfMatrix();
            currentStaticMesh.MatrixTransform(mat.Values);
            ApplyRendererTransform(mat);
        }

        protected virtual void UpdateLegacyMeshAndAnimations()
        {
            if (doorBh == null) return;
            MeshData sourceMesh = doorBh.closedAnimMesh ?? doorBh.animatableOrigMesh;
            if (sourceMesh == null) return;

            mesh = sourceMesh.Clone();
            Matrixf mat = getTfMatrix();
            mesh.MatrixTransform(mat.Values);
            ApplyRendererTransform(mat);
        }

        private Matrixf getTfMatrix(float rotz=0)
        {
            if (BlockFacing.ALLFACES[AttachedFace].IsVertical)
            {
                return new Matrixf()
                    .Translate(0.5f, 0.5f, 0.5f)
                    .RotateYDeg(RotDeg)
                    .RotateZDeg(BlockFacing.ALLFACES[AttachedFace].Negative ? 180 : 0)
                    .Translate(-0.5f, -0.5f, -0.5f)
                ;
            }

            int hai = BlockFacing.ALLFACES[AttachedFace].HorizontalAngleIndex;

            Matrixf mat = new Matrixf();
            mat
                .Translate(0.5f, 0.5f, 0.5f)
                .RotateYDeg(hai * 90)
                .RotateYDeg(90)
                .RotateZDeg(RotDeg)
                .Translate(-0.5f, -0.5f, -0.5f)
            ;
            return mat;
        }

        private void ApplyRendererTransform(Matrixf mat)
        {
            if (animUtil?.renderer == null) return;
            animUtil.renderer.CustomTransform = mat.Values;
        }

        protected virtual void UpdateHitBoxes()
        {
            Matrixf mat = getTfMatrix();

            boxesClosed = Blockentity.Block.CollisionBoxes;
            var boxes = new Cuboidf[boxesClosed.Length];
            for (int i = 0; i < boxesClosed.Length; i++)
            {
                boxes[i] = boxesClosed[i].TransformedCopy(mat.Values);
            }


            var boxesopened = new Cuboidf[boxesClosed.Length];
            for (int i = 0; i < boxesClosed.Length; i++)
            {
                boxesopened[i] = boxesClosed[i].RotatedCopy(90, 0, 0, new Vec3d(0.5, 0.5, 0.5)).TransformedCopy(mat.Values); 
            }

            this.boxesOpened = boxesopened;
            boxesClosed = boxes;
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

            AttachedFace = blockSel.Face.Index;

            var center = blockSel.Face.ToAB(blockSel.Face.PlaneCenter);
            var hitpos = blockSel.Face.ToAB(blockSel.HitPosition.ToVec3f());
            RotDeg = (int)Math.Round(GameMath.RAD2DEG * (float)Math.Atan2(center.A - hitpos.A, center.B - hitpos.B) / 90) * 90;

            if (blockSel.Face == BlockFacing.WEST || blockSel.Face == BlockFacing.SOUTH) RotDeg *= -1; // Why?

            SetupRotationsAndColSelBoxes(true);
        }

        public bool IsSideSolid(BlockFacing facing)
        {
            return (!opened && facing == facingWhenClosed) || (opened && facing == facingWhenOpened);
        }

        public bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
        {
            if (!doorBh.handopenable && byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
            {
                (Api as ICoreClientAPI).TriggerIngameError(this, "nothandopenable", Lang.Get("This door cannot be opened by hand."));
                return true;
            }

            ToggleDoorState(byPlayer, !opened);
            handling = EnumHandling.PreventDefault;
            return true;
        }

        public void ToggleDoorState(IPlayer byPlayer, bool opened)
        {
            this.opened = opened;
            ToggleDoorWing(opened);

            var be = Blockentity;
            float pitch = opened ? 1.1f : 0.9f;

            var bh = Blockentity.Block.GetBehavior<BlockBehaviorGlassTrapdoor>();
            var sound = opened ? bh?.OpenSound : bh?.CloseSound;

            Api.World.PlaySoundAt(sound, be.Pos.X + 0.5f, be.Pos.Y + 0.5f, be.Pos.Z + 0.5f, byPlayer, EnumSoundType.Sound, pitch);

            be.MarkDirty(true);

            if (Api.Side == EnumAppSide.Server)
            {
                Api.World.BlockAccessor.TriggerNeighbourBlockUpdate(Pos);
            }
        }

        private void ToggleDoorWing(bool opened)
        {
            this.opened = opened;

            if (!UseAdvancedAnimation)
            {
                if (animUtil == null)
                {
                    Blockentity.MarkDirty();
                    return;
                }

                if (!opened)
                {
                    animUtil.StopAnimation(LegacyOpenedAnimCode);
                }
                else
                {
                    float easingSpeed = Blockentity.Block.Attributes?["easingSpeed"].AsFloat(10) ?? 10;
                    animUtil.StartAnimation(new AnimationMetaData() { Animation = LegacyOpenedAnimCode, Code = LegacyOpenedAnimCode, EaseInSpeed = easingSpeed, EaseOutSpeed = easingSpeed });
                }
                Blockentity.MarkDirty();
                return;
            }

            string animCode = opened ? OpeningAnimCode : ClosingAnimCode;
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
            capi.Event.EnqueueMainThreadTask(callback, "GlassTrapdoorDeferred");
        }

        private bool ToggleDoorAnimation(string animCode)
        {
            if (!UseAdvancedAnimation) return false;
            EnsureAnimatorTrackReady(animCode);
            ApplyRendererTransform(getTfMatrix());
            return StartAnimationAndCallback(animCode);
        }

        private bool StartAnimationAndCallback(string animCode)
        {
            if (!UseAdvancedAnimation) return false;

            CompletePendingAnimationCallbacks(!renderCurrentMeshAnimation);

            float easingBase = Blockentity.Block.Attributes?["easingSpeed"].AsFloat(10) ?? 10;
            float easeInSpeed = Blockentity.Block.Attributes?["openingSpeed"].AsFloat(easingBase) ?? easingBase;
            float easeOutSpeed = Blockentity.Block.Attributes?["closingSpeed"].AsFloat(easingBase) ?? easingBase;
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
                UpdateStaticMeshAndRotation(opened);
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

            bool beforeOpened = opened;

            AttachedFace = tree.GetInt("attachedFace");
            RotDeg = tree.GetInt("rotDeg");
            opened = tree.GetBool("opened");

            if (opened != beforeOpened && animUtil != null)
            {
                ToggleDoorWing(opened);
            }
            if (Api != null && Api.Side is EnumAppSide.Client)
            {
                if (UseAdvancedAnimation)
                {
                    bool needPrep = doorBh?.closedStaticMesh == null || (advancedAnimationAvailable && doorBh?.openedStaticMesh == null);
                    if (needPrep)
                    {
                        PrepareStaticAndAnimMeshes();
                        string primaryTrack = opened ? ClosingAnimCode : OpeningAnimCode;
                        EnsureAnimatorTrackReady(primaryTrack);
                    }

                    if (!renderCurrentMeshAnimation)
                    {
                        UpdateStaticMeshAndRotation(opened);
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

            tree.SetInt("attachedFace", AttachedFace);
            tree.SetInt("rotDeg", RotDeg);
            tree.SetBool("opened", opened);
        }

        public void OnTransformed(IWorldAccessor worldAccessor, ITreeAttribute tree, int degreeRotation, Dictionary<int, AssetLocation> oldBlockIdMapping, Dictionary<int, AssetLocation> oldItemIdMapping, EnumAxis? flipAxis)
        {
            AttachedFace = tree.GetInt("attachedFace");
            var face = BlockFacing.ALLFACES[AttachedFace];
            if (face.IsVertical)
            {
                RotDeg = tree.GetInt("rotDeg");
                RotDeg = GameMath.Mod(RotDeg - degreeRotation, 360);
                tree.SetInt("rotDeg", RotDeg);
            }
            else
            {
                var rIndex = degreeRotation / 90;
                var horizontalAngleIndex = GameMath.Mod(face.HorizontalAngleIndex - rIndex, 4);
                var newFace = BlockFacing.HORIZONTALS_ANGLEORDER[horizontalAngleIndex];
                AttachedFace = newFace.Index;
                tree.SetInt("attachedFace", AttachedFace);
            }
        }
    }
}
