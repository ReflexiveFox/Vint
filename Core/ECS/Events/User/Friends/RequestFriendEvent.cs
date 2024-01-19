using LinqToDB;
using Vint.Core.Database;
using Vint.Core.Database.Models;
using Vint.Core.ECS.Entities;
using Vint.Core.Protocol.Attributes;
using Vint.Core.Server;

namespace Vint.Core.ECS.Events.User.Friends;

[ProtocolId(1450168139800)]
public class RequestFriendEvent : FriendBaseEvent, IServerEvent {
    public void Execute(IPlayerConnection connection, IEnumerable<IEntity> entities) {
        using DbConnection db = new();
        Player? player = db.Players.SingleOrDefault(player => player.Id == User.Id);

        if (player == null) return;

        db.BeginTransaction();

        Relation? thisToTargetRelation = db.Relations
            .SingleOrDefault(relation => relation.SourcePlayerId == connection.Player.Id &&
                                         relation.TargetPlayerId == player.Id);

        Relation? targetToThisRelation = db.Relations
            .SingleOrDefault(relation => relation.SourcePlayerId == player.Id &&
                                         relation.TargetPlayerId == connection.Player.Id);

        if (thisToTargetRelation != null &&
            targetToThisRelation != null &&
            (thisToTargetRelation.Types & RelationTypes.IncomingRequest) == RelationTypes.IncomingRequest &&
            (targetToThisRelation.Types & RelationTypes.OutgoingRequest) == RelationTypes.OutgoingRequest) {
            thisToTargetRelation.Types = thisToTargetRelation.Types & ~RelationTypes.IncomingRequest | RelationTypes.Friend;
            targetToThisRelation.Types = targetToThisRelation.Types & ~RelationTypes.OutgoingRequest | RelationTypes.Friend;

            db.Update(thisToTargetRelation);
            db.Update(targetToThisRelation);
        } else {
            if (thisToTargetRelation == null) {
                db.Insert(new Relation { SourcePlayer = connection.Player, TargetPlayer = player, Types = RelationTypes.OutgoingRequest });
            } else {
                thisToTargetRelation.Types &= ~RelationTypes.Blocked;
                thisToTargetRelation.Types |= RelationTypes.OutgoingRequest;
                db.Update(thisToTargetRelation);
            }

            if (targetToThisRelation == null) {
                db.Insert(new Relation { SourcePlayer = player, TargetPlayer = connection.Player, Types = RelationTypes.IncomingRequest });
            } else {
                targetToThisRelation.Types |= RelationTypes.IncomingRequest;
                db.Update(targetToThisRelation);
            }
        }

        db.CommitTransaction();

        connection.Unshare(User);
        connection.Send(new OutgoingFriendAddedEvent(player.Id), connection.User);
        connection.Server.PlayerConnections
            .Where(conn => conn.IsOnline)
            .SingleOrDefault(conn => conn.Player.Id == player.Id)?
            .Send(new IncomingFriendAddedEvent(connection.Player.Id), User);
    }
}