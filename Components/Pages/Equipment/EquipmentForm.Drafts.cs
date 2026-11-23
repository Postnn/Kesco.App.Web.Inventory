using System.Text.Json;
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

    private bool _draftRegistered;
    private bool _draftRestoring;
    private bool _draftPersisting;
    private string? _lastPersistedDraft;
    private string? _draftStorageError;
    private EquipmentDraftEditor? _activeEditor;
    private List<UpsServerLink>? _activeUpsServers;
    private List<UpsTest>? _activeUpsTests;
    private List<UpsServerLink>? _restoredUpsServers;
    private List<UpsTest>? _restoredUpsTests;
    private TaskCompletionSource _restoredEditorOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record EquipmentDraftEditor(string Kind, string? Key, int? NestedTestId = null);

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
        public List<UpsServerLink>? ActiveUpsServers { get; set; }
        public List<UpsTest>? ActiveUpsTests { get; set; }
    }

    private async Task InitializeDraftAsync()
    {
        if (_draftRegistered || _draftRestoring || CurrentUser is null || _model is null || _loadError is not null)
            return;
        try
        {
            var restore = await Drafts.RegisterAsync(
                "equipment-form", _model.Id.ToString(), $"inventory:{CurrentUser.Id}", DraftSchemaVersion, _pageCts.Token);
            if (restore is null)
                return;
            _draftRegistered = true;
            if (restore.HasDraft && !string.IsNullOrWhiteSpace(restore.TypedState))
            {
                _draftRestoring = true;
                _restoredEditorOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await RestoreDraftAsync(restore.TypedState);
                if (restore.Operation is not null)
                    _warningMessage = "Предыдущая команда могла быть отправлена, но её результат неизвестен. Сверьте данные; автоматический повтор не выполняется.";
                var editorTask = RestoreActiveEditorAsync();
                await Task.WhenAny(_restoredEditorOpened.Task, editorTask);
                await Drafts.ApplyRawAsync(_pageCts.Token);
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
        _model = state.Model;
        _writeOffDate = state.WriteOffDate;
        _writeOffTime = state.WriteOffTime;
        _phones = state.Phones;
        _originalPhones = state.OriginalPhones;
        _originalSnapshot = string.IsNullOrWhiteSpace(state.OriginalSnapshot) ? freshBaseline : state.OriginalSnapshot;
        _pendingPhoneSync = state.PendingPhoneSync;
        _activeEditor = state.ActiveEditor;
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
        _statusMessage = "Восстановлен несохранённый черновик.";
        if (freshBaseline != _originalSnapshot)
            _warningMessage = "Запись изменилась в другой сессии. Черновик восстановлен поверх свежих данных без автоматического сохранения; проверьте конфликт.";
        StateHasChanged();
    }

    private async Task PersistDraftAsync()
    {
        if (!_draftRegistered || _draftRestoring || _draftPersisting || _model is null)
            return;
        var json = SerializeDraftState();
        if (json == _lastPersistedDraft)
            return;
        _draftPersisting = true;
        try
        {
            await Drafts.SaveTypedAsync(json, _pageCts.Token);
            _lastPersistedDraft = json;
            _draftStorageError = null;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            _draftStorageError = "Черновик не сохранён. Не закрывайте страницу; бизнес-команды временно заблокированы.";
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
            ActiveUpsServers = _activeUpsServers,
            ActiveUpsTests = _activeUpsTests,
        };
        return JsonSerializer.Serialize(state);
    }

    private async Task<long?> BeginDraftOperationAsync(string operationName)
    {
        if (!_draftRegistered)
        {
            if (_draftStorageError is not null)
                throw new InvalidOperationException(_draftStorageError);
            return null;
        }
        var operation = JsonSerializer.Serialize(new
        {
            Kind = operationName,
            EquipmentId = _model?.Id,
            Editor = _activeEditor?.Kind,
        });
        return await Drafts.BeginOperationAsync(operation, _pageCts.Token);
    }

    private async Task RejectDraftOperationAsync(long? revision)
    {
        if (revision is not null && _draftRegistered)
            await Drafts.RejectOperationAsync(revision.Value, _pageCts.Token);
    }

    private async Task UnknownDraftOperationAsync(long? revision)
    {
        if (revision is not null && _draftRegistered)
            await Drafts.UnknownOperationAsync(revision.Value, _pageCts.Token);
    }

    private async Task CommitDraftOperationAsync(long? revision)
    {
        if (revision is null || !_draftRegistered)
            return;
        var json = SerializeDraftState();
        await Drafts.CommitOperationAsync(revision.Value, json, _pageCts.Token);
        _lastPersistedDraft = json;
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
        if (!_draftRegistered)
            return;
        await Drafts.DiscardAsync(_pageCts.Token);
        _draftRegistered = false;
        _lastPersistedDraft = null;
        _activeEditor = null;
    }
}
