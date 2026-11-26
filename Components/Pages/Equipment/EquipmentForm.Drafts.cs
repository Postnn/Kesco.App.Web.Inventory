using System.Security.Cryptography;
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
    private const int DraftSchemaVersion = 2;
    private const string PhoneSyncCheckpointScope = "equipment-phone-progress";
    private static readonly byte[] IntentProofKey = RandomNumberGenerator.GetBytes(32);

    [Inject] private ClayDraftService Drafts { get; set; } = null!;

    private readonly string _draftOwner = Guid.NewGuid().ToString("N");
    private ClayDraftRegistration? _draftRegistration;
    private bool _draftRegistered => _draftRegistration is not null;
    private string DraftRootSelector => $"[data-draft-owner='{_draftOwner}'], .equipment-draft-dialog-{_draftOwner}";
    private bool _draftRestoring;
    private bool _draftPersisting;
    private bool _draftPersistPending;
    private string? _lastPersistedDraft;
    private string? _draftStorageError;
    private bool _draftOperationUnresolved;
    private string? _draftOperationScope;
    private ClayDraftOperation? _draftOperation;
    private string? _locallyConfirmedAttemptId;
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

    private async Task InitializeDraftAsync()
    {
        if (_draftRegistered || _draftRestoring || CurrentUser is null || _model is null || _loadError is not null)
            return;
        var openedId = _model.Id;
        var actorId = CurrentUser.Id;
        var openedVersion = _operationVersion;
        try
        {
            var registration = await Drafts.RegisterHandleAsync(
                "equipment-form", openedId.ToString(), $"inventory:{actorId}", DraftSchemaVersion,
                DraftRootSelector, _pageCts.Token);
            if (!IsCurrent(openedVersion, openedId) || CurrentUser?.Id != actorId || _draftRegistration is not null)
            {
                await registration.DisposeAsync();
                return;
            }
            _draftRegistration = registration;
            _recoveryPristine = !registration.Restore.HasDraft;
            // Свежая регистрация остаётся пустой в браузере до первого ввода.
            if (_recoveryPristine)
                _lastPersistedDraft = SerializeDraftState();
            await ScanEquipmentRecoveryAsync(registration, openedVersion, openedId, actorId);
            if (OwnsMainDraft(registration, openedId) && IsCurrent(openedVersion, openedId) && registration.Restore.HasDraft)
                await ApplyEquipmentDraftAsync(registration, openedId, openedVersion);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            if (IsCurrent(openedVersion, openedId))
            {
                _draftRestoring = false;
                _draftStorageError = "Не удалось открыть защищённый черновик. Сохранение и внешние команды заблокированы до восстановления хранилища.";
            }
        }
    }

    private async Task ApplyEquipmentDraftAsync(ClayDraftRegistration registration, int openedId, int openedVersion, Func<bool>? canPublishRecovery = null)
    {
        var restore = registration.Restore;
        _draftRestoring = true;
        _restoredEditorOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (restore.OperationSnapshot is not null)
        {
            _draftOperationUnresolved = true;
            _draftOperation = restore.OperationSnapshot;
            _draftOperationScope = restore.OperationSnapshot.Scopes.FirstOrDefault()?.Name;
            _warningMessage = "Предыдущая команда могла быть отправлена, но её результат неизвестен. Сверьте данные; автоматический повтор не выполняется.";
        }
        var mainState = restore.TypedState ?? restore.TypedScopes?.GetValueOrDefault("main");
        if (!string.IsNullOrWhiteSpace(mainState))
        {
            if (canPublishRecovery is not null)
            {
                var stored = JsonSerializer.Deserialize<EquipmentDraftState>(mainState);
                if (stored?.Model is null)
                    throw new InvalidOperationException("Не удалось прочитать снимок сохранённого черновика.");
                if (stored.OriginalSnapshot != _originalSnapshot)
                {
                    BlockEquipmentRecoveryPublication();
                    _equipmentRecoveryNotice = "Исходная версия изменилась. Сохранённый черновик оставлен для сверки.";
                    return;
                }
            }
            if (!await RestoreDraftIfCurrentAsync(mainState, canPublishRecovery))
            {
                if (OwnsMainDraft(registration, openedId)) BlockEquipmentRecoveryPublication();
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

    private async Task<bool> RestoreDraftIfCurrentAsync(string json, Func<bool>? canPublishRecovery = null)
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
        if (canPublishRecovery?.Invoke() == false) return false;
        _originalPhones = state.OriginalPhones;
        ApplyMainMergeResult(merge);
        _phones = state.Phones;
        _pendingPhoneSync = state.PendingPhoneSync;
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
        if (!_draftRegistered || _draftRestoring || _recoveringEquipmentDraft || _orphanAdoptionConflicted || _mainDraftCancelled || _model is null)
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
                    _recoveryPristine = false;
                    await _draftRegistration!.SaveTypedAsync(json, _pageCts.Token);
                    _lastPersistedDraft = json;
                    _draftStorageError = null;
                }
                catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
                {
                    _draftStorageError = "Черновик не сохранён. Не закрывайте страницу; бизнес-команды временно заблокированы.";
                    return;
                }
            } while (_draftPersistPending && _draftRegistered && !_draftRestoring);
        }
        finally
        {
            _draftPersisting = false;
        }
    }

    private string SerializeDraftState()
    {
        var state = new EquipmentDraftState
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
        return JsonSerializer.Serialize(state);
    }

    private static T FreezeChildValue<T>(T value) where T : class =>
        JsonSerializer.SerializeToElement(value).Deserialize<T>()
        ?? throw new InvalidOperationException("Не удалось зафиксировать значения команды.");

    private static string FingerprintProtectedInput<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try { return Convert.ToHexString(HMACSHA256.HashData(IntentProofKey, bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<long?> BeginDraftOperationAsync(string operationName, object? intent = null)
    {
        if (_mainTransition || _mainDraftCancelled)
            throw new InvalidOperationException("Сначала завершите отмену или обновление основной формы.");
        var registration = _draftRegistration ??
                           throw new InvalidOperationException(_draftStorageError ??
                               "Черновик ещё не зарегистрирован; бизнес-команда заблокирована.");
        if (_model is not { Id: > 0 } current || CurrentUser is not { } actor)
            throw new InvalidOperationException("Не удалось подтвердить контекст записи оборудования.");
        var version = _operationVersion;
        await ScanEquipmentRecoveryAsync(registration, version, current.Id, actor.Id);
        if (!IsEquipmentRecoveryCurrent(registration, version, current.Id, actor.Id) || EquipmentRecoveryWriteBlocked)
            throw new InvalidOperationException("Сначала проверьте незавершённый черновик оборудования.");
        await PersistDraftAsync();
        var mainOperation = operationName is "equipment-save" or "equipment-phone-save";
        var scope = mainOperation ? "main" : ActiveDraftScope;
        var equipmentId = _model?.Id;
        var editor = _activeEditor?.Kind;
        string[] acceptedScopes = scope is null ? [] : [scope];
        ClayDraftOperation operation;
        try
        {
            await registration.EnsureScopesCanBeAcceptedAsync(acceptedScopes, _pageCts.Token);
            if (!IsEquipmentRecoveryCurrent(registration, version, current.Id, actor.Id) || EquipmentRecoveryWriteBlocked)
                throw new InvalidOperationException("Контекст операции изменился. Команда не отправлена.");
            operation = await registration.BeginOperationAsync(new ClayDraftOperationIntent(
                operationName,
                JsonSerializer.Serialize(new
                {
                    Kind = operationName,
                    EquipmentId = equipmentId,
                    Editor = editor,
                    Origin = mainOperation ? MainDraftScope : operationName == "configure-phone-equipment" ? "configure" : "child",
                    Intent = intent,
                }),
                acceptedScopes,
                AcceptLegacyTyped: mainOperation, BlockOtherDraftOperations: true), _pageCts.Token);
        }
        catch (Exception ex) when (equipmentId is int id && OwnsMainDraft(registration, id) &&
            ex is InvalidOperationException or JSException)
        {
            await ScanEquipmentRecoveryAsync(registration, version, id, actor.Id);
            if (mainOperation) await ObserveMainDraftReadinessAsync(registration, id);
            throw;
        }
        if (!ReferenceEquals(_draftRegistration, registration))
            throw new InvalidOperationException("Контекст подтверждения изменился. Команда не отправлена.");
        _locallyConfirmedAttemptId = null;
        _draftOperation = operation;
        _draftOperationScope = scope;
        return operation.Revision;
    }

    private async Task RejectDraftOperationAsync(long? revision)
    {
        if (revision is not null && _draftRegistered)
        {
            try
            {
                await _draftRegistration!.RejectOperationAsync(revision.Value, _pageCts.Token);
                _draftOperationUnresolved = false;
                _draftOperationScope = null;
                _draftOperation = null;
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException)
            {
                _draftOperationUnresolved = true;
                _draftStorageError = "Не удалось зафиксировать отказ команды. Повтор заблокирован до восстановления хранилища.";
            }
        }
    }

    private async Task UnknownDraftOperationAsync(long? revision)
    {
        if (revision is not null && _draftRegistered)
        {
            try
            {
                await _draftRegistration!.UnknownOperationAsync(revision.Value, _pageCts.Token);
                _draftOperationUnresolved = true;
            }
            catch (Exception ex) when (ex is JSException or JSDisconnectedException)
            {
                _draftOperationUnresolved = true;
                _draftStorageError = "Не удалось зафиксировать неизвестный исход. Повтор заблокирован до восстановления хранилища.";
            }
        }
    }

    private async Task RecordConfirmedChildOperationAsync(ClayDraftRegistration registration, ClayDraftOperation operation)
    {
        bool IsCurrentAttempt() => ReferenceEquals(_draftRegistration, registration) &&
            _draftOperation?.AttemptId == operation.AttemptId;

        if (!IsCurrentAttempt())
            throw new InvalidOperationException("Контекст подтверждённой команды изменился.");

        _locallyConfirmedAttemptId = operation.AttemptId;
        _draftOperationUnresolved = true;
        try
        {
            if (operation.Status != ClayDraftOperationStatus.Confirmed)
                await registration.RecordConfirmedOperationAsync(operation.Revision, _pageCts.Token);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or OperationCanceledException)
        {
            if (IsCurrentAttempt())
                _warningMessage = "Запись подтверждена. Повторите только сверку журнала.";
            throw;
        }
        if (!IsCurrentAttempt())
            throw new InvalidOperationException("Контекст подтверждённой команды изменился.");
        _draftOperation = operation with { Status = ClayDraftOperationStatus.Confirmed };
    }

    private async Task CommitDraftOperationAsync(long? revision)
    {
        if (revision is null || !_draftRegistered)
            return;
        var registration = _draftRegistration!;
        var json = SerializeDraftState();
        var hasCheckpoint = _phoneSyncCheckpoint is not null &&
            _phoneSyncCheckpoint.DraftAttemptId == _draftOperation?.AttemptId;
        await registration.CommitOperationAsync(revision.Value, json, _pageCts.Token);
        if (!ReferenceEquals(_draftRegistration, registration))
            return;
        _draftOperationUnresolved = false;
        _draftOperationScope = null;
        _draftOperation = null;
        _locallyConfirmedAttemptId = null;
        _lastPersistedDraft = json;
        if (hasCheckpoint)
        {
            _phoneSyncCheckpoint = null;
            try { await registration.DiscardScopeAsync(PhoneSyncCheckpointScope, _pageCts.Token); }
            catch (Exception ex) when (ex is JSException or InvalidOperationException or OperationCanceledException)
            {
                Logger.LogDebug("Осталась завершённая область журнала АТС ({ErrorType}).", ex.GetType().Name);
            }
        }
    }

    private Func<PhoneSyncAttempt, Task> CreatePhoneSyncCheckpointWriter()
    {
        var registration = _draftRegistration ?? throw new InvalidOperationException("Журнал АТС недоступен.");
        var operation = _draftOperation ?? throw new InvalidOperationException("Команда АТС не зарегистрирована.");
        return async attempt =>
        {
            if (!ReferenceEquals(_draftRegistration, registration) || _draftOperation?.AttemptId != operation.AttemptId ||
                attempt.Intent.EquipmentId != _model?.Id)
                throw new JSException("Контекст журнала АТС изменился.");

            var checkpoint = new EquipmentPhoneSyncCheckpoint(operation.AttemptId, attempt);
            _draftOperationUnresolved = true;
            try
            {
                if (_phoneSyncCheckpoint?.DraftAttemptId != operation.AttemptId)
                {
                    await registration.OpenScopeAsync(PhoneSyncCheckpointScope, _pageCts.Token);
                    await registration.OpenScopeAsync(ActiveDraftScope ?? "main", _pageCts.Token);
                }
                await registration.SaveTypedScopeAsync(PhoneSyncCheckpointScope, JsonSerializer.Serialize(checkpoint), _pageCts.Token);
                _phoneSyncCheckpoint = checkpoint;
            }
            catch (Exception ex) when (ex is JSException or InvalidOperationException or OperationCanceledException)
            {
                if (ReferenceEquals(_draftRegistration, registration) && _draftOperation?.AttemptId == operation.AttemptId)
                    _warningMessage = "Не удалось подтвердить запись этапа АТС в журнале. Повтор заблокирован.";
                throw new JSException("Не удалось сохранить этап синхронизации АТС.", ex);
            }
        };
    }

    private void RestorePhoneSyncCheckpoint(ClayDraftRestore restore)
    {
        _phoneSyncCheckpoint = null;
        if (!IsPhoneSyncDraftOperation(_draftOperation?.Kind))
            return;
        if (restore.TypedScopes?.TryGetValue(PhoneSyncCheckpointScope, out var json) == true)
        {
            var checkpoint = JsonSerializer.Deserialize<EquipmentPhoneSyncCheckpoint>(json);
            if (checkpoint?.Attempt?.Intent is { } intent && intent.EquipmentId == _model?.Id &&
                checkpoint.DraftAttemptId == _draftOperation?.AttemptId)
                _phoneSyncCheckpoint = checkpoint;
        }
        _warningMessage = PhoneSyncCheckpointMessage();
    }

    private static bool IsPhoneSyncDraftOperation(string? kind) =>
        kind is "equipment-phone-save" or "configure-phone-equipment" or "retry-phone-sync";

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

    private async Task ReconcileDraftOperationAsync()
    {
        if (_draftOperation is not { } operation || _model is null || _draftRegistration is not { } registration)
            return;
        if (operation.Status == ClayDraftOperationStatus.Confirmed || _locallyConfirmedAttemptId == operation.AttemptId)
        {
            try
            {
                await RecordConfirmedChildOperationAsync(registration, operation);
                await CommitDraftOperationAsync(operation.Revision);
                _warningMessage = null;
                ShowStatus("Журнал подтверждённой записи завершён. Повторная запись не отправлялась.");
            }
            catch (Exception ex) when (ex is JSException or InvalidOperationException or OperationCanceledException)
            {
                _warningMessage = "Запись подтверждена, завершение журнала пока недоступно.";
            }
            StateHasChanged();
            return;
        }

        if (IsPhoneSyncDraftOperation(operation.Kind))
        {
            _warningMessage = PhoneSyncCheckpointMessage();
            StateHasChanged();
            return;
        }
        try
        {
            using var data = JsonDocument.Parse(operation.Data);
            var payload = data.RootElement;
            if (payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("Kind", out var kind) || kind.ValueKind != JsonValueKind.String ||
                kind.GetString() != "equipment-save" ||
                !payload.TryGetProperty("EquipmentId", out var equipmentKey) || equipmentKey.ValueKind != JsonValueKind.Number ||
                !equipmentKey.TryGetInt32(out var equipmentId) || equipmentId != _model.Id ||
                !payload.TryGetProperty("Intent", out var intentElement))
            {
                _warningMessage = "Результат предыдущей операции нельзя доказать доступным чтением. Состояние Unknown сохранено; повторная команда заблокирована.";
                StateHasChanged();
                return;
            }
            var intent = intentElement.Deserialize<EquipmentMainDraftIntent>();
            if (intent is null)
                throw new InvalidOperationException("Контекст незавершённой операции повреждён.");
            var actual = await Service.GetAsync(equipmentId);
            if (actual is null)
                throw new InvalidOperationException("Оборудование не найдено при read-only сверке.");
            var actualPhones = await Service.GetPhonesAsync(equipmentId);
            var actualWriteOff = actual.WriteOffAtUtc is null
                ? (DateTime?)null
                : await ConvertUtcToLocalAsync(actual.WriteOffAtUtc.Value);
            var actualSnapshot = Snapshot(actual, actualWriteOff?.Date, actualWriteOff?.TimeOfDay, actualPhones.ToList());
            if (ComparableBusinessSnapshot(intent.AfterSnapshot) != ComparableBusinessSnapshot(actualSnapshot))
            {
                _warningMessage = "Read-only сверка не подтвердила результат предыдущей записи. Состояние Unknown сохранено; повторная команда заблокирована.";
                StateHasChanged();
                return;
            }
            _warningMessage = "Данные совпадают, но принадлежность результата предыдущей операции не подтверждена. Повтор заблокирован.";
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException or JsonException)
        {
            _warningMessage = "Не удалось завершить сверку данных. Состояние неизвестного результата сохранено.";
        }
        StateHasChanged();
    }

    private async Task OpenDraftScopeAsync(string? scope)
    {
        if (_draftRegistered && scope is not null)
            await _draftRegistration!.OpenScopeAsync(scope, _pageCts.Token);
    }

    private async Task DiscardDraftScopeAsync(string? scope)
    {
        if (_draftRegistered && scope is not null && !(_draftOperationUnresolved && scope == _draftOperationScope))
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
        _locallyConfirmedAttemptId = null;
        _draftOperation = null;
        _draftOperationScope = null;
        _draftOperationUnresolved = false;
        _draftRegistration = null;
        ResetEquipmentRecovery();
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
        if (!IsCurrent(opening) || revision.HasOperation ||
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
