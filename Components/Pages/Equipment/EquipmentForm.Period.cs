using System.Data.SqlTypes;
using System.Globalization;
using Clayzor.Lib.Entities.Inventory;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private const string AddLocationPeriodCommand = "equipment-location-period.add";
    private const string UpdateLocationPeriodCommand = "equipment-location-period.update";
    private const string IssueResponsiblePeriodCommand = "equipment-responsible-period.issue";
    private const string UpdateResponsiblePeriodCommand = "equipment-responsible-period.update";
    private const string WithdrawResponsiblePeriodCommand = "equipment-responsible-period.withdraw";

    private static string PeriodDraftScope(string kind, long ticks) => kind switch
    {
        AddLocationPeriodCommand => $"child:{nameof(EquipmentLocationDialog)}:new:root",
        UpdateLocationPeriodCommand => $"child:{nameof(EquipmentLocationDialog)}:{ticks.ToString(CultureInfo.InvariantCulture)}:root",
        IssueResponsiblePeriodCommand => $"child:{nameof(EquipmentResponsibleDialog)}:Issue::root",
        UpdateResponsiblePeriodCommand => $"child:{nameof(EquipmentResponsibleDialog)}:Edit:{ticks.ToString(CultureInfo.InvariantCulture)}:root",
        WithdrawResponsiblePeriodCommand => $"child:{nameof(EquipmentResponsibleDialog)}:Withdraw:{ticks.ToString(CultureInfo.InvariantCulture)}:root",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private Task<EquipmentEditorSaveResult> SavePeriodDraftAsync(string kind, DateTime fromUtc,
        int equipmentId, int locationVersion, Func<int, Task> write)
    {
        DateTime normalized;
        try { normalized = EquipmentPeriodWrite.NormalizeStartUtc(fromUtc); }
        catch (SqlTypeException)
        {
            return Task.FromResult(EquipmentEditorSaveResult.Reject("Недопустимая дата начала периода."));
        }
        if (_model?.Id != equipmentId || CurrentUser is not { } actor || _activeEditor is not { } editor ||
            ActiveDraftScope != PeriodDraftScope(kind, normalized.Ticks))
            return Task.FromResult(EquipmentEditorSaveResult.Reject("Контекст периода изменился. Откройте редактор заново."));

        var pageVersion = _operationVersion;
        var isLocation = kind is AddLocationPeriodCommand or UpdateLocationPeriodCommand;
        var label = kind switch
        {
            AddLocationPeriodCommand => "Добавление расположения",
            UpdateLocationPeriodCommand => "Изменение расположения",
            _ => "Сохранение ответственного",
        };
        return SaveChildDraftAsync(label,
            isLocation ? "Расположение сохранено." : "Сведения об ответственном сохранены.", new { FromUtcTicks = normalized.Ticks },
            () => write(actor.Id), () => RefreshPeriodAsync(isLocation),
            () => IsCurrent(pageVersion, equipmentId) && _model?.Id == equipmentId && _locationVersion == locationVersion &&
                CurrentUser?.Id == actor.Id && ReferenceEquals(_activeEditor, editor));
    }

    private async Task RefreshPeriodAsync(bool locationFirst)
    {
        if (_model is not { } model) return;
        var pageVersion = _operationVersion;
        var version = _locationVersion;
        if (locationFirst) await RefreshLocationsAsync();
        else await RefreshResponsiblesAsync();
        if (!IsCurrentSave(model, pageVersion) || version != _locationVersion) return;
        if (locationFirst) await RefreshResponsiblesAsync();
        else await RefreshLocationsAsync();
    }
}
