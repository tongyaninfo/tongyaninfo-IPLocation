namespace IpLocation.Api.Models.Requests;

public sealed class ConsoleLoginRequest
{
    public string Account { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
