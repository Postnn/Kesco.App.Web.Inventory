using System.Data.Common;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private static string PeriodDraftScope(string kind, long ticks) => kind switch
    {
        EquipmentFormService.AddLocationPeriodCommand => $"child:{nameof(EquipmentLocationDialog)}:new:root",
        EquipmentFormService.UpdateLocationPeriodCommand =>
            $"child:{nameof(EquipmentLocationDialog)}:{ticks.ToString(CultureInfo.InvariantCulture)}:root",
        EquipmentFormService.IssueResponsiblePeriodCommand => $"child:{nameof(EquipmentResponsibleDialog)}:Issue::root",
        EquipmentFormService.UpdateResponsiblePeriodCommand =>
            $"child:{nameof(EquipmentResponsibleDialog)}:Edit:{ticks.ToString(CultureInfo.InvariantCulture)}:root",
        EquipmentFormService.WithdrawResponsiblePeriodCommand =>
            $"child:{nameof(EquipmentResponsibleDialog)}:Withdraw:{ticks.ToString(CultureInfo.InvariantCulture)}:root",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private Task<EquipmentEditorSaveResult> SaveJournaledPeriodDraftAsync(string kind, DateTime fromUtc,
        int equipmentId, int locationVersion, Func<int, Guid, Task> write)
    {
        DateTime normalized;
        try { normalized = EquipmentPeriodJournalWrite.NormalizeStartUtc(fromUtc); }
        catch (SqlTypeException)
        {
            return Task.FromResult(EquipmentEditorSaveResult.Reject("Недопустимая дата начала периода."));
        }
        if (_model?.Id != equipmentId || CurrentUser is not { } actor || _activeEditor is not { } editor ||
            ActiveDraftScope != PeriodDraftScope(kind, normalized.Ticks))
            return Task.FromResult(EquipmentEditorSaveResult.Reject("Контекст периода изменился. Откройте редактор заново."));

        var pageVersion = _operationVersion;
        var attemptId = Guid.NewGuid();
        var isLocation = IsLocationPeriodCommand(kind);
        var label = kind switch
        {
            EquipmentFormService.AddLocationPeriodCommand => "Добавление расположения",
            EquipmentFormService.UpdateLocationPeriodCommand => "Изменение расположения",
            _ => "Сохранение ответственного",
        };
        return SaveChildDraftAsync(label,
            isLocation ? "Расположение сохранено." : "Сведения об ответственном сохранены.", new { FromUtcTicks = normalized.Ticks },
            () => write(actor.Id, attemptId), () => RefreshJournaledPeriodAsync(isLocation),
            () => IsCurrent(pageVersion, equipmentId) && _model?.Id == equipmentId && _locationVersion == locationVersion &&
                CurrentUser?.Id == actor.Id && ReferenceEquals(_activeEditor, editor), attemptId: attemptId, commandKind: kind);
    }

    private async Task RefreshJournaledPeriodAsync(bool locationFirst)
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

    private async Task ReconcilePeriodWriteReceiptAsync(ClayDraftRegistration registration, ClayDraftOperation operation)
    {
        _warningMessage = "Серверный исход изменения периода не подтверждён. Повторная запись заблокирована.";
        if (!Guid.TryParseExact(operation.AttemptId, "N", out var attemptId) || attemptId == Guid.Empty ||
            _model is not { } model || CurrentUser is not { } actor || operation.Scopes is not [{ Name: var scope }])
            return;

        var version = _operationVersion;
        bool Current() => IsCurrentSave(model, version) && CurrentUser?.Id == actor.Id &&
            ReferenceEquals(_draftRegistration, registration) && _draftOperation?.AttemptId == operation.AttemptId;
        try
        {
            using var document = JsonDocument.Parse(operation.Data);
            var payload = document.RootElement;
            var location = IsLocationPeriodCommand(operation.Kind);
            if (!payload.TryGetProperty("Kind", out var kind) || kind.GetString() != operation.Kind ||
                !payload.TryGetProperty("EquipmentId", out var equipment) || !equipment.TryGetInt32(out var equipmentId) ||
                equipmentId != model.Id || !payload.TryGetProperty("Editor", out var editor) ||
                editor.GetString() != (location ? nameof(EquipmentLocationDialog) : nameof(EquipmentResponsibleDialog)) ||
                !payload.TryGetProperty("Intent", out var intent) || !intent.TryGetProperty("FromUtcTicks", out var key) ||
                !key.TryGetInt64(out var ticks) || ticks <= 0 || ticks > DateTime.MaxValue.Ticks ||
                scope != PeriodDraftScope(operation.Kind, ticks))
                return;

            var receipt = await Service.ReadPeriodWriteReceiptAsync(model.Id, new DateTime(ticks, DateTimeKind.Utc),
                operation.Kind, actor.Id, attemptId, _pageCts.Token);
            if (!Current() || receipt?.AttemptId != attemptId) return;
            if (receipt.State == InventoryOperationState.Rejected)
            {
                await RejectDraftOperationAsync(operation.Revision);
                _warningMessage = "Сервер подтвердил отказ изменения периода. Введённые данные сохранены на форме.";
                return;
            }
            if (receipt.State != InventoryOperationState.Confirmed || receipt.ResultEntityId != model.Id)
                return;

            await RefreshJournaledPeriodAsync(location);
            if (!Current()) return;
            await RecordConfirmedChildOperationAsync(registration, operation);
            if (!Current()) return;
            await CommitDraftOperationAsync(operation.Revision);
            if (IsCurrentSave(model, version) && CurrentUser?.Id == actor.Id)
            {
                _warningMessage = null;
                ShowStatus("Сервер подтвердил изменение периода. Повторная запись не отправлялась.");
            }
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or UnauthorizedAccessException or
            SqlTypeException or JsonException or JSException or TimeoutException or OperationCanceledException)
        {
            if (Current()) _warningMessage = "Не удалось сверить серверный исход изменения периода. Повторная запись заблокирована.";
        }
    }

    private static bool IsLocationPeriodCommand(string kind) =>
        kind is EquipmentFormService.AddLocationPeriodCommand or EquipmentFormService.UpdateLocationPeriodCommand;
}
