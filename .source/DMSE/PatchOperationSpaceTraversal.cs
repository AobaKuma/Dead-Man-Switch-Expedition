using System.Xml;
using Verse;

namespace DMSE
{
    // XML patches run after Mod constructors have loaded the saved settings.
    public class PatchOperationSpaceTraversal : PatchOperation
    {
        public PatchOperation operation;

        protected override bool ApplyWorker(XmlDocument xml)
        {
            if (DMSEMod.Settings?.allowSpaceTraversal != true)
                return true;

            return operation != null && operation.Apply(xml);
        }
    }
}
