using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 可掛載於雷達／導彈發射建築上的通用「朝向目標」砲塔頭渲染 Comp。
    /// 本身不參與任何戰鬥判定，純粹視覺表現：每 tick 從 <see cref="MapComponent_BVRCombat"/>
    /// 讀取本陣營最緊急（tickToImpact 最早）的一筆目標，平滑轉向該目標所在方位；
    /// 無目標或裝置未運作時則回退為類似 <see cref="TurretTop"/> 的閒置左右擺動。
    ///
    /// 與具體火控邏輯（<see cref="CompFireControlRadar"/> / <see cref="CompMissileLauncher"/>）解耦：
    /// 只要 parent 建築位於防禦方陣營且地圖上存在 BVR 目標，就會轉向，因此同一顆 Comp
    /// 可同時安裝在雷達與發射架建築上重複使用。
    /// </summary>
    public class CompProperties_TurretFacingRenderer : CompProperties
    {
        /// <summary>砲塔頭貼圖路徑（單張，會依旋轉角度即時旋轉繪製）。</summary>
        public string texPath;

        /// <summary>砲塔頭繪製尺寸（格）。</summary>
        public Vector2 drawSize = new Vector2(1.5f, 1.5f);

        /// <summary>相對建築中心的本地位置偏移（不隨旋轉角度改變）。</summary>
        public Vector3 offset = Vector3.zero;

        /// <summary>額外高度偏移，用於微調疊繪層級（避免與主體貼圖 Z-fighting）。</summary>
        public float altitudeOffset = 0f;

        /// <summary>
        /// 每 tick 最大轉動角度（度）。&lt;= 0 表示瞬間轉向（不平滑）。
        /// </summary>
        public float rotationSpeedDegPerTick = 3f;

        /// <summary>無目標時是否啟用類 TurretTop 的閒置左右擺動。</summary>
        public bool idleSwayEnabled = true;

        /// <summary>閒置擺動：每 tick 轉動角度。</summary>
        public float idleTurnDegreesPerTick = 0.26f;

        /// <summary>閒置擺動：單次擺動持續 ticks。</summary>
        public int idleTurnDuration = 140;

        /// <summary>閒置擺動：下次擺動觸發前的最短等待 ticks。</summary>
        public int idleTurnIntervalMin = 150;

        /// <summary>閒置擺動：下次擺動觸發前的最長等待 ticks。</summary>
        public int idleTurnIntervalMax = 350;

        /// <summary>
        /// 是否僅在建築有效運作時（未斷電／未關閉／未損壞／未被擊暈；若可操作則需已有人操作）才轉向追蹤目標。
        /// 未運作時仍會顯示砲塔頭，但停止追蹤並停止閒置擺動（維持最後角度）。
        /// </summary>
        public bool requireActive = true;

        /// <summary>
        /// 選擇性功能：是否額外繪製「掛載中的彈藥」，隨砲塔頭一起旋轉（例如掛在旋轉發射架上的待射導彈）。
        /// 預設 false，沿用舊行為（不繪製彈藥），既有 Def 不需修改。
        /// 彈藥來源為 parent 身上的 <see cref="IThingHolder"/> 容器（與 <see cref="CompMissileLauncher_Interceptor"/>
        /// 讀取裝填彈藥的方式相同），因此本 Comp 不直接依賴任何具體發射邏輯，只要 parent 是彈藥容器
        /// （例如 <see cref="Building_MissileRack"/>）即可。依序取容器內每一枚 Thing，對應
        /// <see cref="ammoSlotOffsets"/>（未設定時退回單槽 <see cref="ammoOffset"/>）逐一疊繪，
        /// 藉此支援多彈頭（每槽各掛一枚，如雙聯裝／多聯裝發射架）。
        /// </summary>
        public bool drawLoadedAmmo = false;

        /// <summary>
        /// 單槽彈藥相對砲塔頭中心的本地位移（格）。會隨 curRotation 一起旋轉，
        /// 因此可用來把彈藥定位在砲管／發射軌前端。
        /// 僅在 <see cref="ammoSlotOffsets"/> 未設定（null 或空）時作為唯一槽位的位移使用。
        /// </summary>
        public Vector3 ammoOffset = Vector3.zero;

        /// <summary>
        /// 多槽彈藥位移列表（格），支援多彈頭渲染：容器內第 i 枚 Thing 對應第 i 個槽位。
        /// 每個槽位的座標皆為「相對砲塔頭中心的本地位移」，會隨 curRotation 一起旋轉
        /// （與 <see cref="ammoOffset"/> 相同語意，只是可以有多個）。
        /// 留空（null 或空）時退回單槽行為：僅使用 <see cref="ammoOffset"/> 一個槽位。
        /// 實際繪製數量為 Min(容器內彈藥數, 本列表槽位數)。
        /// </summary>
        public List<Vector3> ammoSlotOffsets;

        /// <summary>
        /// 彈藥繪製尺寸覆寫；為 null 時採用彈藥自身 Graphic 的原生 drawSize（1:1），
        /// 與 <see cref="CompMissileRackRenderer"/> 的 useNativeDrawSize 行為一致。
        /// </summary>
        public Vector2? ammoDrawSizeOverride = null;

        /// <summary>彈藥貼圖疊圖的額外高度偏移，避免與砲塔頭貼圖 Z-fighting。</summary>
        public float ammoAltitudeOffset = 0.005f;

        public CompProperties_TurretFacingRenderer()
        {
            compClass = typeof(CompTurretFacingRenderer);
        }
    }

    public class CompTurretFacingRenderer : ThingComp
    {
        public CompProperties_TurretFacingRenderer Props => (CompProperties_TurretFacingRenderer)props;

        private float curRotation;
        private bool rotationInitialized;

        private int ticksUntilIdleTurn;
        private int idleTurnTicksLeft;
        private bool idleTurnClockwise;

        private Graphic cachedGraphic;

        private Graphic HeadGraphic
        {
            get
            {
                if (cachedGraphic == null && !string.IsNullOrEmpty(Props.texPath))
                {
                    cachedGraphic = GraphicDatabase.Get<Graphic_Single>(
                        Props.texPath, ShaderDatabase.Cutout, Props.drawSize, Color.white);
                }
                return cachedGraphic;
            }
        }

        /// <summary>裝置是否處於「有效運作」狀態（斷電／關閉／損壞／被擊暈／未有人操作時視為 false）。</summary>
        public bool DeviceActive
        {
            get
            {
                if (parent == null || !parent.Spawned || parent.Destroyed) { return false; }

                CompBreakdownable breakdownable = parent.GetComp<CompBreakdownable>();
                if (breakdownable != null && breakdownable.BrokenDown) { return false; }

                CompFlickable flickable = parent.GetComp<CompFlickable>();
                if (flickable != null && !flickable.SwitchIsOn) { return false; }

                CompPowerTrader power = parent.GetComp<CompPowerTrader>();
                if (power != null && !power.PowerOn) { return false; }

                CompStunnable stunnable = parent.GetComp<CompStunnable>();
                if (stunnable != null && stunnable.StunHandler != null && stunnable.StunHandler.Stunned) { return false; }

                CompMannable mannable = parent.GetComp<CompMannable>();
                if (mannable != null && !mannable.MannedNow) { return false; }

                return true;
            }
        }

        /// <summary>
        /// 從地圖的 BVR 管理器中，挑選本陣營最緊急（tickToImpact 最早）波次裡，
        /// 距離 parent 最近的一筆目標位置，作為砲塔頭朝向的依據。
        /// </summary>
        private IntVec3? FindFacingTargetCell()
        {
            Map map = parent?.MapHeld;
            if (map == null) { return null; }
            MapComponent_BVRCombat manager = map.GetComponent<MapComponent_BVRCombat>();
            if (manager == null) { return null; }

            BVRWave bestWave = null;
            foreach (BVRWave wave in manager.Waves)
            {
                if (wave.defenderFaction != parent.Faction) { continue; }
                if (wave.targets == null || wave.targets.Count == 0) { continue; }
                if (bestWave == null || wave.tickToImpact < bestWave.tickToImpact)
                {
                    bestWave = wave;
                }
            }
            if (bestWave == null) { return null; }

            IntVec3? best = null;
            float bestDistSq = float.MaxValue;
            foreach (BVRTarget target in bestWave.targets)
            {
                if (target.map != map) { continue; }
                float distSq = (target.position - parent.Position).LengthHorizontalSquared;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = target.position;
                }
            }
            return best;
        }

        public override void CompTick()
        {
            base.CompTick();
            if (parent == null || !parent.Spawned) { return; }

            if (!rotationInitialized)
            {
                curRotation = parent.Rotation.AsAngle;
                rotationInitialized = true;
            }

            if (Props.requireActive && !DeviceActive)
            {
                // 未運作：停在最後角度，不追蹤也不閒置擺動。
                return;
            }

            IntVec3? targetCell = FindFacingTargetCell();
            if (targetCell.HasValue)
            {
                Vector3 toTarget = targetCell.Value.ToVector3Shifted() - parent.DrawPos;
                float desired = toTarget.AngleFlat();
                RotateTowards(desired);

                // 有目標時重置閒置計時，離開閒置狀態。
                ticksUntilIdleTurn = Rand.RangeInclusive(Props.idleTurnIntervalMin, Props.idleTurnIntervalMax);
                idleTurnTicksLeft = 0;
                return;
            }

            if (Props.idleSwayEnabled)
            {
                IdleTurnTick();
            }
        }

        /// <summary>依 rotationSpeedDegPerTick 平滑轉向；&lt;= 0 時瞬間轉向。</summary>
        private void RotateTowards(float desiredAngle)
        {
            if (Props.rotationSpeedDegPerTick <= 0f)
            {
                curRotation = desiredAngle;
                return;
            }

            float delta = Mathf.DeltaAngle(curRotation, desiredAngle);
            float step = Mathf.Clamp(delta, -Props.rotationSpeedDegPerTick, Props.rotationSpeedDegPerTick);
            curRotation = Mathf.Repeat(curRotation + step, 360f);
        }

        /// <summary>沿用 <see cref="TurretTop"/> 的左右來回擺動邏輯，用於無目標時的待機動畫。</summary>
        private void IdleTurnTick()
        {
            if (ticksUntilIdleTurn > 0)
            {
                ticksUntilIdleTurn--;
                if (ticksUntilIdleTurn == 0)
                {
                    idleTurnClockwise = Rand.Value < 0.5f;
                    idleTurnTicksLeft = Props.idleTurnDuration;
                }
                return;
            }

            if (idleTurnTicksLeft <= 0)
            {
                ticksUntilIdleTurn = Rand.RangeInclusive(Props.idleTurnIntervalMin, Props.idleTurnIntervalMax);
                return;
            }

            curRotation = Mathf.Repeat(
                curRotation + (idleTurnClockwise ? Props.idleTurnDegreesPerTick : -Props.idleTurnDegreesPerTick),
                360f);
            idleTurnTicksLeft--;
        }

        public override void PostDraw()
        {
            base.PostDraw();

            Graphic graphic = HeadGraphic;
            if (graphic == null || parent == null) { return; }

            Vector3 pos = parent.DrawPos + Props.offset;
            pos.y = AltitudeLayer.BuildingOnTop.AltitudeFor() + Props.altitudeOffset;

            Quaternion quat = Quaternion.Euler(0f, curRotation, 0f);
            Vector3 scale = new Vector3(Props.drawSize.x, 1f, Props.drawSize.y);

            Graphics.DrawMesh(
                MeshPool.plane10,
                Matrix4x4.TRS(pos, quat, scale),
                graphic.MatSingle,
                0);

            if (Props.drawLoadedAmmo)
            {
                DrawLoadedAmmo();
            }
        }

        private List<Vector3> singleAmmoSlotCache;

        /// <summary>
        /// 有效彈藥槽位列表：優先採用 <see cref="CompProperties_TurretFacingRenderer.ammoSlotOffsets"/>；
        /// 未設定時退回僅含 <see cref="CompProperties_TurretFacingRenderer.ammoOffset"/> 一個元素的單槽列表
        /// （向後相容舊行為）。
        /// </summary>
        private List<Vector3> AmmoSlots
        {
            get
            {
                if (Props.ammoSlotOffsets != null && Props.ammoSlotOffsets.Count > 0)
                {
                    return Props.ammoSlotOffsets;
                }
                return singleAmmoSlotCache ?? (singleAmmoSlotCache = new List<Vector3> { Props.ammoOffset });
            }
        }

        /// <summary>
        /// 選擇性繪製：依序取出 parent 的 <see cref="IThingHolder"/> 容器內的彈藥，逐一對應
        /// <see cref="AmmoSlots"/> 疊繪於砲塔頭上，位置隨 curRotation 一起旋轉
        /// （掛在旋轉發射架上的視覺效果）。支援多彈頭：容器第 i 枚對應第 i 個槽位，
        /// 實際繪製數量為 Min(容器彈藥數, 槽位數)。無容器或無彈藥時什麼都不畫。
        /// </summary>
        private void DrawLoadedAmmo()
        {
            ThingOwner owner = (parent as IThingHolder)?.GetDirectlyHeldThings();
            if (owner == null || owner.Count == 0) { return; }

            List<Vector3> slots = AmmoSlots;
            int count = Mathf.Min(owner.Count, slots.Count);
            if (count <= 0) { return; }

            Quaternion headQuat = Quaternion.Euler(0f, curRotation, 0f);
            float y = AltitudeLayer.BuildingOnTop.AltitudeFor() + Props.altitudeOffset + Props.ammoAltitudeOffset;

            for (int i = 0; i < count; i++)
            {
                Thing ammo = owner[i];
                Graphic graphic = ammo?.Graphic;
                if (graphic == null) { continue; }

                Vector3 pos = parent.DrawPos + Props.offset + (headQuat * slots[i]);
                pos.y = y;

                Vector2 size = Props.ammoDrawSizeOverride ?? graphic.drawSize;
                Vector3 scale = new Vector3(size.x, 1f, size.y);

                // 掛在旋轉發射架上時彈藥沒有「固定朝向」可言：一律以北面貼圖為基準角度（0 度），
                // 再依 curRotation 旋轉貼圖本身來表示實際朝向。
                //
                // 不可沿用 CompMissileRackRenderer 的 ShouldDrawRotated 判斷：那是給「建築依 Rot4
                // 四向切換貼圖、不旋轉」的情境設計，對 Graphic_Multi（如 DMSE_Missile_Termite）
                // 而言 ShouldDrawRotated 常為 false，若照搬會讓貼圖固定不轉。
                //
                // graphic.MatNorth 對 Graphic_Single 會退回 MatSingle（同一張貼圖，行為不變）；
                // 對 Graphic_Multi 則正確取得 _north 貼圖本身（MatSingle 在 Graphic_Multi 上其實是
                // MatSouth，方向會對不上）。DrawRotatedExtraAngleOffset 用於補償「north 貼圖缺失、
                // 由其他方向貼圖代打」時的角度差，一併沿用。
                Quaternion quat = Quaternion.Euler(0f, curRotation + graphic.DrawRotatedExtraAngleOffset, 0f);

                Graphics.DrawMesh(
                    MeshPool.plane10,
                    Matrix4x4.TRS(pos, quat, scale),
                    graphic.MatNorth,
                    0);
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref curRotation, "turretFacingCurRotation", 0f);
            Scribe_Values.Look(ref rotationInitialized, "turretFacingRotationInitialized", false);
        }
    }
}
