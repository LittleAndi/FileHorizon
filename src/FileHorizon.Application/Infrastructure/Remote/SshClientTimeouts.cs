using FileHorizon.Application.Abstractions;
using Renci.SshNet;

namespace FileHorizon.Application.Infrastructure.Remote;

/// <summary>
/// Applies <see cref="SftpTimeouts"/> to an SSH.NET client. Shared by every SSH.NET client the
/// application creates so polling and downloads cannot drift apart.
/// </summary>
internal static class SshClientTimeouts
{
    public static void Apply(SftpClient client, SftpTimeouts timeouts)
    {
        client.OperationTimeout = timeouts.Operation;
        client.KeepAliveInterval = timeouts.KeepAlive > TimeSpan.Zero ? timeouts.KeepAlive : Timeout.InfiniteTimeSpan;
        client.ConnectionInfo.Timeout = timeouts.Connect;
    }
}
