using System.Collections.Generic;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 小人可以搬料、施工的裝配目標：導彈物品／儲存架內的導彈（改裝，<see cref="CompMissileConfig"/>）
    /// 或組裝台（生產，<see cref="CompMissileAssemblyBench"/>）。
    /// <see cref="JobDriver_DeliverMissileResource"/> 與 <see cref="JobDriver_AssembleMissile"/> 只依賴這組成員，
    /// 所以兩種目標共用同一套搬料與施工 Job。
    /// </summary>
    public interface IMissileAssemblyTarget
    {
        /// <summary>帶有此目標的 Thing（導彈物品或組裝台建築）。</summary>
        Thing Parent { get; }

        /// <summary>有待施工的工作，且玩家已確認（改裝的 assemblyConfirmed／生產的 ordered）。</summary>
        bool WantsWork { get; }

        /// <summary>尚需搬運的資源（總需求扣除已搬運）。</summary>
        Dictionary<ThingDef, int> StillNeeded();

        bool ResourcesComplete { get; }

        void Deposit(ThingDef def, int count);

        /// <summary>總工作量（以速度 1.0 計的 ticks）。</summary>
        int WorkAmount { get; }

        /// <summary>已完成的工作量。存檔保存、中斷可接續；目標構型改變時歸零。</summary>
        float WorkDone { get; set; }

        /// <summary>此小人每 tick 推進的工作量。</summary>
        float WorkSpeedFor(Pawn pawn);

        /// <summary>此刻能否完工（組裝台：出廠暫存未滿）；改裝恆為 true。</summary>
        bool CanFinishNow { get; }

        /// <summary>完工：改裝套用 pending；組裝台產出一枚導彈。</summary>
        void ApplyAssembly();
    }
}
