using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 舊版駕駛台轉移/撞擊飛行的世界物件，只供舊存檔遷移（設計稿 §10-2）：
    /// 讀檔後第一個 tick 直接抵達（轉移：原地圖父物件移到目的地；撞擊：執行撞擊結局），之後自行銷毀。
    /// 新的轉移飛行見 <see cref="WorldObject_TransferShip"/>。
    /// </summary>
    public abstract class TravelingObject : WorldObject
    {
        /// <summary>被搬運的原地圖父物件。</summary>
        public WorldObject worldObject;

        protected PlanetTile _start = PlanetTile.Invalid;

        protected PlanetTile _end = PlanetTile.Invalid;

        protected float progress;

        public override Material Material => worldObject?.Material;

        public override Material ExpandingMaterial => worldObject?.ExpandingMaterial;

        public override Texture2D ExpandingIcon => worldObject?.ExpandingIcon;

        public override Color ExpandingIconColor => worldObject?.ExpandingIconColor ?? Color.white;

        public override string Label => worldObject?.Label ?? base.Label;

        public override string GetDescription()
        {
            return worldObject?.GetDescription();
        }

        public override Vector3 DrawPos
        {
            get
            {
                if (!_start.Valid || !_end.Valid)
                {
                    return base.DrawPos;
                }
                return Vector3.Slerp(Find.WorldGrid.GetTileCenter(_start), Find.WorldGrid.GetTileCenter(_end), progress);
            }
        }

        protected override void Tick()
        {
            base.Tick();
            if (!Destroyed)
            {
                Log.Message("[DMSE] Legacy " + GetType().Name + " found in save; finishing its flight immediately.");
                Arrive();
            }
        }

        public virtual void Arrive()
        {
            if (worldObject != null && !worldObject.Destroyed && _end.Valid)
            {
                worldObject.Tile = _end;
            }
            if (!Destroyed)
            {
                Destroy();
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref worldObject, "worldObject");
            Scribe_Values.Look(ref progress, "progress");
            Scribe_Values.Look(ref _start, "initialTile");
            Scribe_Values.Look(ref _end, "destTile");
        }
    }

    public class WorldObject_Transfer : TravelingObject
    {
    }
}
