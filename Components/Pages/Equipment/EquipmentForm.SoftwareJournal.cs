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
    private static string SoftwareDraftScope(int installationId) =>
        $"child:{nameof(EquipmentSoftwareDialog)}:{(installationId == 0 ? "new" : installationId.ToString(CultureInfo.InvariantCulture))}:root";

    private async Task ReconcileSoftwareWriteReceiptAsync(ClayDraftRegistration registration, ClayDraftOperation operation)
    {
        _warningMessage = "Серверный исход установки ПО не подтверждён. Повторная запись заблокирована.";
        if (!Guid.TryParseExact(operation.AttemptId, "N", out var attemptId) || attemptId == Guid.Empty ||
            _model is not { } model || CurrentUser is not { } actor || operation.Scopes.Count != 1)
            return;

        var version = _operationVersion;
        bool Current() => IsCurrentSave(model, version) && CurrentUser?.Id == actor.Id &&
            ReferenceEquals(_draftRegistration, registration) && _draftOperation?.AttemptId == operation.AttemptId;
        try
        {
            using var document = JsonDocument.Parse(operation.Data);
            var payload = document.RootElement;
            if (!payload.TryGetProperty("Kind", out var kind) || kind.GetString() != operation.Kind ||
                !payload.TryGetProperty("EquipmentId", out var equipmentId) || !equipmentId.TryGetInt32(out var equipmentKey) ||
                equipmentKey != model.Id ||
                !payload.TryGetProperty("Editor", out var editor) || editor.GetString() != nameof(EquipmentSoftwareDialog) ||
                !payload.TryGetProperty("Intent", out var intent) || !intent.TryGetProperty("InstallationId", out var key) ||
                !key.TryGetInt32(out var installationId) || installationId < 0 ||
                (installationId == 0) != (operation.Kind == EquipmentFormService.InsertSoftwareCommand) ||
                operation.Scopes[0].Name != SoftwareDraftScope(installationId))
                return;

            var receipt = await Service.ReadSoftwareWriteReceiptAsync(model.Id, installationId, actor.Id, attemptId, _pageCts.Token);
            if (!Current() || receipt?.AttemptId != attemptId) return;
            if (receipt.State == InventoryOperationState.Rejected)
            {
                await RejectDraftOperationAsync(operation.Revision);
                _warningMessage = "Сервер подтвердил отказ изменения установки ПО. Введённые данные сохранены на форме.";
                return;
            }
            if (receipt.State != InventoryOperationState.Confirmed || receipt.ResultEntityId is not > 0 ||
                installationId > 0 && receipt.ResultEntityId != installationId)
                return;

            var installations = await Service.GetSoftwareInstallationsAsync(model.Id);
            if (!Current()) return;
            await RecordConfirmedChildOperationAsync(registration, operation);
            if (!Current()) return;
            await CommitDraftOperationAsync(operation.Revision);
            if (IsCurrentSave(model, version) && CurrentUser?.Id == actor.Id)
            {
                _softwareInstallations = installations.ToList();
                _warningMessage = null;
                await RefreshLocationDisplayDatesAsync();
                ShowStatus("Сервер подтвердил изменение установки ПО. Повторная запись не отправлялась.");
            }
        }
        catch (Exception error) when (error is DbException or InvalidOperationException or UnauthorizedAccessException or
            JsonException or JSException or TimeoutException or OperationCanceledException)
        {
            if (Current()) _warningMessage = "Не удалось сверить серверный исход установки ПО. Повторная запись заблокирована.";
        }
    }
}
