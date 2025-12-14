namespace Lagrange.Milky.Configuration;

public class OneBotReverseConfiguration
{
    public bool Enabled { get; set; }
    public string? Url { get; set; }
    public string? AccessToken { get; set; }
}

public class OneBotConfiguration
{
    public bool EnabledHttpApi { get; set; }
    public bool EnabledWebSocket { get; set; }

    public string? Host { get; set; }
    public ulong? Port { get; set; }
    public string Prefix { get; set; } = "/";
    public string? AccessToken { get; set; }

    public OneBotReverseConfiguration Reverse { get; set; } = new();
}
