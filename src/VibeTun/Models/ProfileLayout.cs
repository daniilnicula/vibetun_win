namespace VibeTun.Models;

/// <summary>Порядок корневых элементов вкладки профилей и состав пользовательских групп.</summary>
public sealed class ProfileLayout
{
    public List<ProfileLayoutEntry> Entries { get; set; } = new();
    public List<ProfileGroupDefinition> Groups { get; set; } = new();
}

public sealed class ProfileLayoutEntry
{
    public ProfileLayoutEntryKind Kind { get; set; }
    public string Id { get; set; } = "";
}

public enum ProfileLayoutEntryKind
{
    Profile,
    Group
}

/// <summary>Пользовательская группа профилей. Подписочная метка ProxyProfile.Group не меняется.</summary>
public sealed class ProfileGroupDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool IsExpanded { get; set; } = true;
    public bool IsSubscription { get; set; }
    public string? SubscriptionUrl { get; set; }
    public List<string> ProfileIds { get; set; } = new();
}
