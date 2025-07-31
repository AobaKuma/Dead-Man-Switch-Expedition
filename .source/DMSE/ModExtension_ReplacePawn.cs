using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace DMSE
{
    public class ModExtension_ReplacePawn : DefModExtension
    {
        public Dictionary<FloatRange,PawnKindDef> replaces = new Dictionary<FloatRange, PawnKindDef> ();
    }
}
