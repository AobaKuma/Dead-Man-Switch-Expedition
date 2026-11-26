using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 撞擊飛行抵達時的世界效果與片尾文字（<see cref="TransferImpactUtility.Resolve"/> 呼叫）。
    /// 由 <see cref="TransferFlightTuningDef.impactWorkerClass"/> 指定；預設只有通用片尾，
    /// 撞擊坑、陣營反應、劇情結局等內容由內容 mod 繼承後提供（DMSE：<see cref="TransferImpactWorker_Hellfire"/>）。
    /// 框架本身不引用任何內容 mod 的 Def（docs/轉移船與BVR移植FFF_可行性評估.md §2.1）。
    /// </summary>
    public class TransferImpactWorker
    {
        /// <summary>
        /// 片尾文字。在 <see cref="ApplyWorldEffects"/> 之前呼叫，看得到撞擊前的世界狀態。
        /// </summary>
        /// <param name="time">撞擊時間與經緯度（片頭第一行）。</param>
        /// <param name="planetkiller">撞擊用引擎達 planetkillerMinEngines：行星殺手結局，片尾後回主選單。</param>
        public virtual string MakeCredits(string time, bool planetkiller, PlanetTile target)
        {
            if (planetkiller)
            {
                return GameVictoryUtility.MakeEndCredits(
                    time + "\n" + "DMSE.Impact.Planetkiller.Ending.Intro".Translate(),
                    "DMSE.Impact.Planetkiller.Ending.Outro".Translate(),
                    string.Empty,
                    "DMSE.Impact.Planetkiller.Ending.Aboard".Translate(),
                    new System.Collections.Generic.List<Pawn>());
            }
            return GameVictoryUtility.MakeEndCredits(
                time + "\n" + "DMSE.Impact.Ending.Intro".Translate(),
                "DMSE.Impact.Ending.Outro".Translate(),
                string.Empty,
                "DMSE.Impact.Ending.Aboard".Translate(),
                new System.Collections.Generic.List<Pawn>());
        }

        /// <summary>撞擊對世界的效果（目標據點已摧毀、船尚未銷毀）。預設沒有額外效果。</summary>
        /// <param name="shipName">撞擊坑等命名用。</param>
        /// <param name="engineCount">撞擊用引擎數，決定規模。</param>
        public virtual void ApplyWorldEffects(string shipName, int engineCount, PlanetTile target, bool planetkiller)
        {
        }
    }
}
