using System.Text.Json;
using System.Text.Json.Nodes;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>DR-01: долговечный черновик основной формы и открытых дочерних редакторов.</summary>
public partial class EquipmentForm
{
    private const int DraftSchemaVersion = 2;

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
    private EquipmentDraftEditor? _activeEditor;
    private EquipmentEditorContext? _activeEditorContext;
    private string? _restoredEditorState;
    private List<UpsServerLink>? _activeUpsServers;
    private List<UpsTest>? _activeUpsTests;
    private List<UpsServerLink>? _restoredUpsServers;
    private List<UpsTest>? _restoredUpsTests;
    private TaskCompletionSource _restoredEditorOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record EquipmentDraftEditor(string Kind, string? Key, int? NestedTestId = null);

    private sealed record EquipmentMainDraftIntent(string ExpectedSnapshot, string AfterSnapshot);

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
    }

    private string? ActiveDraftScope => _activeEditor is null
        ? null
        : $"child:{_activeEditor.Kind}:{_activeEditor.Key ?? "new"}:{_activeEditor.NestedTestId?.ToString() ?? "root"}";

    private async Task InitializeDraftAsync()
    {
        if (_draftRegistered || _draftRestoring || CurrentUser is null || _model is null || _loadError is not null)
            return;
        try
        {
            _draftRegistration = await Drafts.RegisterHandleAsync(
                "equipment-form", _model.Id.ToString(), $"inventory:{CurrentUser.Id}", DraftSchemaVersion,
                DraftRootSelector, _pageCts.Token);
            var restore = _draftRegistration.Restore;
            if (restore.HasDraft)
            {
                _draftRestoring = true;
                _restoredEditorOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (restore.OperationSnapshot is not null)
                {
                    _draftOperationUnresolved = true;
                    _draftOperation = restore.OperationSnapshot;
                    _draftOperationScope = restore.OperationSnapshot.Scopes.FirstOrDefault()?.Name;
                    _warningMessage = "Предыдущая команда могла быть отправлена, но её результат неизвестен. Сверьте данные; автоматический повтор не выполняется.";
                }
                if (!string.IsNullOrWhiteSpace(restore.TypedState))
                    await RestoreDraftAsync(restore.TypedState);
                var editorTask = RestoreActiveEditorAsync();
                await Task.WhenAny(_restoredEditorOpened.Task, editorTask);
                await _draftRegistration.ApplyRawWithResultAsync("main", _pageCts.Token);
                _draftRestoring = false;
                _lastPersistedDraft = null;
                await PersistDraftAsync();
                StateHasChanged();
            }
            else
            {
                await PersistDraftAsync();
            }
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            _draftRestoring = false;
            _draftStorageError = "Не удалось открыть защищённый черновик. Сохранение и внешние команды заблокированы до восстановления хранилища.";
        }
    }

    private async Task RestoreDraftAsync(string json)
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
        _originalPhones = state.OriginalPhones;
        var merge = await MergeMainStateAsync(original, state.Model, _model, state.WriteOffDate, state.WriteOffTime);
        ApplyMainMergeResult(merge);
        _phones = state.Phones;
        _pendingPhoneSync = state.PendingPhoneSync;
        _activeEditor = state.ActiveEditor;
        _restoredEditorState = state.ActiveEditorState;
        _restoredUpsServers = state.ActiveUpsServers;
        _restoredUpsTests = state.ActiveUpsTests;
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
    }

    private async Task PersistDraftAsync()
    {
        if (!_draftRegistered || _draftRestoring || _model is null)
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
            ActiveEditorState = _activeEditorContext?.CaptureDraftState() ?? _restoredEditorState,
            ActiveUpsServers = _activeUpsServers,
            ActiveUpsTests = _activeUpsTests,
        };
        return JsonSerializer.Serialize(state);
    }

    private async Task<long?> BeginDraftOperationAsync(string operationName, object? intent = null)
    {
        var registration = _draftRegistration ??
                           throw new InvalidOperationException(_draftStorageError ??
                               "Черновик ещё не зарегистрирован; бизнес-команда заблокирована.");
        await PersistDraftAsync();
        await registration.EnsureCommandAllowedAsync(_pageCts.Token);
        var mainOperation = operationName is "equipment-save" or "equipment-phone-save";
        var scope = mainOperation ? "main" : ActiveDraftScope;
        var operation = await registration.BeginOperationAsync(new ClayDraftOperationIntent(
            operationName,
            JsonSerializer.Serialize(new
            {
                Kind = operationName,
                EquipmentId = _model?.Id,
                Editor = _activeEditor?.Kind,
                Origin = mainOperation ? "main" : operationName == "configure-phone-equipment" ? "configure" : "child",
                Intent = intent,
            }),
            scope is null ? [] : [scope],
            AcceptLegacyTyped: mainOperation), _pageCts.Token);
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

    private async Task CommitDraftOperationAsync(long? revision)
    {
        if (revision is null || !_draftRegistered)
            return;
        var json = SerializeDraftState();
        await _draftRegistration!.CommitOperationAsync(revision.Value, json, _pageCts.Token);
        _draftOperationUnresolved = false;
        _draftOperationScope = null;
        _draftOperation = null;
        _lastPersistedDraft = json;
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
        if (_draftOperation is null || _model is null)
            return;
        try
        {
            using var data = JsonDocument.Parse(_draftOperation.Data);
            var kind = data.RootElement.GetProperty("Kind").GetString();
            var equipmentId = data.RootElement.GetProperty("EquipmentId").GetInt32();
            if (equipmentId != _model.Id || kind is not ("equipment-save" or "equipment-phone-save") ||
                !data.RootElement.TryGetProperty("Intent", out var intentElement))
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
            await CommitDraftOperationAsync(_draftOperation.Revision);
            _warningMessage = null;
            ShowStatus("Read-only сверка подтвердила сохранение записи. Повторная бизнес-команда не выполнялась.");
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException)
        {
            _warningMessage = $"Не удалось завершить read-only сверку. Состояние Unknown сохранено. {ex.Message}";
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
        _draftRegistration = null;
        _lastPersistedDraft = null;
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

    private async Task DiscardDraftAsync()
    {
        if (_draftRegistration is null)
            return;
        if (_draftOperationUnresolved)
        {
            await PersistDraftAsync();
            return;
        }
        await _draftRegistration.DiscardAsync(_pageCts.Token);
        _draftRegistration = null;
        _draftOperationUnresolved = false;
        _draftOperationScope = null;
        _draftOperation = null;
        _lastPersistedDraft = null;
        _activeEditor = null;
    }
}
