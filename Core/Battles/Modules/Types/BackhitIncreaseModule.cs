using Vint.Core.Battles.Effects;
using Vint.Core.Battles.Modules.Interfaces;
using Vint.Core.Battles.Modules.Types.Base;
using Vint.Core.Battles.Player;
using Vint.Core.ECS.Components.Server.Effect;
using Vint.Core.ECS.Entities;
using Vint.Core.Utils;

namespace Vint.Core.Battles.Modules.Types;

public class BackhitIncreaseModule : PassiveBattleModule, IAlwaysActiveModule {
    public override string ConfigPath => "garage/module/upgrade/properties/backhitincrease";
    
    public override BackhitIncreaseEffect GetEffect() => new(Tank, Level, Multiplier);
    
    float Multiplier { get; set; }
    
    public override void Activate() {
        if (!CanBeActivated) return;
        
        BackhitIncreaseEffect? effect = Tank.Effects.OfType<BackhitIncreaseEffect>().SingleOrDefault();
        
        if (effect != null) return;
        
        GetEffect().Activate();
    }
    
    public override void Init(BattleTank tank, IEntity userSlot, IEntity marketModule) {
        base.Init(tank, userSlot, marketModule);
        
        Multiplier = Leveling.GetStat<ModuleBackhitModificatorEffectPropertyComponent>(ConfigPath, Level);
    }
}