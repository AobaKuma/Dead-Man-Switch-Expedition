using System.Collections.Generic;
using System.Linq;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 波次打擊的地圖管理器（docs/BVR_波次打擊規劃.md §4-1）：
    ///   - 世界選靶發射平台註冊表（平台在 PostSpawnSetup／PostDeSpawn 自行註冊，比照 BVR 裝置）；
    ///   - 打擊保留索引：彈藥 Thing → 擁有者（打擊任務 id）。
    ///
    /// 保留索引不存檔：打擊任務（P1）存在指揮台上，讀檔後由任務重建；
    /// 開發者模式的手動保留（<see cref="DebugOwner"/>）讀檔後即消失。
    /// </summary>
    public class MapComponent_StrikePlanner : MapComponent
    {
        /// <summary>開發者模式手動保留使用的擁有者；打擊任務 id 從 1 起算。</summary>
        public const int DebugOwner = 0;

        public readonly HashSet<CompMissileLauncher_WorldTargeting> launchers = new HashSet<CompMissileLauncher_WorldTargeting>();

        private readonly Dictionary<Thing, int> reservations = new Dictionary<Thing, int>();

        public MapComponent_StrikePlanner(Map map) : base(map) { }

        /// <summary>彈藥的保留擁有者；未保留（或已銷毀）時回傳 -1。</summary>
        public int OwnerOf(Thing round)
        {
            if (round == null || !reservations.TryGetValue(round, out int owner)) { return -1; }
            if (round.Destroyed)
            {
                reservations.Remove(round);
                return -1;
            }
            return owner;
        }

        public bool IsReserved(Thing round) => OwnerOf(round) >= 0;

        /// <summary>保留彈藥；已被其他擁有者保留時失敗，同一擁有者重複保留視為成功。</summary>
        public bool TryReserve(Thing round, int owner)
        {
            if (round == null || round.Destroyed || owner < 0) { return false; }
            int current = OwnerOf(round);
            if (current >= 0 && current != owner) { return false; }
            reservations[round] = owner;
            return true;
        }

        public void Release(Thing round)
        {
            if (round != null) { reservations.Remove(round); }
        }

        public void ReleaseAll(int owner)
        {
            foreach (Thing round in reservations.Where(kv => kv.Value == owner).Select(kv => kv.Key).ToList())
            {
                reservations.Remove(round);
            }
        }

        /// <summary>某擁有者目前保留的彈藥（略過已銷毀者）。</summary>
        public List<Thing> ReservedBy(int owner)
        {
            return reservations.Where(kv => kv.Value == owner && !kv.Key.Destroyed).Select(kv => kv.Key).ToList();
        }
    }
}
