using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace DMSE
{
    public class GameComponent_MissileEngage : GameComponent
    {
        public GameComponent_MissileEngage(Game game)  { }
        public static GameComponent_MissileEngage Comp 
        {
            get 
            {
                if (comp == null) 
                {
                    comp = Current.Game.GetComponent<GameComponent_MissileEngage>();
                }
                return comp;
            }
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            this.times.RemoveAll(t =>
            { 
                return Find.TickManager.TicksGame >= t.Value
                ||
                t.Key == null || t.Key.Parent.Destroyed;
                }
            );

        }
        public override void ExposeData()
        {
            base.ExposeData(); 
            Scribe_Collections.Look(ref this.times, "times", LookMode.Reference, LookMode.Value,
                ref this.tmp_maps, ref this.tmp_times);
            comp = null;
        }


        public Dictionary<Map,int> times = new Dictionary<Map,int>();
        List<Map> tmp_maps;
        List<int> tmp_times;

        static GameComponent_MissileEngage comp;
    }
}
