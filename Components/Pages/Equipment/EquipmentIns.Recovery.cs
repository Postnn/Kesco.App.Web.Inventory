using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentIns
{
    private IReadOnlyList<ClayDraftRecoveryCandidate> _creationRecoveryCandidates = [];
    private bool _creationRecoveryReady;
    private string? _creationRecoveryNotice;

    private bool CreationRecoveryBlocked =>
        !_creationRecoveryReady || _creationRecoveryCandidates.Any(candidate => candidate.HasOperation);

    private async Task ScanCreationRecoveryAsync(ClayDraftRegistration registration, int version, CancellationToken ct, Employee? actor = null)
    {
        if (!IsCurrentCreationInput(registration, version)) return;
        _creationRecoveryReady = false;
        try
        {
            var candidates = await registration.ListRecoveryCandidatesAsync(ct);
            if (!IsCurrentCreationInput(registration, version)) return;
            if (candidates is null)
                throw new InvalidOperationException("Ответ поиска черновиков отсутствует.");

            if (actor?.HasRole(Роли.Специалист_по_инвентаризации_оборудования) == true &&
                !registration.Restore.HasDraft && registration.Restore.OperationSnapshot is null &&
                candidates is [{ CanRecover: true } candidate] &&
                await registration.RecoverListedDraftAsync(candidate.DraftId, ct))
            {
                if (!IsCurrentCreationInput(registration, version)) return;
                candidates = [];
            }

            _creationRecoveryCandidates = candidates;
            _creationRecoveryReady = true;
            _creationRecoveryNotice = candidates.Any(candidate => candidate.HasOperation)
                ? "Есть незавершённая попытка создания. Повторное создание заблокировано."
                : null;
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or OperationCanceledException)
        {
            if (IsCurrentCreationInput(registration, version))
                _creationRecoveryNotice = "Не удалось проверить прежние попытки создания. Нажмите «Обновить», чтобы повторить проверку.";
        }
    }
}
