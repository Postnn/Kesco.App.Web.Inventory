using System.Text.Json;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.Data.SqlClient;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentIns
{
    private IReadOnlyList<ClayDraftRecoveryCandidate> _creationRecoveryCandidates = [];
    private bool _creationRecoveryReady;
    private bool _creationRecoveryBusy;
    private string? _creationRecoveryNotice;

    private bool CreationRecoveryBlocked =>
        !_creationRecoveryReady || _creationRecoveryBusy || _creationRecoveryCandidates.Any(candidate => candidate.HasOperation);

    private bool CanRecoverCreationDraft => !_disposed && !_loading && !_saving && !_applyingCreationRaw &&
        !_creationRecoveryBusy && !PreservesCreationOutcome && !HasUnacceptedCreationInput &&
        !_editorRestored && _lastEditorDraft is null;

    private async Task ScanCreationRecoveryAsync(ClayDraftRegistration registration, int version, CancellationToken ct)
    {
        if (!IsCurrentCreationInput(registration, version)) return;
        _creationRecoveryReady = false;
        try
        {
            var candidates = await registration.ListRecoveryCandidatesAsync(ct);
            if (!IsCurrentCreationInput(registration, version)) return;
            if (candidates is null)
                throw new InvalidOperationException("Ответ поиска черновиков отсутствует.");

            _creationRecoveryCandidates = candidates;
            _creationRecoveryReady = true;
            _creationRecoveryNotice = candidates.Any(candidate => candidate.HasOperation)
                ? "Есть незавершённая попытка создания. Откройте её для сверки перед новой записью."
                : null;
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or OperationCanceledException)
        {
            if (IsCurrentCreationInput(registration, version))
                _creationRecoveryNotice = "Не удалось проверить прежние попытки создания. Нажмите «Обновить», чтобы повторить проверку.";
        }
    }

    private async Task RecoverCreationDraftAsync(ClayDraftRecoveryCandidate candidate)
    {
        if (!CanRecoverCreationDraft || !candidate.CanRecover || !_creationRecoveryCandidates.Contains(candidate) ||
            CurrentUser is not { } previousActor || _creationDraft is not { } registration)
            return;

        var version = _operationVersion;
        var source = SourceKey;
        var live = Snapshot();
        bool Current() => IsCurrentCreationInput(registration, version) && SourceKey == source;
        _creationRecoveryBusy = true;
        StateHasChanged();
        try
        {
            var actor = await Service.GetCurrentUserAsync(await PrincipalAsync(), _pageCts.Token);
            if (!Current()) return;
            if (actor?.Id != previousActor.Id || !actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
            {
                _creationRecoveryNotice = "Не удалось подтвердить доступ к сохранённому черновику.";
                return;
            }
            if (live != Snapshot() || !await registration.RecoverListedDraftAsync(candidate.DraftId, _pageCts.Token))
            {
                if (Current())
                    _creationRecoveryNotice = "Черновик занят или появился новый ввод. Текущие данные сохранены.";
                return;
            }
            if (!Current()) return;
            if (live != Snapshot())
            {
                _creationUncertain = true;
                _creationRecoveryNotice = "Ввод изменился во время открытия. Текущие поля оставлены на экране, выбранный черновик доступен после повторного открытия.";
                return;
            }

            RestoreCreationState(actor, registration.Restore, source);
            await LoadCreationInputsAsync(actor, version);
            if (Current())
                await ScanCreationRecoveryAsync(registration, version, _pageCts.Token);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or JsonException or SqlException or OperationCanceledException)
        {
            if (Current())
            {
                _creationRecoveryReady = false;
                _creationUncertain = registration.Restore.HasDraft;
                _creationRecoveryNotice = "Не удалось открыть сохранённый черновик. Создание заблокировано до повторной проверки.";
            }
        }
        finally
        {
            if (Current()) _creationRecoveryBusy = false;
        }
    }
}
