using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 單一朝向的渲染覆寫。可單獨指定該朝向（Rot4）下導彈的座標與旋轉，
    /// 兩者彼此獨立、亦與其他朝向獨立，因此不同方向可分別微調。
    /// </summary>
    public class MissileRackDirectional
    {
        /// <summary>
        /// 此朝向下各槽位相對建築中心的偏移（單位：格）。
        /// 與北向自動旋轉不同：此處的座標「按原值」使用，不再乘上 parent.Rotation，
        /// 因此可針對該方向獨立擺放。留空則回退使用共用 slotOffsets（自動旋轉）。
        /// </summary>
        public List<Vector3> slotOffsets = new List<Vector3>();

        /// <summary>
        /// 此朝向下導彈貼圖的旋轉角度（度，繞 Y 軸）。
        /// 留空（null）則回退使用建築朝向（單張貼圖時旋轉、方向貼圖時不旋轉）。
        /// 設定後即與座標獨立、可單獨調整，且一律優先於自動旋轉。
        /// </summary>
        public float? rotation;
    }

    public class CompProperties_MissileRackRenderer : CompProperties
    {
        /// <summary>
        /// 最大渲染數量（顯示幾枚）。僅控制「渲染」，與容器容量分離：
        /// 容量請改由 <see cref="MissileRackExtension"/>.capacity 指定；未指定時
        /// <see cref="Building_MissileRack"/> 會向後相容地以本值作為容量。
        /// </summary>
        public int maxStored = 4;

        /// <summary>
        /// 是否以導彈自身 Graphic 的原生 drawSize（即導彈貼圖原本的尺寸）做 1:1 渲染。
        /// 預設 true：每枚導彈以其 ThingDef graphicData.drawSize 等比繪製，不再被壓縮變形。
        /// 若為 false，則回退使用下方 <see cref="missileDrawSize"/> 的固定尺寸。
        /// </summary>
        public bool useNativeDrawSize = true;

        /// <summary>
        /// 每枚導彈的繪製尺寸（x = 寬，y = 長／沿建築朝向）。
        /// 僅在 <see cref="useNativeDrawSize"/> 為 false 時生效。
        /// </summary>
        public Vector2 missileDrawSize = new Vector2(0.85f, 1.9f);

        /// <summary>
        /// 北向 (Rot4.North) 時各槽位相對建築中心的本地偏移（單位：格）。
        /// 留空則沿建築長軸自動均分產生 maxStored 個槽位。
        /// 實際繪製時整體會依 parent.Rotation 旋轉，因此順序與排列會隨朝向一起轉。
        /// 注意：若提供下方對應方向的 <see cref="north"/>/<see cref="east"/>/<see cref="south"/>/<see cref="west"/>
        /// 覆寫，則該方向改用覆寫值（座標不再自動旋轉）。
        /// </summary>
        public List<Vector3> slotOffsets = new List<Vector3>();

        /// <summary>北向覆寫（座標與旋轉可單獨調整）。留空則沿用共用 slotOffsets 自動旋轉。</summary>
        public MissileRackDirectional north;

        /// <summary>東向覆寫（座標與旋轉可單獨調整）。</summary>
        public MissileRackDirectional east;

        /// <summary>南向覆寫（座標與旋轉可單獨調整）。</summary>
        public MissileRackDirectional south;

        /// <summary>西向覆寫（座標與旋轉可單獨調整）。</summary>
        public MissileRackDirectional west;

        /// <summary>額外高度偏移（用於微調疊繪層級）。</summary>
        public float altitudeOffset = 0f;

        public CompProperties_MissileRackRenderer()
        {
            compClass = typeof(CompMissileRackRenderer);
        }
    }

    /// <summary>
    /// 依建築旋轉方向，按順序最多繪製 maxStored 枚已存放的導彈。
    /// 由 ThingWithComps.DrawAt → Comps_PostDraw 每幀呼叫（需 drawerType 含 realtime）。
    ///
    /// 發射模式（來自合併後的 <see cref="MissileRackExtension"/>）影響渲染：
    ///   發射模式為 Vertical（發射管／發射井）時不顯示彈體。量級（sizeClass）不影響渲染。
    ///
    /// 彈體貼圖取向依建築朝向（parent.Rotation）決定：
    ///   <see cref="Graphic_Multi"/>（含 _north/_east/_south/_west）→ 依朝向換對應方向貼圖、貼圖本身不再旋轉；
    ///   <see cref="Graphic_Single"/>（單張）→ 旋轉單張貼圖使其指向該朝向（沿用舊行為）。
    ///
    /// 座標與旋轉來源（各自獨立）：
    ///   座標：對應方向覆寫的 slotOffsets（原值，不自動旋轉）→ 共用 slotOffsets（依朝向旋轉）→ 自動均分。
    ///   旋轉：方向覆寫的 rotation（度，一律優先）→ 依 Graphic 取向規則自動處理。
    /// </summary>
    public class CompMissileRackRenderer : ThingComp
    {
        public CompProperties_MissileRackRenderer Props => (CompProperties_MissileRackRenderer)props;

        private List<Vector3> cachedSlots;

        /// <summary>共用（北向）槽位：有設定則用設定值，否則自動均分。</summary>
        private List<Vector3> SharedSlots
        {
            get
            {
                if (Props.slotOffsets != null && Props.slotOffsets.Count > 0)
                {
                    return Props.slotOffsets;
                }
                return cachedSlots ?? (cachedSlots = GenerateLineSlots());
            }
        }

        private MissileRackDirectional DirectionalFor(Rot4 rot)
        {
            switch (rot.AsInt)
            {
                case Rot4.NorthInt: return Props.north;
                case Rot4.EastInt: return Props.east;
                case Rot4.SouthInt: return Props.south;
                case Rot4.WestInt: return Props.west;
                default: return null;
            }
        }

        private List<Vector3> GenerateLineSlots()
        {
            List<Vector3> list = new List<Vector3>();
            IntVec2 size = parent.def.size;
            bool alongZ = size.z >= size.x;
            float longLen = Mathf.Max(size.x, size.z);
            int n = Mathf.Max(1, Props.maxStored);
            float spacing = longLen / n;
            float start = -longLen / 2f + spacing / 2f;
            for (int i = 0; i < n; i++)
            {
                float along = start + i * spacing;
                list.Add(alongZ ? new Vector3(0f, 0f, along) : new Vector3(along, 0f, 0f));
            }
            return list;
        }

        public override void PostDraw()
        {
            base.PostDraw();

            if (!(parent is Building_MissileRack rack))
            {
                return;
            }
            IReadOnlyList<Thing> held = rack.HeldThings;
            if (held == null || held.Count == 0)
            {
                return;
            }

            // 發射模式（來自合併後的 MissileRackExtension）影響渲染：
            // 垂直發射（發射管／發射井）不顯示彈體。
            MissileRackExtension ext = rack.RackExt;
            if (ext != null && !ext.ShowStoredBody)
            {
                return;
            }

            Rot4 rot = parent.Rotation;
            MissileRackDirectional dir = DirectionalFor(rot);

            // ---- 座標來源：方向覆寫（原值）優先，否則共用槽位（依朝向旋轉）----
            bool useDirectionalSlots = dir != null && dir.slotOffsets != null && dir.slotOffsets.Count > 0;
            List<Vector3> slots = useDirectionalSlots ? dir.slotOffsets : SharedSlots;

            int count = Mathf.Min(held.Count, Mathf.Min(slots.Count, Props.maxStored));
            if (count <= 0)
            {
                return;
            }

            // ---- 旋轉覆寫：方向覆寫的 rotation（度）一律優先 ----
            bool hasManualRotation = dir != null && dir.rotation.HasValue;
            Quaternion manualQuat = hasManualRotation
                ? Quaternion.Euler(0f, dir.rotation.Value, 0f)
                : Quaternion.identity;

            float y = AltitudeLayer.BuildingOnTop.AltitudeFor() + Props.altitudeOffset;
            Vector3 center = parent.DrawPos;
            Vector3 fallbackScale = new Vector3(Props.missileDrawSize.x, 1f, Props.missileDrawSize.y);

            for (int i = 0; i < count; i++)
            {
                Thing missile = held[i];
                Graphic graphic = missile.Graphic;
                if (graphic == null)
                {
                    continue;
                }

                // 方向覆寫的座標為該朝向的原值，不再自動旋轉；共用槽位則依朝向旋轉。
                Vector3 pos = center + (useDirectionalSlots ? slots[i] : slots[i].RotatedBy(rot));
                pos.y = y;

                // 1:1 渲染：直接採用導彈 Graphic 的原生 drawSize（與導彈貼圖原始比例一致），
                // 不再壓縮成固定尺寸。useNativeDrawSize=false 時回退使用 missileDrawSize。
                Vector3 scale = Props.useNativeDrawSize
                    ? new Vector3(graphic.drawSize.x, 1f, graphic.drawSize.y)
                    : fallbackScale;

                // 依建築朝向取方向貼圖：Graphic_Multi → _north/_east/_south/_west；Graphic_Single → 單張。
                Material mat = graphic.MatAt(rot, missile);

                // 旋轉決策：
                //   1) 手動角度覆寫（dir.rotation）一律優先；
                //   2) 否則該 Graphic 需旋轉（單張：Graphic_Single 或退化的 Multi）→ 依朝向旋轉，使彈體指向該朝向（沿用舊行為）；
                //   3) 否則（具方向貼圖的 Graphic_Multi）→ 不旋轉，貼圖本身已朝該向。
                Quaternion quat;
                if (hasManualRotation)
                {
                    quat = manualQuat;
                }
                else if (graphic.ShouldDrawRotated)
                {
                    quat = Quaternion.Euler(0f, rot.AsAngle + graphic.DrawRotatedExtraAngleOffset, 0f);
                }
                else
                {
                    quat = Quaternion.identity;
                }

                // 方向貼圖以鏡像合成東/西時（EastFlipped/WestFlipped），採用翻轉網格。
                bool flip = (rot == Rot4.East && graphic.EastFlipped)
                    || (rot == Rot4.West && graphic.WestFlipped);

                Matrix4x4 matrix = Matrix4x4.TRS(pos, quat, scale);
                Graphics.DrawMesh(MeshPool.GridPlane(Vector2.one, flip), matrix, mat, 0);
            }
        }
    }
}
