using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DMSE;
using RimWorld;
using Verse;


static partial class Test
{
    static string Str(object obj, string name)
        => (string)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(obj);

    // Missile info cards: every entry is translated, placeholders are filled, and N-scaled effect text
    // matches the numbers the effects actually use.
    static void InfoCard()
    {
        // Vanilla keys the cards use; the fixture only loads DMSE's own Keyed files.
        foreach (var (key, value) in new[] { ("Yes", "Yes"), ("No", "No"),
            ("Period1Second", "1 second"), ("Period1Hour", "1 hour"), ("PeriodHours", "{0} hours"),
            ("Period1Day", "1 day"), ("PeriodDays", "{0} days") })
        {
            LanguageDatabase.activeLanguage.keyedReplacements[key] = new LoadedLanguage.KeyedReplacement { key = key, value = value };
        }
        var bomb = new DamageDef { defName = "Test_Card_Bomb", label = "bomb" };
        var small = new MissileBodyDef
        {
            baseDamageDef = bomb,
            defName = "Test_Card_Small", label = "small body", payloadCapacity = 5f, baseFuel = 100f,
            baseSpecificImpulse = 3f, rangePerFuelImpulse = 0.1f, terminalUpdates = 2, interceptHitFactor = 1.2f,
            slots = new List<MissilePartCategory> { MissilePartCategory.Warhead, MissilePartCategory.Guidance,
                MissilePartCategory.Propulsion, MissilePartCategory.Payload }
        };
        var big = new MissileBodyDef
        {
            defName = "Test_Card_Big", label = "big body", baseDamageDef = bomb, payloadCapacity = 8f, sizeClass = MissileSizeClass.Ballistic,
            launchMode = MissileLaunchMode.Vertical, customizeLocation = MissileCustomizeLocation.Launcher,
            slots = new List<MissilePartCategory> { MissilePartCategory.Warhead, MissilePartCategory.Payload }
        };
        var steel = new ThingDef { defName = "Test_Card_Steel", label = "steel" };
        MissilePartDef Part(string name, MissilePartCategory cat) => new MissilePartDef
        {
            defName = name, label = name, category = cat, description = name + " desc",
            costList = new List<ThingDefCountClass> { new ThingDefCountClass(steel, 10) }
        };
        MissilePartDef incendiary = Part("Test_Card_Incendiary", MissilePartCategory.Warhead);
        incendiary.warheadEffect = new WarheadEffect_Incendiary();
        incendiary.damageAmountOffset = 40;
        MissilePartDef airburst = Part("Test_Card_Airburst", MissilePartCategory.Warhead);
        airburst.warheadEffect = new WarheadEffect_Airburst();
        airburst.compatibleBodies = new List<MissileBodyDef> { small };
        MissilePartDef antiShip = Part("Test_Card_AntiShip", MissilePartCategory.Guidance);
        antiShip.guidanceType = new GuidanceType_AntiShip();
        antiShip.scatterOffset = -2f;
        MissilePartDef radio = Part("Test_Card_Radio", MissilePartCategory.Guidance);
        radio.guidanceType = new GuidanceType_RadioCommand();
        MissilePartDef booster = Part("Test_Card_Booster", MissilePartCategory.Propulsion);
        booster.fuelOffset = 50f;
        booster.specificImpulseOffset = 1f;
        booster.chaffCharges = 3;
        booster.jamStrength = 0.6f;
        booster.signatureFactor = 0.5f;
        MissilePartDef bunker = Part("Test_Card_Bunker", MissilePartCategory.Payload);
        bunker.payloadEffect = new PayloadEffect_BunkerBuster();
        MissilePartDef tungsten = Part("Test_Card_Tungsten", MissilePartCategory.Payload);
        tungsten.payloadEffect = new PayloadEffect_AirburstTungsten();
        tungsten.payloadSpawnThing = steel;
        tungsten.payloadSpawnCount = 2;
        MissilePartDef loiter = Part("Test_Card_Loiter", MissilePartCategory.Payload);
        loiter.loiterTicks = 2500;
        loiter.requiresLoiteringBody = true;
        MissilePartDef[] parts = { incendiary, airburst, antiShip, radio, booster, bunker, tungsten, loiter };
        small.defaultParts = new List<MissilePartDef> { incendiary, antiShip, booster, tungsten };

        DefDatabase<ThingDef>.Add(steel);
        DefDatabase<MissileBodyDef>.Add(small);
        DefDatabase<MissileBodyDef>.Add(big);
        foreach (MissilePartDef p in parts) { DefDatabase<MissilePartDef>.Add(p); }

        var rawKey = new Regex(@"DMSE\.[A-Za-z]+\.[A-Za-z.]+");
        var placeholder = new Regex(@"\{\d+\}");
        List<(string label, string value, string report)> Texts(IEnumerable<StatDrawEntry> entries)
            => entries.Select(e => (Str(e, "labelInt"), Str(e, "valueStringInt"), Str(e, "overrideReportText"))).ToList();
        void CheckClean(string what, List<(string label, string value, string report)> texts)
        {
            Check(texts.Count > 0, what + ": has entries");
            foreach (var (label, value, report) in texts)
            {
                foreach (string s in new[] { label, value, report })
                {
                    if (s == null) { continue; }
                    Check(!rawKey.IsMatch(s), $"{what}: no raw key in '{s}'");
                    Check(!placeholder.IsMatch(s), $"{what}: no unfilled placeholder in '{s}'");
                }
            }
        }
        string Report(List<(string label, string value, string report)> texts, string labelKey)
            => texts.FirstOrDefault(t => t.label == labelKey.Translate().ToString()).report;
        string Value(List<(string label, string value, string report)> texts, string labelKey)
            => texts.FirstOrDefault(t => t.label == labelKey.Translate().ToString()).value;

        foreach (MissilePartDef p in parts) { CheckClean(p.defName, Texts(MissileInfoCardUtility.PartStats(p))); }
        CheckClean("small body", Texts(MissileInfoCardUtility.BodyStats(small)));
        CheckClean("big body", Texts(MissileInfoCardUtility.BodyStats(big)));

        // Incendiary fits both bodies: the report lists the radius for each N (N cells).
        var inc = Texts(MissileInfoCardUtility.PartStats(incendiary));
        string incEffect = Report(inc, "DMSE.Card.WarheadEffect");
        Check(incEffect != null && incEffect.Contains("within 5 cells") && incEffect.Contains("within 8 cells"),
            "incendiary card lists radius per N: " + incEffect);
        Check(Value(inc, "DMSE.Card.WarheadEffect") == "N = 5~8", "incendiary N range value");
        Check(Value(inc, "DMSE.UI.DamageChange") == "+40", "damage modifier shown with sign");

        // Airburst only fits the small body: single N, extra intercept rolls floor(5/5) = 1.
        string air = Report(Texts(MissileInfoCardUtility.PartStats(airburst)), "DMSE.Card.WarheadEffect");
        Check(air != null && air.Contains("1 extra independent intercept rolls") && air.Contains("N = 5"), "airburst card: " + air);

        // Guidance cards: anti-ship +2 updates, radio command books a channel.
        var ship = Texts(MissileInfoCardUtility.PartStats(antiShip));
        Check(Value(ship, "DMSE.Bench.Stat.TerminalUpdates") == "+2", "anti-ship extra terminal updates");
        Check(Value(ship, "DMSE.Card.TerminalGuidance") == "Yes".Translate().ToString(), "anti-ship provides guidance");
        Check(Value(Texts(MissileInfoCardUtility.PartStats(radio)), "DMSE.Bench.Stat.Channels") != null, "radio command shows channel use");

        // Configured missile: effective values equal MissileConfig.
        MissileConfig cfg = MissileConfig.NewWithDefaults(small);
        var stats = Texts(MissileInfoCardUtility.MissileStats(cfg, null));
        CheckClean("configured missile", stats);
        Check(Value(stats, "DMSE.UI.DamageChange").StartsWith(cfg.DamageAmount.ToString()), "missile damage matches config");
        Check(Value(stats, "DMSE.UI.RangeChange") == "DMSE.Card.RangeTiles".Translate(cfg.Range).ToString(), "missile range matches config");
        Check(Value(stats, "DMSE.Bench.Stat.TerminalUpdates") == cfg.TerminalUpdates.ToString() && cfg.TerminalUpdates == 4,
            "missile terminal updates include seeker bonus");
        string range = Report(stats, "DMSE.UI.RangeChange");
        Check(range.Contains("Fuel: 150") && range.Contains("Specific impulse: 4") && range.Contains("75%"), "range breakdown: " + range);
        Check(Value(stats, "DMSE.Card.Intercept") != null, "intercept factor shown when not 1");
        Check(Report(stats, "DMSE.Bench.Stat.Chaff")?.Contains("30%") == true, "chaff break chance = 0.5 × 0.6");
        string payload = Report(stats, "DMSE.Card.PayloadEffect");
        Check(payload != null && payload.Contains("2 × ") && payload.Contains("within radius 5"), "payload effect text: " + payload);

        // Loiter part on a non-loitering body is not reported on the missile, only on the part card.
        MissileConfig loiterCfg = MissileConfig.NewWithDefaults(small);
        loiterCfg.SetPart(MissilePartCategory.Payload, loiter);
        Check(Value(Texts(MissileInfoCardUtility.MissileStats(loiterCfg, null)), "DMSE.Bench.Stat.Loiter") == null,
            "loiter not shown for non-loitering body");
        Check(Value(Texts(MissileInfoCardUtility.PartStats(loiter)), "DMSE.Bench.Stat.Loiter") != null, "loiter shown on part card");

        // Every DMSE.Card key used by code exists in all three languages with the same placeholders.
        foreach (string lang in new[] { "English", "ChineseTraditional (繁體中文)", "ChineseSimplified (简体中文)" })
        {
            var doc = System.Xml.Linq.XDocument.Load($"1.7/Languages/{lang}/Keyed/DMSE_MissileInfoCard.xml");
            var en = System.Xml.Linq.XDocument.Load("1.7/Languages/English/Keyed/DMSE_MissileInfoCard.xml")
                .Root.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
            var here = doc.Root.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
            Check(here.Keys.OrderBy(k => k).SequenceEqual(en.Keys.OrderBy(k => k)), lang + ": same key set as English");
            foreach (var kv in en)
            {
                if (!here.TryGetValue(kv.Key, out string v)) { continue; }
                var a = placeholder.Matches(kv.Value).Select(m => m.Value).Distinct().OrderBy(x => x);
                var b = placeholder.Matches(v).Select(m => m.Value).Distinct().OrderBy(x => x);
                Check(a.SequenceEqual(b), $"{lang}: placeholders match for {kv.Key}");
            }
        }
    }
}
