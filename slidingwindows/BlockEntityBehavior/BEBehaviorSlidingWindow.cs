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

        protected bool isOpen;
        protected bool invertHandles;
    
        protected MeshData currentStaticMesh;
        long scheduledCallbackId = -1;
        Action scheduledCallback = null;
        bool skipStaticTesselation = false;
        enum CurrentStaticMeshType { Closed, Opened }
        CurrentStaticMeshType currentStaticMeshType;

        // Open/closed collision + selection boxes for this controller.
        // For sliding windows we treat Block.CollisionBoxes (from the shape file)
        // as [frame, sashClosed, sashOpen] and build two explicit box sets
        // instead of rotating shapes like vanilla does, because our shapes are more complex.
        protected Cuboidf[] boxesClosed, boxesOpened;

        public BlockFacing windowFacing { get { return BlockFacing.HorizontalFromYaw(RotateYRad); } }

        public BlockBehaviorSlidingWindow windowBh;

        public Cuboidf[] ColSelBoxes => isOpen ? boxesOpened : boxesClosed;
        public bool Opened => isOpen;
        public bool InvertHandles => invertHandles;
        ICoreClientAPI capi;

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
            capi = api as ICoreClientAPI;   // will be null on server side

            SetupMeshAndBoxes(false);
            Api.World.BlockAccessor.MarkBlockDirty(Pos);

            if (Api.Side == EnumAppSide.Client)
            {
                PrepareStaticAndAnimMeshes(); // or 
                UpdateStaticMeshAndRotation(isOpen);
                InitializeAnimatorTracks("opening");
                InitializeAnimatorTracks("closing");
                if (animUtil?.renderer != null)
                {
                    animUtil.renderer.ShouldRender = false;
                }

                Api.World.BlockAccessor.MarkBlockDirty(Pos);
            }
            UpdateHitBoxes();
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
                        if (Api.Side == EnumAppSide.Server)
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
                // lonely unpaired window
                LeftWindow = null;
                RightWindow = null;
                invertHandles = false;
                Blockentity.MarkDirty(true);
            }
            UpdateHitBoxes();
        }
        private void PrepareStaticAndAnimMeshes()
        {
            if (Api.Side != EnumAppSide.Client) return;
            if (windowBh == null) return;
            
            // register default mesh as closed shape with animation
            string closedAnimKey = Block.Shape.ToString();
            if (windowBh.closedDictKey == null) windowBh.closedDictKey = closedAnimKey;

            // Prefer the shape returned by CreateMesh when animUtil is available
            if (animUtil != null && windowBh.closedAnimMesh == null)
            {
                windowBh.closedAnimMesh = animUtil.CreateMesh(closedAnimKey, null, out Shape animShape, null);
                if (windowBh.closedShape == null) windowBh.closedShape = animShape;
            }

            // If we still don't have a shape (e.g. animUtil null on first run), load it directly
            if (windowBh.closedShape == null && Block.Shape != null)
            {
                windowBh.closedShape = Shape.TryGet(Api, Block.Shape.Base);
            }

            // if (windowBh.closedShape == null && Block.Shape != null) windowBh.closedShape = Shape.TryGet(Api, Block.Shape.Base);
            // if (animUtil != null && windowBh.closedAnimMesh == null) windowBh.closedAnimMesh = animUtil.CreateMesh(closedAnimKey, null, out Shape _, null);
            if (windowBh.closedStaticMesh == null && windowBh.closedShape != null) 
            {
                MeshData closedStatic;
                capi.Tesselator.TesselateShape(Block, windowBh.closedShape, out closedStatic, null);
                windowBh.closedStaticMesh = closedStatic;
            }

            if (Block.Attributes?["openedShape"].Exists == true)
            {
                var openedShapeCode = Block.Attributes["openedShape"].AsString(null);
                var openedShapeLoc  = AssetLocation.Create(openedShapeCode);
                var openedAnimKey = closedAnimKey + "-alt";
                
                if (windowBh.openedDictKey == null) windowBh.openedDictKey = openedAnimKey;

                // Ensure openedShape
                if (windowBh.openedShape == null) windowBh.openedShape = Shape.TryGet(Api, openedShapeLoc);

                // Again, prefer the shape returned by CreateMesh when possible
                if (animUtil != null && windowBh.openedAnimMesh == null && windowBh.openedShape != null)
                {
                    windowBh.openedAnimMesh = animUtil.CreateMesh(openedAnimKey, windowBh.openedShape, out Shape animShape, null);
                    windowBh.openedShape = animShape;  // keep the animator’s shape as the canonical one
                }

                if (windowBh.openedStaticMesh == null && windowBh.openedShape != null)
                {
                    MeshData openedStatic;
                    capi.Tesselator.TesselateShape(Block, windowBh.openedShape, out openedStatic, null);
                    windowBh.openedStaticMesh = openedStatic;
                }
            }
        
        }
        private void InitializeAnimatorTracks(string animCode)
        {
            if (Api.Side != EnumAppSide.Client || animUtil == null) return;

            PrepareStaticAndAnimMeshes();

            MeshData animMesh;
            Shape animShape;
            string animKey;

            if (animCode == "opening")
            {
                animMesh  = windowBh.closedAnimMesh;
                animShape = windowBh.closedShape;
                animKey   = windowBh.closedDictKey;
            }
            else if (animCode == "closing")
            {
                animMesh  = windowBh.openedAnimMesh ?? windowBh.closedAnimMesh;
                animShape = windowBh.openedShape    ?? windowBh.closedShape;
                animKey   = windowBh.openedDictKey  ?? windowBh.closedDictKey;
            }
            else
            {
                return;
            }

            if (animMesh == null || animShape == null) return;
            Api.Logger.Debug($">>> Animation meshes prepared, now calling animUtil.InitializeAnimator({animKey}) ");
            animUtil.InitializeAnimator(animKey, animMesh, animShape, null);
        }


        private void InitializeAnimatorForOpenState(string animCode)
        {
            if (Api.Side != EnumAppSide.Client || animUtil == null) return;
        
            InitializeAnimatorTracks(animCode);

            // UpdateStaticMeshAndRotation(isOpen);
            // Api.World.BlockAccessor.MarkBlockDirty(Pos);
            // Blockentity.MarkDirty();

        }


        protected virtual void UpdateStaticMeshAndRotation(bool useOpened = false)
        {
            if (Api.Side != EnumAppSide.Client) return;
            MeshData baseStaticMesh;

            if (useOpened && windowBh.openedStaticMesh  != null)
            {
                baseStaticMesh = windowBh.openedStaticMesh ;
                currentStaticMeshType = CurrentStaticMeshType.Opened;
                Api.Logger.Debug(">>> UpdateStaticMeshAndRotation() set baseStaticMesh to windowBh.openedStaticMesh");
            }
            else
            {
                baseStaticMesh = windowBh.closedStaticMesh ?? windowBh.closedAnimMesh; // fallback
                currentStaticMeshType = CurrentStaticMeshType.Closed;

                Api.Logger.Debug(">>> UpdateStaticMeshAndRotation() set baseStaticMesh to windowBh.closedStaticMesh");
            }

            // Clone so we can mutate safely for this block instance
            currentStaticMesh = baseStaticMesh?.Clone();

            if (currentStaticMesh == null)
            {
                Api.Logger.Debug(">>> UpdateStaticMeshAndRotation() no baseStaticMesh available, skipping static orientation");
                return;
            }

            Api.Logger.Debug($">>> Inside UpdateStaticMeshAndRotation(useOpened = {useOpened}), windowBh.currentStaticMesh is a clone of {currentStaticMeshType}");

            float rot = RotateYRad;

            if (invertHandles) rot = -rot;

            if (rot != 0f)
            {
                currentStaticMesh = currentStaticMesh.Rotate(new Vec3f(0.5f, 0.5f, 0.5f), 0, rot, 0);
            }

            if (invertHandles)
            {
                Matrixf matf = new Matrixf();
                matf.Translate(0.5f, 0.5f, 0.5f)
                    .Scale(-1f, 1f, 1f)
                    .Translate(-0.5f, -0.5f, -0.5f);
                currentStaticMesh.MatrixTransform(matf.Values);
            }

            // Orient renderer to match (so anim meshes line up visually)
            if (animUtil?.renderer != null)
            {
                animUtil.renderer.rotationDeg.Y = rot * GameMath.RAD2DEG;
                animUtil.renderer.ScaleX = invertHandles ? -1f : 1f;
                animUtil.renderer.backfaceCulling = !invertHandles;
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
            return !isOpen && facing == windowFacing;
        }

        public bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref EnumHandling handling)
        {
            if (!windowBh.handopenable && byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
            {
                capi?.TriggerIngameError(this, "nothandopenable", Lang.Get("This window cannot be opened by hand."));
                return true;
            }
            Api.Logger.Debug($">>>>>> Begin interaction; OnBlockInteractStart will now call ToggleSashState() with !isOpen = {!isOpen}");

            ToggleSashState(byPlayer, !isOpen);
            handling = EnumHandling.PreventDefault;
            return true;
        }

        internal void ToggleWindowSashFromPartner(IPlayer byPlayer, bool isOpen)
        {
            // Same behavior as a normal toggle, but mark it as coming from a neighbor
            ToggleSashState(byPlayer, isOpen, true);
        }


        public void ToggleSashState(IPlayer byPlayer, bool toOpenState, bool fromPartner = false)
        {
            Api.Logger.Debug($">>> ToggleSashState called with toOpenState = {toOpenState} while isOpen = {isOpen}");
            // If we're already in that state, bail
            // if you want to be explicit about the 'opening' animation: (animUtil.activeAnimationsByAnimCode.ContainsKey("opening") == isOpen)
            if (isOpen == toOpenState)  return;
            string animCode = toOpenState ? "opening" : "closing";

            bool wasOpen = isOpen;
            isOpen = toOpenState;

            Api.Logger.Debug($">>> Just set isOpen to {isOpen} instead of {wasOpen} will play animation {animCode}.");

            // Sync state to paired neighbors, but only from the "source" leaf so no recursion
            if (!fromPartner && Api.Side == EnumAppSide.Server)
            {
                if (LeftWindow != null && invertHandles)
                {
                    LeftWindow.ToggleSashState(byPlayer, isOpen, true);
                }
                if (RightWindow != null)
                {
                    RightWindow.ToggleSashState(byPlayer, isOpen, true);
                }
                Blockentity.MarkDirty(true);
            }

            // ---- Sounds (always per-leaf) ----
            float pitch = isOpen ? 0.8f : 0.7f;
            var sound = isOpen ? windowBh?.OpenSound : windowBh?.CloseSound;

            // Secondary sounds from attributes
            var customSoundKey = Block.Attributes?["secondarySounds"]?[isOpen ? "open" : "close"]?.AsString(null);
            if (customSoundKey != null)
            {
                var customSound = new AssetLocation(customSoundKey);
                float customSoundPitch = isOpen ? 0.8f : 1f;

                Api.World.PlaySoundAt(
                    customSound,
                    Pos.X + 0.5f,
                    Pos.InternalY + 0.5f,
                    Pos.Z + 0.5f,
                    byPlayer,
                    EnumSoundType.Sound,
                    customSoundPitch,
                    32f,
                    2f
                );
            }

            if (sound != null)
            {
                Api.World.PlaySoundAt(
                    sound,
                    Pos.X + 0.5f,
                    Pos.InternalY + 0.5f,
                    Pos.Z + 0.5f,
                    byPlayer,
                    EnumSoundType.Sound,
                    pitch,
                    32f,
                    1f
                );
            }
            if (Api.Side == EnumAppSide.Client)
            {
                Api.Logger.Debug($">>> ToggleSashState(toOpenState={toOpenState}) will call ToggleSashAnimation({animCode}), UpdateStaticMeshAndRotation({isOpen}) and then mark dirty");
                ToggleSashAnimation(animCode);  // movement/animation only
                UpdateStaticMeshAndRotation(isOpen);
                Api.World.BlockAccessor.MarkBlockDirty(Pos);
                // Blockentity.MarkDirty();
            }
        }

        public void RunAfterMs(int delayMs, Action callback)
        {
            // Only makes sense on client
            if (capi == null) return;

            // Optional: cancel any previous scheduled callback
            if (scheduledCallbackId != -1)
            {
                capi.Event.UnregisterCallback(scheduledCallbackId);
                scheduledCallbackId = -1;
            }

            scheduledCallback = callback;

            scheduledCallbackId = capi.Event.RegisterCallback(dt =>
            {
                var cb = scheduledCallback;
                scheduledCallback = null;
                scheduledCallbackId = -1;
                cb?.Invoke();
            }, delayMs);
        }
        void StartAnimationAndCallback(string animCode)
        {
            if (Api.Side != EnumAppSide.Client || animUtil == null) return;
            Api.Logger.Debug($">>> In StartAnimationAndCallback('{animCode}') ");

            float easeInSpeed = Block.Attributes?["openingSpeed"].AsFloat(10) ?? 10;
            float easeOutSpeed = Block.Attributes?["closingSpeed"].AsFloat(10) ?? 10;
            var meta = new AnimationMetaData
            {
                Animation = animCode,
                Code = animCode,
                EaseInSpeed = easeInSpeed,
                EaseOutSpeed = easeOutSpeed,
                AnimationSpeed = 1f 
            };

            float easeSpeed = animCode == "opening" ? easeInSpeed : easeOutSpeed;
            double baseDurationMs = 1000.0 / easeSpeed; 

            // enable the renderer for the next draw
            if (animUtil.renderer != null) animUtil.renderer.ShouldRender = true;
            Api.Logger.Debug($">>> ShouldRender is true, now starting animation!");
            animUtil.StartAnimation(meta);
            UpdateStaticMeshAndRotation(isOpen);
            // disable the tesselator for the next draw
            skipStaticTesselation = true;
            Api.World.BlockAccessor.MarkBlockDirty(Pos);

            // this timeout does not make sense
            RunAfterMs((int)(baseDurationMs * 5), () =>
            {
                Api.Logger.Debug($">>>>> Callback for Animation '{animCode}' ran after {baseDurationMs * 5}ms, will set ShouldRender to false, skipStaticTesselation to false (again) and mark dirty on client");

                // enable the tesselator for the next draw
                skipStaticTesselation = false;
                UpdateStaticMeshAndRotation(isOpen);
                
                // disable the renderer for the next draw
                if (animUtil?.renderer != null) animUtil.renderer.ShouldRender = false;

                Api.World.BlockAccessor.MarkBlockDirty(Pos);

                // Pre-initialize the opposite direction for next time
                string nextAnimCode = animCode == "closing" ? "opening" : "closing";
                Api.Logger.Debug($"Prewarming animation for {nextAnimCode}");
                InitializeAnimatorTracks(nextAnimCode);
                // return to base state if changed
                if (animUtil?.renderer != null) animUtil.renderer.ShouldRender = false;

                // Api.Logger.Debug($">>> Expect OnTesselate to run soon, will update renderer's ShouldRender to false and swap to static tesselation for currentStaticMesh: {currentStaticMeshType}");
            });

        }
        private void ToggleSashAnimation(string animCode)
        {

            // Server is not invited to the animation party
            if (Api.Side != EnumAppSide.Client || animUtil == null) return;

            Api.Logger.Debug($">>> Just called ToggleSashAnimation('{animCode}') while isOpen = {isOpen}, will initalize animators and start animation");

            // Choose the correct anim mesh (ClosedAnim vs OpenAnim, once you’ve wired those up)
            // InitializeAnimatorForOpenState(animCode);

            // Actually play the right clip
            StartAnimationAndCallback(animCode);
        }

        // marking the block dirty will trigger OnTesselation, eventually. No contract for frame or time frame, may be batched
        public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
        {
            if (Api.Side != EnumAppSide.Client) return true; // shhh server you're not invited to the tesselation party

            // unreliableIsAnimating comes from BehaviorAnimatable OnTessselation
            // the logic is not great; returns true if an animation is registered, using animUtil.activeAnimationsByAnimCode.Count
            // returns false if no animation is currently playing, using animUtil.animator.ActiveAnimationCount 
            // however, it is possible to have an animation registered but paused, which will return true (is animating)
            // even while the mesh itself isn't moving anymore, because that's still an "active" animation, just not "Running"

            // Api.Logger.Debug($">>> In OnTesselation, isOpen = {isOpen}, currentStaticMeshType = {currentStaticMeshType}");

            bool unreliableIsAnimating = base.OnTesselation(mesher, tessThreadTesselator);
            if (animUtil != null && animUtil.renderer != null)
            {
                Api.Logger.Debug($">>> In OnTesselation, AnimUtil believes it {(unreliableIsAnimating ? "IS" : "IS NOT")} currently animating, while ShouldRender = {animUtil.renderer.ShouldRender}");
                foreach (string animCode in animUtil.activeAnimationsByAnimCode.Keys) Api.Logger.Debug($">>> In OnTesselation, AnimCode '{animCode}' is active");
            }
            // check whether timeout has finished
            // skipStaticTesselation should be false if animation is expected to be done by now
            // ShouldRender and skipStaticTesselation should be in sync before mesher.AddMeshData() is called
            // animUtil doesn't exist on load

            
            if (animUtil?.renderer != null && skipStaticTesselation == true)
            {
                animUtil.renderer.ShouldRender = true;
                Api.Logger.Debug($">>> In OnTesselation, just set ShouldRender={animUtil.renderer.ShouldRender} (even if unnecessary)");
            }

            if (!skipStaticTesselation && currentStaticMesh != null && currentStaticMesh.VerticesCount > 0 )
            {
                // if (animUtil != null && animUtil?.renderer != null) animUtil.renderer.ShouldRender = false;  Api.Logger.Debug($">>> In OnTesselation, just set ShouldRender={animUtil.renderer.ShouldRender} to match skipStaticTesselation = false");
                
                // bake this custom model into the chunk mesh as static geometry
                Api.Logger.Debug($">>> In OnTesselation, Adding currentStaticMesh ({currentStaticMeshType}) to mesherPool for static tesselation this render");
                mesher.AddMeshData(currentStaticMesh);
            } else
            {
                Api.Logger.Debug($">>> In OnTesselation, NOT adding currentStaticMesh ({currentStaticMeshType}) to mesherPool for this render, no static tesselation");
            }

            // return true to skip engine's default mesh tesselation; false will also add the default model to the mesher
            return true;
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
        {
            base.FromTreeAttributes(tree, worldAccessForResolve);

            bool beforeOpened = isOpen;

            RotateYRad = tree.GetFloat("rotateYRad");
            isOpen = tree.GetBool("isOpen");
            invertHandles = tree.GetBool("invertHandles");
            leftWindowOffset = tree.GetVec3i("leftWindowPos");
            rightWindowOffset = tree.GetVec3i("rightWindowPos");


            if (Api != null && Api.Side is EnumAppSide.Client)
            {
                // Api.Logger.Debug($">>> FromTreeAttributes will invoke starting methods (isOpen = {isOpen})");
                // PrepareStaticAndAnimMeshes();
                // // what order to call this?
                // if (animUtil?.renderer != null)
                // {
                //     animUtil.renderer.rotationDeg.Y = RotateYRad * GameMath.RAD2DEG;
                // }
                // UpdateStaticMeshAndRotation(isOpen);
                // InitializeAnimatorTracks("opening");
                // InitializeAnimatorTracks("closing");

                // UpdateHitBoxes();
                // Api.World.BlockAccessor.MarkBlockDirty(Pos);
            }
        }

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetFloat("rotateYRad", RotateYRad);
            tree.SetBool("isOpen", isOpen);
            Api.Logger.Debug($">>> toTreeAttributes just Setbool('isOpen', {isOpen})");
            tree.SetBool("invertHandles", invertHandles);
            if (leftWindowOffset != null) tree.SetVec3i("leftWindowPos", leftWindowOffset);
            if (rightWindowOffset != null) tree.SetVec3i("rightWindowPos", rightWindowOffset);
        }

        public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
        {
            if (capi != null && capi.Settings.Bool["extendedDebugInfo"] == true)
            {
                dsc.AppendLine("" + windowFacing + " " + (isOpen ? "open" : "closed"));
                dsc.AppendLine("" + windowBh.height + "x" + windowBh.width);
                EnumHandling h = EnumHandling.PassThrough;
                if (windowBh.GetLiquidBarrierHeightOnSide(BlockFacing.NORTH, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: North");
                if (windowBh.GetLiquidBarrierHeightOnSide(BlockFacing.EAST, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: East");
                if (windowBh.GetLiquidBarrierHeightOnSide(BlockFacing.SOUTH, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: South");
                if (windowBh.GetLiquidBarrierHeightOnSide(BlockFacing.WEST, Pos, ref h) > 0) dsc.AppendLine("Barrier to liquid on side: West");
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
