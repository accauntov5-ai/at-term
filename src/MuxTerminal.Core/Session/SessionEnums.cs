namespace MuxTerminal.Core.Session;

public enum MuxSessionState
{
    Idle,
    Initializing,
    Running,
    Stopping,
    Stopped,
    Faulted,
}

public enum ChannelState
{
    Closed,
    Opening,
    Open,
    Closing,
    /// <summary>Модем отказал (DM) или не ответил.</summary>
    Failed,
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public enum TrafficDirection
{
    Rx,
    Tx,
}
