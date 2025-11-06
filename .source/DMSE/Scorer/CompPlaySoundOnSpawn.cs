using RimWorld;
using Verse;
using Verse.Sound;

namespace DMSE
{
    public class CompProperties_PlaySoundOnSpawn : CompProperties
    {
        public SoundDef sound;

        public CompProperties_PlaySoundOnSpawn()
        {
            compClass = typeof(CompPlaySoundOnSpawn);
        }
    }
    public class CompPlaySoundOnSpawn : ThingComp
    {
        private CompProperties_PlaySoundOnSpawn Props => (CompProperties_PlaySoundOnSpawn)props;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (!respawningAfterLoad)
            {
                Props.sound.PlayOneShot(new TargetInfo(this.parent.DrawPos.ToIntVec3(), parent.Map));
            }
        }
    }
}
