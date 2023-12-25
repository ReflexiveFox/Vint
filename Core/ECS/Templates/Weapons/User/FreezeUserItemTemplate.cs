﻿using Vint.Core.ECS.Templates.Weapons.Market;
using Vint.Core.Protocol.Attributes;

namespace Vint.Core.ECS.Templates.Weapons.User;

[ProtocolId(1433406804439)]
public class FreezeUserItemTemplate : UserEntityTemplate {
    public override MarketEntityTemplate MarketTemplate => new FreezeMarketItemTemplate();
}