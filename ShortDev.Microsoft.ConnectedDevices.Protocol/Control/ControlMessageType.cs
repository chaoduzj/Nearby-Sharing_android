﻿namespace ShortDev.Microsoft.ConnectedDevices.Protocol.Control;

public enum ControlMessageType
{
    StartChannelRequest = 0,
    StartChannelResponse = 1,
    EnumerateAppsReponse = 4,
    EnumerateAppTargetNamesRequest = 5,
    EnumerateAppTargetNamesResponse = 6
}