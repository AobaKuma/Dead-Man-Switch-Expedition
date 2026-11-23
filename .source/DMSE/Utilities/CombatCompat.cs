using RimWorld;
using Verse;
using Verse.AI;

namespace DMSE
{
    /// <summary>
    /// 戰鬥系統相容層（目前唯一的實作是 Combat Extended，在 1.7/CE/Assemblies/DMSE_CE.dll）。
    ///
    /// CE 會把砲塔換成自己的 Building_TurretGunCE（繼承 <see cref="Building_Turret"/>，<b>不是</b>
    /// <see cref="Building_TurretGun"/>），彈藥改成 CompAmmoUser 彈匣、砲彈改成 ProjectileCE。
    /// DMSE 本體不參考 CE；凡是要碰砲塔頭、砲塔彈藥或在地圖上發射砲彈的地方，先問
    /// <see cref="Active"/>，沒有相容層（原版）時一律回 false / null，走原本的原版路線。
    /// </summary>
    public interface ICombatCompat
    {
        // ── 砲塔頭 ──

        /// <summary>把砲塔頭轉向 <paramref name="target"/>；不是相容層管的砲塔回 false。</summary>
        bool TryFaceTurret(Thing building, LocalTargetInfo target);

        /// <summary>把砲塔頭直接設成方位角（0=North/+Z，90=East/+X）；不是相容層管的砲塔回 false。</summary>
        bool TrySetTurretRotation(Thing building, float angleDeg);

        // ── 彈匣（相容層的砲塔彈藥）──

        /// <summary>建築（或它的 gun）是否有相容層的彈匣（CE：CompAmmoUser 且有彈匣）。</summary>
        bool HasMagazine(Thing building);

        /// <summary>彈匣目前裝的彈種會打出的投射物 def；空匣回 null。</summary>
        ThingDef LoadedProjectile(Thing building);

        /// <summary>彈匣剩餘發數（無彈匣回 0）。</summary>
        int LoadedRounds(Thing building);

        /// <summary>從彈匣扣 <paramref name="rounds"/> 發；不夠時不扣、回 false。</summary>
        bool TryConsumeRounds(Thing building, int rounds);

        /// <summary>
        /// 自動裝填架（<see cref="CompShellAutoloader"/>）：彈匣空了就從 <paramref name="storage"/> 取彈裝填。
        /// 回 true 表示這門砲由相容層處理（不論這次有沒有裝到），呼叫端不要再走原版裝填。
        /// </summary>
        bool TryAutoload(Thing turret, Building_Storage storage);

        // ── 投射物 ──

        /// <summary>
        /// 在地圖上由 <paramref name="entry"/> 朝 <paramref name="target"/> 發射一發從地圖外飛來的砲彈
        /// （世界砲擊落彈）。<paramref name="projectileDef"/> 不是相容層的投射物時回 false。
        /// </summary>
        bool TryLaunchIncomingShell(ThingDef projectileDef, Map map, IntVec3 entry, IntVec3 target);

        // ── NPC 陣地補給 ──

        /// <summary>
        /// NPC 砲塔彈匣低於 <paramref name="refillBelowPercent"/> 時，要空投哪種彈藥、幾個物品才補到
        /// <paramref name="refillToPercent"/>。沒有彈匣或不缺彈時回 false。
        /// </summary>
        bool TryGetMagazineRefill(Building turret, float refillBelowPercent, float refillToPercent,
            out ThingDef ammoDef, out int count);

        /// <summary>彈匣是否已滿（沒有彈匣也視為滿）。</summary>
        bool MagazineFull(Building turret);

        /// <summary>讓 <paramref name="pawn"/> 把地上的 <paramref name="ammo"/> 搬去裝進砲塔彈匣的工作；無法時回 null。</summary>
        Job MakeMagazineLoadJob(Pawn pawn, Building turret, Thing ammo);

        /// <summary><see cref="MakeMagazineLoadJob"/> 產生的工作種類（守軍忙碌計數用）。</summary>
        bool IsMagazineLoadJob(JobDef def);

        /// <summary>NPC 炮手是否值得去操作這座相容層砲塔（彈匣空且附近沒彈時回 false）。</summary>
        bool TurretUsable(Pawn pawn, Building turret);

        // ── 導彈爆炸與直接傷害 ──

        /// <summary>
        /// 由相容層執行一次導彈爆炸（CE：ExplosionCE 的穿甲＋CE 彈片）。回 false 時呼叫端照原版 GenExplosion 處理。
        /// </summary>
        bool TryDoMissileExplosion(MissileExplosionArgs args);

        /// <summary>導彈效果直接造成傷害（不經爆炸）時使用的穿甲值；<paramref name="vanillaPen"/> 是原版刻度的設定值。</summary>
        float MissileDirectArmorPenetration(float vanillaPen, DamageDef damageDef, System.Collections.Generic.IReadOnlyList<Def> sources);
    }

    /// <summary>一次導彈爆炸的參數（<see cref="MissileExplosion.Do"/> 傳給相容層）。</summary>
    public class MissileExplosionArgs
    {
        public IntVec3 center;
        public Map map;
        public float radius;
        public DamageDef damageDef;
        public Thing instigator;
        public int damageAmount;
        /// <summary>原版刻度的穿甲（-1 = 未指定）。</summary>
        public float armorPenetration = -1f;
        /// <summary>造成這次爆炸的部件 def，越前面越具體（戰鬥部／酬載部件在前，彈體在後）；相容層從這裡讀各自的設定。</summary>
        public System.Collections.Generic.IReadOnlyList<Def> sources;
    }

    /// <summary>
    /// 導彈的爆炸與直接傷害一律經過這裡：原版照舊呼叫 GenExplosion，戰鬥相容層（CE）可接手改成自己的爆炸與彈片。
    /// 「來源」是目前正在套用的導彈部件（<see cref="MissileImpactUtility"/> 在主爆炸、戰鬥部、酬載前後設定），
    /// 戰鬥部／酬載效果類別不必知道自己掛在哪個 def 上。
    /// </summary>
    public static class MissileExplosion
    {
        private static readonly Def[] NoSources = new Def[0];
        private static System.Collections.Generic.IReadOnlyList<Def> currentSources = NoSources;

        /// <summary>目前套用中的部件（沒有時為空清單）。</summary>
        public static System.Collections.Generic.IReadOnlyList<Def> CurrentSources => currentSources;

        /// <summary>設定目前的來源部件（null 項會略過）；回傳舊值，呼叫端在 finally 用 <see cref="RestoreSources"/> 還原。</summary>
        public static System.Collections.Generic.IReadOnlyList<Def> SetSources(params Def[] sources)
        {
            System.Collections.Generic.IReadOnlyList<Def> old = currentSources;
            System.Collections.Generic.List<Def> list = new System.Collections.Generic.List<Def>();
            if (sources != null) { foreach (Def d in sources) { if (d != null) list.Add(d); } }
            currentSources = list;
            return old;
        }

        public static void RestoreSources(System.Collections.Generic.IReadOnlyList<Def> old) => currentSources = old ?? NoSources;

        public static void Do(IntVec3 center, Map map, float radius, DamageDef damageDef, Thing instigator,
            int damageAmount = -1, float armorPenetration = -1f)
        {
            if (CombatCompat.Active != null && damageDef != null)
            {
                MissileExplosionArgs args = new MissileExplosionArgs
                {
                    center = center, map = map, radius = radius, damageDef = damageDef, instigator = instigator,
                    damageAmount = damageAmount, armorPenetration = armorPenetration, sources = currentSources,
                };
                if (CombatCompat.Active.TryDoMissileExplosion(args)) return;
            }
            GenExplosion.DoExplosion(center, map, radius, damageDef, instigator, damageAmount, armorPenetration);
        }

        /// <summary>直接傷害（刀刃、鎢針等）的穿甲：原版回傳 <paramref name="vanillaPen"/>，相容層換成自己的刻度。</summary>
        public static float DirectArmorPenetration(float vanillaPen, DamageDef damageDef)
            => CombatCompat.Active != null ? CombatCompat.Active.MissileDirectArmorPenetration(vanillaPen, damageDef, currentSources) : vanillaPen;
    }

    public static class CombatCompat
    {
        /// <summary>啟用中的相容層；原版為 null。由相容層組件在啟動時設定。</summary>
        public static ICombatCompat Active;

        public static bool HasMagazine(Thing building) => Active != null && building != null && Active.HasMagazine(building);

        /// <summary>轉砲塔頭：相容層砲塔交給相容層，原版砲塔直接設 <see cref="TurretTop"/>。</summary>
        public static void FaceTurret(Thing building, LocalTargetInfo target)
        {
            if (Active != null && Active.TryFaceTurret(building, target)) return;
            if (building is Building_TurretGun turret) turret.Top?.ForceFaceTarget(target);
        }
    }
}
