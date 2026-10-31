using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DMSE
{
    /// <summary>導彈可自定義的部件類別（裝配槽位）。</summary>
    public enum MissilePartCategory
    {
        Warhead,    // 彈頭
        Guidance,   // 導引
        Propulsion, // 推進／射程
        Payload     // 特殊酬載
    }

    /// <summary>
    /// 導彈尺寸分級。決定對應的發射平台與儲藏架（見設計文件「DMSE 導彈戰鬥內容」）。
    /// </summary>
    public enum MissileSizeClass
    {
        Loitering,  // 巡飛彈：軌道傾斜發射、最前期 BVR、隱身與速度差
        Light,      // 輕型導彈：中短程防空/地對地、四發彈容、無法自定義
        Medium,     // 中型導彈：巡航/反彈道、可在儲存設備上自定義
        Ballistic   // 彈道導彈：長程、只能垂直發射、不可上重力船、無物品型態
    }

    /// <summary>
    /// 發射方式。純建造限制＋視覺：垂直發射架不可建於重力船基structure 且為單一朝向；
    /// 傾斜發射架可旋轉。不影響彈道數值。Both 表示彈體本身可被任一型發射平台使用。
    /// </summary>
    public enum MissileLaunchMode
    {
        Tilt,       // 傾斜發射（可旋轉、可上重力船）
        Vertical,   // 垂直發射（單一朝向、禁止建於 substructure）
        Both        // 彈體可同時相容傾斜與垂直平台
    }

    /// <summary>導彈自定義（裝配）介面所在位置。</summary>
    public enum MissileCustomizeLocation
    {
        None,            // 不可自定義（巡飛彈、輕型導彈）
        Item,            // 在導彈物品上以 ITab 裝配（現有中型彈體的範本行為）
        StorageBuilding, // 在專屬儲存建築上消耗資源調整（設計文件之中型導彈目標行為）
        Launcher         // 直接在發射架上建造／自定義（彈道導彈，無物品型態）
    }

    /// <summary>
    /// 導彈彈體定義。提供基礎參數，並決定此彈體開放哪些裝配槽位、抵達時使用哪個 incoming skyfaller。
    /// </summary>
    public class MissileBodyDef : Def
    {
        // ===== 分級（四階導彈分類）=====
        /// <summary>導彈尺寸分級，決定可用的發射平台與儲存架。</summary>
        public MissileSizeClass sizeClass = MissileSizeClass.Medium;

        /// <summary>此彈體可被哪種發射平台使用（Tilt／Vertical／Both）。</summary>
        public MissileLaunchMode launchMode = MissileLaunchMode.Both;

        /// <summary>自定義介面所在位置；None 表示此彈體不可自定義。</summary>
        public MissileCustomizeLocation customizeLocation = MissileCustomizeLocation.Item;

        // 基礎參數
        public float baseExplosionRadius = 5f;
        public DamageDef baseDamageDef;          // 為 null 時於 MissileConfig 內回退為 Bomb
        public int baseDamageAmount = 100;
        public float baseScatter = 3f;           // 命中偏移半徑（格）
        public float baseWorldSpeedFactor = 1f;  // 世界地圖旅行速度倍率
        public int baseRange = 0;                // 額外固定射程加成（世界 tile）

        /// <summary>
        /// 具導引能力時，飛行中可更新終端位置（追蹤移動目標）的次數。
        /// 次數用完後沿最後位置飛行，目標若已離開即落空（逃逸區）。無導引能力時不生效。
        /// </summary>
        public int terminalUpdates = 0;

        // 燃料與推進效率（決定射程）
        public float baseFuel = 100f;            // 彈體基礎燃料量
        public float baseSpecificImpulse = 3f;   // 基礎比衝（推進效率）
        public float rangePerFuelImpulse = 0.1f; // 射程換算係數：射程 ≈ 燃料 × 比衝 × 此係數

        // 載荷容量係數（N）
        /// <summary>
        /// 載荷容量係數 N。所有戰鬥部（WarheadEffect）與載荷（PayloadEffect）
        /// 的效果強度、範圍均以此值縮放。預設 5。
        /// </summary>
        public float payloadCapacity = 5f;

        // 裝配相關
        public int assembleWorkAmount = 600;     // 套用一次裝配變更所需工作量
        public float refundFraction = 0.5f;      // 移除/更換部件時退還的資源比例

        /// <summary>此彈體開放的裝配槽位。</summary>
        public List<MissilePartCategory> slots = new List<MissilePartCategory>();

        /// <summary>
        /// 建造時自帶的預設零件。新造的導彈（物品或發射井）會直接套用這組零件，
        /// 建造價格已包含它們的材料；之後重新裝配才需再搬料。
        /// 只取 CompatibleWith(body) 成立者，同類別取第一個。
        /// </summary>
        public List<MissilePartDef> defaultParts;

        /// <summary>
        /// 彈體內建的選目標方式（XML Class 指定 <see cref="GuidanceType"/> 子類），沒有裝導引部件時使用，
        /// 例如巡飛彈以電視制導由操作員選點。預設只影響落點選擇，不提供導引能力（跨圖層、終端更新）；
        /// 見 <see cref="builtInTerminalGuidance"/>。
        /// </summary>
        public GuidanceType builtInGuidance;

        /// <summary>
        /// 內建導引是否也提供導引能力（可跨圖層、可依 <see cref="terminalUpdates"/> 追蹤移動目標），
        /// 例如反艦巡飛彈的內建主動雷達。只在沒有導引部件、且 <see cref="builtInGuidance"/> 本身具導引能力時生效。
        /// </summary>
        public bool builtInTerminalGuidance = false;

        /// <summary>
        /// 作為攔截彈時的命中率係數：乘在火控雷達算出的中過程命中率上（地圖層與世界層皆同）。
        /// 老式攔截彈 &lt; 1，先進半主動彈 &gt; 1。
        /// </summary>
        public float interceptHitFactor = 1f;

        /// <summary>
        /// 在發射架上選用此彈體所需的研究（只對 <see cref="MissileCustomizeLocation.Launcher"/> 型彈體有意義：
        /// 物品型彈體由配方的研究擋住）。
        /// </summary>
        public List<ResearchProjectDef> researchPrerequisites;

        public bool ResearchSatisfied
        {
            get
            {
                if (researchPrerequisites == null) { return true; }
                for (int i = 0; i < researchPrerequisites.Count; i++)
                {
                    if (!researchPrerequisites[i].IsFinished) { return false; }
                }
                return true;
            }
        }

        public override IEnumerable<StatDrawEntry> SpecialDisplayStats(StatRequest req)
        {
            foreach (StatDrawEntry e in base.SpecialDisplayStats(req)) { yield return e; }
            foreach (StatDrawEntry e in MissileInfoCardUtility.BodyStats(this)) { yield return e; }
        }

        /// <summary>抵達目標後生成的 incoming skyfaller（thingClass 應為 DMSE.MissileIncoming）。</summary>
        public ThingDef incomingSkyfaller;

        /// <summary>
        /// 此彈體的發射 Projectile（thingClass 應為 <see cref="Projectile_MissileLaunch"/>）：升空外觀與發射尾焰隨彈體而定，
        /// 對地發射與當作攔截彈發射都用它。沒有時退回平台的 skyfaller / interceptorSkyfaller。
        /// </summary>
        public ThingDef launchProjectile;

        /// <summary>設計名稱用的短詞（見 <see cref="MissileDesignNamer"/>）；null 時退回 label。</summary>
        [MustTranslate]
        public string designWord;

        /// <summary>
        /// 此彈體專屬的命名規則（型號代字、編號範圍與樣板包，見 <see cref="MissileNamerDef"/>）。
        /// null 時沿用通用樣板 <c>DMSE_NamerMissileDesign</c>：只有部件詞，沒有型號代字。
        /// </summary>
        public MissileNamerDef namer;

        public string uiIconPath;

        private Texture2D iconInt;
        public Texture2D Icon
        {
            get
            {
                if (iconInt == null && !uiIconPath.NullOrEmpty())
                {
                    iconInt = ContentFinder<Texture2D>.Get(uiIconPath, false);
                }
                return iconInt;
            }
        }
    }

    /// <summary>
    /// 導彈部件定義。屬於某個裝配槽位類別，提供對彈體基礎參數的數值修正。
    /// </summary>
    public class MissilePartDef : Def
    {
        public MissilePartCategory category;

        /// <summary>裝配此部件所需資源。</summary>
        public List<ThingDefCountClass> costList;

        /// <summary>
        /// 雷達截面倍率（P4 誘餌酬載）：乘在世界航跡的 <see cref="BVRTargetProps.distanceLevel"/> 上，
        /// 讓便宜的導彈看起來像大彈——更早被偵測，具威脅評估的防空會優先接戰它。
        /// 小於 1 者為突防誘餌（較晚被偵測）。
        /// </summary>
        public float signatureFactor = 1f;

        /// <summary>
        /// 電子干擾強度（0–1）：箔條／干擾絲每次觸發時的斷鎖機率係數。
        /// 與 <see cref="signatureFactor"/> 不同：這是主動、有限次、只影響鎖定的手段。
        /// </summary>
        public float jamStrength = 0f;

        /// <summary>箔條／干擾絲的攜帶次數（發射時裝填、飛行中不補充）。</summary>
        public int chaffCharges = 0;

        // 數值修正（加算；guidance 的 scatterOffset 可為負以收緊散布）
        public float explosionRadiusOffset = 0f;
        public DamageDef damageDefOverride;       // 彈頭：覆寫傷害型別
        public int damageAmountOffset = 0;
        public float scatterOffset = 0f;          // 導引：負值降低命中偏移
        public float worldSpeedFactorOffset = 0f; // 推進：飛行速度
        public float specificImpulseOffset = 0f;  // 推進：比衝加成（推進效率，影響射程）
        public float fuelOffset = 0f;             // 燃料量加成
        public int rangeOffset = 0;               // 額外固定射程加成

        // 特殊酬載：落點額外效果
        public float payloadExplosionRadius = 0f;
        public DamageDef payloadDamageDef;
        public int payloadDamageAmount = 0;
        public ThingDef payloadSpawnThing;        // 例如毒氣／煙霧的填充物
        public float payloadSpawnChance = 1f;
        public int payloadSpawnCount = 1;

        // ---- 行為類別（以 XML Class 屬性指定子類別；留空 = 無特殊效果）----

        /// <summary>
        /// 戰鬥部效果（Warhead slot 專用）。在主彈體爆炸後執行。
        /// 支援 8 種子類別：ContinuousRod、ExtendedFuel、Airburst、TungstenPenetrator、
        /// Incendiary、EMP、AnimalBerserk、BerserkPulse。
        /// </summary>
        public WarheadEffect warheadEffect;

        /// <summary>
        /// 導引頭行為（Guidance slot 專用）。決定導彈抵達後的落點（玩家與敵方發射皆適用，
        /// 電視制導在玩家發射時改為手動選點）。子類別：Inertial、AntiRadiation、HeatSource（舊名 Infrared）、
        /// ElectromagneticSignal（舊名 HighEnergy）、TV、Laser、FireControl。
        /// </summary>
        public GuidanceType guidanceType;

        /// <summary>
        /// 載荷效果（Payload slot 專用）。在戰鬥部效果後執行。
        /// 支援 8 種子類別：None、HighExplosive、Cluster、Defoliant、FireFoam、
        /// Antiparticle、ToxicGas、AirburstTungsten。
        /// </summary>
        public PayloadEffect payloadEffect;

        // ---- 盤旋飛控（Payload 槽專用；與戰鬥部／載荷效果無關，屬於飛行行為）----

        /// <summary>
        /// 盤旋時間（ticks；0 = 沒有盤旋能力）。導引頭在目標地圖上找不到符合的目標時，
        /// 導彈不直接回退慣性制導，而是在目標上空盤旋等待：每 <see cref="loiterRetryTicks"/>
        /// 重新執行一次導引頭，找到目標就俯衝；時間用盡才以慣性落點俯衝。
        /// 盤旋期間導彈仍留在攔截波次中，末端防禦（CIWS）照常接戰。
        /// </summary>
        public int loiterTicks = 0;

        /// <summary>盤旋期間重新執行導引頭的間隔（ticks）。</summary>
        public int loiterRetryTicks = 250;

        /// <summary>只有巡飛彈（<see cref="MissileSizeClass.Loitering"/>）能裝配此部件。</summary>
        public bool requiresLoiteringBody = false;

        /// <summary>
        /// 沒有指定 <see cref="guidanceType"/> 的導引部件是否仍具導引能力（例：精確導引頭）。
        /// 有 guidanceType 時以 <see cref="GuidanceType.TerminalGuidance"/> 為準。
        /// </summary>
        public bool terminalGuidance = false;

        /// <summary>此部件是否提供導引能力：可跨圖層打擊、可依彈體次數更新終端位置。</summary>
        public bool ProvidesTerminalGuidance => guidanceType != null ? guidanceType.TerminalGuidance : terminalGuidance;

        /// <summary>相容彈體；null/空 = 相容所有具有此槽位的彈體。</summary>
        public List<MissileBodyDef> compatibleBodies;

        public List<ResearchProjectDef> researchPrerequisites;

        /// <summary>設計名稱用的短詞（見 <see cref="MissileDesignNamer"/>）；null 時退回 label。</summary>
        [MustTranslate]
        public string designWord;

        /// <summary>命名時略過此部件（例如「無酬載」「標準推進」這類預設空部件）。</summary>
        public bool omitFromDesignName = false;

        public string uiIconPath;

        private Texture2D iconInt;
        public Texture2D Icon
        {
            get
            {
                if (iconInt == null && !uiIconPath.NullOrEmpty())
                {
                    iconInt = ContentFinder<Texture2D>.Get(uiIconPath, false);
                }
                return iconInt;
            }
        }

        public bool ResearchSatisfied
        {
            get
            {
                if (researchPrerequisites == null) { return true; }
                for (int i = 0; i < researchPrerequisites.Count; i++)
                {
                    if (!researchPrerequisites[i].IsFinished) { return false; }
                }
                return true;
            }
        }

        public override IEnumerable<StatDrawEntry> SpecialDisplayStats(StatRequest req)
        {
            foreach (StatDrawEntry e in base.SpecialDisplayStats(req)) { yield return e; }
            foreach (StatDrawEntry e in MissileInfoCardUtility.PartStats(this)) { yield return e; }
        }

        public bool CompatibleWith(MissileBodyDef body)
        {
            if (body == null) { return false; }
            if (!body.slots.Contains(category)) { return false; }
            if (requiresLoiteringBody && body.sizeClass != MissileSizeClass.Loitering) { return false; }
            if (compatibleBodies != null && compatibleBodies.Count > 0 && !compatibleBodies.Contains(body)) { return false; }
            return true;
        }
    }
}
