namespace Rasel.Domain.Entities;

public class ApiClient
{
    private ApiClient() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string ApiKeyHash { get; private set; } = "";
    public int DailyMessageLimit { get; private set; }
    public bool Active { get; private set; }

    public static ApiClient Create(string name, string apiKeyHash, int dailyMessageLimit) => new()
    {
        Id = Guid.NewGuid(), Name = name, ApiKeyHash = apiKeyHash, DailyMessageLimit = dailyMessageLimit, Active = true,
    };
}
