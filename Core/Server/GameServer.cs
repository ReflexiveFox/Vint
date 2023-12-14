﻿using System.Net;
using System.Net.Sockets;
using NetCoreServer;
using Serilog;
using Vint.Core.Utils;

namespace Vint.Core.Server;

public class GameServer(IPAddress host, ushort port) : TcpServer(host, port) {
    ILogger Logger { get; } = Log.Logger.ForType(typeof(GameServer));
    Protocol.Protocol Protocol { get; } = new();

    protected override PlayerConnection CreateSession() => new(this, Protocol);

    protected override void OnStarted() => Logger.Information("Started");

    protected override void OnError(SocketError error) => Logger.Error("Server caught an error: {Error}", error);
}