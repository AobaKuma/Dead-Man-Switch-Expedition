using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    public class CompProperties_MissileBodyRenderer : CompProperties
    {
        /// <summary>
        /// 繪製尺寸（格，x = 寬、y = 長）。彈體貼圖多為正方形，兩軸給同值可避免變形。
        /// </summary>
        public Vector2 drawSize = new Vector2(2.2f, 2.2f);

        /// <summary>相對建築中心的繪製偏移（單位：格）。</summary>
        public Vector3 drawOffset = Vector3.zero;

        /// <summary>額外高度偏移（用於微調疊繪層級）。</summary>
        public float altitudeOffset = 0f;

        /// <summary>額外旋轉角度（度，繞 Y 軸）。垂直發射架不旋轉，預設 0。</summary>
        public float rotation = 0f;

        public CompProperties_MissileBodyRenderer()
        {
            compClass = typeof(CompMissileBodyRenderer);
        }
    }

    /// <summary>
    /// 在「無物品型態、直接於建築上裝配」的彈道發射平台（<see cref="CompMissileLauncher_Ballistic"/>）上，
    /// 繪製已裝配彈體的直立彈體貼圖。
    ///
    /// 與 <see cref="CompMissileRackRenderer"/> 的差異：
    ///   - 彈藥不是容器裡的實體導彈，而是同一建築上 <see cref="CompMissileConfig"/> 的彈體設定，
    ///     因此沒有 Thing 可畫，改為讀 <see cref="MissileBodyDef.incomingSkyfaller"/> 的貼圖
    ///     （彈體的落點 skyfaller 就是該型號的彈體圖，且會隨發射井換彈體而變）。
    ///   - 只在「已裝配完成」（<see cref="CompMissileLauncher_Ballistic.IsLoaded"/>）時繪製：
    ///     未建造彈體、資源搬運中、施工中、發射後冷卻中，都只看到空架子。
    ///
    /// 由 ThingWithComps.Draw → Comps_PostDraw 每幀呼叫（建築需 drawerType 含 realtime）。
    /// </summary>
    public class CompMissileBodyRenderer : ThingComp
    {
        public CompProperties_MissileBodyRenderer Props => (CompProperties_MissileBodyRenderer)props;

        /// <summary>目前是否應顯示彈體：已裝配完成，且彈體有可用的貼圖。</summary>
        private bool TryGetBodyGraphic(out Graphic graphic)
        {
            graphic = null;

            CompMissileLauncher_Ballistic launcher = parent?.TryGetComp<CompMissileLauncher_Ballistic>();
            if (launcher == null || !launcher.IsLoaded) { return false; }

            MissileBodyDef body = parent.TryGetComp<CompMissileConfig>()?.config?.body;
            if (body == null) { return false; }

            graphic = body.incomingSkyfaller?.graphic;
            return graphic != null;
        }

        public override void PostDraw()
        {
            base.PostDraw();

            if (!TryGetBodyGraphic(out Graphic graphic)) { return; }

            // 貼圖一律以北向（彈頭朝上）繪製：垂直發射架不可旋轉，彈體也是立著朝上。
            float y = AltitudeLayer.BuildingOnTop.AltitudeFor() + Props.altitudeOffset;
            Vector3 pos = parent.DrawPos + Props.drawOffset;
            pos.y = y;

            Vector3 scale = new Vector3(Props.drawSize.x, 1f, Props.drawSize.y);
            Quaternion quat = Quaternion.Euler(0f, Props.rotation, 0f);
            Matrix4x4 matrix = Matrix4x4.TRS(pos, quat, scale);

            Graphics.DrawMesh(
                MeshPool.GridPlane(Vector2.one),
                matrix,
                graphic.MatAt(Rot4.North, parent),
                0);
        }
    }
}
