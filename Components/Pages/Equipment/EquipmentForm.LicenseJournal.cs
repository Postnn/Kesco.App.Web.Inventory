using System.Data.Common;
using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.JSInterop;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private const string LicenseDraftScope = "child:" + nameof(EquipmentLicenseDialog) + ":new:root";

    private async Task<bool> CheckLicenseEditorAvailableAsync(EquipmentEntity model, Employee actor, int version)
    {
        bool Current() => IsCurrentSave(model, version) && CurrentUser?.Id == actor.Id;
        try
        {
            var pending = await Service.ReadLicenseWritePendingAsync(model.Id, actor.Id, _pageCts.Token);
            if (!Current()) return false;
            if (!pending) return true;
            _warningMessage = "В серверном журнале есть незавершённая операция с оборудованием. Повтор записи заблокирован.";
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or UnauthorizedAccessException or
            TimeoutException or OperationCanceledException)
        {
            if (!Current()) return false;
            _warningMessage = "Не удалось проверить серверный журнал лицензии. Запись заблокирована.";
        }
        StateHasChanged();
        return false;
    }

    private async Task ReconcileLicenseWriteReceiptAsync(ClayDraftRegistration registration, ClayDraftOperation operation)
    {
        _warningMessage = "Серверный исход изменения лицензии не подтверждён. Повторная запись заблокирована.";
        if (!Guid.TryParseExact(operation.AttemptId, "N", out var attemptId) || attemptId == Guid.Empty ||
            _model is not { } model || CurrentUser is not { } actor || operation.Scopes is not [{ Name: LicenseDraftScope }])
            return;

        var version = _operationVersion;
        bool Current() => IsCurrentSave(model, version) && CurrentUser?.Id == actor.Id &&
            ReferenceEquals(_draftRegistration, registration) && _draftOperation?.AttemptId == operation.AttemptId;
        try
        {
            using var document = JsonDocument.Parse(operation.Data);
            var payload = document.RootElement;
            if (!payload.TryGetProperty("Kind", out var kind) || kind.GetString() != operation.Kind ||
                !payload.TryGetProperty("EquipmentId", out var equipment) || !equipment.TryGetInt32(out var equipmentId) ||
                equipmentId != model.Id || !payload.TryGetProperty("Editor", out var editor) ||
                editor.GetString() != nameof(EquipmentLicenseDialog) || !payload.TryGetProperty("Intent", out var intent) ||
                !intent.TryGetProperty("ExpectedExisting", out var existing) ||
                existing.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                existing.GetBoolean() != (operation.Kind == EquipmentFormService.UpdateLicenseCommand))
                return;

            var receipt = await Service.ReadLicenseWriteReceiptAsync(model.Id, existing.GetBoolean(), actor.Id, attemptId, _pageCts.Token);
            if (!Current() || receipt?.AttemptId != attemptId) return;
            if (receipt.State == InventoryOperationState.Rejected)
            {
                await RejectDraftOperationAsync(operation.Revision);
                _warningMessage = "Сервер подтвердил отказ изменения лицензии. Введённые данные сохранены на форме.";
                return;
            }
            if (receipt.State != InventoryOperationState.Confirmed || receipt.ResultEntityId != model.Id)
                return;

            await RefreshLicensesAsync();
            if (!Current()) return;
            await RecordConfirmedChildOperationAsync(registration, operation);
            if (!Current()) return;
            await CommitDraftOperationAsync(operation.Revision);
            if (IsCurrentSave(model, version) && CurrentUser?.Id == actor.Id)
            {
                _warningMessage = null;
                ShowStatus("Сервер подтвердил изменение лицензии. Повторная запись не отправлялась.");
            }
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or UnauthorizedAccessException or
            JsonException or JSException or TimeoutException or OperationCanceledException)
        {
            if (Current()) _warningMessage = "Не удалось сверить серверный исход лицензии. Повторная запись заблокирована.";
        }
    }
}
