﻿using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using LinqToDB;
using NetCoreServer;
using Serilog;
using Vint.Core.Database;
using Vint.Core.Database.Models;
using Vint.Core.ECS.Components.Group;
using Vint.Core.ECS.Entities;
using Vint.Core.ECS.Events;
using Vint.Core.ECS.Events.Entrance.Login;
using Vint.Core.ECS.Templates.Entrance;
using Vint.Core.ECS.Templates.User;
using Vint.Core.Protocol.Codecs.Buffer;
using Vint.Core.Protocol.Codecs.Impl;
using Vint.Core.Protocol.Commands;
using Vint.Core.Utils;

namespace Vint.Core.Server;

public interface IPlayerConnection {
    public ILogger Logger { get; }

    public GameServer Server { get; }
    public Player Player { get; set; }
    public IEntity User { get; }
    public IEntity ClientSession { get; }

    public bool IsOnline { get; }

    public List<IEntity> SharedEntities { get; }
    public Dictionary<string, List<IEntity>> UserEntities { get; }

    public void Register(
        string username,
        string encryptedPasswordDigest,
        string email,
        string hardwareFingerprint,
        bool subscribed,
        bool steam,
        bool quickRegistration);

    public void Login(
        bool rememberMe,
        string hardwareFingerprint);

    public void ChangePassword(string passwordDigest);

    public void Send(ICommand command);

    public void Send(IEvent @event);

    public void Send(IEvent @event, params IEntity[] entities);

    public void Share(IEntity entity);

    public void Share(params IEntity[] entities);

    public void Share(IEnumerable<IEntity> entities);
}

public class PlayerConnection(GameServer server, Protocol.Protocol protocol) : TcpSession(server), IPlayerConnection {
    public ILogger Logger { get; private set; } = Log.Logger.ForType(typeof(PlayerConnection));
    public Dictionary<string, List<IEntity>> UserEntities { get; } = new();

    public new GameServer Server { get; } = server;
    public Player Player { get; set; } = null!;
    public IEntity User { get; private set; } = null!;
    public IEntity ClientSession { get; private set; } = null!;
    public List<IEntity> SharedEntities { get; private set; } = [];

    public bool IsOnline => ClientSession != null! && User != null! && Player != null!;

    public void Register(
        string username,
        string encryptedPasswordDigest,
        string email,
        string hardwareFingerprint,
        bool subscribed,
        bool steam,
        bool quickRegistration) {
        Logger.Information("Registering player '{Username}'", username);

        byte[] passwordHash = new Encryption().RsaDecrypt(Convert.FromBase64String(encryptedPasswordDigest));

        Player = new Player {
            Username = username,
            Email = email,
            CountryCode = IpUtils.GetCountryCode((Socket.RemoteEndPoint as IPEndPoint)!.Address) ?? "US",
            HardwareFingerprint = hardwareFingerprint,
            Subscribed = subscribed,
            RegistrationTime = DateTimeOffset.UtcNow,
            LastLoginTime = DateTimeOffset.UtcNow,
            PasswordHash = passwordHash
        };

        using (DbConnection database = new()) {
            Player.Id = database.InsertWithInt64Identity(Player);
        }

        Player.InitializeNew();

        Login(true, hardwareFingerprint);
    }

    public void Login(
        bool rememberMe,
        string hardwareFingerprint) {
        Player.LastLoginTime = DateTimeOffset.UtcNow;
        Player.HardwareFingerprint = hardwareFingerprint;

        if (rememberMe) {
            Encryption encryption = new();

            byte[] autoLoginToken = new byte[32];
            new Random().NextBytes(autoLoginToken);

            byte[] encryptedAutoLoginToken = encryption.EncryptAutoLoginToken(autoLoginToken, Player.PasswordHash);

            Player.AutoLoginToken = autoLoginToken;

            Send(new SaveAutoLoginTokenEvent(Player.Username, encryptedAutoLoginToken));
        }

        User = new UserTemplate().Create(Player);
        Share(User);

        ClientSession.AddComponent(User.GetComponent<UserGroupComponent>());

        Logger.Warning("'{Username}' logged in", Player.Username);

        using DbConnection database = new();

        database.Update(Player);
    }

    public void ChangePassword(string passwordDigest) {
        Encryption encryption = new();

        byte[] passwordHash = encryption.RsaDecrypt(Convert.FromBase64String(passwordDigest));
        Player.PasswordHash = passwordHash;

        using DbConnection database = new();

        database.Players
            .Where(player => player.Id == Player.Id)
            .Set(player => player.PasswordHash, Player.PasswordHash)
            .Update();
    }

    public void Send(ICommand command) {
        try {
            Logger.Debug("Sending {Command}", command);

            ProtocolBuffer buffer = new(new OptionalMap(), this);

            protocol.GetCodec(new TypeCodecInfo(typeof(ICommand))).Encode(buffer, command);

            using MemoryStream stream = new();
            using BinaryWriter writer = new BigEndianBinaryWriter(stream);

            buffer.Wrap(writer);

            byte[] bytes = stream.ToArray();

            SendAsync(bytes);

            Logger.Verbose("Sent {Command}: {Size} bytes ({Hex})", command, bytes.Length, Convert.ToHexString(bytes));
        } catch (Exception e) {
            Logger.Error(e, "Socket caught an exception while sending {Command}", command);
            Disconnect();
        }
    }

    public void Send(IEvent @event) => ClientSession.Send(@event);

    public void Send(IEvent @event, params IEntity[] entities) => Send(new SendEventCommand(@event, entities));

    public void Share(IEntity entity) => entity.Share(this);

    public void Share(params IEntity[] entities) => entities.ToList().ForEach(Share);

    public void Share(IEnumerable<IEntity> entities) => entities.ToList().ForEach(Share);

    protected override void OnConnecting() =>
        Logger = Logger.WithPlayer(this);

    protected override void OnConnected() {
        ClientSession = new ClientSessionTemplate().Create();

        Logger.Information("New socket connected");

        Send(new InitTimeCommand(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        ClientSession.Share(this);
    }

    protected override void OnDisconnected() =>
        Logger.Information("Socket disconnected");

    protected override void OnError(SocketError error) =>
        Logger.Error("Socket caught an error: {Error}", error);

    protected override void OnReceived(byte[] bytes, long offset, long size) {
        try {
            Logger.Verbose("Received {Size} bytes ({Hex})", size, Convert.ToHexString(bytes[..(int)size]));

            ProtocolBuffer buffer = new(new OptionalMap(), this);
            MemoryStream stream = new(bytes);
            BinaryReader reader = new BigEndianBinaryReader(stream);

            if (!buffer.Unwrap(reader))
                throw new InvalidDataException("Failed to unwrap packet");

            long availableForRead = buffer.Stream.Length - buffer.Stream.Position;

            while (availableForRead > 0) {
                Logger.Verbose("Decode buffer bytes available: {Available}", availableForRead);

                ICommand command = (ICommand)protocol.GetCodec(new TypeCodecInfo(typeof(ICommand))).Decode(buffer);
                Logger.Debug("Received {Command}", command);

                availableForRead = buffer.Stream.Length - buffer.Stream.Position;

                try {
                    command.Execute(this);
                } catch (Exception e) {
                    Logger.Error(e, "Failed to execute {Command}", command);
                }
            }
        } catch (Exception e) {
            Logger.Error(e, "Socket caught an exception while receiving data");
            Disconnect();
        }
    }

    [SuppressMessage("ReSharper", "ConditionalAccessQualifierIsNonNullableAccordingToAPIContract")]
    public override string ToString() => $"PlayerConnection {{ " +
                                         $"ClientSession Id: '{ClientSession?.Id}'; " +
                                         $"Username: '{Player?.Username}' }}";
}