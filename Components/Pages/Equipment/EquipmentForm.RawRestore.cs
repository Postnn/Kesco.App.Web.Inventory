using Clayzor.Lib.Entities;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.Data.SqlClient;
using Microsoft.JSInterop;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private const string MainDraftScope = "main";
    private const string MainRawAccessError = "Не удалось подтвердить доступ к восстановлению ввода. Черновик сохранён.";

    private readonly ClayToolbarButtonOptions _retryMainRawOptions = new()
    {
        Text = "Повторить восстановление ввода",
        Icon = Icons.Material.Filled.Refresh,
    };

    private bool _mainRawRestorePending;
    private bool _restoringMainRaw;

    private bool MainRawReady => !_draftRestoring && !_restoringMainRaw && !_mainRawRestorePending;
    private bool CanRetryMainRaw => !_disposed && _loaded && !_loading && !_saving &&
        !_draftRestoring && !_restoringMainRaw && !_mainTransition && !_mainDraftCancelled;

    private bool OwnsMainDraft(ClayDraftRegistration registration, int equipmentId) =>
        !_disposed && Id == equipmentId && _model?.Id == equipmentId && ReferenceEquals(_draftRegistration, registration);

    private static bool HasUnrestoredMainRaw(ClayDraftRestoreReadiness? readiness) =>
        readiness is null || readiness.PendingScopes.Contains(MainDraftScope) || readiness.FailedScopes.Contains(MainDraftScope);

    private async Task ApplyMainDraftRawAsync(ClayDraftRegistration registration, int equipmentId, int version, bool canApply)
    {
        bool Current() => OwnsMainDraft(registration, equipmentId) && IsCurrent(version, equipmentId);
        if (!Current())
            return;

        try
        {
            var pending = false;
            if (canApply)
            {
                var raw = await registration.ApplyPendingRawAsync(MainDraftScope, _pageCts.Token);
                pending = raw is null || raw.Pending.Count != 0;
            }
            if (!Current())
                return;

            var readiness = await registration.GetRestoreReadinessAsync(_pageCts.Token);
            if (Current())
                _mainRawRestorePending = pending || HasUnrestoredMainRaw(readiness);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            if (Current())
                _mainRawRestorePending = true;
        }
    }

    private async Task ObserveMainDraftReadinessAsync(ClayDraftRegistration registration, int equipmentId)
    {
        try
        {
            var readiness = await registration.GetRestoreReadinessAsync(_pageCts.Token);
            if (OwnsMainDraft(registration, equipmentId) && HasUnrestoredMainRaw(readiness))
                _mainRawRestorePending = true;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            // The command gate has already rejected the write and reports its original error.
        }
    }

    private async Task RetryMainDraftRawAsync()
    {
        if (!_mainRawRestorePending || !CanRetryMainRaw || !_canEdit || CurrentUser is not { } previousActor ||
            _draftRegistration is not { } registration || _model is not { } model)
            return;

        var version = _operationVersion;
        bool Current() => OwnsMainDraft(registration, model.Id) && IsCurrent(version, model.Id) &&
            CurrentUser?.Id == previousActor.Id;
        _restoringMainRaw = true;
        try
        {
            var actor = await Service.GetCurrentUserWithRolesAsync();
            if (!Current())
                return;
            if (actor?.Id != previousActor.Id || !actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
            {
                _validationMessage = MainRawAccessError;
                return;
            }

            await ApplyMainDraftRawAsync(registration, model.Id, version, canApply: true);
            if (Current() && !_mainRawRestorePending && _validationMessage == MainRawAccessError)
                _validationMessage = null;
        }
        catch (Exception ex) when (ex is SqlException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            if (Current())
                _validationMessage = MainRawAccessError;
        }
        finally
        {
            if (ReferenceEquals(_draftRegistration, registration))
                _restoringMainRaw = false;
            if (!_disposed)
                StateHasChanged();
        }
    }
}
