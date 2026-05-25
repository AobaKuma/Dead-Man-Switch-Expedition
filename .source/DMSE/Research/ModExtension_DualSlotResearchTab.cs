using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DMSE
{
    public class ModExtension_UniqueResearchTab : DefModExtension
    {
        // The list of KnowledgeCategoryDefs to display as vertical columns (left-to-right)
        public List<KnowledgeCategoryDef> categories = new List<KnowledgeCategoryDef>();
    }
}