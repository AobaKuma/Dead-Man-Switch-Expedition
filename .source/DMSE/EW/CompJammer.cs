using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>地面干擾站（docs/電子戰建築_實作計畫.md §3）。效果全部在世界層結算，地圖層沒有半徑。</summary>
    public class CompProperties_Jammer : CompProperties
    {
        /// <summary>滿段干擾強度；實際值再乘功率段位。</summary>
        public float jamPower = 0.6f;

        /// <summary>對敵方雷達壓制的等級（等級差 → 乘冪，見 <see cref="EWEnvironment"/>）。</summary>
        public int jamLevel = 2;

        /// <summary>世界地圖紅圈半徑（世界 tile）。圈內全強度、無距離衰減。</summary>
        public float worldRange = 8f;

        /// <summary>運作中是否讓本圖通訊台停用。</summary>
        public bool blocksComms = true;

        public float jamGuidanceBreakFactor = 1f;

        /// <summary>導引被打掉後，直接在世界層消失（否則轉慣性落地）的機率。</summary>
        public float offMapChance = 0.5f;

        public CompProperties_Jammer() { compClass = typeof(CompJammer); }
    }

    public class CompJammer : CompBVRDevice
    {
        public CompProperties_Jammer Props => (CompProperties_Jammer)props;

        /// <summary>目前的干擾強度；關機、斷電、擊暈或待機時為 0。</summary>
        public float CurrentJamPower => Active ? Props.jamPower * PowerThrottleUtility.JamFactor(parent) : 0f;

        public bool BlocksCommsNow => Props.blocksComms && CurrentJamPower > 0f;

        /// <summary>反輻射導引頭看到的等級：干擾機就是燈塔，等級隨功率段位下降。</summary>
        public int SeekerLevel => PowerThrottleUtility.SeekerLevel(parent, Props.jamLevel);

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            MapComponent_EWControl.For(parent.Map)?.jammers.Add(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            MapComponent_EWControl.For(map)?.jammers.Remove(this);
        }

        public override string CompInspectStringExtra()
        {
            string text = "DMSE.EW.Jammer.Inspect".Translate(Props.worldRange.ToString("0.#"), CurrentJamPower.ToStringPercent("0"),
                JammerUtility.TracksCoveredBy(this));
            if (BlocksCommsNow) { text += "\n" + "DMSE.EW.Jammer.CommsBlocked".Translate().ToString(); }
            return text;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra()) { yield return gizmo; }
            if (parent.Faction != Faction.OfPlayer || parent.Map?.Parent == null) { yield break; }
            yield return new Command_Action
            {
                defaultLabel = "DMSE.EW.Jammer.ShowRing".Translate(),
                defaultDesc = "DMSE.EW.Jammer.ShowRingDesc".Translate(Props.worldRange.ToString("0.#")),
                icon = parent.def.uiIcon,
                action = () => CameraJumper.TryJumpAndSelect(new GlobalTargetInfo(parent.Map.Parent)),
            };
        }
    }
}
