using System.Text.Json;
using System.Web;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Documents;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>Создание оборудования с начальным расположением, ответственным и бухгалтерским учётом.</summary>
public partial class EquipmentIns : IAsyncDisposable
{
    private const int OnHandsLocationId = (int)Расположения.На_руках;
    private readonly CancellationTokenSource _pageCts = new();
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    private Employee? CurrentUser { get; set; }
    private readonly ClayEntityFormOptions _entityFormOptions = new()
    {
        Class = "model-equipment-form equipment-ins-form",
        MaxWidth = "760px",
    };
    private readonly ClayToolbarButtonOptions _okOptions = new()
    {
        Text = "OK", Icon = Icons.Material.Filled.Check, Title = "Сохранить оборудование и закрыть форму",
        Variant = Variant.Filled, Color = Color.Primary,
    };
    private readonly ClayToolbarButtonOptions _applyOptions = new()
    {
        Text = "Применить", Icon = Icons.Material.Filled.Save, Title = "Сохранить и открыть форму редактирования",
    };
    private readonly ClayToolbarButtonOptions _refreshOptions = new()
    {
        Text = "Обновить", Icon = Icons.Material.Filled.Refresh, Title = "Восстановить исходные значения",
    };
    private readonly ClayToolbarButtonOptions _cancelOptions = new()
    {
        Text = "Отмена", Icon = Icons.Material.Filled.Close, Title = "Закрыть форму",
    };
    private readonly ClaySelectOptions _typeOptions = new()
    {
        DraftKey = "ins.type", AdvanceFocusOnSelection = true,
        Required = true, AutoSelectSingle = true, MaxItems = 300, Placeholder = "Выберите тип оборудования",
    };
    private readonly ClaySelectOptions _modelOptions = new()
    {
        DraftKey = "ins.model",
        Required = true, AutoSelectSingle = true, Placeholder = "Выберите модель",
    };
    private readonly ClaySelectOptions _ownerOptions = new()
    {
        DraftKey = "ins.owner",
        Required = true, AutoSelectSingle = true, Placeholder = "Выберите владельца",
    };
    private readonly ClaySelectOptions _personOptions = new()
    {
        DraftKey = "ins.shipper", AdvanceFocusOnSelection = true,
        AutoSelectSingle = true, Placeholder = "Выберите поставщика",
    };
    private readonly ClaySelectOptions _documentOptions = new()
    {
        DraftKey = "ins.receipt-document",
        AutoSelectSingle = true, Placeholder = "Выберите документ поступления",
    };
    private readonly ClaySelectOptions _locationOptions = new()
    {
        DraftKey = "ins.location", AdvanceFocusOnSelection = true,
        Required = true, AutoSelectSingle = true, Placeholder = "Выберите расположение",
    };
    private readonly ClaySelectOptions _responsibleOptions = new()
    {
        DraftKey = "ins.employee", AdvanceFocusOnSelection = true,
        AutoSelectSingle = true, Placeholder = "Выберите сотрудника",
    };
    private readonly ClaySelectOptions _accountingDocumentOptions = new()
    {
        DraftKey = "ins.accounting-document",
        AutoSelectSingle = true, Placeholder = "Выберите документ поступления",
    };
    private readonly ClaySelectOptions _tenantOptions = new()
    {
        DraftKey = "ins.tenant",
        AutoSelectSingle = true, Placeholder = "Выберите арендатора",
    };
    private readonly ClayDatePickerOptions _dateOptions = new() { DraftKey = "ins.date", Required = true, Clearable = false };
    private readonly ClayTimeOptions _timeOptions = new() { DraftKey = "ins.time", Required = true, Clearable = false };
    private readonly ClayTextBoxOptions _snOptions = new() { DraftKey = "ins.serial", MaxLength = 50 };
    private readonly ClayTextBoxOptions _networkNameOptions = new() { DraftKey = "ins.network", MaxLength = 50 };
    private readonly ClayTextBoxOptions _macOptions = new() { DraftKey = "ins.mac", MaxLength = 17 };
    private readonly ClayTextBoxOptions _macSecondOptions = new() { DraftKey = "ins.mac-2", MaxLength = 17 };
    private readonly ClayTextBoxOptions _macIloOptions = new() { DraftKey = "ins.mac-ilo", MaxLength = 17 };
    private readonly ClayTextBoxOptions _locationDescriptionOptions = new() { DraftKey = "ins.location-note", MaxLength = 300 };
    private readonly ClayTextBoxOptions _responsibleDescriptionOptions = new() { DraftKey = "ins.employee-note", MaxLength = 300 };
    private readonly ClayTextBoxOptions _inventoryNumberOptions = new() { DraftKey = "ins.inventory-number", MaxLength = 50 };
    private readonly ClayTextBoxOptions _accountingDescriptionOptions = new() { DraftKey = "ins.accounting-note", MaxLength = 300 };
    private readonly ClayTextAreaOptions _descriptionOptions = new() { DraftKey = "ins.notes", MaxLength = 300, Lines = 3 };

    private global::Clayzor.Lib.Entities.Inventory.Equipment _model = new();
    private MudForm? _form;
    private EquipmentType? _selectedType;
    private EquipmentModel? _selectedModel;
    private DateTime? _receiptDate;
    private TimeSpan? _receiptTime = TimeSpan.Zero;
    private bool _sortTypesByName;
    private bool _loading;
    private bool _saving;
    private bool _loaded;
    private bool _canEdit;
    private bool _notFound;
    private bool _accountingAvailable;
    private bool _allowNavigation;
    private bool _disposed;
    private int _operationVersion;
    private int _ownerVersion;
    private int _shipperVersion;
    private int _selectionVersion;
    private int? _confirmedOwnerId;
    private int? _createdEquipmentId;
    private string? _createdEditUrl;
    private string? _sourceKey;
    private bool _initialRenderCompleted;
    private bool _loadedFromCopy;
    private string? _validationMessage;
    private string _originalSnapshot = string.Empty;

    private DocumentSelectDataSource ReceiptDocumentSource =>
        Services.GetRequiredKeyedService<DocumentSelectDataSource>("receipt");
    private DocumentSelectDataSource AccountingDocumentSource =>
        Services.GetRequiredKeyedService<DocumentSelectDataSource>("accounting");

    private bool IsModified => _loaded && _createdEquipmentId is null && Snapshot() != _originalSnapshot;
    private bool HasPendingCreation => _createdEquipmentId is null && (_loadedFromCopy || IsModified);
    private bool CanSave => _loaded && _canEdit && !_loading && !_saving && !CreationBlocked && HasPendingCreation;
    private bool CreationFocusReady => !_disposed && _loaded && _canEdit && !_loading && !_saving &&
        !_restoreCreationRaw && !CreationBlocked && _createdEquipmentId is null && _sourceKey == SourceKey;
    private bool ShowNetworkName => _selectedType?.HasNetworkName > 0;
    private int MacCount => Math.Min(3, _selectedModel?.NetworkInterfaces ?? 0);
    private Func<Employee, Task>? EmployeeViewItem =>
        !string.IsNullOrWhiteSpace(Configuration["URI_user_form"]) ? OpenEmployeeViewAsync : null;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _sourceKey = SourceKey;
        _entityFormOptions.Class += $" {_creationRoot}";
        Navigation.LocationChanged += OnNavigationLocationChanged;
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _initialRenderCompleted = true;
            _sourceKey = SourceKey;
            await LoadAsync();
        }
        else if (!_loading && !_saving)
        {
            if (_restoreCreationRaw)
                await RestoreCreationRawAsync();
            await AutosaveCreationEditorAsync();
        }
    }

    private async void OnNavigationLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        if (_disposed || !string.Equals(new Uri(args.Location).AbsolutePath.TrimEnd('/'),
                "/Equipment/EquipmentIns", StringComparison.OrdinalIgnoreCase))
            return;
        var sourceKey = SourceKey;
        if (_sourceKey == sourceKey)
            return;
        _sourceKey = sourceKey;
        if (_initialRenderCompleted)
            await InvokeAsync(LoadAsync);
    }

    private string SourceKey => FormattableString.Invariant($"{QueryInt("copyId")}:{QueryInt("codetype")}");

    private async Task LoadAsync()
    {
        var version = ++_operationVersion;
        _ownerVersion++;
        _loading = true;
        _loaded = false;
        _canEdit = false;
        _loadedFromCopy = false;
        _notFound = false;
        _allowNavigation = false;
        _createdEquipmentId = null;
        _createdEditUrl = null;
        _creationResult = null;
        _unverifiedCreationReceipt = null;
        _creationAttempt = null;
        _creationSentSnapshot = null;
        _creationLateEditorPayload = null;
        _retainedCreationInput = false;
        _creationServerChecked = _creationServerPending = false;
        _creationServerNotice = null;
        _priorCreatedEquipmentId = null;
        _receiptPersisted = _creationUncertain = _creationRawPending = _restoreCreationRaw = false;
        _creationRawFailed = _applyingCreationRaw = false;
        _notSentCreationId = null;
        _sourceUnavailable = _draftNeedsReview = _editorRestored = _editorSaveFailed = false;
        _lastEditorDraft = _freshOriginal = null;
        _pendingCreationClose = null;
        _creationDraftClosed = false;
        CurrentUser = null;
        _confirmedOwnerId = null;
        _shipperVersion++;
        StateHasChanged();
        await Task.Delay(100);
        try
        {
            if (_creationAutosave is { } pending) await pending;
            if (!IsCurrent(version)) return;
            await ReleaseCreationDraftAsync();
            if (!IsCurrent(version)) return;
            var copyId = QueryInt("copyId");
            var initialTypeId = QueryInt("codetype");
            global::Clayzor.Lib.Entities.Inventory.Equipment? model = null;
            try
            {
                if (copyId > 0) model = await Service.GetForCopyAsync(copyId);
            }
            catch (Exception error) when (error is InvalidOperationException or Microsoft.Data.SqlClient.SqlException)
            {
                // Без свежего источника можно открыть только авторизованный локальный черновик.
            }
            if (!IsCurrent(version))
                return;
            _sourceUnavailable = copyId > 0 && model is null;
            model ??= new global::Clayzor.Lib.Entities.Inventory.Equipment();
            model.Id = 0;
            if (model.EquipmentTypeId is null && initialTypeId > 0)
                model.EquipmentTypeId = initialTypeId;

            var principal = await PrincipalAsync();
            if (!IsCurrent(version))
                return;
            var currentUser = await Service.GetCurrentUserAsync(principal, _pageCts.Token);
            if (!IsCurrent(version))
                return;
            var localOffset = await JS.InvokeAsync<int>("clayGridTimeZone.getOffsetMinutes", _pageCts.Token);
            if (!IsCurrent(version))
                return;

            CurrentUser = currentUser;
            _model = model;
            _receiptDate = DateTime.UtcNow.AddMinutes(localOffset).Date;
            _receiptTime = TimeSpan.Zero;
            _freshOriginal = _originalSnapshot = Snapshot();
            _validationMessage = null;
            await AttachCreationDraftAsync(currentUser, principal, version);
            if (!IsCurrent(version)) return;
            if (_sourceUnavailable && !_editorRestored && _createdEquipmentId is null && !_creationUncertain)
            {
                _notFound = true;
                return;
            }
            await LoadCreationInputsAsync(currentUser, version);
        }
        catch (OperationCanceledException) when (_disposed || !IsCurrent(version))
        {
        }
        finally
        {
            if (IsCurrent(version))
            {
                _loading = false;
                StateHasChanged();
            }
        }
    }

    private async Task LoadCreationInputsAsync(Employee? actor, int version)
    {
        var model = _model;
        var selectedType = model.EquipmentTypeId is > 0
            ? await EquipmentTypeSource.GetByIdAsync(model.EquipmentTypeId, _pageCts.Token) : null;
        if (!IsCurrent(version)) return;
        var selectedModel = model.ModelId is > 0
            ? await ModelSource.GetByIdAsync(model.ModelId, _pageCts.Token) : null;
        if (!IsCurrent(version)) return;
        var accountingAvailable = await Service.CanUseAccountingAsync(actor, model.OwnerId, _pageCts.Token);
        if (!IsCurrent(version)) return;

        _confirmedOwnerId = model.OwnerId;
        ModelSource.Filter.EquipmentTypeId = model.EquipmentTypeId;
        ReceiptDocumentSource.PersonId = model.OwnerId;
        _selectedType = selectedType;
        _selectedModel = selectedModel;
        CurrentUser = actor;
        _canEdit = actor?.HasRole(Роли.Специалист_по_инвентаризации_оборудования) == true;
        _accountingAvailable = accountingAvailable;
        UpdateOptions();
        _notFound = false;
        _loaded = true;
        _loadedFromCopy = QueryInt("copyId") > 0 && !_sourceUnavailable;
    }

    private void OnTypeIdChanged(int? id)
    {
        _selectionVersion++;
        if (id is null)
        {
            _model.EquipmentTypeId = null;
            return;
        }
        if (_selectedType?.Id == id)
        {
            _model.EquipmentTypeId = id;
            ModelSource.Filter.EquipmentTypeId = id;
            return;
        }
        _model.EquipmentTypeId = id;
        _model.ModelId = null;
        _selectedType = null;
        _selectedModel = null;
        ModelSource.Filter.EquipmentTypeId = id;
        ClearHiddenFeatures();
        UpdateOptions();
    }

    private void OnTypeChanged(EquipmentType? type)
    {
        if (type is null || type.Id != _model.EquipmentTypeId)
            return;
        _selectedType = type;
        _model.EquipmentTypeId = type.Id;
        ModelSource.Filter.EquipmentTypeId = type.Id;
        UpdateOptions();
    }

    private void OnModelIdChanged(int? id)
    {
        _selectionVersion++;
        if (id is null)
        {
            _model.ModelId = null;
            return;
        }
        if (_selectedModel?.Id == id)
        {
            _model.ModelId = id;
            return;
        }
        _model.ModelId = id;
        _selectedModel = null;
    }

    private async Task OnModelChangedAsync(EquipmentModel? model)
    {
        if (model is null)
            return;
        if (_model.ModelId != model.Id) return;
        var selection = ++_selectionVersion;
        var route = _operationVersion;
        _selectedModel = model;
        _model.ModelId = model.Id;
        _model.EquipmentTypeId = model.EquipmentTypeId;
        ModelSource.Filter.EquipmentTypeId = model.EquipmentTypeId;
        if (_selectedType?.Id != model.EquipmentTypeId)
        {
            var type = await EquipmentTypeSource.GetByIdAsync(model.EquipmentTypeId, _pageCts.Token);
            if (!IsCurrent(route) || selection != _selectionVersion || _model.ModelId != model.Id ||
                _model.EquipmentTypeId != model.EquipmentTypeId)
                return;
            _selectedType = type?.Id == model.EquipmentTypeId ? type : null;
        }
        if (_selectedType is not null)
            ClearHiddenFeatures();
        UpdateOptions();
    }

    private async Task OnOwnerChangedAsync(int? id)
    {
        if (id is null)
        {
            _ownerVersion++;
            _shipperVersion++;
            _model.OwnerId = null;
            return;
        }
        if (_confirmedOwnerId == id)
        {
            _model.OwnerId = id;
            ReceiptDocumentSource.PersonId = id;
            if (!_accountingAvailable)
                await RefreshAccountingAsync(id.Value);
            return;
        }
        _shipperVersion++;
        _confirmedOwnerId = id;
        _model.OwnerId = id;
        _model.DocumentReceiptOuId = null;
        _model.ShipperId = null;
        ReceiptDocumentSource.PersonId = id;
        _accountingAvailable = false;
        _model.IsAccounting = false;
        ClearAccountingValues();
        UpdateOptions();
        await RefreshAccountingAsync(id.Value);
    }

    private async Task RefreshAccountingAsync(int personId)
    {
        var version = ++_ownerVersion;
        var available = await Service.CanUseAccountingAsync(CurrentUser, personId, _pageCts.Token);
        if (_disposed || version != _ownerVersion || _confirmedOwnerId != personId || _model.OwnerId != personId)
            return;
        _accountingAvailable = available;
        UpdateOptions();
        StateHasChanged();
    }

    private async Task OnReceiptDocumentChangedAsync(int? id)
    {
        var version = ++_shipperVersion;
        _model.DocumentReceiptOuId = id;
        if (id is null || _model.OwnerId is null)
            return;
        var ownerId = _model.OwnerId.Value;
        var shipperId = await ReceiptDocumentSource.GetSingleShipperIdAsync(id.Value, ownerId);
        if (!_disposed && version == _shipperVersion && _model.DocumentReceiptOuId == id &&
            _model.OwnerId == ownerId && shipperId is not null)
            _model.ShipperId = shipperId;
    }

    private void OnShipperChanged(int? id)
    {
        _shipperVersion++;
        _model.ShipperId = id;
    }

    private void OnLocationChanged(int? id)
    {
        _locationChoiceVersion++;
        _model.LocationId = id;
        UpdateOptions();
    }

    private void OnAccountingChanged(bool? value)
    {
        _model.IsAccounting = value == true;
        if (!_model.IsAccounting)
            ClearAccountingValues();
        UpdateOptions();
    }

    private void OnTypeSortChanged(bool? value) => EquipmentTypeSource.SortByName = _sortTypesByName = value == true;

    private void OnMacChanged(string? value, int index)
    {
        var normalized = global::Clayzor.Lib.Entities.Inventory.Equipment.NormalizeMacAddress(value);
        if (index == 1) _model.MacAddress = normalized;
        if (index == 2) _model.MacAddress2 = normalized;
        if (index == 3) _model.MacAddressIlo = normalized;
    }

    private void ClearHiddenFeatures()
    {
        if (!ShowNetworkName)
            _model.NetworkName = null;
        if (MacCount < 1) _model.MacAddress = null;
        if (MacCount < 2) _model.MacAddress2 = null;
        if (MacCount < 3) _model.MacAddressIlo = null;
    }

    private void ClearAccountingValues()
    {
        _model.InventoryNumber = null;
        _model.AccountingDocumentId = null;
        _model.TenantId = null;
        _model.AccountingDescription = null;
    }

    private void UpdateOptions()
    {
        _snOptions.Required = _selectedType?.IsSerialNumberRequired > 0;
        _responsibleOptions.Required = _model.LocationId == OnHandsLocationId;
        foreach (var options in new[]
                 {
                     _typeOptions, _modelOptions, _ownerOptions, _personOptions, _documentOptions,
                     _locationOptions, _responsibleOptions,
                 })
            options.ReadOnly = !_canEdit;
        foreach (var options in new ClayInputOptions[]
                 {
                     _dateOptions, _timeOptions, _snOptions, _networkNameOptions, _macOptions, _macSecondOptions, _macIloOptions,
                     _locationDescriptionOptions, _responsibleDescriptionOptions, _descriptionOptions,
                 })
            options.ReadOnly = !_canEdit;
        _tenantOptions.ReadOnly = !_canEdit;
        _tenantOptions.Disabled = !_model.IsAccounting;
        _accountingDocumentOptions.ReadOnly = !_canEdit;
        _accountingDocumentOptions.Disabled = !_model.IsAccounting;
        _inventoryNumberOptions.ReadOnly = !_canEdit;
        _inventoryNumberOptions.Disabled = !_model.IsAccounting;
        _accountingDescriptionOptions.ReadOnly = !_canEdit;
        _accountingDescriptionOptions.Disabled = !_model.IsAccounting;
    }

    private bool ValidateBusinessRules()
    {
        var errors = new List<string>();
        if (_receiptDate is null || _receiptTime is null) errors.Add("Укажите дату и время поступления.");
        if (_model.EquipmentTypeId is null) errors.Add("Укажите тип оборудования.");
        if (_model.ModelId is null) errors.Add("Укажите модель оборудования.");
        if (_selectedType?.IsSerialNumberRequired > 0 && string.IsNullOrWhiteSpace(_model.SerialNumber))
            errors.Add("Укажите серийный номер.");
        if (_model.OwnerId is null) errors.Add("Укажите владельца оборудования.");
        if (_model.LocationId is null) errors.Add("Укажите расположение оборудования.");
        if (_model.LocationId == OnHandsLocationId && _model.EmployeeId is null)
            errors.Add("Для расположения «На руках» укажите ответственного сотрудника.");
        if (!global::Clayzor.Lib.Entities.Inventory.Equipment.IsValidMacAddress(_model.MacAddress) ||
            !global::Clayzor.Lib.Entities.Inventory.Equipment.IsValidMacAddress(_model.MacAddress2) ||
            !global::Clayzor.Lib.Entities.Inventory.Equipment.IsValidMacAddress(_model.MacAddressIlo))
            errors.Add("MAC-адрес должен содержать 12 шестнадцатеричных символов.");
        if (_model.IsAccounting && !_accountingAvailable)
            errors.Add("Бухгалтерский учёт недоступен для выбранного владельца.");
        _validationMessage = errors.Count == 0 ? null : string.Join(" ", errors);
        return errors.Count == 0;
    }

    private async Task<string?> PrincipalAsync()
    {
        var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
        return identity?.IsAuthenticated == true ? identity.Name : null;
    }

    private async Task SaveAsync(bool closeAfterSave)
    {
        if (!_loaded || !_canEdit || CreationBlocked || _creationDraft is not { } registration || CurrentUser is not { } actor)
            return;
        if (_createdEquipmentId is not null)
        {
            if (_saving)
                return;
            if (closeAfterSave)
                await CancelAsync();
            else
                await OpenCreatedEquipment();
            return;
        }
        if (_saving)
            return;
        if (!HasPendingCreation)
        {
            if (closeAfterSave)
                await CancelAsync();
            return;
        }

        var version = ++_operationVersion;
        var serverAttemptId = Guid.NewGuid();
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            var editFormUrl = closeAfterSave ? null : GetConfiguredUrl("URI_equipment_form");
            await Task.Delay(100);
            if (_form is not null)
            {
                await _form.ValidateAsync();
                if (!IsCurrent(version) || !_form.IsValid)
                    return;
            }
            if (!await VerifyCreationRawReadyAsync(registration, version, _pageCts.Token)) return;
            if (!ValidateBusinessRules())
                return;
            _model.ReceivedAtUtc = await ConvertReceiptTimeToUtcAsync();
            if (!IsCurrent(version))
                return;
            var principal = await PrincipalAsync();
            if (!IsCurrent(version))
                return;
            if (!await ReadCreationServerStateAsync(actor, principal, version, _pageCts.Token))
                return;
            var id = await Service.CreateJournaledAsync(_model, principal, actor.Id, serverAttemptId, _pageCts.Token,
                (request, ct) => BeginCreationAsync(request, registration, version, serverAttemptId, ct));
            if (!IsCurrent(version))
                return;
            if (id <= 0)
                throw new InvalidOperationException("База данных не вернула идентификатор созданного оборудования.");
            if (_creationAttempt is not { } attempt)
                throw new InvalidOperationException("Не получено подтверждение отправленной попытки.");

            _creationResult = new CreationReceipt(SourceKey, attempt.AttemptId, attempt.Revision, id);
            _createdEquipmentId = id;
            _createdEditUrl = editFormUrl is null ? null : BuildEditUrl(editFormUrl, id);
            var laterInput = _creationSentSnapshot is { } submitted && Snapshot() != submitted;
            if (laterInput)
            {
                _creationLateEditorPayload = BuildEditorDraft(_model, _originalSnapshot);
                _retainedCreationInput = true;
            }
            else
            {
                _model.Id = id;
                _originalSnapshot = Snapshot();
            }
            await FinishCreationAsync(registration, _pageCts.Token);
            if (!IsCurrentCreationInput(registration, version)) return;
            var scope = await registration.ReadEditorScopeAsync(CreationScope, _pageCts.Token);
            if (!IsCurrentCreationInput(registration, version)) return;
            if (laterInput || scope.HasRaw || scope.HasOperation)
            {
                _retainedCreationInput = laterInput || scope.HasRaw;
                _validationMessage = $"Оборудование №{id} создано. Более поздний ввод сохранён.";
                return;
            }
            var beforeExit = Snapshot();
            await JS.InvokeVoidAsync("clayModelEquipmentForm.refreshOpener", _pageCts.Token);
            if (!IsCurrent(version))
                return;
            if (Snapshot() != beforeExit)
            {
                _retainedCreationInput = true;
                var later = JsonSerializer.Deserialize<EquipmentEntity>(JsonSerializer.Serialize(_model))!;
                later.Id = 0;
                _creationLateEditorPayload = BuildEditorDraft(later, _freshOriginal ?? _originalSnapshot);
                await FinishCreationAsync(registration, _pageCts.Token);
                if (IsCurrent(version)) _validationMessage = $"Оборудование №{id} создано. Более поздний ввод сохранён.";
                return;
            }
            await CloseCreationDraftAsync(registration, scope.Revision,
                closeAfterSave ? CloseAsync : () => NavigateToCreatedAsync(_createdEditUrl!), _pageCts.Token);
        }
        catch (OperationCanceledException) when (!IsCurrent(version)) { }
        catch (EquipmentCreationRejectedException error) when (IsCurrent(version) && _creationResult is null)
        {
            try
            {
                if (_creationAttempt is { } rejected)
                    await registration.RejectOperationAsync(rejected.Revision, _pageCts.Token);
                if (!IsCurrentCreationInput(registration, version)) return;
                _creationAttempt = null;
                _creationUncertain = false;
                _validationMessage = error.Message;
            }
            catch (Exception storageError) when (storageError is JSException or InvalidOperationException or OperationCanceledException)
            {
                if (IsCurrentCreationInput(registration, version)) SetCreationUnknown();
            }
        }
        catch (Exception error) when (IsCurrent(version))
        {
            if (_creationResult is { } receipt)
                _validationMessage = CreationCleanupPending
                    ? $"Оборудование №{receipt.EquipmentId} создано. Для проверки результата нажмите «Обновить»."
                    : $"Оборудование №{receipt.EquipmentId} создано. Не удалось завершить переход.";
            else if (_creationAttempt is { } attempt)
                await MarkCreationUnknownAsync(registration, attempt);
            else if (_notSentCreationId is not null)
                _validationMessage = "Команда не отправлена. Проверьте состояние операции кнопкой «Обновить».";
            else
                _validationMessage = error is InvalidOperationException or UnauthorizedAccessException
                    ? error.Message
                    : "Создание не начато. Не удалось завершить проверку команды.";
        }
        finally
        {
            if (IsCurrent(version))
            {
                _saving = false;
                StateHasChanged();
            }
        }
    }

    private async Task<DateTime> ConvertReceiptTimeToUtcAsync()
    {
        var milliseconds = await JS.InvokeAsync<long>(
            "clayGridTimeZone.localDateTimeToUtcMilliseconds", _pageCts.Token,
            _receiptDate!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            _receiptTime!.Value.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture));
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
    }

    internal static string BuildEditUrl(string url, int id) =>
        $"{url}{(url.Contains('?') ? '&' : '?')}id={id}";

    private string GetConfiguredUrl(string key) => Configuration[key] is { Length: > 0 } url
        ? url
        : throw new InvalidOperationException($"Не задан URL внешней формы: {key}");

    private int QueryInt(string name)
    {
        var query = HttpUtility.ParseQueryString(new Uri(Navigation.Uri).Query);
        return int.TryParse(query[name], out var value) ? value : 0;
    }

    private string Snapshot() => Snapshot(_model, _receiptDate, _receiptTime);

    private static string Snapshot(EquipmentEntity model, DateTime? date, TimeSpan? time) => JsonSerializer.Serialize(new
    {
        Model = model,
        Date = date,
        Time = time,
    });

    private bool IsCurrent(int version) => !_disposed && version == _operationVersion;

    private async Task OpenEmployeeViewAsync(Employee employee) =>
        await JS.InvokeVoidAsync("clayOpen", BuildEditUrl(Configuration["URI_user_form"]!, employee.Id), "_blank");

    private async Task RefreshAsync()
    {
        if (_applyingCreationRaw || _saving || _creationDraftClosed) return;
        if (_pendingCreationClose is { } pending)
        {
            _saving = true;
            try { await RetryCreationCloseAsync(pending, _pageCts.Token); }
            finally { if (!_disposed) _saving = false; }
            return;
        }
        if (CreationCleanupPending || _notSentCreationId is not null || _creationAttempt is not null ||
            _unverifiedCreationReceipt is not null)
        {
            await RetryCreationCompletionAsync();
            return;
        }
        if (_creationUncertain || _createdEquipmentId is not null)
            return;
        if (_creationDraft is null && !_creationServerChecked && CurrentUser is { } draftActor &&
            draftActor.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
        {
            await LoadAsync();
            return;
        }
        if ((!_creationServerChecked || _creationServerPending) && CurrentUser is { } actor &&
            actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
        {
            var version = _operationVersion;
            await ReadCreationServerStateAsync(actor, await PrincipalAsync(), version, _pageCts.Token);
            return;
        }
        if (_editorSaveFailed)
        {
            await AutosaveCreationEditorAsync(retry: true);
            return;
        }
        if (_creationRawPending)
        {
            await RestoreCreationRawAsync(pendingOnly: true);
            return;
        }
        if (_sourceUnavailable || _draftNeedsReview)
        {
            await AutosaveCreationEditorAsync(retry: true);
            if (!_editorSaveFailed) await LoadAsync();
            return;
        }
        if (_saving || IsModified && !await ConfirmAsync("Несохранённые данные будут потеряны. Продолжить?"))
            return;
        await LoadAsync();
    }

    private async Task CloseAsync()
    {
        _allowNavigation = true;
        await JS.InvokeVoidAsync("clayModelEquipmentForm.close");
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        var dialog = await DialogService.ShowAsync<ConfirmDialog>(
            "Подтверждение",
            new DialogParameters<ConfirmDialog> { { component => component.Message, message } },
            new DialogOptions { CloseButton = true, MaxWidth = MaxWidth.Small });
        var result = await dialog.Result;
        return result is { Canceled: false };
    }

    /// <summary>Освобождает ресурсы страницы и отменяет ожидания.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        Navigation.LocationChanged -= OnNavigationLocationChanged;
        _disposed = true;
        _operationVersion++;
        _ownerVersion++;
        _pageCts.Cancel();
        try { await ReleaseCreationDraftAsync(); }
        finally { _pageCts.Dispose(); }
    }
}
