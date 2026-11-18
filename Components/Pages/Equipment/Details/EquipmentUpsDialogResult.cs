using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Результат редактирования связей и тестов UPS.</summary>
/// <param name="Servers">Текущие серверы.</param>
/// <param name="Tests">Текущие тесты.</param>
public sealed record EquipmentUpsDialogResult(
    IReadOnlyCollection<UpsServerLink> Servers,
    IReadOnlyCollection<UpsTest> Tests);
