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
    private static string AccountingDraftScope(int id) =>
        $"child:{nameof(EquipmentAccountingDialog)}:{(id == 0 ? "new" : id.ToString(CultureInfo.InvariantCulture))}:root";

    private async Task ReconcileAccountingWriteReceiptAsync(ClayDraftRegistration registration, ClayDraftOperation operation)
    {
        _warningMessage = "Серверный исход изменения бухгалтерского учёта не подтверждён. Повторная запись заблокирована.";
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
                !payload.TryGetProperty("Intent", out var intent) || !intent.TryGetProperty("AccountingId", out var key) ||
                !key.TryGetInt32(out var accountingId) || accountingId < 0 ||
                (accountingId == 0) != (operation.Kind == EquipmentFormService.InsertAccountingCommand))
                return;
            if (operation.Kind == EquipmentFormService.DeleteAccountingCommand
                ? operation.Scopes.Count != 0 || editor.ValueKind != JsonValueKind.Null
                : editor.GetString() != nameof(EquipmentAccountingDialog) || operation.Scopes.Count != 1 ||
                    operation.Scopes[0].Name != AccountingDraftScope(accountingId))
                return;

            var receipt = await Service.ReadAccountingWriteReceiptAsync(model.Id, accountingId, operation.Kind,
                actor.Id, attemptId, _pageCts.Token);
            if (!Current() || receipt?.AttemptId != attemptId) return;
            if (receipt.State == InventoryOperationState.Rejected)
            {
                await RejectDraftOperationAsync(operation.Revision);
                _warningMessage = "Сервер подтвердил отказ изменения бухгалтерского учёта. Введённые данные сохранены на форме.";
                return;
            }
            if (receipt.State != InventoryOperationState.Confirmed || receipt.ResultEntityId is not > 0 ||
                accountingId > 0 && receipt.ResultEntityId != accountingId)
                return;

            await RefreshAccountingAsync();
            if (!Current()) return;
            await RecordConfirmedChildOperationAsync(registration, operation);
            if (!Current()) return;
            await CommitDraftOperationAsync(operation.Revision);
            if (IsCurrentSave(model, version) && CurrentUser?.Id == actor.Id)
            {
                _warningMessage = null;
                ShowStatus("Сервер подтвердил изменение бухгалтерского учёта. Повторная запись не отправлялась.");
            }
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or UnauthorizedAccessException or
            JsonException or JSException or TimeoutException or OperationCanceledException)
        {
            if (Current()) _warningMessage = "Не удалось сверить серверный исход бухгалтерского учёта. Повторная запись заблокирована.";
        }
    }
}
