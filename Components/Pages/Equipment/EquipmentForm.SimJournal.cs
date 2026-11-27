using System.Data.Common;
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
    private const string SimDraftScope = "child:" + nameof(EquipmentSimDialog) + ":new:root";
    private static string SimServiceDraftScope(int id) =>
        $"child:{nameof(EquipmentSimServiceDialog)}:{(id == 0 ? "new" : id.ToString(CultureInfo.InvariantCulture))}:root";

    private async Task ReconcileSimWriteReceiptAsync(ClayDraftRegistration registration, ClayDraftOperation operation)
    {
        _warningMessage = "Серверный исход операции SIM не подтверждён. Повторная запись заблокирована.";
        if (!Guid.TryParseExact(operation.AttemptId, "N", out var attemptId) || attemptId == Guid.Empty ||
            _model is not { } model || CurrentUser is not { } actor)
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
                !payload.TryGetProperty("Intent", out var intent))
                return;

            var service = operation.Kind is EquipmentFormService.InsertSimServiceCommand or EquipmentFormService.UpdateSimServiceCommand;
            InventoryOperationReceipt? receipt;
            int? expectedResult;
            if (service)
            {
                if (!intent.TryGetProperty("ServiceId", out var serviceKey) || !serviceKey.TryGetInt32(out var serviceId) ||
                    serviceId < 0 || (serviceId == 0) != (operation.Kind == EquipmentFormService.InsertSimServiceCommand) ||
                    editor.GetString() != nameof(EquipmentSimServiceDialog) ||
                    operation.Scopes is not [{ Name: var scope }] || scope != SimServiceDraftScope(serviceId))
                    return;

                expectedResult = serviceId > 0 ? serviceId : null;
                receipt = await Service.ReadSimServiceWriteReceiptAsync(model.Id, serviceId, actor.Id, attemptId, _pageCts.Token);
            }
            else
            {
                if (!intent.TryGetProperty("DuplicateEquipmentId", out var duplicate)) return;
                int? duplicateId = null;
                if (duplicate.ValueKind != JsonValueKind.Null)
                {
                    if (!duplicate.TryGetInt32(out var id) || id <= 0 || id == model.Id) return;
                    duplicateId = id;
                }
                if (operation.Kind == EquipmentFormService.DeleteSimCommand
                    ? duplicateId is not null || operation.Scopes.Count != 0 || editor.ValueKind != JsonValueKind.Null
                    : editor.GetString() != nameof(EquipmentSimDialog) || operation.Scopes is not [{ Name: SimDraftScope }])
                    return;

                expectedResult = model.Id;
                receipt = await Service.ReadSimWriteReceiptAsync(model.Id, duplicateId, operation.Kind, actor.Id, attemptId, _pageCts.Token);
            }
            if (!Current() || receipt?.AttemptId != attemptId) return;
            if (receipt.State == InventoryOperationState.Rejected)
            {
                await RejectDraftOperationAsync(operation.Revision);
                _warningMessage = "Сервер подтвердил отказ операции SIM. Введённые данные сохранены на форме.";
                return;
            }
            if (receipt.State != InventoryOperationState.Confirmed || receipt.ResultEntityId is not > 0 ||
                expectedResult is int expected && receipt.ResultEntityId != expected)
                return;

            if (service) await RefreshSimServicesAsync();
            else await RefreshSimAsync();
            if (!Current()) return;
            await RecordConfirmedChildOperationAsync(registration, operation);
            if (!Current()) return;
            await CommitDraftOperationAsync(operation.Revision);
            if (IsCurrentSave(model, version) && CurrentUser?.Id == actor.Id)
            {
                _warningMessage = null;
                ShowStatus("Сервер подтвердил операцию SIM. Повторная запись не отправлялась.");
            }
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or UnauthorizedAccessException or
            JsonException or JSException or TimeoutException or OperationCanceledException)
        {
            if (Current()) _warningMessage = "Не удалось сверить серверный исход операции SIM. Повторная запись заблокирована.";
        }
    }
}
