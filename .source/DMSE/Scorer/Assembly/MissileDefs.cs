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

        /// <summary>抵達目標後生成的 incoming skyfaller（thingClass 應為 DMSE.MissileIncoming）。</summary>
        public ThingDef incomingSkyfaller;

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
        /// 導引頭行為（Guidance slot 專用）。決定來襲導彈的落點選擇邏輯。
        /// 支援 7 種子類別：Inertial、HighEnergy、HeatSource、AntiRadiation、
        /// Infrared、TV、FireControl。
        /// </summary>
        public GuidanceType guidanceType;

        /// <summary>
        /// 載荷效果（Payload slot 專用）。在戰鬥部效果後執行。
        /// 支援 8 種子類別：None、HighExplosive、Cluster、Defoliant、FireFoam、
        /// Antiparticle、ToxicGas、AirburstTungsten。
        /// </summary>
        public PayloadEffect payloadEffect;

        /// <summary>相容彈體；null/空 = 相容所有具有此槽位的彈體。</summary>
        public List<MissileBodyDef> compatibleBodies;

        public List<ResearchProjectDef> researchPrerequisites;

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

        public bool CompatibleWith(MissileBodyDef body)
        {
            if (body == null) { return false; }
            if (!body.slots.Contains(category)) { return false; }
            if (compatibleBodies != null && compatibleBodies.Count > 0 && !compatibleBodies.Contains(body)) { return false; }
            return true;
        }
    }
}
