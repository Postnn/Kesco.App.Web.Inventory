using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>Создание и редактирование модели оборудования.</summary>
public partial class ModelEquipmentForm : IDisposable, IAsyncDisposable
{

    private readonly CancellationTokenSource _pageCts = new();
    private readonly ClayEntityFormOptions _entityFormOptions = new() { Class = "model-equipment-form" };
    private readonly ClaySelectOptions _typeOptions = new()
    {
        DraftKey = "model.type",
        Required = true,
        RequiredError = "Выберите тип оборудования",
        AutoSelectSingle = true,
        MaxItems = 300,
        Placeholder = "Выберите название типа",
    };
    private readonly ClaySelectOptions _resourceOptions = new()
    {
        DraftKey = "model.resource",
        AutoSelectSingle = true,
        AlwaysShowAdvancedSearch = true,
        Placeholder = "Выберите название ресурса",
        CreateItemText = "Создать ресурс",
    };
    private readonly ClayTextBoxOptions _modelNameOptions = new() { DraftKey = "model.name", Required = true, RequiredError = "Укажите модель", MaxLength = 100 };
    private readonly ClayTextBoxOptions _partNumberOptions = new() { DraftKey = "model.pn", MaxLength = 50 };
    private readonly ClayTextBoxOptions _cpuOptions = new() { DraftKey = "model.cpu", MaxLength = 50 };
    private readonly ClayTextBoxOptions _networkNameOptions = new() { DraftKey = "model.net-name", MaxLength = 50 };
    private readonly ClayTextBoxOptions _lifeTimeOptions = new() { DraftKey = "model.lifetime", MaxLength = 50 };
    private readonly ClayTextAreaOptions _driverOptions = new() { DraftKey = "model.driver", MaxLength = 300, Lines = 3 };
    private readonly ClayTextAreaOptions _descriptionOptions = new() { DraftKey = "model.notes", MaxLength = 300, Lines = 2 };
    private readonly ClayNumberOptions _monitorSizeOptions = new() { DraftKey = "model.monitor", Precision = 1, Min = 0, Step = 0.1m };
    private readonly ClayNumberOptions _memoryBanksOptions = new() { DraftKey = "model.memory-banks", Precision = 0, Min = 0 };
    private readonly ClayDropDownListOptions _networkSelectOptions = new() { DraftKey = "model.net" };
    private readonly ClayDropDownListOptions _memoryTypeOptions = new() { DraftKey = "model.memory-type" };
    private readonly ClayToolbarButtonOptions _okOptions = new()
    {
        Text = "OK",
        Icon = Icons.Material.Filled.Check,
        Title = "Сохранить и закрыть",
        Color = Color.Primary,
        Variant = Variant.Filled,
    };
    private readonly ClayToolbarButtonOptions _applyOptions = new()
    {
        Text = "Применить",
        Icon = Icons.Material.Filled.Save,
        Title = "Сохранить без закрытия",
    };
    private readonly ClayToolbarButtonOptions _refreshOptions = new()
    {
        Text = "Обновить",
        Icon = Icons.Material.Filled.Refresh,
        Title = "Загрузить запись повторно",
    };
    private readonly ClayToolbarButtonOptions _cancelOptions = new()
    {
        Text = "Отмена",
        Icon = Icons.Material.Filled.Close,
        Title = "Закрыть форму",
    };
    private readonly ClayToolbarButtonOptions _phoneTemplateOptions = new()
    {
        Text = "Изменить",
        Icon = Icons.Material.Filled.Edit,
        Title = "Изменить шаблон телефона",
    };

    private MudForm? _form;
    private EquipmentModel? _model;
    private EquipmentType? _selectedType;
    private EquipmentModelUsage _usage = new();
    private IReadOnlyList<MemoryType> _memoryTypes = [];
    private IReadOnlyList<NetworkOption> _networkOptions = [];
    private int? _equipmentTypeId;
    private bool _sortTypesByName;
    private Employee? CurrentUser;
    private bool _canEdit;
    private bool _isDutyEngineer;
    private bool _loading = true;
    private bool _saving;
    private bool _loaded;
    private bool _notFound;
    private bool _allowNavigation;
    private bool _disposed;
    private int _loadVersion;
    private (int Id, int InitialType) _loadedKey = (-1, 0);
    private EquipmentModelWriteBaseline? _writeBaseline;
    private string? _validationMessage;

    /// <summary>Идентификатор редактируемой модели.</summary>
    [SupplyParameterFromQuery(Name = "id")]
    public int Id { get; set; }

    /// <summary>Начальный тип новой модели.</summary>
    [SupplyParameterFromQuery(Name = "codetype")]
    public int? CodeType { get; set; }

    /// <summary>Начальный тип новой модели в legacy-варианте параметра.</summary>
    [SupplyParameterFromQuery(Name = "_codetype")]
    public int? LegacyCodeType { get; set; }

    private string Title => Id > 0 ? $"Модель оборудования #{Id}" : "Новая модель оборудования";
    private int InitialTypeId => LegacyCodeType ?? CodeType ?? 0;
    private bool ShowMonitor => _selectedType?.HasMonitorCharacteristics > 0;
    private bool ShowMultimedia => ShowMonitor || _selectedType?.HasComputerCharacteristics > 0;
    private bool ShowComputer => _selectedType?.HasComputerCharacteristics > 0;
    private bool ShowNetwork => _selectedType?.MacAddressCount > 0;
    private bool ShowPhoneTemplate => _model?.Id > 0 && _selectedType?.Id == (int)ТипыОборудования.IP_телефон && _isDutyEngineer;
    private bool IsModified => _loaded && _model is not null && _editorOriginal is not null && _editorOriginal.Fields != ModelEditorFields.From(_model);
    private bool CanSave => IsModified && _canEdit && !_loading && !_saving && !WriteBlocked;
    private decimal? MonitorSize => _model is null || _model.MonitorSize == 0 ? null : (decimal)_model.MonitorSize;
    private string EquipmentUsageUrl(int modelId) => Navigation.GetUriWithQueryParameters(Navigation.BaseUri,
        new Dictionary<string, object?> { ["id"] = (int)Запросы.Оборудование, ["modelId"] = modelId });
    private string UsageSuffix => _usage.WrittenOff > 0 ? $", из них списано {_usage.WrittenOff}." : ".";
    private string? ChangedByUrl => _model?.ChangedById is > 0 && !string.IsNullOrWhiteSpace(Configuration["URI_user_form"])
        ? AppendQuery(Configuration["URI_user_form"]!, $"id={_model.ChangedById}")
        : null;
    private Func<ClaySelectAdvancedSearchContext, Task<ClaySelectExternalResult<ResourceLookup, int?>?>>? ResourceAdvancedSearch =>
        !string.IsNullOrWhiteSpace(Configuration["URI_resource_search"]) ? OpenResourceSearchAsync : null;
    private Func<ClaySelectAdvancedSearchContext, Task<ClaySelectExternalResult<ResourceLookup, int?>?>>? ResourceCreateItem =>
        !string.IsNullOrWhiteSpace(Configuration["URI_resource_form"]) ? OpenResourceCreateAsync : null;
    private Func<ResourceLookup, Task>? ResourceViewItem =>
        !string.IsNullOrWhiteSpace(Configuration["URI_resource_form"]) ? OpenResourceViewAsync : null;

    /// <inheritdoc />
    protected override void OnInitialized() => _entityFormOptions.Class += $" {_journalRoot}";

    /// <summary>Загружает запись после рендера и повторно загружает её при смене параметров URL.</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var key = (Id: Id, InitialType: Id > 0 ? 0 : InitialTypeId);
        if (_loadedKey != key)
        {
            if (_saving || _journalBusy)
                return;
            _loadedKey = key;
            await LoadCurrentAsync(key.Id, key.InitialType);
            return;
        }

        if (_journalBusy) return;

        if (_localRecovery is { RestoreRaw: true } recovery)
            await RestoreReadOnlyRawAsync(recovery);
        else if (_restoreRawRequested)
            await RestoreRawInputAsync();
        else
            await PersistEditedInputAsync();
    }

    private async Task LoadCurrentAsync(int id, int initialTypeId)
    {
        var version = ++_loadVersion;
        _loading = true;
        _loaded = false;
        _notFound = false;
        _canEdit = false;
        _writeBaseline = null;
        CurrentUser = null;
        _validationMessage = null;
        _model = null;
        _selectedType = null;
        _equipmentTypeId = null;
        StateHasChanged();
        await Task.Delay(100);
        try
        {
            await ResetReadOnlyRecoveryAsync();
            await ResetJournalAsync();
            await LoadDataAsync(id, initialTypeId, version);
            if (_disposed || version != _loadVersion)
                return;
            if (_loaded && _canEdit)
                await InitializeJournalAsync(id, initialTypeId, version);
            else if (id > 0 && (_notFound || _loaded))
                await LoadReadOnlyRecoveryAsync(id, version);
        }
        catch (OperationCanceledException) when (_disposed || version != _loadVersion) { }
        catch (Microsoft.Data.SqlClient.SqlException error) when (error.Number == 229 &&
            id > 0 && !_disposed && version == _loadVersion && !_loaded)
        {
            _validationMessage = "Недостаточно прав для чтения модели оборудования.";
            await LoadReadOnlyRecoveryAsync(id, version);
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _loadVersion)
                _validationMessage = "Не удалось загрузить модель или подтвердить текущего сотрудника.";
        }
        finally
        {
            if (version == _loadVersion && !_disposed)
            {
                _loading = false;
                StateHasChanged();
            }
        }
    }

    private async Task LoadDataAsync(int id, int initialTypeId, int version, Func<Task<bool>>? canPublish = null,
        Func<EquipmentModel, Employee?, Task<bool>>? acceptReset = null, CancellationToken? cancellationToken = null)
    {
        var token = cancellationToken ?? _pageCts.Token;
        var model = id > 0
            ? await EquipmentModel.GetForEditAsync(Db, id, token)
            : new EquipmentModel { EquipmentTypeId = initialTypeId };
        if (version != _loadVersion || _disposed)
            return;
        if (canPublish is not null && (!await canPublish() || _disposed || version != _loadVersion))
            return;
        if (model is null)
        {
            if (acceptReset is null) _notFound = true;
            return;
        }

        var type = model.EquipmentTypeId > 0
            ? await EquipmentTypeSource.GetByIdAsync(model.EquipmentTypeId, token)
            : null;
        if (version != _loadVersion || _disposed)
            return;
        var memoryTypes = await MemoryTypeSource.GetAllAsync(token);
        if (version != _loadVersion || _disposed)
            return;
        var actor = await Employee.GetCurrentWithRolesAsync(Db, await ReadPrincipalAsync(), token);
        if (version != _loadVersion || _disposed)
            return;
        var usage = id > 0 ? await EquipmentModel.GetUsageAsync(Db, id, cancellationToken: token) : new EquipmentModelUsage();
        if (version != _loadVersion || _disposed)
            return;

        if (canPublish is not null && (!await canPublish() || _disposed || version != _loadVersion))
            return;
        if (acceptReset is not null && (model.Id != id || model.EquipmentTypeId > 0 && type?.Id != model.EquipmentTypeId ||
            !await acceptReset(model, actor) || _disposed || version != _loadVersion || token.IsCancellationRequested ||
            Id != id || id <= 0 && InitialTypeId != initialTypeId))
            return;
        _model = model;
        _selectedType = type;
        _equipmentTypeId = model.EquipmentTypeId > 0 ? model.EquipmentTypeId : null;
        _memoryTypes = [new MemoryType { Id = 0, Name = "—" }, .. memoryTypes.Where(item => item.Id != 0)];
        CurrentUser = actor;
        _canEdit = actor?.HasRole(Роли.Специалист_по_инвентаризации_оборудования) == true;
        _isDutyEngineer = actor?.HasRole(Роли.Дежурный_инженер) == true;
        _usage = usage;
        _writeBaseline = _canEdit ? model.CaptureWriteBaseline() : null;
        CaptureEditorOriginal(model);
        _validationMessage = null;
        _loaded = true;
        UpdateTypeState(false);
        UpdateReadOnlyState();
    }

    private void OnEquipmentTypeIdChanged(int? id)
    {
        _equipmentTypeId = id;
        if (_model is not null)
            _model.EquipmentTypeId = id ?? 0;
        _validationMessage = null;
    }

    private void OnEquipmentTypeChanged(EquipmentType? type)
    {
        if (_model is null || type is null)
            return;
        var previousTypeId = _selectedType?.Id;
        _selectedType = type;
        _model.EquipmentTypeId = type.Id;
        _equipmentTypeId = type.Id;
        UpdateTypeState(previousTypeId is not null && previousTypeId.Value != type.Id);
    }

    private void UpdateTypeState(bool resetDependentValues)
    {
        if (_model is null)
            return;
        if (resetDependentValues)
        {
            if (!ShowMonitor)
                _model.MonitorSize = 0;
            if (!ShowMultimedia)
                _model.IsMultimedia = false;
            if (!ShowComputer)
            {
                _model.Cpu = string.Empty;
                _model.MemoryTypeId = 0;
                _model.MemoryBanks = 0;
            }
            if (!ShowNetwork || _model.NetworkInterfaces > (_selectedType?.MacAddressCount ?? 0))
                _model.NetworkInterfaces = 0;
            if (_model.NetworkInterfaces == 0)
                _model.NetworkName = string.Empty;

        }

        _networkOptions = Enumerable.Range(0, (_selectedType?.MacAddressCount ?? 0) + 1)
            .Select(value => new NetworkOption(value, value.ToString())).ToList();
        _networkNameOptions.ReadOnly = !_canEdit || _model.NetworkInterfaces == 0;
    }

    private void OnTypeSortChanged(bool? value) => EquipmentTypeSource.SortByName = _sortTypesByName = value == true;

    private void OnMonitorSizeChanged(decimal? value)
    {
        if (_model is not null)
            _model.MonitorSize = value is > 0 ? (double)value.Value : 0;
    }

    private void OnMultimediaChanged(bool? value)
    {
        if (_model is not null)
            _model.IsMultimedia = value == true;
    }

    private void OnNetworkInterfacesChanged(int value)
    {
        if (_model is null)
            return;
        _model.NetworkInterfaces = value;
        if (value == 0)
            _model.NetworkName = string.Empty;
        _networkNameOptions.ReadOnly = !_canEdit || value == 0;
    }

    private void OnNetworkNameChanged(string? value)
    {
        if (_model is not null)
            _model.NetworkName = value ?? string.Empty;
    }

    private void OnMemoryBanksChanged(decimal? value)
    {
        if (_model is not null)
            _model.MemoryBanks = value is > 0 ? decimal.ToInt32(value.Value) : 0;
    }

    private async Task SaveAsync(bool closeAfterSave)
    {
        if (_model is null || CurrentUser is not { } actor || _saving || !_canEdit || WriteBlocked || _journal is not { } registration)
            return;
        if (!IsModified)
        {
            if (closeAfterSave)
                await CancelAsync();
            return;
        }

        var model = _model;
        var baseline = _writeBaseline;
        var route = (Id, InitialTypeId);
        var version = ++_loadVersion;
        var dispatched = false;
        var noEffect = false;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            await Task.Delay(100);
            if (!IsCurrentSave(model, version))
                return;
            if (_form is not null)
            {
                await _form.ValidateAsync();
                if (!IsCurrentSave(model, version) || !_form.IsValid)
                    return;
            }
            if (model.Validate() is { } validation)
            {
                _validationMessage = validation;
                return;
            }
            if (await EquipmentModel.HasDuplicateAsync(Db, model, _pageCts.Token))
            {
                if (IsCurrentSave(model, version))
                    _validationMessage = "Модель с таким наименованием и PN уже существует.";
                return;
            }
            if (!IsCurrentSave(model, version))
                return;
            int? confirmedTypeId = null;
            var requestedTypeId = model.EquipmentTypeId;
            if (model.Id > 0 && baseline?.EquipmentTypeId != requestedTypeId)
            {
                var usage = await EquipmentModel.GetUsageAsync(Db, model.Id, requestedTypeId, _pageCts.Token);
                if (!IsCurrentSave(model, version))
                    return;
                if (usage.Total > 0)
                {
                    var writtenOff = usage.WrittenOff > 0 ? $", из них списано {usage.WrittenOff}" : string.Empty;
                    var confirmed = await ConfirmAsync(
                        "Внимание!",
                        $"С этой моделью связано {usage.Total} единиц оборудования{writtenOff}. " +
                        "Изменение типа изменит характеристики этого оборудования. Продолжить?");
                    if (!IsCurrentSave(model, version) || !confirmed)
                        return;
                    confirmedTypeId = requestedTypeId;
                }
            }

            var principal = await ReadPrincipalAsync();
            if (!IsCurrentSave(model, version))
                return;
            var fingerprint = EditableFingerprint(model);
            var input = SerializeEditorDraft(model);
            var id = await model.SaveAsync(Db, principal, actor.Id, baseline, confirmedTypeId, _pageCts.Token,
                new EquipmentModelWriteBoundary(
                    ct => PrepareJournalAsync(registration, model.Id, baseline?.ChangedAt, fingerprint, input, actor.Id, ct),
                    () =>
                    {
                        if (!IsCurrentSave(model, version) || route != (Id, InitialTypeId))
                            throw new OperationCanceledException();
                        dispatched = true;
                    },
                    () => noEffect = true));
            _knownWrite = new KnownModelWrite(_writeAttempt!.AttemptId, id, fingerprint);
            await FinishKnownJournalAsync(registration);
            if (!IsCurrentSave(model, version))
                return;
            await JS.InvokeVoidAsync("clayModelEquipmentForm.refreshOpener");
            if (!IsCurrentSave(model, version))
                return;
            if (await HasLaterEditorInputAsync(registration, fingerprint))
            {
                _lateInput = true;
                UpdateJournalNotice();
                return;
            }
            if (closeAfterSave)
            {
                await CloseAsync();
                await ClearConfirmedReceiptAsync(registration);
                return;
            }
            await PublishSavedModelAsync(id, registration);
        }
        catch (Exception ex) when (IsJournalError(ex) ||
            ex is Microsoft.Data.SqlClient.SqlException or UnauthorizedAccessException)
        {
            if (_knownWrite is not null)
            {
                if (!_disposed)
                    _journalNotice = "Сохранение подтверждено. Не удалось перечитать модель, обновить окно или завершить журнал.";
            }
            else
            {
                await MarkWriteFailureAsync(registration, dispatched, noEffect);
                if (IsCurrentSave(model, version) && (!dispatched || noEffect))
                    _validationMessage = ex is Microsoft.Data.SqlClient.SqlException
                        ? "Не удалось подготовить сохранение модели." : ex.Message;
            }
        }
        finally
        {
            _saving = false;
            if (_disposed)
                await ResetJournalAsync();
            else
                StateHasChanged();
        }
    }

    private async Task PublishSavedModelAsync(int id, ClayDraftRegistration registration)
    {
        var fingerprint = _model is { } original ? EditableFingerprint(original) : null;
        var version = ++_loadVersion;
        _notFound = false;
        await LoadDataAsync(id, 0, version, async () =>
        {
            _lateInput = fingerprint is not null && await HasLaterEditorInputAsync(registration, fingerprint);
            return !_lateInput;
        });
        if (_disposed || version != _loadVersion)
            return;
        if (_lateInput)
        {
            UpdateJournalNotice();
            return;
        }
        if (_notFound || _model?.Id != id || !_loaded)
            throw new InvalidOperationException("Не удалось прочитать сохранённую модель.");
        await ClearConfirmedReceiptAsync(registration);
        if (Id != id)
        {
            await ResetJournalAsync();
            await InitializeJournalAsync(id, 0, version);
            if (_disposed || version != _loadVersion)
                return;
            _allowNavigation = true;
            _loadedKey = (id, 0);
            Navigation.NavigateTo($"/Equipment/ModelEquipmentForm?id={id}", replace: true);
        }
    }

    private bool IsCurrentSave(EquipmentModel model, int version) =>
        !_disposed && version == _loadVersion && ReferenceEquals(model, _model);

    private async Task<string?> ReadPrincipalAsync()
    {
        var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
        return identity?.IsAuthenticated == true ? identity.Name : null;
    }

    private async Task RefreshAsync()
    {
        if (_model is null || _loading || _saving || _journalBusy)
            return;
        if (_journalNotice is not null)
        {
            await ObserveWriteAsync();
            return;
        }
        if (await ProtectEditorRefreshAsync())
            return;
        if (IsModified && !await ConfirmAsync("Обновление", "Отменить несохранённые изменения и загрузить запись повторно?"))
            return;
        _loadedKey = (Id, Id > 0 ? 0 : InitialTypeId);
        await LoadCurrentAsync(Id, InitialTypeId);
    }

    private async Task CloseAsync()
    {
        _allowNavigation = true;
        await JS.InvokeVoidAsync("clayModelEquipmentForm.close");
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var parameters = new DialogParameters<ConfirmDialog>
        {
            { dialog => dialog.Title, title },
            { dialog => dialog.Message, message },
            { dialog => dialog.ConfirmText, "Да" },
            { dialog => dialog.CancelText, "Нет" },
        };
        var dialog = await DialogService.ShowAsync<ConfirmDialog>(title, parameters);
        var result = await dialog.Result;
        return result is not null && !result.Canceled;
    }

    private Task<ClaySelectExternalResult<ResourceLookup, int?>?> OpenResourceSearchAsync(ClaySelectAdvancedSearchContext context)
    {
        var url = AppendQuery(GetConfiguredUrl("URI_resource_search"), $"search={Uri.EscapeDataString(context.SearchText)}");
        return AdvancedSearchService.OpenAsync<ResourceLookup, int?>(url, _pageCts.Token);
    }

    private Task<ClaySelectExternalResult<ResourceLookup, int?>?> OpenResourceCreateAsync(ClaySelectAdvancedSearchContext context)
    {
        var url = AppendQuery(GetConfiguredUrl("URI_resource_form"), $"search={Uri.EscapeDataString(context.SearchText)}");
        return AdvancedSearchService.OpenAsync<ResourceLookup, int?>(url, _pageCts.Token);
    }

    private async Task OpenResourceViewAsync(ResourceLookup resource) =>
        await JS.InvokeVoidAsync("clayOpen", AppendQuery(GetConfiguredUrl("URI_resource_form"), $"id={resource.Id}"), "_blank");

    private async Task OpenPhoneTemplateAsync()
    {
        if (_model is not null)
            await JS.InvokeVoidAsync("clayOpen", $"/Equipment/PhoneTemplateForm?id={_model.Id}", "_blank");
    }

    private string GetConfiguredUrl(string key) => Configuration[key] is { Length: > 0 } url
        ? url
        : throw new InvalidOperationException($"Не задан URL внешней формы: {key}");

    private static string AppendQuery(string url, string query) => $"{url}{(url.Contains('?') ? '&' : '?')}{query}";

    private void UpdateReadOnlyState()
    {
        var readOnly = !_canEdit;
        _typeOptions.ReadOnly = readOnly;
        _resourceOptions.ReadOnly = readOnly;
        _modelNameOptions.ReadOnly = readOnly;
        _partNumberOptions.ReadOnly = readOnly;
        _cpuOptions.ReadOnly = readOnly;
        _networkNameOptions.ReadOnly = readOnly || _model?.NetworkInterfaces == 0;
        _lifeTimeOptions.ReadOnly = readOnly;
        _driverOptions.ReadOnly = readOnly;
        _descriptionOptions.ReadOnly = readOnly;
        _monitorSizeOptions.ReadOnly = readOnly;
        _memoryBanksOptions.ReadOnly = readOnly;
        _networkSelectOptions.ReadOnly = readOnly;
        _memoryTypeOptions.ReadOnly = readOnly;
    }

/// <summary>Отменяет ожидания внешних форм и блокирует применение поздних ответов.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _loadVersion++;
        _pageCts.Cancel();
        _pageCts.Dispose();
    }

    private sealed record NetworkOption(int Id, string Name);

    /// <summary>Освобождает регистрацию, сохраняя долговечное состояние попытки.</summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await ResetReadOnlyRecoveryAsync();
        if (!_saving)
            await ResetJournalAsync();
    }
}
