using System.Collections.Generic;
using DMSE;
using Fortified;
using RimWorld.Planet;
using Verse;

/// <summary>
/// 給其他 mod 用的擴充點（docs 見 DepthRim 的 DMSE_調整計畫.md）：預設值必須等於加入前的行為，DMSE 自己的內容不變。
/// </summary>
static partial class Test
{
    class DomainEffector : FakeEffector, IDomainAwareEffector
    {
        public CombatDomain engages = CombatDomain.Underwater;
        public bool Engages(CombatDomain domain) => domain == engages;
    }

    static void ExtensionPoints()
    {
        // 交戰領域：預設全部是空中
        Check(new BVRTargetProps().domain == CombatDomain.Air, "target props default to the air domain");
        Check(new MissileBodyDef().domain == CombatDomain.Air, "missile bodies default to the air domain");
        Check(new BVRTarget().domain == CombatDomain.Air, "map targets default to the air domain");
        Check(new AirEngagement().Domain == (byte)CombatDomain.Air, "engagements default to the air domain");
        Check(CombatDomainUtility.Of((MissileConfig)null, null) == CombatDomain.Air, "no config and no props means air");
        Check(CombatDomainUtility.Of((MissileConfig)null, new BVRTargetProps { domain = CombatDomain.Underwater }) == CombatDomain.Underwater,
            "a payload without a body takes its domain from its target props");
        Check(CombatDomainUtility.Covers(null, CombatDomain.Air) && !CombatDomainUtility.Covers(null, CombatDomain.Underwater),
            "an empty domain list covers air only");
        Check(CombatDomainUtility.Covers(new List<CombatDomain> { CombatDomain.Underwater }, CombatDomain.Underwater)
            && !CombatDomainUtility.Covers(new List<CombatDomain> { CombatDomain.Underwater }, CombatDomain.Air),
            "an underwater-only list does not cover air");
        CompProperties_FireControlRadar fc = new CompProperties_FireControlRadar();
        Check(fc.Covers(CombatDomain.Air) && !fc.Covers(CombatDomain.Underwater), "fire-control radars guide air interceptors only by default");
        Check(new CompProperties_TerminalCIWS().domains.NullOrEmpty(), "CIWS covers air only by default");
        FakeEffector plain = new FakeEffector();
        Check(CombatDomainUtility.Engages(plain, CombatDomain.Air) && !CombatDomainUtility.Engages(plain, CombatDomain.Underwater),
            "an effector that declares nothing is air-only");
        DomainEffector underwater = new DomainEffector();
        Check(CombatDomainUtility.Engages(underwater, CombatDomain.Underwater) && !CombatDomainUtility.Engages(underwater, CombatDomain.Air),
            "a domain-aware effector decides for itself");

        // 交戰判定、行為查詢
        Check(new MissileBodyDef().engageWorker == null, "missile bodies use DMSE's engagement rules unless they name a worker");
        Check(MissileBehaviorSet.Empty.Get<MissileBehavior>() == null, "Get<T> on an empty behavior set returns null");

        // 導彈陣地
        CompProperties_MissileBase missileBase = new CompProperties_MissileBase();
        Check(missileBase.partOptions == null, "missile bases use the body's default parts unless partOptions is set");
        Check(missileBase.relocatedLetterLabelKey == "DMSE.MissileBase.Relocated" && missileBase.relocatedLetterTextKey == "DMSE.MissileBase.RelocatedDesc",
            "the relocation letter keeps DMSE's text by default");

        // 抽象防空
        Check(!new CompProperties_WorldAirDefense().ignoreNetworkResearch, "abstract air defence waits for the radar network research by default");

        // 轉移飛行環境
        TransferEnvironmentExtension env = new TransferEnvironmentExtension();
        Check(env.routeWorker == null && env.energyWorker == null && env.landingWorker == null, "transfer environments add no workers by default");
        TransferRouteWorker route = new TransferRouteWorker();
        Check(route.BuildPath(PlanetTile.Invalid, PlanetTile.Invalid) == null, "the base route worker falls back to DMSE's path");
        Check(route.DestinationBlockReason(PlanetTile.Invalid, PlanetTile.Invalid) == null, "the base route worker adds no destination checks");
        Check(new TransferLandingWorker().LandingBlockReason(null, PlanetTile.Invalid) == null, "the base landing worker never blocks");

        // 轉移飛行的演出（2026-11-11）：沒掛的圖層照太空船；掛了的照欄位字面值
        Check(env.presentation == null, "transfer environments use the spaceship presentation unless one is set");
        TransferPresentation p = new TransferPresentation();
        Check(p.cameraShake == 1f && p.nozzleSparks && p.exhaustDamage == 30f && p.takeoffCutsceneClass == null,
            "a presentation's defaults match the spaceship (shake, sparks, 30 exhaust damage, DMSE's cutscene)");
        Check(new List<string>(p.ConfigErrors()).Count == 0, "a default presentation has no config errors");
        Check(new List<string>(new TransferPresentation { takeoffCutsceneClass = typeof(string) }.ConfigErrors()).Count == 1, "a cutscene class that is not a TransferTakeoffCutscene is a config error");
        Check(new List<string>(new TransferPresentation { cameraShake = -1f }.ConfigErrors()).Count == 1, "a negative shake is a config error");
        // 文字覆寫（TransferText）：預設不覆寫，沒有前綴或不是轉移文字的鍵原樣傳回
        Check(p.textKeyPrefix == null, "a presentation keeps DMSE's wording unless textKeyPrefix is set");
        Check(TransferText.Key("DMSE.Transfer.Phase.Coast", (TransferPresentation)null) == "DMSE.Transfer.Phase.Coast" && TransferText.Key("DMSE.Transfer.Phase.Coast", p) == "DMSE.Transfer.Phase.Coast",
            "without a text prefix the transfer keys are unchanged");
        Check(TransferText.Key("DMSE.UI.ShowDetails", new TransferPresentation { textKeyPrefix = "Test.Sub" }) == "DMSE.UI.ShowDetails", "only DMSE.Transfer keys are overridden");
        Check(typeof(TransferTakeoffCutscene).GetConstructor(new[] { typeof(Map), typeof(UnityEngine.Vector3), typeof(System.Action), typeof(Map), typeof(HashSet<IntVec3>) }) != null,
            "the cutscene constructor that MakeTakeoffCutscene calls exists");
    }
}
