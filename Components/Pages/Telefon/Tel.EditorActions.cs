using System.Text.Json;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Services;
using Microsoft.Data.SqlClient;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;

namespace Kesco.App.Web.Inventory.Components.Pages.Telefon;

public partial class Tel
{
    private bool _telEditorTransition;
    private long? _telCloseRevision;

    private bool HasUnresolvedTelEditorWrite =>
        _telMarker is not null || _telProgress is not null || _notSentAttemptId is not null || HasPendingSync;

    private bool HasTelParentInput =>
        HasUnsavedDraft || !string.IsNullOrEmpty(_parallelQuickNumber) || TelInputBlocked;

    private async Task CancelAsync()
    {
        if (_loading || _saving || _telJournalBusy || _telChildOpen || _telEditorTransition) return;
        var version = _operationVersion;
        var route = (Id, CopyId);
        var registration = _telJournal;
        bool Current() => IsCurrent(version) && route == (Id, CopyId) && ReferenceEquals(_telJournal, registration);

        _telEditorTransition = true;
        try
        {
            if (_telCloseRevision is not null)
            {
                await CloseAsync();
                return;
            }
            var fingerprint = CurrentTelInputFingerprint();
            var scope = registration is null ? null : await registration.ReadEditorScopeAsync(TelInputScope, _pageCts.Token);
            if (!Current()) return;
            if (!HasTelParentInput && !HasUnresolvedTelEditorWrite && scope is not { HasRaw: true } and not { HasOperation: true })
            {
                await CloseAsync(scope?.Revision);
                return;
            }

            var dialog = await DialogService.ShowExAsync<ClayEditorCancelDialog>("Отмена изменений",
                new DialogParameters(), new DialogOptionsEx
                {
                    DragMode = MudDialogDragMode.Simple, CloseOnEscapeKey = true, CloseButton = true,
                });
            var result = await dialog.Result;
            if (!Current() || result is not { Canceled: false, Data: ClayEditorCancelChoice choice } ||
                choice == ClayEditorCancelChoice.Continue)
                return;

            if (HasUnresolvedTelEditorWrite || scope?.HasOperation == true)
            {
                if (choice == ClayEditorCancelChoice.CloseWithoutSaving && fingerprint == CurrentTelInputFingerprint())
                    await CloseAsync(scope?.Revision);
                else
                    _telInputNotice = "Сначала требуется сверка предыдущей операции. Ввод и журнал сохранены.";
                return;
            }
            if (registration is null || scope is null || fingerprint != CurrentTelInputFingerprint())
            {
                _telInputNotice = "Ввод не удалось безопасно отменить. Черновик сохранён.";
                return;
            }

            if (choice == ClayEditorCancelChoice.CloseWithoutSaving)
            {
                var actor = await Service.GetCurrentUserAsync(await ReadPrincipalAsync(), _pageCts.Token);
                if (!Current()) return;
                if (actor?.Id != _telJournalActor || !actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования) ||
                    fingerprint != CurrentTelInputFingerprint())
                {
                    _telInputNotice = "Владелец, право записи или ввод изменились. Черновик сохранён.";
                    return;
                }
                await CloseAsync(scope.Revision);
                return;
            }

            await ResetTelEditorAsync(registration, scope.Revision, fingerprint, Current);
        }
        catch (Exception ex) when (IsEditorTransitionError(ex))
        {
            if (Current()) _telInputNotice = "Не удалось завершить отмену. Ввод сохранён.";
        }
        finally { _telEditorTransition = false; }
    }

    private async Task RefreshAsync()
    {
        if (_model is null || _loading || _saving || _telJournalBusy || _telChildOpen || _telEditorTransition) return;
        var refreshVersion = _operationVersion;
        if (_telInputSaveFailed && !_telRawPending)
            await RetryTelInputAsync();
        if (!IsCurrent(refreshVersion)) return;
        if (_telRawPending)
            await RetryTelRawAsync();
        if (!IsCurrent(refreshVersion) || _telRawPending || _telInputSaveFailed) return;
        if (_telJournalNotice is not null && _telJournal is not null)
            await CompleteOrCheckTelJournalAsync();
        if (!IsCurrent(refreshVersion) || _deleted || _model is null || _loading || _saving || _telJournalBusy || _telChildOpen || _telEditorTransition) return;
        if (HasUnresolvedTelEditorWrite)
        {
            await ObserveTelWriteAsync();
            return;
        }
        var version = _operationVersion;
        var route = (Id, CopyId);
        var registration = _telJournal;
        bool Current() => IsCurrent(version) && route == (Id, CopyId) && ReferenceEquals(_telJournal, registration);

        _telEditorTransition = true;
        try
        {
            if (_telCloseRevision is not null)
            {
                await CloseAsync();
                return;
            }
            var fingerprint = CurrentTelInputFingerprint();
            var scope = registration is null ? null : await registration.ReadEditorScopeAsync(TelInputScope, _pageCts.Token);
            if (!Current()) return;
            if (HasTelParentInput || scope is { HasRaw: true } or { HasOperation: true })
            {
                var available = Id <= 0 && CopyId <= 0 || await ReadFreshTelEditorAsync(requireEdit: false, Current) is not null;
                if (Current())
                    _telInputNotice = available ? "Ввод оставлен в открытой форме. Для сброса используйте «Отмена»."
                        : "Не удалось прочитать актуальный номер. Ввод сохранён.";
                return;
            }
            var fresh = await ReadFreshTelEditorAsync(requireEdit: false, Current);
            if (!Current()) return;
            var now = registration is null ? null : await registration.ReadEditorScopeAsync(TelInputScope, _pageCts.Token);
            if (!Current()) return;
            if (fresh is null || now != scope || fingerprint != CurrentTelInputFingerprint())
            {
                _telInputNotice = "Не удалось безопасно обновить номер. Ввод сохранён.";
                return;
            }
            PublishFreshTelEditor(fresh);
        }
        catch (Exception ex) when (IsEditorTransitionError(ex))
        {
            if (Current()) _telInputNotice = "Не удалось прочитать актуальный номер. Ввод сохранён.";
        }
        finally { _telEditorTransition = false; }
    }

    private async Task ResetTelEditorAsync(ClayDraftRegistration registration, long revision, string fingerprint, Func<bool> current)
    {
        var fresh = await ReadFreshTelEditorAsync(requireEdit: true, current);
        if (!current()) return;
        if (fresh is null || fingerprint != CurrentTelInputFingerprint())
        {
            _telInputNotice = "Не удалось прочитать актуальный номер или ввод уже изменился. Черновик сохранён.";
            return;
        }
        var actor = await Service.GetCurrentUserAsync(await ReadPrincipalAsync(), _pageCts.Token);
        if (!current()) return;
        if (actor?.Id != fresh.Actor.Id || !actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования) ||
            HasUnresolvedTelEditorWrite || fingerprint != CurrentTelInputFingerprint())
        {
            _telInputNotice = "Владелец, право записи или ввод изменились. Черновик сохранён.";
            return;
        }

        var phone = ClonePhone(fresh.Data.Phone);
        phone.FullNumber = PhoneFormValidator.CalculateFullNumber(phone.Number, fresh.Dependencies.Displayed?.FullNumber,
            fresh.Dependencies.Driven?.FullNumber, fresh.Dependencies.Station);
        var original = Id > 0 ? phone : new Phone();
        var originalRelations = Id > 0 ? fresh.Data.Relations : new PhoneRelationsSnapshot();
        var replacement = JsonSerializer.Serialize(new TelEditorDraft(1, Id, CopyId, original,
            TelDraftRelations.From(originalRelations), phone, TelDraftRelations.From(fresh.Data.Relations),
            phone.ReturnDate is not null, phone.ServicesKind, phone.ViewInDirectory, string.Empty));
        if (!await registration.ReplaceEditorScopeAsync(TelInputScope, revision, replacement, _pageCts.Token))
        {
            if (current()) _telInputNotice = "Ввод изменился. Подтвердите сброс ещё раз.";
            return;
        }
        if (!current()) return;
        if (fingerprint != CurrentTelInputFingerprint())
        {
            await KeepLateEditorInputAsync(registration, current);
            return;
        }
        PublishFreshTelEditor(fresh);
    }

    private async Task KeepLateEditorInputAsync(ClayDraftRegistration registration, Func<bool> current)
    {
        _scheduledTelInput = null;
        _telInputNotice = "Поздний ввод оставлен в открытой форме.";
        if (!current() || _telOriginalMissing) return;
        var json = SerializeTelInput();
        await PersistTelInputScopeAsync(registration, json);
        if (current() && json == SerializeTelInput()) _scheduledTelInput = json;
    }

    private async Task<FreshTelEditor?> ReadFreshTelEditorAsync(bool requireEdit, Func<bool> current)
    {
        var id = Id;
        var copy = CopyId;
        var expectedActor = CurrentUser?.Id;
        ErrorService.Clear();
        var actor = await Service.GetCurrentUserAsync(await ReadPrincipalAsync(), _pageCts.Token);
        var canEdit = actor?.HasRole(Роли.Специалист_по_инвентаризации_оборудования) == true;
        if (!current() || actor is null || actor.Id != expectedActor || requireEdit && !canEdit) return null;
        var data = id > 0 ? await Service.GetAsync(id, currentUser: actor) :
            copy > 0 ? await Service.GetAsync(copy, copy: true, currentUser: actor) : new PhoneFormData { CanEdit = canEdit };
        if (!current() || data is null || requireEdit && !data.CanEdit ||
            data.Phone.Id != Math.Max(0, id) || ErrorService.HasError)
            return null;
        if (id > 0)
            PhoneSaveBuilder.RequireOriginalVersion(new PhoneSaveRequest { Phone = data.Phone, OriginalPhone = data.Phone });
        var dependencies = await ReadEditorDependenciesAsync(data.Phone, data.Phone.StationId, current);
        return dependencies is null || !current() || ErrorService.HasError
            ? null : new FreshTelEditor(actor, canEdit, data, dependencies);
    }

    private void PublishFreshTelEditor(FreshTelEditor fresh)
    {
        _operationVersion++;
        ResetTelInput();
        _appliedTelRestore = _telJournal?.Restore;
        _groupChoice = null;
        _selectedGroupChoice = null;
        _equipmentChoice = null;
        _selectedEquipmentChoice = null;
        _groupSelectVersion++;
        _equipmentSelectVersion++;
        _socketSelectVersion++;
        _validationMessage = _warningMessage = _loadError = null;
        PublishEditor(fresh.Actor, fresh.CanEdit, Id, fresh.Data.Phone, fresh.Data.Relations, fresh.Data.Memberships,
            null, fresh.Dependencies);
    }

    private static bool IsEditorTransitionError(Exception error) =>
        IsTelJournalError(error) || error is SqlException or UnauthorizedAccessException;

    private sealed record FreshTelEditor(Employee Actor, bool CanEdit, PhoneFormData Data, TelEditorDependencies Dependencies);

    private async Task<TelEditorDependencies?> ReadEditorDependenciesAsync(Phone model, int? originalStationId, Func<bool> current)
    {
        var selectedType = model.PhoneTypeId is not null ? await TypeSource.GetByIdAsync(model.PhoneTypeId, _pageCts.Token) : null;
        if (!current()) return null;
        var selectedStation = model.StationId is not null ? await StationSource.GetByIdAsync(model.StationId, _pageCts.Token) : null;
        if (!current()) return null;
        var originalStation = originalStationId == model.StationId ? selectedStation
            : originalStationId is not null ? await StationSource.GetByIdAsync(originalStationId, _pageCts.Token) : null;
        if (!current()) return null;
        var selectedEmployee = model.EmployeeId is not null ? await EmployeeSource.GetByIdAsync(model.EmployeeId, _pageCts.Token) : null;
        if (!current()) return null;
        var selectedDrivenBy = model.DrivenById is not null ? await DrivenSource.GetByIdAsync(model.DrivenById, _pageCts.Token) : null;
        if (!current()) return null;
        var selectedDisplayedFrom = model.DisplayedFromId is not null ? await DisplayedSource.GetByIdAsync(model.DisplayedFromId, _pageCts.Token) : null;
        if (!current()) return null;
        var capabilities = await Service.GetCapabilitiesAsync(model.StationId, model.PhoneTypeId, model.Id);
        if (!current()) return null;
        var defaultEmployeePhone = model.EmployeeId is not null ? await Service.GetDefaultEmployeePhoneAsync(model.EmployeeId.Value) : null;
        if (!current()) return null;
        return new(selectedType, selectedStation, originalStation, selectedEmployee, selectedDrivenBy, selectedDisplayedFrom,
            capabilities, defaultEmployeePhone);
    }

    private void PublishEditor(Employee? actor, bool canEdit, int id, Phone model, PhoneRelationsSnapshot freshRelations,
        IReadOnlyList<PhoneGroupMembership> memberships, TelEditorDraft? draft, TelEditorDependencies dependencies)
    {
        _model = model;
        _relations = draft is null ? freshRelations : draft.CurrentRelations.ToSnapshot(model.Id, freshRelations);
        _memberships = memberships;
        CurrentUser = actor;
        _canEdit = canEdit;
        _selectedType = dependencies.Type;
        _selectedStation = dependencies.Station;
        _originalStation = dependencies.OriginalStation;
        _selectedEmployee = dependencies.Employee;
        _selectedDrivenBy = dependencies.Driven;
        _selectedDisplayedFrom = dependencies.Displayed;
        _capabilities = dependencies.Capabilities;
        _defaultEmployeePhone = dependencies.DefaultEmployeePhone;
        _hasReturnDate = draft?.HasReturnDate ?? (model.ReturnDate is not null);
        _servicesUserChoice = draft is null ? model.ServicesKind : draft.ServicesChoice;
        _directoryUserChoice = draft is null ? model.ViewInDirectory : draft.DirectoryChoice;
        _lastNumber = model.Number;
        _parallelQuickNumber = draft?.QuickParallel ?? string.Empty;
        _lastAutoAtsDescription = string.Empty;
        RecalculateFullNumber();
        UpdateDependentState();
        _originalPhone = draft is not null ? ClonePhone(draft.Original) :
            id > 0 && !_telOriginalMissing ? ClonePhone(_model) : new Phone();
        _originalRelations = draft is not null ? draft.OriginalRelations.ToSnapshot(draft.Original.Id) :
            id > 0 && !_telOriginalMissing ? _relations.Clone() : new PhoneRelationsSnapshot();
        _originalRelationsSnapshot = SnapshotRelations(_originalRelations);
        _versionConflict = false;
        _loaded = true;
    }

    private sealed record TelEditorDependencies(PhoneTypeLookup? Type, PhoneStationLookup? Station,
        PhoneStationLookup? OriginalStation, Employee? Employee, PhoneDirectoryItem? Driven, PhoneDirectoryItem? Displayed,
        PhoneCapabilities Capabilities, (int Id, string Number)? DefaultEmployeePhone);
}
