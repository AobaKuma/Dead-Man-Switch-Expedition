using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Grammar;


static partial class Test
{
    // Missile content: default parts, jam/chaff aggregation, anti-ship terminal updates, altitude factors.
    static void MissileContent()
    {
        var body = new MissileBodyDef { defName = "ContentBody", terminalUpdates = 3 };
        body.slots.Add(MissilePartCategory.Warhead);
        body.slots.Add(MissilePartCategory.Guidance);
        body.slots.Add(MissilePartCategory.Payload);
        var warhead = new MissilePartDef { defName = "W", category = MissilePartCategory.Warhead };
        var guidance = new MissilePartDef { defName = "G", category = MissilePartCategory.Guidance };
        var payload = new MissilePartDef { defName = "P", category = MissilePartCategory.Payload, jamStrength = 0.35f, chaffCharges = 3 };
        var spare = new MissilePartDef { defName = "P2", category = MissilePartCategory.Payload, jamStrength = 0.2f, chaffCharges = 1 };
        body.defaultParts = new List<MissilePartDef> { warhead, guidance, payload, spare };

        var cfg = MissileConfig.NewWithDefaults(body);
        Check(cfg.parts.Count == 3, "default parts cover each slot once");
        Check(cfg.PartFor(MissilePartCategory.Warhead) == warhead && cfg.PartFor(MissilePartCategory.Payload) == payload,
            "the first part of a category wins");
        Near(cfg.JamStrength, 0.35f, "jam strength takes the strongest part");
        Check(cfg.ChaffCharges == 3, "default payload carries its own chaff charges");
        var twoPayloads = new MissileConfig(body);
        twoPayloads.parts.Add(payload);
        twoPayloads.parts.Add(spare);
        Check(twoPayloads.ChaffCharges == 4, "chaff charges add up across parts");

        spare.compatibleBodies = new List<MissileBodyDef> { new MissileBodyDef { defName = "OtherBody" } };
        var filtered = MissileConfig.NewWithDefaults(body);
        Check(filtered.PartFor(MissilePartCategory.Payload) == payload, "incompatible defaults are dropped");

        var antiShip = new MissilePartDef
        {
            defName = "AS",
            category = MissilePartCategory.Guidance,
            guidanceType = new GuidanceType_AntiShip(),
        };
        var armed = new MissileConfig(body);
        armed.parts.Add(antiShip);
        Check(armed.TracksMobileTargets, "an anti-ship seeker tracks mobile targets");
        Check(armed.TerminalUpdates == 5, "an anti-ship seeker adds two terminal updates");
        Check(!cfg.HasTerminalGuidance && cfg.TerminalUpdates == 0, "no seeker part means no terminal updates");

        Near(MissileWorldMotion.AltitudeFactor(1000f), 1f, "1000 m is neutral");
        Check(MissileWorldMotion.AltitudeFactor(100f) < MissileWorldMotion.AltitudeFactor(1000f), "low altitude shortens detection");
        Check(MissileWorldMotion.AltitudeFactor(100000f) > MissileWorldMotion.AltitudeFactor(1000f), "high altitude lengthens detection");
        Near(MissileWorldMotion.AltitudeFactor(1f), 0.6f, "low altitude clamp");
        Near(MissileWorldMotion.AltitudeFactor(1000000f), 1.3f, "high altitude clamp");

        var low = new BVRTargetProps { altitude = 100f };
        var high = new BVRTargetProps { altitude = 100000f };
        Check(MissileWorldMotion.DetectionRange(12, 2, low) < MissileWorldMotion.DetectionRange(12, 2, high),
            "altitude scales the world detection range");
    }

    // Skyfaller → Projectile migration (docs/Missile_Skyfaller轉Projectile_計畫.md §5): headings, edge points, skyfaller angle.
    static void MissileMapEntry()
    {
        foreach (var (h, x, z) in new[] { (0f, 0f, 1f), (90f, 1f, 0f), (180f, 0f, -1f), (270f, -1f, 0f) })
        {
            Vector3 v = MissileMapEntryUtility.HeadingVector(h);
            Near(v.x, x, $"heading {h} x"); Near(v.z, z, $"heading {h} z");
        }
        foreach (float h in new[] { 0f, 12.5f, 45f, 133f, 180f, 222f, 300f, 359f })
        {
            Near(MissileMapEntryUtility.HeadingOf(MissileMapEntryUtility.HeadingVector(h)), h, $"heading round trip {h}", 0.01f);

            // A skyfaller with the converted angle sits on the heading side of its target (it flies in from there).
            Vector3 center = new Vector3(100f, 0f, 100f);
            Vector3 drawn = SkyfallerDrawPosUtility.DrawPos_Accelerate(center, 100, MissileMapEntryUtility.SkyfallerAngleForHeading(h), 1f);
            Near(MissileMapEntryUtility.HeadingOf(drawn - center), h, $"skyfaller angle matches heading {h}", 0.01f);
        }

        IntVec3 size = new IntVec3(250, 1, 250);
        Vector3 mid = new Vector3(125f, 0f, 125f);
        Vector3 n = MissileMapEntryUtility.EdgePoint(size, mid, 0f);
        Check(Math.Abs(n.x - 125f) < 0.01f && Math.Abs(n.z - 250f) < 0.01f, "north ray exits the +Z edge above the start");
        Vector3 e = MissileMapEntryUtility.EdgePoint(size, mid, 90f);
        Check(Math.Abs(e.x - 250f) < 0.01f && Math.Abs(e.z - 125f) < 0.01f, "east ray exits the +X edge");
        Vector3 ne = MissileMapEntryUtility.EdgePoint(size, mid, 45f);
        Check(Math.Abs(ne.x - 250f) < 0.01f && Math.Abs(ne.z - 250f) < 0.01f, "45° ray exits the north-east corner, not the middle of a side");
        foreach (var (from, h) in new[] { (new Vector3(10f, 0f, 200f), 300f), (new Vector3(240f, 0f, 30f), 170f), (new Vector3(60f, 0f, 60f), 215f), (mid, 359.5f) })
        {
            Vector3 p = MissileMapEntryUtility.EdgePoint(size, from, h);
            bool onEdge = Math.Abs(p.x) < 0.01f || Math.Abs(p.x - 250f) < 0.01f || Math.Abs(p.z) < 0.01f || Math.Abs(p.z - 250f) < 0.01f;
            Check(onEdge, $"edge point {p} lies on the map border (heading {h})");
            Near(MissileMapEntryUtility.HeadingOf(p - from), h, $"edge point keeps the true heading {h}", 0.01f);
            Vector3 inside = MissileMapEntryUtility.InsetFromEdge(size, p);
            Check(inside.x >= 0.5f && inside.x <= 249.5f && inside.z >= 0.5f && inside.z <= 249.5f, "inset edge point is inside the map");
        }

        foreach (var (h, rot) in new[] { (44.9f, Rot4.North), (45f, Rot4.East), (134.9f, Rot4.East), (135f, Rot4.South),
                                          (224.9f, Rot4.South), (225f, Rot4.West), (314.9f, Rot4.West), (315f, Rot4.North), (-10f, Rot4.North) })
        {
            Check(MissileMapEntryUtility.EdgeRot(h) == rot, $"heading {h} maps to the {rot} edge like PlanetLayer.GetRotFromTo");
        }
    }

    // Departure turn (2026-10-26): rate-capped, ramps up, eases out, never overshoots; bigger turns take longer.
    static void DepartureTurn()
    {
        var f = new MissileFlightExtension();
        // The real cruise turn carries its turn rate between ticks (underdamped: it swings past the heading and settles back).
        (int reach, int settle, float overshoot, float maxRate) Turn(float from, float to, float speed = 0f)
        {
            float a = from, rate = 0f, overshoot = 0f, maxRate = 0f;
            int reach = -1, settle = -1;
            float sign = Mathf.Sign(Mathf.DeltaAngle(from, to));
            for (int t = 1; t < 2000; t++)
            {
                a = MissileDepartureKinematics.TurnCorrectionStep(f, a, to, t, speed, -1f, 0, ref rate);
                maxRate = Mathf.Max(maxRate, Mathf.Abs(rate));
                float err = Mathf.DeltaAngle(a, to);
                if (reach < 0 && err * sign <= 0f) { reach = t; }
                if (reach >= 0) { overshoot = Mathf.Max(overshoot, -err * sign); }
                if (Mathf.Abs(err) < 0.5f && Mathf.Abs(rate) < 0.05f) { if (settle < 0) { settle = t; } }
                else { settle = -1; }
            }
            return (reach, settle, overshoot, maxRate);
        }
        var small = Turn(0f, 20f); var right = Turn(0f, 90f); var wide = Turn(0f, -150f);
        Console.WriteLine($"  departure turn 20/90/150: reach {small.reach}/{right.reach}/{wide.reach}, settle {small.settle}/{right.settle}/{wide.settle}, "
            + $"overshoot {small.overshoot:0.#}/{right.overshoot:0.#}/{wide.overshoot:0.#}°, max rate {small.maxRate:0.##}/{right.maxRate:0.##}/{wide.maxRate:0.##}°/tick");
        Check(small.settle > 0 && right.settle > 0 && wide.settle > 0, "every turn settles on the heading");
        Check(small.reach < right.reach && right.reach < wide.reach, "bigger turns take longer to reach the heading");
        Check(Mathf.Max(small.maxRate, Mathf.Max(right.maxRate, wide.maxRate)) <= f.maxTurnRate + 1e-4f, "turn rate never exceeds maxTurnRate");
        Check(wide.overshoot > 0f && wide.overshoot < 45f, "the underdamped turn swings past the heading, but boundedly");
        Near(MissileDepartureKinematics.TurnStep(f, 0f, 90f, 1), f.maxTurnRate / f.turnRampTicks, "turn rate ramps up from zero");
        Check(MissileDepartureKinematics.TurnStep(f, 350f, 10f, 100) > 350f, "turns the short way across north");
        float fast = Turn(0f, 179f, 2f).maxRate;
        Check(fast > f.maxTurnRate && 2f / (fast * Mathf.Deg2Rad) >= f.maxTurnRadius - 0.01f,
            $"fast missiles may turn quicker than maxTurnRate, never tighter than maxTurnRadius ({fast:0.##}°/tick at 2 cells/tick)");
        var map = new IntVec3(250, 1, 250);
        Near(MissileDepartureKinematics.TurnRadiusAt(f, new Vector3(17f, 0f, 125f), 270f, map), 8.5f, "turn radius shrinks to half the room ahead (17 cells to the west edge)");
        Near(MissileDepartureKinematics.TurnRadiusAt(f, new Vector3(125f, 0f, 125f), 0f, map), f.maxTurnRadius, "open map: maxTurnRadius");
        Check(MissileDepartureKinematics.InwardSide(new Vector3(37f, 0f, 21f), 180f, map) == -1, "near the south-west corner heading south, the U-turn swings east (toward the centre)");
        Check(MissileDepartureKinematics.TurnStep(f, 180f, 0f, 100, 0f, -1f, -1) < 180f, "a U-turn follows the preferred side");
        var st = new MissileCruiseState { motion0 = 0f, motion = 45f, heading = 90f };
        Near(MissileDepartureKinematics.TurnBlend(st), 0.5f, "turn blend = share of the turn done");
    }

    static void InertialDistribution()
    {
        Rand.PushState(19871022);
        try
        {
            foreach (IntVec3 size in new[] { new IntVec3(301, 1, 301), new IntVec3(41, 1, 201) })
            {
                int cx = size.x / 2, cz = size.z / 2;
                int radius = Math.Min(Math.Min(cx, size.x - 1 - cx), Math.Min(cz, size.z - 1 - cz));
                double sx = 0, sz = 0, square = 0;
                int samples = 12000, inner = 0;
                for (int i = 0; i < samples; i++)
                {
                    IntVec3 cell = GuidanceType.InertialTarget(size);
                    int x = cell.x - cx, z = cell.z - cz, r2 = x * x + z * z;
                    if (cell.x < 0 || cell.x >= size.x || cell.z < 0 || cell.z >= size.z || r2 > radius * radius)
                        throw new Exception("inertial sample escaped the map's inscribed circle");
                    sx += x; sz += z; square += r2;
                    if (r2 <= radius * radius / 4f) inner++;
                }
                Check(Math.Abs(sx / samples) < 1.5 && Math.Abs(sz / samples) < 1.5,
                    "inertial distribution is centered on square and rectangular maps");
                double normalizedVariance = square / samples / (radius * radius);
                Check(normalizedVariance > 0.19 && normalizedVariance < 0.24,
                    "inertial variance matches a circularly truncated Gaussian, not uniform scatter");
                Check(inner > samples * 0.65 && inner < samples * 0.72,
                    "inertial samples concentrate near the center");
            }
            Check(GuidanceType.InertialTarget(new IntVec3(1, 1, 1)) == IntVec3.Zero,
                "single-cell map has a valid inertial target");
            Check(!new GuidanceType_Inertial().TerminalGuidance && new GuidanceType_ActiveRadar().TerminalGuidance,
                "active radar provides terminal guidance; inertial does not");
        }
        finally { Rand.PopState(); }
    }

    static void ShahedGuidanceOptions()
    {
        // 彈體的預設零件可能來自其他檔案（共用部件，如 DMSE_Payload_None），所以掃整個 Missile 目錄。
        XElement[] all = Directory.GetFiles("1.7/Defs/Missile", "*.xml")
            .SelectMany(f => XDocument.Load(f).Root.Elements()).ToArray();
        XElement Node(string name) => all.Single(x => (string)x.Element("defName") == name);
        XElement bodyXml = Node("DMSE_MissileBody_Shahed");
        var body = new MissileBodyDef
        {
            defName = (string)bodyXml.Element("defName"),
            sizeClass = Enum.Parse<MissileSizeClass>((string)bodyXml.Element("sizeClass")),
            builtInGuidance = (GuidanceType)Activator.CreateInstance(typeof(GuidanceType).Assembly.GetType(
                (string)bodyXml.Element("builtInGuidance").Attribute("Class"), true)),
            terminalUpdates = (int)bodyXml.Element("terminalUpdates"),
            slots = bodyXml.Element("slots").Elements("li").Select(x => Enum.Parse<MissilePartCategory>(x.Value)).ToList()
        };
        var advanced = new ThingDef { defName = "ComponentSpacer" };
        MissilePartDef Part(string name)
        {
            XElement xml = Node(name);
            var part = new MissilePartDef
            {
                defName = name,
                category = Enum.Parse<MissilePartCategory>((string)xml.Element("category")),
                compatibleBodies = new List<MissileBodyDef> { body }
            };
            if (xml.Element("guidanceType") != null)
            {
                part.guidanceType = (GuidanceType)Activator.CreateInstance(typeof(GuidanceType).Assembly.GetType(
                    (string)xml.Element("guidanceType").Attribute("Class"), true));
            }
            if (xml.Element("costList") != null)
            {
                XElement cost = xml.Element("costList").Elements().Single();
                Check(cost.Name.LocalName == advanced.defName, "Shahed TV upgrade uses an advanced component");
                part.costList = new List<ThingDefCountClass> { new ThingDefCountClass(advanced, (int)cost) };
            }
            return part;
        }
        body.defaultParts = bodyXml.Element("defaultParts").Elements("li").Select(x => Part(x.Value)).ToList();
        MissileConfig cfg = MissileConfig.NewWithDefaults(body);
        Check(cfg.TargetingGuidance is GuidanceType_Inertial && !cfg.PlayerChoosesTarget && !cfg.HasTerminalGuidance,
            "manufactured Shahed starts with inertial guidance, not free TV");
        Check(cfg.StrikeChannelCost == 0, "inertial Shahed books no guidance channel");
        var pending = cfg.Clone();
        pending.SetPart(MissilePartCategory.Guidance, Part("DMSE_Shahed_Guidance_TV"));
        var comp = new CompMissileConfig { config = cfg, pending = pending };
        var required = comp.TotalRequired();
        Check(required.Count == 1 && required.TryGetValue(advanced, out int amount) && amount == 1,
            "switching Shahed to TV requires exactly one advanced component");
        Check(pending.PlayerChoosesTarget && pending.StrikeChannelCost == 0,
            "TV Shahed permits manual selection without booking a guidance channel");
        Check(pending.TerminalUpdates == 0 && !pending.TracksMobileTargets,
            "TV upgrade does not give Shahed moving-base tracking");
        pending.SetPart(MissilePartCategory.Guidance, null);
        Check(pending.TargetingGuidance is GuidanceType_Inertial && !pending.PlayerChoosesTarget && pending.StrikeChannelCost == 0,
            "removing the TV module restores inertial targeting");
    }

    static void ShortBoosterEffects()
    {
        XElement launch = XDocument.Load("1.7/Defs/Missile/DMSE_MissileLaunchProjectiles.xml").Root.Elements()
            .Single(x => (string)x.Element("defName") == "DMSE_Launch_Shahed");
        XElement effect = launch.Element("comps").Elements("li")
            .Single(x => (string)x.Attribute("Class") == "DMSE.CompProperties_EffecterHotLaunch");
        var props = new CompProperties_EffecterHotLaunch
        {
            burnDurationTicks = (int)effect.Element("burnDurationTicks"),
            effectScale = (float)effect.Element("effectScale")
        };
        Check(props.IsBurning(0, 0) && props.IsBurning(29, 0) && !props.IsBurning(30, 0)
            && !props.IsBurning(300, 0), "short booster ends after 30 ticks, including cruise");
        Check(!props.IsBurning(9, 10) && props.IsBurning(10, 10) && props.IsBurning(39, 10)
            && !props.IsBurning(40, 10), "booster duration starts at ignition, respecting cold-launch delay");
        Near(props.effectScale, 0.35f, "long-range loitering munition effect scale");
        var unchanged = new CompProperties_EffecterHotLaunch();
        Check(unchanged.IsBurning(3000, 0) && unchanged.effectScale == 1f,
            "other missiles retain unlimited exhaust and original effect size");
        props.burnDurationTicks = 0;
        Check(!props.IsBurning(0, 0), "zero-duration booster is disabled");
    }
}
