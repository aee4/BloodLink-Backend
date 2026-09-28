namespace BloodLink.Api.Configuration;

public sealed class ApiTokenOptions
{
    public const string SectionName = "Api:Tokens";
    public string Issuer { get; set; } = "BloodLink.Api";
    public string Audience { get; set; } = "BloodLink.Frontend";
    public string SigningKey { get; set; } = string.Empty;
    public int LifetimeMinutes { get; set; } = 15;
}
