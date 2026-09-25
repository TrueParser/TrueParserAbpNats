using System.Collections.Generic;
using NATS.Client.Core;

namespace TrueParser.Abp.Nats;

public class AbpNatsOptions
{
    public string Connections { get; set; } = "nats://localhost:4222";

    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string? Jwt { get; set; }

    public string? Seed { get; set; }
    
    public string? ClientName { get; set; }

    /// <summary>
    /// TLS settings applied to each connection created by the pool.
    /// </summary>
    public NatsTlsOpts Tls { get; set; } = NatsTlsOpts.Default;

    public Dictionary<string, string> NamedConnections { get; set; } = new();

    public AbpNatsOptions()
    {
    }
}
