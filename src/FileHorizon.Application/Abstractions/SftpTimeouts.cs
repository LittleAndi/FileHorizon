namespace FileHorizon.Application.Abstractions;

/// <summary>
/// Network timeouts applied to an SFTP connection.
/// </summary>
/// <remarks>
/// SSH.NET's default operation timeout is infinite, so without these a server that stops answering
/// mid-request (a half-open connection) blocks the caller forever.
/// </remarks>
/// <param name="Operation">Maximum wait for the response to a single SFTP request (not a whole listing or transfer).</param>
/// <param name="KeepAlive">Interval between SSH keep-alive messages; <see cref="TimeSpan.Zero"/> disables them.</param>
/// <param name="Connect">Maximum wait for connecting and completing the SSH handshake.</param>
public sealed record SftpTimeouts(TimeSpan Operation, TimeSpan KeepAlive, TimeSpan Connect)
{
    public const int DefaultOperationSeconds = 60;
    public const int DefaultKeepAliveSeconds = 30;
    public const int DefaultConnectSeconds = 30;

    public static SftpTimeouts Default { get; } = FromSeconds(DefaultOperationSeconds, DefaultKeepAliveSeconds, DefaultConnectSeconds);

    public static SftpTimeouts FromSeconds(int operationSeconds, int keepAliveSeconds, int connectSeconds) =>
        new(TimeSpan.FromSeconds(operationSeconds), TimeSpan.FromSeconds(keepAliveSeconds), TimeSpan.FromSeconds(connectSeconds));
}
