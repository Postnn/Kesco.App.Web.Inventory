using System.Text.Json.Serialization;
using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Результат редактирования SIM.</summary>
/// <param name="PhoneNumber">Номер телефона.</param>
/// <param name="ActivatedLocal">Локальные дата и время активации.</param>
/// <param name="ContractId">Договор связи.</param>
/// <param name="PersonalDataPersonId">Лицо ПДн.</param>
/// <param name="Secrets">PIN/PUK, передаваемые только в памяти circuit.</param>
public sealed record EquipmentSimDialogResult(
    string PhoneNumber,
    DateTime ActivatedLocal,
    int? ContractId,
    int? PersonalDataPersonId,
    [property: JsonIgnore] EquipmentSimSecrets Secrets)
{
    /// <summary>Явный режим Add либо Edit, заданный исходным открытием редактора.</summary>
    public EquipmentSimWriteMode Mode { get; init; }

    /// <summary>Непрозрачная защищённая исходная версия для Edit.</summary>
    public string? OriginalVersion { get; init; }

    /// <inheritdoc />
    public override string ToString() => nameof(EquipmentSimDialogResult);
}
