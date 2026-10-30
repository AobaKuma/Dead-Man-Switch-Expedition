using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Verse;

namespace DMSE
{
    /// <summary>
    /// 一份裝配方案：彈體＋部件＋名稱，存成 Config 目錄下的一個 XML 檔（跨存檔、可分享）。
    /// 檔案只存 defName 字串，載入時再解析：Def 被移除或換了模組組合時該筆停用，不會壞掉其他方案。
    /// </summary>
    public class MissileDesignBlueprint
    {
        public const int CurrentVersion = 1;

        public int version = CurrentVersion;
        public string name;
        public bool nameLocked;
        public string ruleString;
        public string bodyDefName;
        public List<string> partDefNames = new List<string>();
        public string note;
        public string savedByModVersion;

        /// <summary>載入來源檔；刪除／覆蓋用。</summary>
        public string filePath;

        /// <summary>解析失敗的原因；null＝可用。</summary>
        public string unusableReason;

        public bool Usable => unusableReason == null;

        /// <summary>解析成構型；任何 defName 解不到、或部件與彈體不相容 → null，並填 <see cref="unusableReason"/>。</summary>
        public MissileConfig ToConfig()
        {
            unusableReason = null;
            MissileBodyDef body = DefDatabase<MissileBodyDef>.GetNamedSilentFail(bodyDefName ?? string.Empty);
            if (body == null)
            {
                unusableReason = "DMSE.Blueprint.MissingDef".Translate(bodyDefName ?? "?");
                return null;
            }
            MissileConfig cfg = new MissileConfig(body);
            foreach (string pn in partDefNames)
            {
                MissilePartDef part = DefDatabase<MissilePartDef>.GetNamedSilentFail(pn ?? string.Empty);
                if (part == null)
                {
                    unusableReason = "DMSE.Blueprint.MissingDef".Translate(pn ?? "?");
                    return null;
                }
                if (!part.CompatibleWith(body) || cfg.PartFor(part.category) != null)
                {
                    unusableReason = "DMSE.Blueprint.Incompatible".Translate(part.LabelCap);
                    return null;
                }
                cfg.parts.Add(part);
            }
            return cfg;
        }

        public static MissileDesignBlueprint From(MissileConfig cfg, string name, bool nameLocked, string ruleString)
        {
            return new MissileDesignBlueprint
            {
                name = name,
                nameLocked = nameLocked,
                ruleString = ruleString,
                bodyDefName = cfg?.body?.defName,
                partDefNames = cfg?.parts?.Where(p => p != null).Select(p => p.defName).ToList() ?? new List<string>(),
                savedByModVersion = DMSEModVersion
            };
        }

        private static string DMSEModVersion
        {
            get
            {
                ModContentPack pack = LoadedModManager.GetMod<DMSEMod>()?.Content;
                return pack?.ModMetaData?.ModVersion ?? "1.7-dev";
            }
        }

        // ---------------- XML ----------------

        public XDocument ToXml()
        {
            XElement root = new XElement("MissileDesignBlueprint",
                new XElement("version", version),
                new XElement("name", name ?? string.Empty),
                new XElement("nameLocked", nameLocked ? "true" : "false"),
                new XElement("ruleString", ruleString ?? string.Empty),
                new XElement("bodyDefName", bodyDefName ?? string.Empty),
                new XElement("partDefNames", partDefNames.Select(p => new XElement("li", p))));
            if (!note.NullOrEmpty()) { root.Add(new XElement("note", note)); }
            root.Add(new XElement("savedByModVersion", savedByModVersion ?? string.Empty));
            return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        }

        /// <summary>讀一個方案檔；XML 本身壞掉時回傳 null（呼叫端記錄警告）。</summary>
        public static MissileDesignBlueprint FromXml(string path)
        {
            XElement root = XDocument.Load(path).Root;
            if (root == null || root.Name.LocalName != "MissileDesignBlueprint") { return null; }
            MissileDesignBlueprint bp = new MissileDesignBlueprint
            {
                filePath = path,
                version = int.TryParse((string)root.Element("version"), out int v) ? v : CurrentVersion,
                name = (string)root.Element("name"),
                nameLocked = string.Equals((string)root.Element("nameLocked"), "true", StringComparison.OrdinalIgnoreCase),
                ruleString = (string)root.Element("ruleString"),
                bodyDefName = (string)root.Element("bodyDefName"),
                partDefNames = root.Element("partDefNames")?.Elements("li").Select(e => e.Value.Trim()).Where(s => s.Length > 0).ToList()
                               ?? new List<string>(),
                note = (string)root.Element("note"),
                savedByModVersion = (string)root.Element("savedByModVersion")
            };
            bp.ToConfig();   // 填 unusableReason
            return bp;
        }
    }

    /// <summary>
    /// 裝配方案庫：<c>Config/DMSE_MissileDesigns/*.xml</c>，一方案一檔。不進存檔，換存檔、開新局都在。
    /// 延遲到第一次讀取時才載入（此時 DefDatabase 已就緒）。
    /// </summary>
    public static class MissileDesignBlueprintDatabase
    {
        private static List<MissileDesignBlueprint> all;

        public static string FolderPath => Path.Combine(GenFilePaths.ConfigFolderPath, "DMSE_MissileDesigns");

        public static IReadOnlyList<MissileDesignBlueprint> All
        {
            get
            {
                if (all == null) { Reload(); }
                return all;
            }
        }

        public static void Reload()
        {
            all = new List<MissileDesignBlueprint>();
            if (!Directory.Exists(FolderPath)) { return; }
            foreach (string path in Directory.GetFiles(FolderPath, "*.xml"))
            {
                try
                {
                    MissileDesignBlueprint bp = MissileDesignBlueprint.FromXml(path);
                    if (bp != null) { all.Add(bp); }
                    else { Log.Warning("[DMSE] Skipped missile design file (not a MissileDesignBlueprint): " + path); }
                }
                catch (Exception ex)
                {
                    Log.Warning("[DMSE] Skipped unreadable missile design file " + path + ": " + ex.Message);
                }
            }
            all.SortBy(b => b.name ?? string.Empty);
        }

        public static MissileDesignBlueprint FindByName(string name)
            => name.NullOrEmpty() ? null : All.FirstOrDefault(b => b.name == name);

        /// <summary>寫入方案；同名者覆蓋原檔（呼叫端先確認）。</summary>
        public static bool Save(MissileDesignBlueprint bp, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(FolderPath);
                MissileDesignBlueprint existing = FindByName(bp.name);
                string path = existing?.filePath ?? NewFilePath(bp.name);
                bp.ToXml().Save(path);
                bp.filePath = path;
                Reload();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Warning("[DMSE] Failed to save missile design: " + ex);
                return false;
            }
        }

        public static bool Delete(MissileDesignBlueprint bp)
        {
            if (bp?.filePath == null) { return false; }
            try
            {
                if (File.Exists(bp.filePath)) { File.Delete(bp.filePath); }
                Reload();
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[DMSE] Failed to delete missile design " + bp.filePath + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>{安全檔名}_{短雜湊}.xml：可單獨刪除／分享，壞檔只壞一筆。</summary>
        private static string NewFilePath(string name)
        {
            string safe = GenFile.SanitizedFileName(name.NullOrEmpty() ? "design" : name);
            if (safe.Length > 40) { safe = safe.Substring(0, 40); }
            string hash = ((uint)(name ?? string.Empty).GetHashCode() ^ (uint)DateTime.UtcNow.Ticks).ToString("x8").Substring(0, 6);
            return Path.Combine(FolderPath, safe + "_" + hash + ".xml");
        }
    }
}
