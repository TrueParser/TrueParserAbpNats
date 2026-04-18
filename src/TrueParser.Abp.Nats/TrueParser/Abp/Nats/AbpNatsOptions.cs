using System.Collections.Generic;

namespace TrueParser.Abp.Nats;

public class AbpNatsOptions
{
    public string Connections { get; set; } = "nats://localhost:4222";

    public string? UserName { get; set; }

    public string? Password { get; set; }

    public string? Jwt { get; set; }

    public string? Seed { get; set; }
    
    public string? ClientName { get; set; }

    public Dictionary<string, string> NamedConnections { get; set; } = new();

    public AbpNatsOptions()
    {
    }
}
