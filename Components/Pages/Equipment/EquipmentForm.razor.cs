using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.DBSelect.Documents;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>Редактирование основной записи оборудования.</summary>
public partial class EquipmentForm : IDisposable
{
    private const int InventorySpecialistRoleId = 43;
    private const int SimEquipmentTypeId = 27;

    private readonly CancellationTokenSource _pageCts = new();
    private readonly ClayEntityFormOptions _entityFormOptions = new() { Class = "equipment-form" };
    private readonly ClaySelectOptions _typeOptions = new() { Required = true, AutoSelectSingle = true, MaxItems = 300, Placeholder = "Введите тип оборудования" };
    private readonly ClaySelectOptions _modelOptions = new() { Required = true, AutoSelectSingle = true, Placeholder = "Введите модель" };
    private readonly ClaySelectOptions _phonePoolOptions = new() { AutoSelectSingle = true, Placeholder = "Введите телефонный пул" };
    private readonly ClaySelectOptions _ownerOptions = new() { Required = true, AutoSelectSingle = true, Placeholder = "Введите владельца" };
    private readonly ClaySelectOptions _receiptDocumentOptions = new() { Placeholder = "Введите документ поступления" };
    private readonly ClaySelectOptions _writeOffDocumentOptions = new() { Placeholder = "Введите документ списания" };
    private readonly ClaySelectOptions _shipperOptions = new() { AutoSelectSingle = true, Placeholder = "Введите поставщика" };
    private readonly ClaySelectOptions _tenantOptions = new() { AutoSelectSingle = true, Placeholder = "Введите арендатора" };
    private readonly ClayTextBoxOptions _snOptions = new() { MaxLength = 50 };
    private readonly ClayTextBoxOptions _networkNameOptions = new() { MaxLength = 50 };
    private readonly ClayTextBoxOptions _macOptions = new() { MaxLength = 17 };
    private readonly ClayTextAreaOptions _descriptionOptions = new() { MaxLength = 300, Lines = 3 };
    private readonly ClayDatePickerOptions _writeOffDateOptions = new();
    private readonly ClayTimeOptions _writeOffTimeOptions = new();
    private readonly ClayToolbarButtonOptions _okOptions = new() { Text = "OK", Icon = Icons.Material.Filled.Check, Title = "Сохранить и закрыть", Color = Color.Primary, Variant = Variant.Filled };
    private readonly ClayToolbarButtonOptions _applyOptions = new() { Text = "Применить", Icon = Icons.Material.Filled.Save, Title = "Сохранить без закрытия" };
    private readonly ClayToolbarButtonOptions _refreshOptions = new() { Text = "Обновить", Icon = Icons.Material.Filled.Refresh, Title = "Загрузить запись повторно" };
    private readonly ClayToolbarButtonOptions _cancelOptions = new() { Text = "Отмена", Icon = Icons.Material.Filled.Close, Title = "Закрыть форму" };
    private readonly ClayToolbarButtonOptions _copyOptions = new() { Text = "Копировать", Icon = Icons.Material.Filled.ContentCopy, Title = "Создать оборудование копированием" };

    private MudForm? _form;
    private EquipmentEntity? _model;
    private EquipmentType? _selectedType;
    private EquipmentModel? _selectedModel;
    private DateTime? _writeOffDate;
    private TimeSpan? _writeOffTime;
    private bool _sortTypesByName;
    private bool _canEdit;
    private bool _loading = true;
    private bool _saving;
    private bool _loaded;
    private bool _notFound;
    private bool _allowNavigation;
    private bool _disposed;
    private int _loadedId = -1;
    private int _operationVersion;
    private int _modelVersion;
    private int _ownerVersion;
    private int _shipperVersion;
    private int? _confirmedOwnerId;
    private string _originalSnapshot = string.Empty;
    private string? _validationMessage;

    /// <summary>Идентификатор редактируемого оборудования.</summary>
    [SupplyParameterFromQuery(Name = "id")]
    public int Id { get; set; }

    private DocumentSelectDataSource ReceiptDocumentSource => Services.GetRequiredKeyedService<DocumentSelectDataSource>("receipt");
    private DocumentSelectDataSource WriteOffDocumentSource => Services.GetRequiredKeyedService<DocumentSelectDataSource>("writeoff");
    private string Title => _model is null ? $"Оборудование #{Id}" : $"[{Id}] {_model.EquipmentTypeName} {_model.ModelName}";
    private bool IsModified => _loaded && _model is not null && Snapshot() != _originalSnapshot;
    private bool CanSave => IsModified && _canEdit && !_loading && !_saving;
    private bool ShowNetworkName => _selectedType?.HasNetworkName > 0;
    private bool ShowPhonePool => _selectedType?.HasPhoneNumber > 0;
    private bool ShowCamera => _selectedType?.HasMonitorCharacteristics > 0;
    private int MacCount => Math.Min(3, _selectedModel?.NetworkInterfaces ?? 0);
    private string? ChangedByUrl => _model?.ChangedById > 0 && !string.IsNullOrWhiteSpace(Configuration["URI_user_form"])
        ? EquipmentIns.BuildEditUrl(Configuration["URI_user_form"]!, _model.ChangedById)
        : null;
    private Func<EquipmentModel, Task>? ModelViewItem => !string.IsNullOrWhiteSpace(Configuration["URI_model_form"]) ? OpenModelViewAsync : null;

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_loadedId == Id)
            return;
        _loadedId = Id;
        await LoadCurrentAsync(Id);
    }

    private async Task LoadCurrentAsync(int id)
    {
        var version = ++_operationVersion;
        _modelVersion++;
        _ownerVersion++;
        _shipperVersion++;
        _loading = true;
        _loaded = false;
        _notFound = false;
        _validationMessage = null;
        StateHasChanged();
        await Task.Delay(100);
        try
        {
            var model = id > 0 ? await Service.GetAsync(id) : null;
            if (!IsCurrent(version, id))
                return;
            if (model is null)
            {
                _model = null;
                _notFound = true;
                return;
            }
            var canEdit = await Service.HasRoleAsync(InventorySpecialistRoleId);
            if (!IsCurrent(version, id))
                return;
            var selectedType = model.EquipmentTypeId is > 0
                ? await EquipmentTypeSource.GetByIdAsync(model.EquipmentTypeId, _pageCts.Token)
                : null;
            if (!IsCurrent(version, id))
                return;
            var selectedModel = model.ModelId is > 0
                ? await ModelSource.GetByIdAsync(model.ModelId, _pageCts.Token)
                : null;
            if (!IsCurrent(version, id))
                return;
            DateTime? localWriteOff = null;
            if (model.WriteOffAtUtc is not null)
            {
                var utc = DateTime.SpecifyKind(model.WriteOffAtUtc.Value, DateTimeKind.Utc);
                var offset = await JS.InvokeAsync<int>("clayGridTimeZone.getOffsetMinutes", utc.ToString("O"));
                localWriteOff = utc.AddMinutes(offset);
                if (!IsCurrent(version, id))
                    return;
            }

            _model = model;
            _selectedType = selectedType;
            _selectedModel = selectedModel;
            _confirmedOwnerId = model.OwnerId;
            ModelSource.Filter.EquipmentTypeId = model.EquipmentTypeId;
            ReceiptDocumentSource.PersonId = model.OwnerId;
            WriteOffDocumentSource.PersonId = model.OwnerId;
            _writeOffDate = localWriteOff?.Date;
            _writeOffTime = localWriteOff?.TimeOfDay;
            _canEdit = canEdit;
            _loaded = true;
            UpdateOptions();
            _originalSnapshot = Snapshot();
        }
        catch (OperationCanceledException) when (_disposed || !IsCurrent(version, id))
        {
        }
        finally
        {
            if (IsCurrent(version, id))
            {
                _loading = false;
                StateHasChanged();
            }
        }
    }

    private bool IsCurrent(int version, int id) => !_disposed && version == _operationVersion && Id == id;

    private void OnTypeIdChanged(int? id)
    {
        if (_model is null)
            return;
        _modelVersion++;
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
        if (_model is null || type is null)
            return;
        _selectedType = type;
        _model.EquipmentTypeId = type.Id;
        ModelSource.Filter.EquipmentTypeId = type.Id;
        ClearHiddenFeatures();
        UpdateOptions();
    }

    private void OnModelIdChanged(int? id)
    {
        if (_model is null)
            return;
        _modelVersion++;
        _model.ModelId = id;
        if (id is null)
            return;
        if (_selectedModel?.Id != id)
            _selectedModel = null;
    }

    private async Task OnModelChangedAsync(EquipmentModel? model)
    {
        if (_model is null || model is null)
            return;
        var version = ++_modelVersion;
        _selectedModel = model;
        _model.ModelId = model.Id;
        if (_model.EquipmentTypeId != model.EquipmentTypeId)
        {
            _model.EquipmentTypeId = model.EquipmentTypeId;
            ModelSource.Filter.EquipmentTypeId = model.EquipmentTypeId;
            var selectedType = await EquipmentTypeSource.GetByIdAsync(model.EquipmentTypeId, _pageCts.Token);
            if (_disposed || version != _modelVersion || _model.ModelId != model.Id)
                return;
            _selectedType = selectedType;
        }
        ClearHiddenFeatures();
        UpdateOptions();
    }

    private async Task OnOwnerChangedAsync(int? id)
    {
        if (_model is null)
            return;
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
            SetDocumentOwner(id);
            return;
        }
        _ownerVersion++;
        _shipperVersion++;
        _confirmedOwnerId = id;
        _model.OwnerId = id;
        _model.DocumentReceiptOuId = null;
        _model.DocumentWriteOffId = null;
        _model.ShipperId = null;
        SetDocumentOwner(id);
        await Task.CompletedTask;
    }

    private void SetDocumentOwner(int? id) => ReceiptDocumentSource.PersonId = WriteOffDocumentSource.PersonId = id;

    private async Task OnReceiptDocumentChangedAsync(int? id)
    {
        if (_model is null)
            return;
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
        if (_model is not null)
            _model.ShipperId = id;
    }

    private void OnCameraChanged(bool? value)
    {
        if (_model is not null)
            _model.CameraIsConnected = value == true ? (byte)1 : (byte)0;
    }

    private void OnTypeSortChanged(bool? value) => EquipmentTypeSource.SortByName = _sortTypesByName = value == true;

    private void OnMacChanged(string? value, int index)
    {
        if (_model is null)
            return;
        var normalized = global::Clayzor.Lib.Entities.Inventory.Equipment.NormalizeMacAddress(value);
        if (index == 1) _model.MacAddress = normalized;
        if (index == 2) _model.MacAddress2 = normalized;
        if (index == 3) _model.MacAddressIlo = normalized;
    }

    private void ClearHiddenFeatures()
    {
        if (_model is null)
            return;
        if (!ShowNetworkName) _model.NetworkName = string.Empty;
        if (MacCount < 1) _model.MacAddress = string.Empty;
        if (MacCount < 2) _model.MacAddress2 = string.Empty;
        if (MacCount < 3) _model.MacAddressIlo = string.Empty;
        if (!ShowPhonePool) _model.PhonePoolId = null;
        if (!ShowCamera) _model.CameraIsConnected = 0;
    }

    private void UpdateOptions()
    {
        if (_model is null)
            return;
        _snOptions.Required = _selectedType?.IsSerialNumberRequired > 0;
        foreach (var options in new[] { _typeOptions, _modelOptions, _phonePoolOptions, _ownerOptions, _receiptDocumentOptions, _writeOffDocumentOptions, _shipperOptions, _tenantOptions })
            options.ReadOnly = !_canEdit;
        foreach (var options in new ClayInputOptions[] { _writeOffDateOptions, _writeOffTimeOptions, _snOptions, _networkNameOptions, _macOptions, _descriptionOptions })
            options.ReadOnly = !_canEdit;
    }

    private bool ValidateBusinessRules()
    {
        if (_model is null)
            return false;
        var errors = new List<string>();
        if (_model.EquipmentTypeId is null || _model.ModelId is null) errors.Add("Выберите тип и модель оборудования.");
        if (_model.OwnerId is null) errors.Add("Укажите владельца оборудования.");
        if (_selectedType?.IsSerialNumberRequired > 0 && string.IsNullOrWhiteSpace(_model.SerialNumber)) errors.Add("Укажите серийный номер.");
        if (_model.DocumentWriteOffId is not null && _writeOffDate is null) errors.Add("Укажите дату списания.");
        if (_model.EquipmentTypeId != SimEquipmentTypeId && _writeOffDate is not null && _model.DocumentWriteOffId is null) errors.Add("Укажите документ списания.");
        if (_selectedType?.MacAddressCount > 0 &&
            (!global::Clayzor.Lib.Entities.Inventory.Equipment.IsValidMacAddress(_model.MacAddress) ||
             !global::Clayzor.Lib.Entities.Inventory.Equipment.IsValidMacAddress(_model.MacAddress2) ||
             !global::Clayzor.Lib.Entities.Inventory.Equipment.IsValidMacAddress(_model.MacAddressIlo)))
            errors.Add("MAC-адрес должен содержать 12 hex-символов; допустимы разделители пробел, двоеточие, дефис и точка.");
        _validationMessage = errors.Count == 0 ? null : string.Join(" ", errors);
        return errors.Count == 0;
    }

    private async Task SaveAsync(bool closeAfterSave)
    {
        if (_model is null || !_loaded || !_canEdit || _loading || _saving)
            return;
        if (!IsModified)
        {
            if (closeAfterSave)
                await CloseAsync();
            return;
        }
        var model = _model;
        var version = ++_operationVersion;
        _saving = true;
        _validationMessage = null;
        StateHasChanged();
        try
        {
            await Task.Delay(100);
            if (_form is not null)
            {
                await _form.ValidateAsync();
                if (!IsCurrentSave(model, version) || !_form.IsValid)
                    return;
            }
            if (!ValidateBusinessRules())
                return;
            model.WriteOffAtUtc = await ConvertWriteOffToUtcAsync();
            if (!IsCurrentSave(model, version))
                return;
            await Service.UpdateAsync(model);
            if (!IsCurrentSave(model, version))
                return;
            _originalSnapshot = Snapshot();
            await JS.InvokeVoidAsync("clayModelEquipmentForm.refreshOpener", _pageCts.Token);
            if (!IsCurrentSave(model, version))
                return;
            if (closeAfterSave)
                await CloseAsync();
            else
                await LoadCurrentAsync(Id);
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (IsCurrentSave(model, version))
                _validationMessage = "Оборудование не сохранено. Подробности — в сообщении базы данных.";
        }
        catch (InvalidOperationException ex)
        {
            if (IsCurrentSave(model, version))
                _validationMessage = ex.Message;
        }
        finally
        {
            if (!_disposed)
            {
                _saving = false;
                StateHasChanged();
            }
        }
    }

    private bool IsCurrentSave(EquipmentEntity model, int version) => !_disposed && version == _operationVersion && ReferenceEquals(model, _model);

    private async Task<DateTime?> ConvertWriteOffToUtcAsync()
    {
        if (_writeOffDate is null)
            return null;
        var milliseconds = await JS.InvokeAsync<long>(
            "clayGridTimeZone.localDateTimeToUtcMilliseconds", _pageCts.Token,
            _writeOffDate.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            (_writeOffTime ?? TimeSpan.Zero).ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture));
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
    }

    private string Snapshot() => JsonSerializer.Serialize(new { Equipment = _model, WriteOffDate = _writeOffDate, WriteOffTime = _writeOffTime });

    private async Task RefreshAsync()
    {
        if (_model is null || _loading || _saving)
            return;
        if (IsModified && !await ConfirmAsync("Обновление", "Отменить несохранённые изменения и загрузить запись повторно?"))
            return;
        await LoadCurrentAsync(Id);
    }

    private async Task CancelAsync()
    {
        if (IsModified && !await ConfirmAsync("Закрытие формы", "Закрыть форму без сохранения изменений?"))
            return;
        await CloseAsync();
    }

    private async Task CopyAsync()
    {
        if (Id > 0)
            await JS.InvokeVoidAsync("open", $"/Equipment/EquipmentIns?copyId={Id}", "_blank");
    }

    private async Task OpenModelViewAsync(EquipmentModel model) =>
        await JS.InvokeVoidAsync("open", EquipmentIns.BuildEditUrl(Configuration["URI_model_form"]!, model.Id), "_blank");

    private async Task CloseAsync()
    {
        _allowNavigation = true;
        await JS.InvokeVoidAsync("clayModelEquipmentForm.close");
    }

    private async Task BeforeInternalNavigationAsync(LocationChangingContext context)
    {
        if (_allowNavigation)
        {
            _allowNavigation = false;
            return;
        }
        if (_saving)
        {
            context.PreventNavigation();
            return;
        }
        if (!IsModified)
            return;
        context.PreventNavigation();
        if (!await ConfirmAsync("Переход на другую страницу", "Покинуть форму без сохранения изменений?"))
            return;
        _allowNavigation = true;
        Navigation.NavigateTo(context.TargetLocation);
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

    /// <summary>Отменяет незавершённые операции формы.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pageCts.Cancel();
        _pageCts.Dispose();
    }
}
