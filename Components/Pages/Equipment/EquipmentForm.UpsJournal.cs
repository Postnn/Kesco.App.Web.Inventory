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
    private static string UpsDraftScope(int id) => $"child:{nameof(EquipmentUpsDialog)}:{id.ToString(CultureInfo.InvariantCulture)}:root";

    private async Task ReconcileUpsWriteReceiptAsync(ClayDraftRegistration registration, ClayDraftOperation operation)
    {
        _warningMessage = "Серверный исход операции UPS не подтверждён. Повторная запись заблокирована.";
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
                !payload.TryGetProperty("Intent", out var intent) || !intent.TryGetProperty("UpsEquipmentId", out var ups) ||
                !ups.TryGetInt32(out var upsId) || upsId <= 0 || upsId == model.Id)
                return;
            if (operation.Kind == EquipmentFormService.SaveUpsDetailsCommand
                ? editor.GetString() != nameof(EquipmentUpsDialog) || operation.Scopes is not [{ Name: var scope }] || scope != UpsDraftScope(upsId)
                : editor.ValueKind != JsonValueKind.Null || operation.Scopes.Count != 0)
                return;

            var receipt = await Service.ReadUpsWriteReceiptAsync(upsId, model.Id, operation.Kind, actor.Id, attemptId, _pageCts.Token);
            if (!Current() || receipt?.AttemptId != attemptId) return;
            if (receipt.State == InventoryOperationState.Rejected)
            {
                await RejectDraftOperationAsync(operation.Revision);
                _warningMessage = "Сервер подтвердил отказ операции UPS. Введённые данные сохранены на форме.";
                return;
            }
            if (receipt.State != InventoryOperationState.Confirmed || receipt.ResultEntityId != model.Id)
                return;

            await RefreshUpsLinksAsync();
            if (!Current()) return;
            await RecordConfirmedChildOperationAsync(registration, operation);
            if (!Current()) return;
            await CommitDraftOperationAsync(operation.Revision);
            if (IsCurrentSave(model, version) && CurrentUser?.Id == actor.Id)
            {
                _warningMessage = null;
                ShowStatus("Сервер подтвердил операцию UPS. Повторная запись не отправлялась.");
            }
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or UnauthorizedAccessException or
            JsonException or JSException or TimeoutException or OperationCanceledException)
        {
            if (Current()) _warningMessage = "Не удалось сверить серверный исход операции UPS. Повторная запись заблокирована.";
        }
    }
}
