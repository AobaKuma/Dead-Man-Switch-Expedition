using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    // =========================================================================
    //  CompProperties_ArtilleryStrike
    // =========================================================================

    public class CompProperties_ArtilleryStrike : CompProperties
    {
        /// <summary>
        /// 離場投射物 ThingDef（thingClass 為 DMSE.Projectile_ArtilleryDeparture）。
        /// 發射後從建築位置沿弧線飛向地圖邊緣，抵達時建立世界地圖航跡物件。
        /// </summary>
        public ThingDef departureProjectileDef;

        /// <summary>世界地圖航跡物件 def（worldObjectClass 為 DMSE.WorldObject_ArtilleryStrike）。</summary>
        public WorldObjectDef worldObjectDef;

        /// <summary>
        /// 落地 Projectile ThingDef（後備；優先讀取 CompChangeableProjectile 已裝填的砲彈）。
        /// </summary>
        public ThingDef projectileDef;

        /// <summary>
        /// 每次開火後的裝填冷卻（tick）。預設 3600（60 秒）。
        /// 對應原版砲塔的 <see cref="BuildingProperties.turretBurstCooldownTime"/>。
        /// </summary>
        public int cooldownTicks = 3600;

        /// <summary>
        /// 每次開火前的瞄準時間（tick）。預設 240（4 秒，同原版迫擊砲 warmupTime=4）。
        /// 冷卻結束後開始計時；結束後才真正發射。
        /// </summary>
        public int warmupTicks = 240;

        /// <summary>以目標格為圓心的散佈半徑（格數）。0 = 完全精準。</summary>
        public float scatterRadius = 2f;

        /// <summary>齊射發數（每次開火生成的 WorldObject 數量）。</summary>
        public int salvoCount = 1;

        /// <summary>齊射各發間隔（tick）。</summary>
        public int salvoIntervalTicks = 60;

        /// <summary>是否需要同陣營的火控雷達佔用一個空閒通道方能開火。</summary>
        public bool requiresFireControlChannel = false;

        /// <summary>開火後佔用火力通道的時間（tick）。</summary>
        public int fireControlHoldTicks = 900;

        public CompProperties_ArtilleryStrike()
        {
            compClass = typeof(CompArtilleryStrike);
        }
    }

    // =========================================================================
    //  CompArtilleryStrike
    // =========================================================================

    /// <summary>
    /// 超視距火砲建築的核心元件，繼承 <see cref="CompBVRDevice"/> 以共用
    /// 電力 / 損壞 / 開關的可用性判定。
    ///
    /// 持續射擊工作流程：
    ///   1. 玩家點擊「開火」Gizmo → 切換至世界地圖，選擇目標 Tile。
    ///   2. 進入持續射擊狀態（targetTileId 設為選定 tile）。
    ///   3. 每個 tick 推進射擊狀態機：
    ///      a. 若在裝填冷卻中 → 等待；
    ///      b. 冷卻結束後開始瞄準計時（warmupTicks）；
    ///      c. 瞄準完畢 → 發射 Projectile_ArtilleryDeparture；
    ///      d. 設置冷卻計時 → 回到 (a) 循環。
    ///   4. 玩家點擊「停止」Gizmo 或裝備無法運作（無彈、無砲管）→ 結束持續射擊。
    /// </summary>
    public class CompArtilleryStrike : CompBVRDevice
    {
        public CompProperties_ArtilleryStrike Props =>
            (CompProperties_ArtilleryStrike)props;

        // ---- 冷卻 ----
        private int cooldownUntil;

        // ---- 持續射擊狀態 ----
        /// <summary>目標世界 tile ID；-1 = 閒置（未在持續射擊）。</summary>
        private int targetTileId = -1;

        /// <summary>
        /// 瞄準結束的 tick；-1 = 尚未開始瞄準（冷卻中或剛重置）。
        /// 冷卻結束後才開始計時；不隨存檔保留，載入後自動重新瞄準。
        /// </summary>
        private int warmupEndTick = -1;

        /// <summary>是否在持續射擊狀態。</summary>
        public bool IsSustainedFiring => targetTileId >= 0;

        // ---- 火力通道 ----
        private CompFireControlRadar reservedRadar;
        private Thing reservedRadarThing; // 序列化中繼
        private int channelReleaseTick = -1;

        // =====================================================================
        //  存檔
        // =====================================================================

        public override void PostExposeData()
        {
            base.PostExposeData();

            Scribe_Values.Look(ref cooldownUntil,      "artilleryCooldownUntil", 0);
            Scribe_Values.Look(ref targetTileId,        "artilleryTargetTileId",  -1);
            Scribe_Values.Look(ref channelReleaseTick,  "channelReleaseTick",     -1);
            // warmupEndTick 不序列化：載入後自動從冷卻結束時重新開始瞄準

            if (Scribe.mode == LoadSaveMode.Saving)
            {
                reservedRadarThing = reservedRadar?.parent;
            }
            Scribe_References.Look(ref reservedRadarThing, "reservedRadarThing");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                reservedRadar      = reservedRadarThing?.TryGetComp<CompFireControlRadar>();
                reservedRadarThing = null;
                warmupEndTick      = -1; // 載入後從冷卻結束再重新瞄準
            }
        }

        // =====================================================================
        //  Tick
        // =====================================================================

        public override void CompTick()
        {
            base.CompTick();

            // 火力通道釋放
            if (reservedRadar != null && Find.TickManager.TicksGame >= channelReleaseTick)
            {
                reservedRadar.ReleaseArtilleryChannel();
                reservedRadar      = null;
                channelReleaseTick = -1;
            }

            TickSustainedFire();
        }

        /// <summary>
        /// 持續射擊狀態機。每 tick 驅動：等待冷卻 → 瞄準 → 開火 → 等待冷卻 → …
        /// </summary>
        private void TickSustainedFire()
        {
            if (!IsSustainedFiring) { return; }

            int now = Find.TickManager.TicksGame;

            // 裝置失效（無電、損壞、開關關閉）→ 暫停，保留目標等恢復後繼續
            if (!Active)
            {
                warmupEndTick = -1;
                return;
            }

            // 裝填冷卻中 → 重置瞄準計時，等待冷卻結束
            if (cooldownUntil > now)
            {
                warmupEndTick = -1;
                return;
            }

            // 確認彈藥
            ThingDef projDef = ResolveProjectileDef();
            if (projDef == null)
            {
                Messages.Message("DMSE.Artillery.NoAmmo".Translate(),
                    parent, MessageTypeDefOf.NegativeEvent, historical: false);
                StopSustainedFire();
                return;
            }

            // 確認砲管耐久（CompRefuelable，fuelIsMortarBarrel=true）
            CompRefuelable barrel = parent.GetComp<CompRefuelable>();
            if (barrel != null && !barrel.HasFuel)
            {
                Messages.Message("DMSE.Artillery.NoBarrel".Translate(),
                    parent, MessageTypeDefOf.NegativeEvent, historical: false);
                StopSustainedFire();
                return;
            }

            // 火力通道確認（若需要）
            if (Props.requiresFireControlChannel && FindAvailableRadar() == null)
            {
                // 無可用通道時暫停瞄準，等通道釋放後自動恢復
                warmupEndTick = -1;
                return;
            }

            // 開始瞄準（冷卻剛結束時，warmupEndTick 為 -1）
            if (warmupEndTick < 0)
            {
                warmupEndTick = now + Props.warmupTicks;

                // 砲管轉向目標世界方位（瞄準開始時，讓玩家有 4 秒看到砲管旋轉）
                int srcTileId = parent.Map?.Tile.tileId ?? -1;
                if (srcTileId >= 0 && srcTileId != targetTileId)
                {
                    float angle = ArtilleryStrikeUtility.WorldDirectionAngle(srcTileId, targetTileId);
                    SetTurretRotation(angle);
                }
                return;
            }

            // 瞄準進行中
            if (now < warmupEndTick) { return; }

            // 瞄準完畢 → 開火
            warmupEndTick = -1;
            FireSingleShot(targetTileId);
        }

        // =====================================================================
        //  停止持續射擊
        // =====================================================================

        private void StopSustainedFire()
        {
            targetTileId  = -1;
            warmupEndTick = -1;
        }

        // =====================================================================
        //  Gizmo
        // =====================================================================

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (IsSustainedFiring)
            {
                // ---- 停止按鈕（含狀態說明）----
                yield return new Command_Action
                {
                    defaultLabel = "DMSE.Artillery.Stop".Translate(),
                    defaultDesc  = BuildStopDesc(),
                    icon         = TexCommand.ClearPrioritizedWork,
                    action       = StopSustainedFire,
                };
            }
            else
            {
                // ---- 開火按鈕（選世界目標）----
                Command_Action cmd = new Command_Action
                {
                    defaultLabel = "DMSE.Artillery.Fire".Translate(),
                    defaultDesc  = "DMSE.Artillery.FireDesc".Translate(),
                    icon         = CompLaunchable.LaunchCommandTex,
                    action       = BeginWorldTargeting,
                };

                if (!CanFireGizmo(out string reason))
                {
                    cmd.Disable(reason);
                }

                yield return cmd;
            }
        }

        private string BuildStopDesc()
        {
            int now = Find.TickManager.TicksGame;
            string stateStr;

            if (!Active)
            {
                stateStr = "DMSE.BVR.DeviceInactive".Translate();
            }
            else if (cooldownUntil > now)
            {
                stateStr = "DMSE.Artillery.StateCooldown".Translate(
                    (cooldownUntil - now).ToStringTicksToPeriod());
            }
            else if (warmupEndTick > now)
            {
                int pct = Mathf.RoundToInt(
                    (1f - (float)(warmupEndTick - now) / Mathf.Max(1, Props.warmupTicks)) * 100f);
                stateStr = "DMSE.Artillery.StateAiming".Translate(pct);
            }
            else
            {
                stateStr = "DMSE.Artillery.StateReady".Translate();
            }

            return "DMSE.Artillery.StopDesc".Translate(stateStr);
        }

        // =====================================================================
        //  開火前判定（Gizmo 用）
        //  注意：不檢查冷卻，允許在冷卻中選定目標並排程持續射擊。
        // =====================================================================

        private bool CanFireGizmo(out string reason)
        {
            if (!Active)
            {
                reason = "DMSE.BVR.DeviceInactive".Translate();
                return false;
            }
            if (Props.departureProjectileDef == null || Props.worldObjectDef == null)
            {
                reason = "DMSE.Artillery.NotConfigured".Translate();
                return false;
            }
            if (ResolveProjectileDef() == null)
            {
                reason = "DMSE.Artillery.NotConfigured".Translate();
                return false;
            }
            reason = null;
            return true;
        }

        // =====================================================================
        //  砲彈 def 解析（優先 CompChangeableProjectile）
        // =====================================================================

        private ThingDef ResolveProjectileDef()
        {
            CompChangeableProjectile loaded = parent.GetComp<CompChangeableProjectile>();
            if (loaded != null && loaded.Loaded)
            {
                return loaded.Projectile;
            }
            return Props.projectileDef;
        }

        // =====================================================================
        //  火力通道管理
        // =====================================================================

        private CompFireControlRadar FindAvailableRadar()
        {
            Map map = parent.MapHeld;
            MapComponent_BVRCombat mgr = map?.GetComponent<MapComponent_BVRCombat>();
            if (mgr == null) { return null; }

            CompFireControlRadar best = null;
            foreach (CompFireControlRadar fc in mgr.fireControlRadars)
            {
                if (fc.parent.Faction != parent.Faction) { continue; }
                if (!fc.Active || !fc.Props.supportsArtilleryGuidance) { continue; }
                if (fc.FreeArtilleryChannels <= 0) { continue; }
                if (best == null || fc.FreeArtilleryChannels > best.FreeArtilleryChannels)
                {
                    best = fc;
                }
            }
            return best;
        }

        // =====================================================================
        //  世界地圖目標選取
        // =====================================================================

        private void BeginWorldTargeting()
        {
            if (!CanFireGizmo(out _)) { return; }

            CameraJumper.TryJump(CameraJumper.GetWorldTarget(parent));

            Find.WorldTargeter.BeginTargeting(
                action:                    ChooseWorldTarget,
                canTargetTiles:            true,
                closeWorldTabWhenFinished: true
            );
        }

        private bool ChooseWorldTarget(GlobalTargetInfo target)
        {
            if (!target.IsValid || target.Tile < 0)
            {
                Messages.Message(
                    "MessageTransportPodsDestinationIsInvalid".Translate(),
                    MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }

            // 設定持續射擊目標；狀態機在下一個 tick 開始瞄準流程
            targetTileId  = target.Tile;
            warmupEndTick = -1;
            return true;
        }

        // =====================================================================
        //  邊緣格計算（球面切線框架，不依賴螢幕空間）
        // =====================================================================

        private IntVec3 CalcExitEdgeCell(int destTileId)
        {
            Map map       = parent.Map;
            int srcTileId = map.Tile.tileId;

            if (srcTileId == destTileId)
            {
                return new IntVec3(
                    Mathf.Clamp(parent.Position.x, 1, map.Size.x - 2), 0, map.Size.z - 1);
            }

            float angle = ArtilleryStrikeUtility.WorldDirectionAngle(srcTileId, destTileId);
            return ArtilleryStrikeUtility.EdgeCellFromAngle(
                map, angle, parent.Position.x, parent.Position.z);
        }

        // =====================================================================
        //  砲管旋轉（反射設定 Building_TurretGun.curRotation）
        // =====================================================================

        private static readonly System.Reflection.FieldInfo s_curRotationField =
            typeof(Building_TurretGun).GetField(
                "curRotation",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        /// <summary>
        /// 將砲管轉向指定方位角（0=North/+Z，90=East/+X）。
        /// 透過反射存取 <see cref="Building_TurretGun"/> 的私有 curRotation 欄位。
        /// </summary>
        private void SetTurretRotation(float angleDeg)
        {
            if (parent is Building_TurretGun turret)
            {
                s_curRotationField?.SetValue(turret, angleDeg);
            }
        }

        private static IntVec3 GetMapCenterCell(int tileId)
        {
            Map existing = Find.Maps.FirstOrDefault(m => m.Tile.tileId == tileId);
            if (existing != null)
            {
                return new IntVec3(existing.Size.x / 2, 0, existing.Size.z / 2);
            }
            return new IntVec3(125, 0, 125);
        }

        // =====================================================================
        //  單次開火（由狀態機呼叫，不再重複確認冷卻）
        // =====================================================================

        private void FireSingleShot(int destTileId)
        {
            ThingDef projDef = ResolveProjectileDef();
            if (projDef == null) { StopSustainedFire(); return; }

            // 消耗彈藥與砲管
            parent.GetComp<CompChangeableProjectile>()?.RemoveShell();
            parent.GetComp<CompRefuelable>()?.ConsumeFuel(1f);

            // 生成離場投射物，朝對應方位邊緣格飛去
            IntVec3 edgeCell = CalcExitEdgeCell(destTileId);

            Projectile_ArtilleryDeparture departure =
                (Projectile_ArtilleryDeparture)GenSpawn.Spawn(
                    Props.departureProjectileDef, parent.Position, parent.Map);

            departure.destTileId                  = destTileId;
            departure.artilleryTargetCell         = GetMapCenterCell(destTileId);
            departure.artilleryProjectileDef      = projDef;
            departure.artilleryAttackerFaction    = parent.Faction;
            departure.artilleryScatterRadius      = Props.scatterRadius;
            departure.artillerySalvoCount         = Props.salvoCount;
            departure.artillerySalvoIntervalTicks = Props.salvoIntervalTicks;
            departure.artilleryWorldObjectDef     = Props.worldObjectDef;

            departure.Launch(
                launcher:            parent,
                origin:              parent.Position.ToVector3Shifted(),
                usedTarget:          new LocalTargetInfo(edgeCell),
                intendedTarget:      new LocalTargetInfo(edgeCell),
                hitFlags:            ProjectileHitFlags.None,
                preventFriendlyFire: true,
                equipment:           null
            );

            // 裝填冷卻（下一發要等此 tick 數後才能再瞄準）
            cooldownUntil = Find.TickManager.TicksGame + Props.cooldownTicks;

            // 火力通道佔用
            if (Props.requiresFireControlChannel)
            {
                CompFireControlRadar radar = FindAvailableRadar();
                if (radar != null)
                {
                    radar.OccupyArtilleryChannel();
                    reservedRadar      = radar;
                    channelReleaseTick = Find.TickManager.TicksGame + Props.fireControlHoldTicks;
                }
            }
        }

        // =====================================================================
        //  建築拆除：釋放火力通道
        // =====================================================================

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            if (reservedRadar != null)
            {
                reservedRadar.ReleaseArtilleryChannel();
                reservedRadar      = null;
                channelReleaseTick = -1;
            }
        }
    }
}
