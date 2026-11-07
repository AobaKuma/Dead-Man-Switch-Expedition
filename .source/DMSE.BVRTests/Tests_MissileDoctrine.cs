using System.Collections.Generic;
using System.Linq;
using DMSE;
using Fortified;
using RimWorld;
using RimWorld.Planet;
using Verse;

static partial class Test
{
    // Faction doctrine: lookup order, defaults by tech level, settlement defense settings and the target worker hook.
    static void MissileDoctrines()
    {
        var industrial = new MissileDoctrineDef { defName = "TestDoctrine_Industrial", defaultMinTech = TechLevel.Industrial, defaultMaxTech = TechLevel.Industrial };
        var spacer = new MissileDoctrineDef { defName = "TestDoctrine_Spacer", defaultMinTech = TechLevel.Spacer };
        var explicitOnly = new MissileDoctrineDef { defName = "TestDoctrine_Explicit" };
        var all = new List<MissileDoctrineDef> { explicitOnly, industrial, spacer };

        Check(MissileDoctrine.For(new FactionDef { techLevel = TechLevel.Medieval }, all) == null, "a medieval faction has no default doctrine");
        Check(MissileDoctrine.For(new FactionDef { techLevel = TechLevel.Industrial }, all) == industrial, "industrial gets the industrial default");
        Check(MissileDoctrine.For(new FactionDef { techLevel = TechLevel.Ultra }, all) == spacer, "an open upper bound covers higher levels");
        Check(MissileDoctrine.For(null, all) == null, "no faction def, no doctrine");
        var chosen = new FactionDef
        {
            techLevel = TechLevel.Industrial,
            modExtensions = new List<DefModExtension> { new MissileDoctrineExtension { doctrine = explicitOnly } },
        };
        Check(MissileDoctrine.For(chosen, all) == explicitOnly, "an explicit doctrine wins over the tech default");
        Check(!explicitOnly.IsDefaultFor(TechLevel.Industrial), "a doctrine without a range is never a default");

        // Config errors.
        Check(!industrial.ConfigErrors().Any(e => e.Contains("default")), "a valid range has no errors");
        Check(new MissileDoctrineDef { defName = "Bad", defaultMinTech = TechLevel.Spacer, defaultMaxTech = TechLevel.Industrial }.ConfigErrors().Any(),
            "an inverted range is an error");
        var noRadar = new MissileDoctrineDef { defName = "NoRadar", settlementDefense = new SettlementAirDefenseProps { fireControlDef = new ThingDef { defName = "Plain" } } };
        List<string> errors = noRadar.ConfigErrors().ToList();
        Check(errors.Any(e => e.Contains("fireControlDef")) && errors.Any(e => e.Contains("interceptorBody")), "settlement defense needs a radar and an interceptor");

        // Settlement defense: the fire control comes from the named building, the interceptor is built once.
        var radar = new CompProperties_FireControlRadar { maxTargets = 3 };
        var body = new MissileBodyDef { defName = "TestDoctrineBody" };
        var defense = new SettlementAirDefenseProps
        {
            fireControlDef = new ThingDef { defName = "TestDoctrineRadar", comps = new List<CompProperties> { radar } },
            interceptorBody = body,
        };
        Check(defense.FireControl == radar, "the fire control is read from the building");
        Check(defense.Interceptor != null && defense.Interceptor.body == body && ReferenceEquals(defense.Interceptor, defense.Interceptor), "the interceptor config is built once");
        Check(!new MissileDoctrineDef { defName = "Good", settlementDefense = defense }.ConfigErrors().Any(), "a complete settlement defense has no errors");

        // Target worker: the default only takes player maps; a doctrine can replace it.
        Check(industrial.TargetWorker is StrikeTargetWorker_Players, "no worker means player maps only");
        var custom = new StrikeTargetWorker_Hostile { includePlayer = false };
        Check(new MissileDoctrineDef { targetWorker = custom }.TargetWorker == custom, "a doctrine's worker is used");
        var picked = new List<MapParent> { new MapParent(), new MapParent() };
        Check(picked.Contains(custom.Choose(new StrikeTargetQuery(), picked)), "the default choice is one of the candidates");
        Check(MissileDoctrine.Targets(null, null, null, _ => true).Count == 0, "no config, no targets");
    }
}
