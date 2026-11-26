using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Исходный тест и незавершённый ввод во вложенном редакторе UPS.</summary>
public sealed record EquipmentUpsTestDraft(
    UpsTest Original,
    DateTime? Date,
    TimeSpan? Time,
    int BatteryTime,
    string Servers,
    string Issues);
