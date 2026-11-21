namespace Bearing.Core.Data;

/// <summary>
/// The loopback end of an SSH forward, as a provider should dial it: <see cref="Host"/> is the address the
/// local listener is bound to and <see cref="Port"/> the port it was given. Handed to
/// <see cref="IDbProvider.CreateConnectionFactory(ConnectionInfo, string?, TunnelEndpoint)"/> next to the
/// connection's own record, which keeps the real host name.
/// </summary>
public sealed record TunnelEndpoint(string Host, int Port);
