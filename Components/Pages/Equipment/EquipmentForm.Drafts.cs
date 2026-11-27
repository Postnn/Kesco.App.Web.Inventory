using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>DR-01: долговечный черновик основной формы и открытых дочерних редакторов.</summary>
public partial class EquipmentForm
{
    private const string EquipmentDraftFormKey = "equipment-form";
    private const int DraftSchemaVersion = 2;
    private const string PhoneSyncCheckpointScope = "equipment-phone-progress";

    [Inject] private ClayDraftService Drafts { get; set; } = null!;

    private readonly string _draftOwner = Guid.NewGuid().ToString("N");
    private Guid _draftInstance = Guid.NewGuid();
    private Guid? _draftAttachingInstance;
    private ClayDraftRegistration? _draftRegistration;
    private bool _draftRegistered => _draftRegistration is not null;
    private string DraftRootSelector => $"[data-draft-owner='{_draftOwner}'], .equipment-draft-dialog-{_draftOwner}";
    private bool _draftRestoring;
    private bool _draftRestoreBlocked;
    private bool _draftPersisting;
    private bool _draftPersistPending;
    private string? _lastPersistedDraft;
    private string? _draftStorageError;
    private EquipmentPhoneSyncCheckpoint? _phoneSyncCheckpoint;
    private EquipmentDraftEditor? _activeEditor;
    private EquipmentEditorContext? _activeEditorContext;
    private string? _restoredEditorState;
    private List<UpsServerLink>? _activeUpsServers;
    private List<UpsTest>? _activeUpsTests;
    private EquipmentUpsOriginal? _upsDraftOriginal;
    private EquipmentUpsTestDraft? _upsTestDraft;
    private TaskCompletionSource _restoredEditorOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record EquipmentDraftEditor(string Kind, string? Key, int? NestedTestId = null);

    private sealed record EquipmentUpsOriginal(int UpsEquipmentId, List<UpsServerLink> Servers, List<UpsTest> Tests);

    private sealed record EquipmentMainDraftIntent(string ExpectedSnapshot, string AfterSnapshot);

    private sealed record EquipmentPhoneSyncCheckpoint(string DraftAttemptId, PhoneSyncAttempt Attempt);

    private sealed class EquipmentDraftState
    {
        public EquipmentEntity? Model { get; set; }
        public DateTime? WriteOffDate { get; set; }
        public TimeSpan? WriteOffTime { get; set; }
        public List<EquipmentPhoneLink> Phones { get; set; } = [];
        public List<EquipmentPhoneLink> OriginalPhones { get; set; } = [];
        public string OriginalSnapshot { get; set; } = string.Empty;
        public PendingPhoneSync? PendingPhoneSync { get; set; }
        public EquipmentDraftEditor? ActiveEditor { get; set; }
        public string? ActiveEditorState { get; set; }
        public List<UpsServerLink>? ActiveUpsServers { get; set; }
        public List<UpsTest>? ActiveUpsTests { get; set; }
        public EquipmentUpsOriginal? ActiveUpsOriginal { get; set; }
        public EquipmentUpsTestDraft? ActiveUpsTest { get; set; }
    }

    private string? ActiveDraftScope => _activeEditor is null
        ? null
        : $"child:{_activeEditor.Kind}:{_activeEditor.Key ?? "new"}:root";

    private bool EquipmentRecoveryWriteBlocked => !_draftRegistered || _draftRestoreBlocked ||
        _draftAttachingInstance == _draftInstance || MainCloseInProgress || _mainDraftCancelled;

    private bool IsEquipmentDraftCurrent(ClayDraftRegistration registration, int version, int id, int employeeId) =>
        OwnsMainDraft(registration, id) && IsCurrent(version, id) && CurrentUser?.Id == employeeId;

    private async Task InitializeDraftAsync()
    {
        if (_draftRegistered || _draftRestoring || _mainDraftCancelled || _draftAttachingInstance == _draftInstance ||
            CurrentUser is not { Id: > 0 } actor || _model is not { Id: > 0 } model || _loadError is not null)
            return;
        var openedId = model.Id;
        var actorId = actor.Id;
        var openedVersion = _operationVersion;
        var instance = _draftInstance;
        var entityKey = openedId.ToString(CultureInfo.InvariantCulture);
        var userKey = actorId.ToString(CultureInfo.InvariantCulture);
        var ct = _pageCts.Token;
        var initialState = SerializeDraftState();
        bool Current() => IsCurrent(openedVersion, openedId) && CurrentUser?.Id == actorId && _draftInstance == instance;
        ClayDraftRegistration? registration = null;
        _draftAttachingInstance = instance;
        StateHasChanged();
        try
        {
            registration = await Drafts.RegisterRamHandleAsync(EquipmentDraftFormKey, entityKey, userKey,
                DraftSchemaVersion, DraftRootSelector, instance, ct);
            if (!Current())
                return;
            if (registration is null)
                throw new InvalidOperationException("Экземпляр редактора не подтверждён.");
            _draftRegistration = registration;
            _draftStorageError = null;
            if (registration.Restore.HasDraft)
                await ApplyEquipmentDraftAsync(registration, openedId, openedVersion);
            else
            {
                _lastPersistedDraft = HasUnsavedDraft ? null : initialState;
                await PersistDraftAsync();
            }
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or
            OperationCanceledException or System.Data.Common.DbException or UnauthorizedAccessException or TimeoutException or JsonException)
        {
            if (Current())
            {
                _draftRestoring = false;
                _draftRestoreBlocked = _draftRegistered;
                _draftStorageError = "Не удалось открыть защищённый черновик. Нажмите «Обновить», чтобы повторить проверку.";
            }
        }
        finally
        {
            if (registration is not null && !ReferenceEquals(registration, _draftRegistration))
                await registration.DisposeAsync();
            if (_draftAttachingInstance == instance) _draftAttachingInstance = null;
            if (Current()) StateHasChanged();
        }

    }

    private async Task ApplyEquipmentDraftAsync(ClayDraftRegistration registration, int openedId, int openedVersion)
    {
        var restore = registration.Restore;
        _draftRestoring = true;
        _restoredEditorOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainState = restore.TypedState ?? restore.TypedScopes?.GetValueOrDefault("main");
        if (!string.IsNullOrWhiteSpace(mainState))
        {
            if (!await RestoreDraftIfCurrentAsync(mainState,
                    () => OwnsMainDraft(registration, openedId) && IsCurrent(openedVersion, openedId)))
            {
                return;
            }
        }
        if (!OwnsMainDraft(registration, openedId))
            return;
        if (restore.CancelledScopes?.Contains(MainDraftScope) == true)
            await registration.OpenScopeAsync(MainDraftScope, _pageCts.Token);
        RestorePhoneSyncCheckpoint(restore);
        await ApplyMainDraftRawAsync(registration, openedId, openedVersion, _canEdit);
        if (!OwnsMainDraft(registration, openedId))
            return;
        var editorTask = RestoreActiveEditorAsync();
        await Task.WhenAny(_restoredEditorOpened.Task, editorTask);
        if (!OwnsMainDraft(registration, openedId))
            return;
        _draftRestoring = false;
        _lastPersistedDraft = null;
        await PersistDraftAsync();
        StateHasChanged();
    }

    private Task RestoreDraftAsync(string json) => RestoreDraftIfCurrentAsync(json);

    private async Task<bool> RestoreDraftIfCurrentAsync(string json, Func<bool>? current = null)
    {
        var state = JsonSerializer.Deserialize<EquipmentDraftState>(json) ??
                    throw new InvalidOperationException("Черновик повреждён и не был применён.");
        if (state.Model is null || state.Model.Id != Id || _model is null)
            throw new InvalidOperationException("Черновик принадлежит другому оборудованию и не был применён.");
        var freshBaseline = _originalSnapshot;
        var baselineJson = string.IsNullOrWhiteSpace(state.OriginalSnapshot) ? freshBaseline : state.OriginalSnapshot;
        var original = JsonNode.Parse(baselineJson)?["Equipment"]?.Deserialize<EquipmentEntity>() ?? _model;
        if (original.Id != _model.Id)
            throw new InvalidOperationException("Исходная версия черновика принадлежит другому оборудованию.");
        var merge = await MergeMainStateAsync(original, state.Model, _model, state.WriteOffDate, state.WriteOffTime);
        if (current?.Invoke() == false) return false;
        _originalPhones = state.OriginalPhones;
        ApplyMainMergeResult(merge);
        _phones = state.Phones;
        if (PendingConfigure is null)
        {
            var pending = state.PendingPhoneSync;
            _pendingPhoneSync = pending?.EquipmentId == _model.Id
                ? pending : null;
            _legacyConfigurePending = PendingConfigure is not null;
            if (_legacyConfigurePending) ShowLegacyConfigurePending();
        }
        _activeEditor = state.ActiveEditor;
        _restoredEditorState = state.ActiveEditorState;
        if (_activeEditor?.Kind == nameof(EquipmentUpsDialog))
        {
            var legacy = state.ActiveUpsOriginal is null && state.ActiveEditorState is not null
                ? JsonNode.Parse(state.ActiveEditorState)
                : null;
            if (legacy?["UpsEquipmentId"]?.Deserialize<int>().ToString() != _activeEditor.Key)
                legacy = null;
            _activeUpsServers = legacy?["Servers"]?.Deserialize<List<UpsServerLink>>() ?? state.ActiveUpsServers;
            _activeUpsTests = legacy?["Tests"]?.Deserialize<List<UpsTest>>() ?? state.ActiveUpsTests;
            _upsDraftOriginal = state.ActiveUpsOriginal;
            _upsTestDraft = state.ActiveUpsTest;
            _restoredEditorState = null;
        }
        else
        {
            ClearUpsDraft();
        }
        _selectedType = _model.EquipmentTypeId is > 0
            ? await EquipmentTypeSource.GetByIdAsync(_model.EquipmentTypeId, _pageCts.Token)
            : null;
        _selectedModel = _model.ModelId is > 0
            ? await ModelSource.GetByIdAsync(_model.ModelId, _pageCts.Token)
            : null;
        ModelSource.Filter.EquipmentTypeId = _model.EquipmentTypeId;
        SetDocumentOwner(_model.OwnerId);
        UpdatePhoneSourceFilters();
        ShowStatus(_mainConflictMessage is null && freshBaseline != baselineJson
            ? "Черновик восстановлен; независимые изменения из БД объединены."
            : "Восстановлен несохранённый черновик.");
        StateHasChanged();
        return true;
    }

    private async Task PersistDraftAsync()
    {
        if (!_draftRegistered || _draftRestoring || _draftRestoreBlocked || MainCloseInProgress || _mainDraftCancelled || _model is null)
            return;
        if (_draftPersisting)
        {
            _draftPersistPending = true;
            return;
        }
        _draftPersisting = true;
        try
        {
            do
            {
                _draftPersistPending = false;
                var json = SerializeDraftState();
                if (json == _lastPersistedDraft)
                    continue;
                try
                {
                    await _draftRegistration!.SaveTypedAsync(json, _pageCts.Token);
                    _lastPersistedDraft = json;
                    _draftStorageError = null;
                }
                catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
                {
                    _draftStorageError = "Черновик не сохранён. Не закрывайте страницу; бизнес-команды временно заблокированы.";
                    return;
                }
            } while (_draftPersistPending && _draftRegistered && !_draftRestoring && !MainCloseInProgress);
        }
        finally
        {
            _draftPersisting = false;
        }
    }

    private string SerializeDraftState() => JsonSerializer.Serialize(CaptureEquipmentDraftState());

    private EquipmentDraftState CaptureEquipmentDraftState() =>
        new()
        {
            Model = _model,
            WriteOffDate = _writeOffDate,
            WriteOffTime = _writeOffTime,
            Phones = _phones,
            OriginalPhones = _originalPhones,
            OriginalSnapshot = _originalSnapshot,
            PendingPhoneSync = _pendingPhoneSync,
            ActiveEditor = _activeEditor,
            ActiveEditorState = _activeEditor?.Kind == nameof(EquipmentUpsDialog)
                ? null
                : _activeEditorContext?.CaptureDraftState() ?? _restoredEditorState,
            ActiveUpsServers = _activeUpsServers,
            ActiveUpsTests = _activeUpsTests,
            ActiveUpsOriginal = _upsDraftOriginal,
            ActiveUpsTest = _upsTestDraft,
        };

    private static T FreezeChildValue<T>(T value) where T : class =>
        JsonSerializer.SerializeToElement(value).Deserialize<T>()
        ?? throw new InvalidOperationException("Не удалось зафиксировать значения команды.");

    private Func<PhoneSyncAttempt, Task> CreatePhoneSyncCheckpointWriter()
    {
        var registration = _draftRegistration ?? throw new InvalidOperationException("Хранилище этапов АТС недоступно.");
        return async attempt =>
        {
            if (!ReferenceEquals(_draftRegistration, registration) || attempt.Intent.EquipmentId != _model?.Id)
                throw new JSException("Контекст синхронизации АТС изменился.");

            var checkpoint = new EquipmentPhoneSyncCheckpoint(attempt.Intent.AttemptId, attempt);
            try
            {
                if (_phoneSyncCheckpoint?.DraftAttemptId != attempt.Intent.AttemptId)
                {
                    await registration.OpenScopeAsync(PhoneSyncCheckpointScope, _pageCts.Token);
                    await registration.OpenScopeAsync(ActiveDraftScope ?? "main", _pageCts.Token);
                }
                await registration.SaveTypedScopeAsync(PhoneSyncCheckpointScope, JsonSerializer.Serialize(checkpoint), _pageCts.Token);
                _phoneSyncCheckpoint = checkpoint;
            }
            catch (Exception ex) when (ex is JSException or InvalidOperationException or OperationCanceledException)
            {
                if (ReferenceEquals(_draftRegistration, registration))
                    _warningMessage = "Не удалось сохранить этап синхронизации АТС.";
                throw new JSException("Не удалось сохранить этап синхронизации АТС.", ex);
            }
        };
    }

    private void RestorePhoneSyncCheckpoint(ClayDraftRestore restore)
    {
        _phoneSyncCheckpoint = null;
        if (restore.TypedScopes?.TryGetValue(PhoneSyncCheckpointScope, out var json) == true)
        {
            var checkpoint = JsonSerializer.Deserialize<EquipmentPhoneSyncCheckpoint>(json);
            if (checkpoint?.Attempt?.Intent is { } intent && intent.EquipmentId == _model?.Id)
                _phoneSyncCheckpoint = checkpoint;
        }
        _warningMessage = PhoneSyncCheckpointMessage();
    }

    private string PhoneSyncCheckpointMessage()
    {
        if (_phoneSyncCheckpoint?.Attempt is not { } attempt)
            return "Журнал АТС не подтверждает исход предыдущей команды. Повтор заблокирован.";
        var completed = attempt.Operations.Count(item => item.Phase == PhoneSyncPhase.Succeeded);
        var unknown = attempt.Operations.Count(item => item.Phase == PhoneSyncPhase.Unknown);
        var remaining = attempt.Operations.Count - completed - unknown;
        return $"Последний сохранённый этап АТС: подтверждено {completed}, неизвестно {unknown}, остальных {remaining}. " +
            "Автоматический повтор заблокирован.";
    }

    private static string ComparableBusinessSnapshot(string snapshot)
    {
        var root = JsonNode.Parse(snapshot)!.AsObject();
        var equipment = root["Equipment"]!.AsObject();
        equipment.Remove(nameof(EquipmentEntity.ChangedAtUtc));
        equipment.Remove(nameof(EquipmentEntity.ChangedById));
        equipment.Remove(nameof(EquipmentEntity.ChangedByName));
        return root.ToJsonString();
    }

    private async Task OpenDraftScopeAsync(string? scope)
    {
        if (_draftRegistered && scope is not null)
            await _draftRegistration!.OpenScopeAsync(scope, _pageCts.Token);
    }

    private async Task DiscardDraftScopeAsync(string? scope)
    {
        if (_draftRegistered && scope is not null)
            await _draftRegistration!.DiscardScopeAsync(scope, _pageCts.Token);
        await OpenDraftScopeAsync("main");
    }

    private async Task ReleaseDraftRegistrationAsync()
    {
        if (_draftRegistration is null)
            return;
        await PersistDraftAsync();
        await _draftRegistration.ReleaseAsync(_pageCts.Token);
        _phoneSyncCheckpoint = null;
        _draftRegistration = null;
        _draftRestoreBlocked = false;
        _draftRestoring = false;
        _restoringMainRaw = false;
        _mainRawRestorePending = false;
        _lastPersistedDraft = null;
        ClearUpsDraft();
    }

    private async Task RestoreActiveEditorAsync()
    {
        var editor = _activeEditor;
        if (editor is null)
        {
            _restoredEditorOpened.TrySetResult();
            return;
        }
        try
        {
            switch (editor.Kind)
            {
                case nameof(EquipmentAccountDialog):
                    await OpenAccountDialogAsync(_accounts.FirstOrDefault(item => item.Login == editor.Key));
                    break;
                case nameof(EquipmentComputerDialog):
                    await OpenComputerDialogAsync();
                    break;
                case nameof(EquipmentSimDialog):
                    await OpenSimDialogAsync();
                    break;
                case nameof(EquipmentSimServiceDialog):
                    await OpenSimServiceDialogAsync(_simServices.FirstOrDefault(item => item.Id.ToString() == editor.Key));
                    break;
                case nameof(EquipmentLicenseDialog):
                    await OpenLicenseDialogAsync();
                    break;
                case nameof(EquipmentSoftwareDialog):
                    await OpenSoftwareDialogAsync(_softwareInstallations.FirstOrDefault(item => item.Id.ToString() == editor.Key));
                    break;
                case nameof(EquipmentAccountingDialog):
                    await OpenAccountingDialogAsync(_accounting.FirstOrDefault(item => item.Id.ToString() == editor.Key));
                    break;
                case nameof(EquipmentLocationDialog):
                    await OpenLocationDialogAsync(_locations.FirstOrDefault(item => item.FromUtc.Ticks.ToString() == editor.Key));
                    break;
                case nameof(EquipmentResponsibleDialog):
                    if (Enum.TryParse<EquipmentResponsibleDialogMode>(editor.Key?.Split(':')[0], out var mode))
                        await OpenResponsibleDialogAsync(mode,
                            _responsibles.FirstOrDefault(item => item.FromUtc.Ticks.ToString() == editor.Key.Split(':').LastOrDefault()));
                    break;
                case nameof(EquipmentUpsDialog):
                    var upsLink = _upsLinks.FirstOrDefault(item => item.UpsEquipmentId.ToString() == editor.Key);
                    if (upsLink is not null)
                        await OpenUpsDialogAsync(upsLink);
                    break;
                case nameof(EquipmentUpsLinkDialog):
                    await OpenUpsLinkDialogAsync();
                    break;
                case nameof(EquipmentPhoneDialog):
                    var phone = _phones.FirstOrDefault(item => item.PhoneId.ToString() == editor.Key);
                    if (phone is not null)
                        await EditPhoneReturnAsync(phone);
                    break;
                default:
                    _restoredEditorOpened.TrySetResult();
                    break;
            }
        }
        finally
        {
            _restoredEditorOpened.TrySetResult();
        }
    }

    private static string UpsTestScope(string parentScope, int testId) =>
        $"{parentScope[..^"root".Length]}{testId}";

    private async Task PublishUpsTestDraftAsync(EquipmentEditorOpening opening, string parentScope,
        EquipmentUpsTestDraft state)
    {
        if (!IsCurrent(opening) || _activeEditor is not { Kind: nameof(EquipmentUpsDialog) } editor ||
            _draftRegistration is not { } registration)
            throw new InvalidOperationException("Черновик теста UPS недоступен.");

        if (editor.NestedTestId != state.Original.Id)
        {
            await registration.OpenScopeAsync(UpsTestScope(parentScope, state.Original.Id), _pageCts.Token);
            if (!IsCurrent(opening) || !ReferenceEquals(_draftRegistration, registration))
                throw new InvalidOperationException("Контекст теста UPS изменился.");
            _activeEditor = editor with { NestedTestId = state.Original.Id };
        }
        _upsTestDraft = state;
        await PersistDraftAsync();
        if (_draftStorageError is not null)
            throw new InvalidOperationException(_draftStorageError);
    }

    private async Task<bool> RestoreUpsTestRawAsync(EquipmentEditorOpening opening, string scope, bool retry)
    {
        var editorOpened = _restoredEditorOpened;
        var registration = _draftRegistration;
        bool Current() => IsCurrent(opening) && ReferenceEquals(_draftRegistration, registration) &&
            _activeEditor?.NestedTestId is int testId && ActiveDraftScope is { } parentScope &&
            scope == UpsTestScope(parentScope, testId);
        try
        {
            if (registration is null || !Current())
                return false;
            var result = retry
                ? await registration.ApplyPendingRawAsync(scope, _pageCts.Token)
                : await registration.ApplyRawWithResultAsync(scope, _pageCts.Token);
            if (!Current() || result is not { Pending.Count: 0 })
                return false;
            var readiness = await registration.GetRestoreReadinessAsync(_pageCts.Token);
            return Current() && readiness is not null &&
                !readiness.PendingScopes.Contains(scope) && !readiness.FailedScopes.Contains(scope);
        }
        finally
        {
            editorOpened.TrySetResult();
        }
    }

    private async Task<bool> CanAcceptUpsTestAsync(EquipmentEditorOpening opening, string scope)
    {
        if (!IsCurrent(opening) || _draftStorageError is not null || _draftRegistration is not { } registration)
            return false;
        var readiness = await registration.GetRestoreReadinessAsync(_pageCts.Token);
        return IsCurrent(opening) && ReferenceEquals(_draftRegistration, registration) && readiness is not null &&
            !readiness.PendingScopes.Contains(scope) && !readiness.FailedScopes.Contains(scope);
    }

    private EquipmentUpsTestCancellation CreateUpsTestCancellation(EquipmentEditorOpening opening,
        string parentScope, int upsId, int testId, UpsTest openingOriginal)
    {
        var registration = _draftRegistration;
        var employeeId = CurrentUser?.Id;
        var scope = UpsTestScope(parentScope, testId);
        bool Current() => IsCurrent(opening) && employeeId is > 0 && CurrentUser?.Id == employeeId &&
            registration is not null && ReferenceEquals(_draftRegistration, registration) &&
            ActiveDraftScope == parentScope && _activeEditor?.NestedTestId == testId &&
            _upsTestDraft is { Original: var original } && original.Id == testId && original.UpsEquipmentId == upsId;

        return new(
            async () => Current() ? await registration!.ReadEditorScopeAsync(scope, _pageCts.Token) : null,
            async () =>
            {
                if (!Current()) return null;
                var fresh = await Service.ReadUpsEditorResetAsync(upsId, opening.EquipmentId, employeeId!.Value, _pageCts.Token);
                if (!Current() || fresh is null) return null;
                if (testId == 0)
                    return new(openingOriginal, openingOriginal.TestedAt.Date, openingOriginal.TestedAt.TimeOfDay,
                        openingOriginal.BatteryTime, openingOriginal.Servers, openingOriginal.Issues);

                var row = fresh.Tests.SingleOrDefault(test => test.Id == testId && test.UpsEquipmentId == upsId);
                if (row is null) return null;
                var local = await ConvertUtcToLocalAsync(row.TestedAt);
                return Current() ? new(row, local.Date, local.TimeOfDay, row.BatteryTime, row.Servers, row.Issues) : null;
            },
            async (revision, replacement, childCurrent) =>
            {
                if (!Current() || !childCurrent()) return false;
                var editor = _activeEditor!;
                var parent = CaptureEquipmentDraftState();
                parent.ActiveEditor = replacement is null ? editor with { NestedTestId = null } : editor;
                parent.ActiveUpsTest = replacement;
                var state = JsonSerializer.Serialize(parent);
                if (!await registration!.ReplaceNestedEditorScopeAsync(scope, revision, state,
                        replacement is null, _pageCts.Token) || !Current())
                    return false;
                if (!childCurrent())
                {
                    if (replacement is null) await registration.OpenScopeAsync(scope, _pageCts.Token);
                    _lastPersistedDraft = null;
                    await PersistDraftAsync();
                    return false;
                }
                _activeEditor = parent.ActiveEditor;
                _upsTestDraft = replacement;
                _lastPersistedDraft = state;
                if (replacement is null)
                {
                    try { await registration.OpenScopeAsync(parentScope, _pageCts.Token); }
                    catch (Exception error) when (error is JSException or OperationCanceledException)
                    {
                        // Замена уже принята. Следующее открытие восстановит область родителя.
                    }
                }
                return true;
            });
    }

    private async Task<bool> FinishUpsTestDraftAsync(EquipmentEditorOpening opening, string parentScope,
        int testId, bool accepted)
    {
        if (!IsCurrent(opening) || _activeEditor is not { NestedTestId: not null } editor ||
            editor.NestedTestId != testId || _draftRegistration is not { } registration)
            return false;

        if (accepted)
        {
            await PersistDraftAsync();
            if (!IsCurrent(opening) || _lastPersistedDraft != SerializeDraftState())
                return false;
        }

        var scope = UpsTestScope(parentScope, testId);
        var revision = await registration.ReadEditorScopeAsync(scope, _pageCts.Token);
        if (!IsCurrent(opening) ||
            !await registration.ReplaceEditorScopeAsync(scope, revision.Revision, null, _pageCts.Token))
            return false;

        if (!IsCurrent(opening))
            return false;
        _activeEditor = editor with { NestedTestId = null };
        _upsTestDraft = null;
        await registration.OpenScopeAsync(parentScope, _pageCts.Token);
        await PersistDraftAsync();
        return true;
    }

    private void ClearUpsDraft()
    {
        if (_activeEditor?.Kind == nameof(EquipmentUpsDialog))
            _activeEditor = null;
        _activeUpsServers = null;
        _activeUpsTests = null;
        _upsDraftOriginal = null;
        _upsTestDraft = null;
    }
}
