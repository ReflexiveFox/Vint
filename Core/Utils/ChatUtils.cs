﻿using Vint.Core.Database;
using Vint.Core.Database.Models;
using Vint.Core.ECS.Components.Chat;
using Vint.Core.ECS.Components.User;
using Vint.Core.ECS.Entities;
using Vint.Core.ECS.Events.Chat;
using Vint.Core.ECS.Templates.Chat;
using Vint.Core.Server;

namespace Vint.Core.Utils;

public static class ChatUtils {
    public static Dictionary<string, Dictionary<string, string>> Localization { get; } = new() { // hardcoded, todo parse from configs
        {
            "RU", new Dictionary<string, string> {
                { "SystemUsername", "Системное сообщение" },
                { "BlockedUsername", "Заблокированный игрок" },
                { "BlockedMessage", "Заблокировано" }
            }
        }, {
            "EN", new Dictionary<string, string> {
                { "SystemUsername", "System message" },
                { "BlockedUsername", "Blocked player" },
                { "BlockedMessage", "Blocked" }
            }
        }
    };

    public static ChatMessageReceivedEvent CreateMessageEvent(string message, IPlayerConnection receiver, IPlayerConnection? sender) {
        string receiverLocale = receiver.Player.CountryCode.ToUpper() switch {
            "RU" => "RU",
            "EN" => "EN",
            _ => "EN"
        };

        Dictionary<string, string> localizedStrings = Localization[receiverLocale];

        bool isSystem = sender == null;

        using DbConnection db = new();

        bool isBlocked = !isSystem &&
                         db.Relations.SingleOrDefault(relation => relation.SourcePlayerId == receiver.Player.Id &&
                                                                  relation.TargetPlayerId == sender!.Player.Id &&
                                                                  (relation.Types & RelationTypes.Blocked) == RelationTypes.Blocked) !=
                         null;

        long userId = isSystem ? 0 : sender!.Player.Id;
        string avatarId = isSystem ? "" : sender!.User.GetComponent<UserAvatarComponent>().Id;

        string username = isSystem ? localizedStrings["SystemUsername"]
                          : isBlocked ? localizedStrings["BlockedUsername"]
                          : sender!.Player.Username;

        message = isBlocked ? localizedStrings["BlockedMessage"] : message;

        return new ChatMessageReceivedEvent(username, message, userId, avatarId, isSystem);
    }

    public static void SendMessage(string message, IEntity chat, IEnumerable<IPlayerConnection> receivers, IPlayerConnection? sender) {
        foreach (IPlayerConnection receiver in receivers)
            receiver.Send(CreateMessageEvent(message, receiver, sender), chat);
    }

    // todo
    public static IEnumerable<IPlayerConnection> GetReceivers(IPlayerConnection from, IEntity chat) => chat.TemplateAccessor?.Template switch {
        GeneralChatTemplate => from.Server.PlayerConnections,

        BattleLobbyChatTemplate => from.BattlePlayer!.Battle.Players
            .Select(battlePlayer => battlePlayer.PlayerConnection),

        GeneralBattleChatTemplate => from.BattlePlayer!.Battle.Players
            .Where(battlePlayer => battlePlayer.InBattle)
            .Select(battlePlayer => battlePlayer.PlayerConnection),

        PersonalChatTemplate => chat.GetComponent<ChatParticipantsComponent>().Users
            .Select(user => {
                IPlayerConnection? connection = from.Server.PlayerConnections
                    .Where(conn => conn.IsOnline)
                    .SingleOrDefault(conn => conn.User.Id == user.Id);

                connection?.ShareIfUnshared(chat, from.User);
                return connection!;
            })
            .Where(conn => conn != null!),

        _ => []
    };
}