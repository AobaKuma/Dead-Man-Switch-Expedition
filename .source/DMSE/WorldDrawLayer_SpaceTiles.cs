using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DMSE
{
    public class WorldDrawLayer_SpaceTiles : WorldDrawLayer_RaycastableGrid
    {
        public override bool VisibleInBackground
        {
            get
            {
                return false;
            }
        }
    }
}
