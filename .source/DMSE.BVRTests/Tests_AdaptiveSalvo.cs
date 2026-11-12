using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld.Planet;

static partial class Test
{
    // NPC world salvo scaled by the target's air defense: E = min(channels, launchers), sublinear count, tiered composition.
    static void AdaptiveSalvo()
    {
        // Assessment: the bottleneck counts, not the sum.
        Check(new AirDefenseAssessment(10, 0).Effective == 0, "channels without launchers are no defense");
        Check(new AirDefenseAssessment(0, 8).Effective == 0, "launchers without channels are no defense");
        Check(new AirDefenseAssessment(4, 9).Effective == 4, "E is the smaller of the two");
        Check(new AirDefenseAssessment(-3, 2).Effective == 0, "negative inputs clamp to zero");
        Check(AirDefenseAssessment.Of((MapParent)null).Effective == 0, "no target, no defense");

        // Count: base when undefended, grows sublinearly, never above the cap.
        var props = new AdaptiveSalvoProps { alpha = 0.8f, exponent = 0.7f, maxPerSalvo = 6 };
        Check(props.Count(2, 0, 1f) == 2, "an undefended target gets the base salvo");
        Check(props.Count(0, 0, 1f) == 1, "the base salvo is at least one");
        int[] counts = Enumerable.Range(0, 30).Select(e => props.Count(2, e, 1f)).ToArray();
        Check(counts.Zip(counts.Skip(1), (a, b) => b >= a).All(x => x), "more defense never means fewer missiles");
        Check(counts.Max() == 6, "the salvo stops at maxPerSalvo");
        Check(props.Count(2, 4, 1f) == 2 + 2, "E=4: 0.8 * 4^0.7 = 2.1 extra");
        Check(props.Count(2, 4, 1f) - props.Count(2, 0, 1f) > props.Count(2, 8, 1f) - props.Count(2, 4, 1f),
            "doubling the defense adds fewer missiles than the first steps (sublinear)");
        Check(props.Count(2, 4, 0f) == 2, "zero difficulty adds nothing");
        Check(props.Count(2, 4, 2f) > props.Count(2, 4, 1f), "a higher threat scale adds missiles");
        Check(new AdaptiveSalvoProps { maxPerSalvo = 1 }.Count(3, 20, 1f) == 3, "a cap below the base keeps the base");

        // Tiers: highest eligible tier first, each rolled; none eligible or none hit means the base config.
        var decoy = new AdaptiveSalvoTier { minEffective = 2, chance = 0.4f };
        var cruise = new AdaptiveSalvoTier { minEffective = 5, chance = 0.35f };
        var tiered = new AdaptiveSalvoProps { tiers = new List<AdaptiveSalvoTier> { decoy, cruise } };
        Check(tiered.PickTier(1, _ => true) == null, "below every threshold, no tier");
        Check(tiered.PickTier(3, _ => true) == decoy, "E=3 only reaches the decoy tier");
        Check(tiered.PickTier(6, _ => true) == cruise, "the highest eligible tier is rolled first");
        Check(tiered.PickTier(6, c => c > 0.38f) == decoy, "a missed high roll falls to the next tier");
        Check(tiered.PickTier(6, _ => false) == null, "missing every roll keeps the base missile");
        var rolled = new List<float>();
        tiered.PickTier(6, c => { rolled.Add(c); return false; });
        Check(rolled.SequenceEqual(new[] { 0.35f, 0.4f }), "each eligible tier is rolled once, high to low");
        Check(new AdaptiveSalvoProps().PickTier(10, _ => true) == null, "no tiers, no change");

        // Config errors.
        Check(tiered.ConfigErrors("T").Any(),"a tier that changes nothing is an error");
        var good = new AdaptiveSalvoProps { tiers = new List<AdaptiveSalvoTier> { new AdaptiveSalvoTier { missileBody = new MissileBodyDef { defName = "TestAdaptiveBody" } } } };
        Check(!good.ConfigErrors("T").Any(), "a tier with a body is valid");
        Check(new AdaptiveSalvoProps { exponent = 0f }.ConfigErrors("T").Any(), "a non-positive exponent is an error");
        var emptyOption = new AdaptiveSalvoProps { tiers = new List<AdaptiveSalvoTier> { new AdaptiveSalvoTier { partOptions = new List<MissileBasePartOption> { new MissileBasePartOption() } } } };
        Check(emptyOption.ConfigErrors("T").Any(e => e.Contains("no parts")), "an empty part option is an error");
    }
}
