using System.Text.Json;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>Сравнивает поля создания оборудования с подтверждённым состоянием.</summary>
public partial class EquipmentIns
{
    private sealed record CreationFieldSnapshot(EquipmentEntity Model, DateTime? Date, TimeSpan? Time);

    private string? _creationFieldBaselineSource;
    private CreationFieldSnapshot? _creationFieldBaseline;

    private CreationFieldSnapshot? CreationFieldBaseline
    {
        get
        {
            var source = _createdEquipmentId is not null && _creationSentSnapshot is { } submitted
                ? submitted
                : _originalSnapshot;
            if (!ReferenceEquals(_creationFieldBaselineSource, source))
            {
                _creationFieldBaseline = string.IsNullOrEmpty(source)
                    ? null
                    : JsonSerializer.Deserialize<CreationFieldSnapshot>(source);
                _creationFieldBaselineSource = source;
            }

            return _creationFieldBaseline;
        }
    }
}