// Copyright (c) 2020 Pryaxis & Orion Contributors
// 
// This file is part of Orion.
// 
// Orion is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Orion is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Orion.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Orion.Core;
using Orion.Core.Events;
using Orion.Core.Events.Packets;
using Orion.Core.Events.Players;
using Orion.Core.Packets;
using Orion.Core.Packets.DataStructures.Modules;
using Orion.Core.Packets.Players;
using Orion.Core.Packets.Server;
using Orion.Core.Players;
using Orion.Core.Utils;
using Orion.Launcher.Utils;
using Serilog;

namespace Orion.Launcher.Players
{
    [Binding("orion-players", Author = "Pryaxis", Priority = BindingPriority.Lowest)]
    internal sealed class OrionPlayerService : IPlayerService, IDisposable
    {
        private delegate void PacketHandler(int playerIndex, Span<byte> span);

        private static readonly MethodInfo _onReceivePacket =
            typeof(OrionPlayerService)
                .GetMethod(nameof(OnReceivePacket), BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static readonly MethodInfo _onSendPacket =
            typeof(OrionPlayerService)
                .GetMethod(nameof(OnSendPacket), BindingFlags.NonPublic | BindingFlags.Instance)!;

        [ThreadStatic] internal static bool _ignoreGetData;

        private readonly IEventManager _events;
        private readonly ILogger _log;
        private readonly IReadOnlyList<IPlayer> _players;

        private readonly PacketHandler?[] _onReceivePacketHandlers = new PacketHandler?[256];
        private readonly PacketHandler?[] _onReceiveModuleHandlers = new PacketHandler?[65536];
        private readonly PacketHandler?[] _onSendPacketHandlers = new PacketHandler?[256];
        private readonly PacketHandler?[] _onSendModuleHandlers = new PacketHandler?[65536];

        public OrionPlayerService(IEventManager events, ILogger log)
        {
            Debug.Assert(events != null);
            Debug.Assert(log != null);

            _events = events;
            _log = log;

            // Note that the last player should be ignored, as it is not a real player.
            _players = new WrappedReadOnlyList<OrionPlayer, Terraria.Player>(
                Terraria.Main.player.AsMemory(..^1),
                (playerIndex, terrariaPlayer) => new OrionPlayer(playerIndex, terrariaPlayer, events, log));

            foreach (var packetId in (PacketId[])Enum.GetValues(typeof(PacketId)))
            {
                var packetType = packetId.Type();
                _onReceivePacketHandlers[(byte)packetId] = MakeOnReceivePacketHandler(packetType);
                _onSendPacketHandlers[(byte)packetId] = MakeOnSendPacketHandler(packetType);
            }

            foreach (var moduleId in (ModuleId[])Enum.GetValues(typeof(ModuleId)))
            {
                var packetType = typeof(ModulePacket<>).MakeGenericType(moduleId.Type());
                _onReceiveModuleHandlers[(ushort)moduleId] = MakeOnReceivePacketHandler(packetType);
                _onSendModuleHandlers[(ushort)moduleId] = MakeOnSendPacketHandler(packetType);
            }

            /*OTAPI.Hooks.Net.ReceiveData = ReceiveDataHandler;
            OTAPI.Hooks.Net.SendBytes = SendBytesHandler;
            OTAPI.Hooks.Net.SendNetData = SendNetDataHandler;
            OTAPI.Hooks.Player.PreUpdate = PreUpdateHandler;
            OTAPI.Hooks.Net.RemoteClient.PreReset = PreResetHandler;*/

            OTAPI.Hooks.MessageBuffer.GetData += ReceiveDataHandler;
            OTAPI.Hooks.NetMessage.SendBytes += SendBytesHandler;
            On.Terraria.Net.NetManager.SendData += SendNetDataHandler;
            On.Terraria.Player.Update += PreUpdateHandler;
            On.Terraria.RemoteClient.Reset += PreResetHandler;

            _events.RegisterHandlers(this, _log);

            PacketHandler MakeOnReceivePacketHandler(Type packetType) =>
                (PacketHandler)_onReceivePacket
                    .MakeGenericMethod(packetType)
                    .CreateDelegate(typeof(PacketHandler), this);

            PacketHandler MakeOnSendPacketHandler(Type packetType) =>
                (PacketHandler)_onSendPacket
                    .MakeGenericMethod(packetType)
                    .CreateDelegate(typeof(PacketHandler), this);
        }

        public IPlayer this[int index] => _players[index];

        public int Count => _players.Count;

        public IEnumerator<IPlayer> GetEnumerator() => _players.GetEnumerator();

        public void Dispose()
        {
            OTAPI.Hooks.MessageBuffer.GetData -= ReceiveDataHandler;
            OTAPI.Hooks.NetMessage.SendBytes -= SendBytesHandler;
            On.Terraria.Net.NetManager.SendData -= SendNetDataHandler;
            On.Terraria.Player.Update -= PreUpdateHandler;
            On.Terraria.RemoteClient.Reset -= PreResetHandler;

            _events.DeregisterHandlers(this, _log);
        }

        [ExcludeFromCodeCoverage]
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // =============================================================================================================
        // OTAPI hooks
        //

        private void ReceiveDataHandler(object? sender, OTAPI.Hooks.MessageBuffer.GetDataEventArgs args)
        {
            Debug.Assert(args.Instance != null);
            Debug.Assert(args.Instance.whoAmI >= 0 && args.Instance.whoAmI < Count);
            Debug.Assert(args.Start >= 0 && args.Start + args.Length <= args.Instance.readBuffer.Length);
            Debug.Assert(args.Length > 0);

            // Check `_ignoreGetData` to prevent infinite loops.
            if (_ignoreGetData)
            {
                return;
            }

            PacketHandler handler;
            var span = args.Instance.readBuffer.AsSpan(args.Start..(args.Start + args.Length));
            if (args.PacketId == (byte)PacketId.Module)
            {
                if (span.Length < 3)
                {
                    args.Result = OTAPI.HookResult.Cancel;
                    return;
                }

                var moduleId = Unsafe.ReadUnaligned<ushort>(ref span.At(1));
                handler = _onReceiveModuleHandlers[moduleId] ?? OnReceivePacket<ModulePacket<UnknownModule>>;
            }
            else
            {
                handler = _onReceivePacketHandlers[args.PacketId] ?? OnReceivePacket<UnknownPacket>;
            }

            handler(args.Instance.whoAmI, span);
            args.Result = OTAPI.HookResult.Cancel;
        }

        private void SendBytesHandler(object? sender, OTAPI.Hooks.NetMessage.SendBytesEventArgs args)
        {
            Debug.Assert(args.RemoteClient >= 0 && args.RemoteClient < Count);
            Debug.Assert(args.Data != null);
            Debug.Assert(args.Offset >= 0 && args.Offset + args.Size <= args.Data.Length);
            Debug.Assert(args.Size >= 3);

            var span = args.Data.AsSpan((args.Offset + 2)..(args.Offset + args.Size));
            var packetId = span.At(0);

            // The `SendBytes` event is only triggered for non-module packets.
            var handler = _onSendPacketHandlers[packetId] ?? OnSendPacket<UnknownPacket>;
            handler(args.RemoteClient, span);
            args.Result = OTAPI.HookResult.Cancel;
        }

        private void SendNetDataHandler(On.Terraria.Net.NetManager.orig_SendData orig, Terraria.Net.NetManager self, 
            Terraria.Net.Sockets.ISocket socket, Terraria.Net.NetPacket packet)
        {
            Debug.Assert(socket != null);
            Debug.Assert(packet.Buffer.Data != null);
            Debug.Assert(packet.Writer.BaseStream.Position >= 5);

            // Since we don't have an index, scan through the clients to find the player index.
            //
            // TODO: optimize this using a hash map, if needed
            var playerIndex = -1;
            for (var i = 0; i < Terraria.Netplay.MaxConnections; ++i)
            {
                if (Terraria.Netplay.Clients[i].Socket == socket)
                {
                    playerIndex = i;
                    break;
                }
            }

            Debug.Assert(playerIndex >= 0 && playerIndex < Count);

            var span = packet.Buffer.Data.AsSpan(2..((int)packet.Writer.BaseStream.Position));
            var moduleId = Unsafe.ReadUnaligned<ushort>(ref span.At(1));

            // The `SendBytes` event is only triggered for module packets.
            var handler = _onSendModuleHandlers[moduleId] ?? OnSendPacket<ModulePacket<UnknownModule>>;
            handler(playerIndex, span);
        }

        private void PreUpdateHandler(On.Terraria.Player.orig_Update orig, Terraria.Player self, int playerIndex)
        {
            Debug.Assert(playerIndex >= 0 && playerIndex < Count);

            var player = this[playerIndex];
            var evt = new PlayerTickEvent(player);
            _events.Raise(evt, _log);

            if (!evt.IsCanceled)
                orig(self, playerIndex);
        }

        private void PreResetHandler(On.Terraria.RemoteClient.orig_Reset orig, Terraria.RemoteClient remoteClient)
        {
            Debug.Assert(remoteClient != null);
            Debug.Assert(remoteClient.Id >= 0 && remoteClient.Id < Count);

            // Check if the client was active since this gets called when setting up `RemoteClient` as well.
            if (!remoteClient.IsActive)
            {
                orig(remoteClient);
                return;
            }

            var player = this[remoteClient.Id];
            var evt = new PlayerQuitEvent(player);
            _events.Raise(evt, _log);
            orig(remoteClient);
        }

        // =============================================================================================================
        // Packet event publishers
        //

        private void OnReceivePacket<TPacket>(int playerIndex, Span<byte> span) where TPacket : IPacket
        {
            var packet = MakePacket<TPacket>(span);

            // Read the packet using the `Server` context since we're receiving this packet.
            var packetBodyLength = packet.ReadBody(span[1..], PacketContext.Server);
            Debug.Assert(packetBodyLength == span.Length - 1);

            this[playerIndex].ReceivePacket(packet);
        }

        private void OnSendPacket<TPacket>(int playerIndex, Span<byte> span) where TPacket : IPacket
        {
            var packet = MakePacket<TPacket>(span);

            // Read the packet using the `Client` context since we're sending this packet.
            var packetBodyLength = packet.ReadBody(span[1..], PacketContext.Client);
            Debug.Assert(packetBodyLength == span.Length - 1);

            this[playerIndex].SendPacket(packet);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static TPacket MakePacket<TPacket>(Span<byte> span) where TPacket : IPacket
        {
            TPacket? packet = default;

            // `UnknownPacket` is a special case since it has no default constructor.
            if (typeof(TPacket) == typeof(UnknownPacket))
            {
                return (TPacket)(object)new UnknownPacket(span.Length - 1, (PacketId)span[0]);
            }
            else if (packet is null)
            {
                return (TPacket)Activator.CreateInstance(typeof(TPacket))!;
            }

            return packet;
        }

        // =============================================================================================================
        // Player event publishers
        //

        [EventHandler("orion-players", Priority = EventPriority.Lowest)]
        [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Implicit usage")]
        private void OnPlayerJoin(PacketReceiveEvent<PlayerJoin> evt)
        {
            _events.Forward(evt, new PlayerJoinEvent(evt.Sender), _log);
        }

        [EventHandler("orion-players", Priority = EventPriority.Lowest)]
        [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Implicit usage")]
        private void OnPlayerHealth(PacketReceiveEvent<PlayerHealth> evt)
        {
            var packet = evt.Packet;

            _events.Forward(evt, new PlayerHealthEvent(evt.Sender, packet.Health, packet.MaxHealth), _log);
        }

        [EventHandler("orion-players", Priority = EventPriority.Lowest)]
        [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Implicit usage")]
        private void OnPlayerPvp(PacketReceiveEvent<PlayerPvp> evt)
        {
            var packet = evt.Packet;

            _events.Forward(evt, new PlayerPvpEvent(evt.Sender, packet.IsInPvp), _log);
        }

        [EventHandler("orion-players", Priority = EventPriority.Lowest)]
        [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Implicit usage")]
        private void OnPasswordResponse(PacketReceiveEvent<PasswordResponse> evt)
        {
            var packet = evt.Packet;

            _events.Forward(evt, new PlayerPasswordEvent(evt.Sender, packet.Password), _log);
        }

        [EventHandler("orion-players", Priority = EventPriority.Lowest)]
        [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Implicit usage")]
        private void OnPlayerMana(PacketReceiveEvent<PlayerMana> evt)
        {
            var packet = evt.Packet;

            _events.Forward(evt, new PlayerManaEvent(evt.Sender, packet.Mana, packet.MaxMana), _log);
        }

        [EventHandler("orion-players", Priority = EventPriority.Lowest)]
        [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Implicit usage")]
        private void OnPlayerTeam(PacketReceiveEvent<PlayerTeam> evt)
        {
            var packet = evt.Packet;

            _events.Forward(evt, new PlayerTeamEvent(evt.Sender, packet.Team), _log);
        }

        [EventHandler("orion-players", Priority = EventPriority.Lowest)]
        [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Implicit usage")]
        private void OnClientUuid(PacketReceiveEvent<ClientUuid> evt)
        {
            var packet = evt.Packet;

            _events.Forward(evt, new PlayerUuidEvent(evt.Sender, packet.Uuid), _log);
        }

        [EventHandler("orion-players", Priority = EventPriority.Lowest)]
        [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Implicit usage")]
        private void OnChat(PacketReceiveEvent<ModulePacket<Chat>> evt)
        {
            var module = evt.Packet.Module;

            _events.Forward(evt, new PlayerChatEvent(evt.Sender, module.ClientCommand, module.ClientMessage), _log);
        }
    }
}
