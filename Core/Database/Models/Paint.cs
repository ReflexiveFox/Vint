﻿using LinqToDB.Mapping;

namespace Vint.Core.Database.Models;

[Table("Paints")]
public class Paint {
    [NotColumn] Player _player = null!;
    [PrimaryKey(1)] public long Id { get; init; }

    [Association(ThisKey = nameof(PlayerId), OtherKey = nameof(Player.Id))]
    public Player Player {
        get => _player;
        set {
            _player = value;
            PlayerId = value.Id;
        }
    }

    [PrimaryKey(0)] public long PlayerId { get; private set; }
}