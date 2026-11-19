namespace Kesco.App.Web.Inventory.Services;

/// <summary>Источники допустимых групп перехвата, включая числовую группу 0.</summary>
public static class PhonePickupGroups
{
    /// <summary>Возвращает именованные группы CCM либо группы 0..15 для станций 1–99.</summary>
    public static IReadOnlyList<string> ForStation(int? stationId, string? configuredCcmGroups)
    {
        if (stationId is null or < 0 or > 99) return [];
        if (stationId > 0) return Enumerable.Range(0, 16).Select(item => item.ToString()).ToList();
        return (configuredCcmGroups ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length <= 20).Distinct(StringComparer.Ordinal).ToList();
    }
}
