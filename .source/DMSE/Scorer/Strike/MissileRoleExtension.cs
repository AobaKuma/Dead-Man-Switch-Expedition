using Verse;

namespace DMSE
{
    /// <summary>導彈物品的用途。只影響世界打擊（對地發射軌、波次打擊）；SAM 取彈仍由建築的儲存篩選決定。</summary>
    public enum MissileRole
    {
        /// <summary>對地打擊彈（預設；巡航、巡飛彈）。</summary>
        Strike,
        /// <summary>只作攔截彈：不列入世界打擊。</summary>
        Interceptor,
        /// <summary>兩用：可列入世界打擊，未被保留時也可被同容器的 SAM 取用。</summary>
        Dual,
    }

    /// <summary>
    /// 掛在導彈物品 ThingDef 上，宣告它能否用於世界打擊。未掛擴充的導彈視為 <see cref="MissileRole.Strike"/>。
    /// 不以戰鬥部或導引頭推論用途：玩家可能故意裝連續桿打地面，規則要可預測。
    /// </summary>
    public class MissileRoleExtension : DefModExtension
    {
        public MissileRole role = MissileRole.Strike;

        public static MissileRole RoleOf(ThingDef def)
        {
            return def?.GetModExtension<MissileRoleExtension>()?.role ?? MissileRole.Strike;
        }

        /// <summary>此導彈可否用於世界打擊。</summary>
        public static bool CanStrike(Thing missile)
        {
            return missile != null && RoleOf(missile.def) != MissileRole.Interceptor;
        }
    }
}
