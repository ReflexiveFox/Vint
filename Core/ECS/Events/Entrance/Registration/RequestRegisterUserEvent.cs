﻿using Vint.Core.Database;
using Vint.Core.ECS.Entities;
using Vint.Core.Protocol.Attributes;
using Vint.Core.Server;

namespace Vint.Core.ECS.Events.Entrance.Registration;

[ProtocolId(1438590245672)]
public class RequestRegisterUserEvent : IServerEvent {
    const int MaxRegistrationsFromOneComputer = 2;

    [ProtocolName("uid")] public string Username { get; private set; } = null!;
    public string EncryptedPasswordDigest { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string HardwareFingerprint { get; private set; } = null!;
    public bool Subscribed { get; private set; }
    public bool Steam { get; private set; }
    public bool QuickRegistration { get; private set; }

    public void Execute(IPlayerConnection connection, IEnumerable<IEntity> entities) {
        using (DbConnection database = new()) {
            if (database.Players.Any(player => player.Username == Username) ||
                database.Players.Count(player => player.HardwareFingerprint == HardwareFingerprint) >= MaxRegistrationsFromOneComputer) {
                connection.Send(new RegistrationFailedEvent());
                return;
            }
        }

        connection.Register(
            Username,
            EncryptedPasswordDigest,
            Email,
            HardwareFingerprint,
            Subscribed,
            Steam,
            QuickRegistration);
    }
}