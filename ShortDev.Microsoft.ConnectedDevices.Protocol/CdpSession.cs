﻿using ShortDev.Microsoft.ConnectedDevices.Protocol.Connection;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Connection.Authentication;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Connection.DeviceInfo;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Connection.TransportUpgrade;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Control;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Encryption;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Exceptions;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Platforms;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Security;
using System.Threading.Tasks;

namespace ShortDev.Microsoft.ConnectedDevices.Protocol;

/// <summary>
/// Handles messages that are sent across during an active session between two connected and authenticated devices. <br/>
/// Persists basic state (e.g. encryption) across sockets and transports (e.g. bt, wifi, ...).
/// </summary>
public sealed class CdpSession : IDisposable
{
    public required uint LocalSessionId { get; init; }
    public required uint RemoteSessionId { get; init; }
    public required CdpDevice Device { get; init; }

    internal ulong GetSessionId(bool isHost)
    {
        ulong result = (ulong)LocalSessionId << 32 | RemoteSessionId;
        if (isHost)
            result |= CommonHeader.SessionIdHostFlag;
        return result;
    }

    internal CdpSession() { }

    #region Registration
    static uint sessionIdCounter = 0xe;
    static readonly Dictionary<uint, CdpSession> _registration = new();
    public static CdpSession GetOrCreate(CdpDevice device, CommonHeader header)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(header);

        var sessionId = header.SessionId;
        var localSessionId = sessionId.HighValue();
        var remoteSessionId = sessionId.LowValue() & ~(uint)CommonHeader.SessionIdHostFlag;
        if (localSessionId != 0)
        {
            // Existing session            
            lock (_registration)
            {
                if (!_registration.ContainsKey(localSessionId))
                    throw new CdpSessionException("Session not found");

                var result = _registration[localSessionId];
                if (result.RemoteSessionId != remoteSessionId)
                    throw new CdpSessionException($"Wrong {nameof(RemoteSessionId)}");

                // ToDo: Security (Upgrade -> address change)
                //if (result.Device.Address != device.Address)
                //    throw new CdpSessionException("Wrong device!");

                result.ThrowIfDisposed();

                return result;
            }
        }
        else
        {
            // Create
            localSessionId = sessionIdCounter++;
            CdpSession result = new()
            {
                Device = device,
                LocalSessionId = localSessionId,
                RemoteSessionId = remoteSessionId
            };
            _registration.Add(localSessionId, result);
            return result;
        }
    }
    #endregion

    public ICdpPlatformHandler? PlatformHandler { get; set; } = null;

    internal CdpCryptor? Cryptor = null;
    readonly CdpEncryptionInfo _localEncryption = CdpEncryptionInfo.Create(CdpEncryptionParams.Default);
    CdpEncryptionInfo? _remoteEncryption = null;
    public void HandleMessage(CdpSocket socket, CommonHeader header, BinaryReader reader)
    {
        ThrowIfDisposed();

        var writer = socket.Writer;
        BinaryReader payloadReader = Cryptor?.Read(reader, header) ?? reader;
        {
            header.CorrectClientSessionBit();

            if (header.Type == MessageType.Connect)
            {
                ConnectionHeader connectionHeader = ConnectionHeader.Parse(payloadReader);
                PlatformHandler?.Log(0, $"Received {header.Type} message {connectionHeader.MessageType} from session {header.SessionId.ToString("X")}");
                switch (connectionHeader.MessageType)
                {
                    case ConnectionType.ConnectRequest:
                        {
                            var connectionRequest = ConnectionRequest.Parse(payloadReader);
                            _remoteEncryption = CdpEncryptionInfo.FromRemote(connectionRequest.PublicKeyX, connectionRequest.PublicKeyY, connectionRequest.Nonce, CdpEncryptionParams.Default);

                            var secret = _localEncryption.GenerateSharedSecret(_remoteEncryption);
                            Cryptor = new(secret);

                            header.SessionId |= (ulong)LocalSessionId << 32;

                            header.Write(writer);

                            new ConnectionHeader()
                            {
                                ConnectionMode = ConnectionMode.Proximal,
                                MessageType = ConnectionType.ConnectResponse
                            }.Write(writer);

                            var publicKey = _localEncryption.PublicKey;
                            new ConnectionResponse()
                            {
                                Result = ConnectionResult.Pending,
                                HmacSize = connectionRequest.HmacSize,
                                MessageFragmentSize = connectionRequest.MessageFragmentSize,
                                Nonce = _localEncryption.Nonce,
                                PublicKeyX = publicKey.X!,
                                PublicKeyY = publicKey.Y!
                            }.Write(writer);
                            break;
                        }
                    case ConnectionType.DeviceAuthRequest:
                    case ConnectionType.UserDeviceAuthRequest:
                        {
                            var authRequest = AuthenticationPayload.Parse(payloadReader);
                            if (!authRequest.VerifyThumbprint(_localEncryption.Nonce, _remoteEncryption!.Nonce))
                                throw new CdpSecurityException("Invalid thumbprint");

                            header.Flags = 0;
                            Cryptor!.EncryptMessage(writer, header, (writer) =>
                            {
                                new ConnectionHeader()
                                {
                                    ConnectionMode = ConnectionMode.Proximal,
                                    MessageType = connectionHeader.MessageType == ConnectionType.DeviceAuthRequest ? ConnectionType.DeviceAuthResponse : ConnectionType.UserDeviceAuthResponse
                                }.Write(writer);
                                AuthenticationPayload.Create(
                                    _localEncryption.DeviceCertificate!, // ToDo: User cert
                                    _localEncryption.Nonce, _remoteEncryption!.Nonce
                                ).Write(writer);
                            });
                            break;
                        }
                    case ConnectionType.UpgradeRequest:
                        {
                            var msg = UpgradeRequest.Parse(payloadReader);
                            PlatformHandler?.Log(0, $"Upgrade request {msg.UpgradeId} to {string.Join(',', msg.Endpoints.Select((x) => x.Type.ToString()))}");

                            header.Flags = 0;
                            Cryptor!.EncryptMessage(writer, header, (writer) =>
                            {
                                new ConnectionHeader()
                                {
                                    ConnectionMode = ConnectionMode.Proximal,
                                    MessageType = ConnectionType.UpgradeResponse
                                }.Write(writer);
                                new UpgradeResponse()
                                {
                                    HostEndpoints = new[]
                                    {
                                        new HostEndpointMetadata(CdpTransportType.Tcp, PlatformHandler!.GetLocalIP(), "5040")
                                    },
                                    Endpoints = new[]
                                    {
                                        TransportEndpoint.Tcp
                                    }
                                }.Write(writer);
                            });
                            break;
                        }
                    case ConnectionType.UpgradeFinalization:
                        {
                            var msg = TransportEndpoint.ParseArray(payloadReader);
                            PlatformHandler?.Log(0, "Transport upgrade to TCP");

                            header.Flags = 0;
                            Cryptor!.EncryptMessage(writer, header, (writer) =>
                            {
                                new ConnectionHeader()
                                {
                                    ConnectionMode = ConnectionMode.Proximal,
                                    MessageType = ConnectionType.UpgradeFinalizationResponse
                                }.Write(writer);
                            });
                            break;
                        }
                    case ConnectionType.UpgradeFailure:
                        {
                            var msg = HResultPayload.Parse(payloadReader);
                            PlatformHandler?.Log(0, $"Transport upgrade failed with HResult {msg.HResult}");
                            break;
                        }
                    case ConnectionType.TransportRequest:
                        {
                            var msg = TransportRequest.Parse(payloadReader);
                            PlatformHandler?.Log(0, $"Transport upgrade {msg.UpgradeId} succeded");

                            header.Flags = 0;
                            Cryptor!.EncryptMessage(writer, header, (writer) =>
                            {
                                new ConnectionHeader()
                                {
                                    ConnectionMode = ConnectionMode.Proximal,
                                    MessageType = ConnectionType.TransportConfirmation
                                }.Write(writer);
                                msg.Write(writer);
                            });
                            break;
                        }
                    case ConnectionType.AuthDoneRequest:
                        {
                            header.Flags = 0;
                            Cryptor!.EncryptMessage(writer, header, (writer) =>
                            {
                                new ConnectionHeader()
                                {
                                    ConnectionMode = ConnectionMode.Proximal,
                                    MessageType = ConnectionType.AuthDoneRespone // Ack
                                }.Write(writer);
                                new HResultPayload()
                                {
                                    HResult = 0 // No error
                                }.Write(writer);
                            });
                            break;
                        }
                    case ConnectionType.DeviceInfoMessage:
                        {
                            var msg = DeviceInfoMessage.Parse(payloadReader);

                            header.Flags = 0;
                            Cryptor!.EncryptMessage(writer, header, (writer) =>
                            {
                                new ConnectionHeader()
                                {
                                    ConnectionMode = ConnectionMode.Proximal,
                                    MessageType = ConnectionType.DeviceInfoResponseMessage // Ack
                                }.Write(writer);
                            });
                            break;
                        }
                    default:
                        throw UnexpectedMessage(connectionHeader.MessageType.ToString());
                }
            }
            else if (header.Type == MessageType.Control)
            {
                var controlHeader = ControlHeader.Parse(payloadReader);
                PlatformHandler?.Log(0, $"Received {header.Type} message {controlHeader.MessageType} from session {header.SessionId.ToString("X")}");
                switch (controlHeader.MessageType)
                {
                    case ControlMessageType.StartChannelRequest:
                        {
                            var request = StartChannelRequest.Parse(payloadReader);

                            header.AdditionalHeaders.Clear();
                            header.SetReplyToId(header.RequestID);
                            header.AdditionalHeaders.Add(new(
                                (AdditionalHeaderType)129,
                                new byte[] { 0x30, 0x0, 0x0, 0x1 }
                            ));

                            header.RequestID = 0;

                            StartChannel(request, socket, out var channelId);

                            header.Flags = 0;
                            Cryptor!.EncryptMessage(writer, header, (writer) =>
                            {
                                new ControlHeader()
                                {
                                    MessageType = ControlMessageType.StartChannelResponse
                                }.Write(writer);
                                writer.Write((byte)0);
                                writer.Write(channelId);
                            });
                            break;
                        }
                    default:
                        throw UnexpectedMessage(controlHeader.MessageType.ToString());
                }
            }
            else if (header.Type == MessageType.Session)
            {
                CdpMessage msg = GetOrCreateMessage(header);
                msg.AddFragment(payloadReader.ReadPayload());

                if (msg.IsComplete)
                {
                    // NewSequenceNumber();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var channel = _channelRegistry[header.ChannelId];
                            await channel.HandleMessageAsync(msg);
                        }
                        finally
                        {
                            _msgRegistry.Remove(msg.Id);
                            msg.Dispose();
                        }
                    });
                }
            }
            else
            {
                // We might receive a "ReliabilityResponse"
                // ignore
                PlatformHandler?.Log(0, $"Received {header.Type} message from session {header.SessionId.ToString("X")}");
            }
        }

        writer.Flush();
    }

    #region "SequenceNumber"
    uint _sequenceNumber = 0;
    internal uint NewSequenceNumber()
    {
        throw new NotImplementedException();
        // ToDo: Fix
        lock (this)
            return _sequenceNumber++;
    }
    #endregion

    #region Messages
    readonly Dictionary<uint, CdpMessage> _msgRegistry = new();

    CdpMessage GetOrCreateMessage(CommonHeader header)
    {
        if (_msgRegistry.TryGetValue(header.SequenceNumber, out var result))
            return result;

        result = new(header);
        _msgRegistry.Add(header.SequenceNumber, result);
        return result;
    }
    #endregion

    #region Channels
    ulong channelCounter = 1;
    internal readonly Dictionary<ulong, CdpChannel> _channelRegistry = new();

    ulong StartChannel(StartChannelRequest request, CdpSocket socket, out ulong channelId)
    {
        lock (_channelRegistry)
        {
            channelId = channelCounter++;
            var app = CdpAppRegistration.InstantiateApp(request.Id, request.Name);
            CdpChannel channel = new(this, channelId, app, socket);
            _channelRegistry.Add(channelId, channel);
            return channelId;
        }
    }

    internal void UnregisterChannel(CdpChannel channel)
    {
        lock (_channelRegistry)
        {
            _channelRegistry.Remove(channel.ChannelId);
        }
    }
    #endregion

    Exception UnexpectedMessage(string? info = null)
        => new CdpSecurityException($"Recieved unexpected message {info ?? "null"}");

    public bool IsDisposed { get; private set; } = false;

    private void ThrowIfDisposed()
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(CdpSession));
    }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;

        lock (_registration)
        {
            _registration.Remove(LocalSessionId);
        }

        lock (_channelRegistry)
        {
            foreach (var channel in _channelRegistry.Values)
                channel.Dispose();
            _channelRegistry.Clear();
        }

        lock (_msgRegistry)
        {
            foreach (var msg in _msgRegistry.Values)
                msg.Dispose();
            _msgRegistry.Clear();
        }
    }
}
