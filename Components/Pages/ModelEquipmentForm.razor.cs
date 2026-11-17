using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages;

/// <summary>Создание и редактирование модели оборудования.</summary>
public partial class ModelEquipmentForm : IDisposable
{
    private const int InventorySpecialistRoleId = 43;
    private const int DutyEngineerRoleId = 102;
    private const int PhoneEquipmentTypeId = 30;

    private readonly CancellationTokenSource _pageCts = new();
    private readonly ClaySelectOptions _typeOptions = new()
    {
        Required = true,
        RequiredError = "Выберите тип оборудования",
        AutoSelectSingle = true,
        MaxItems = 300,
        Placeholder = "Введите название типа",
    };
    private readonly ClaySelectOptions _resourceOptions = new()
    {
        AutoSelectSingle = true,
        AlwaysShowAdvancedSearch = true,
        Placeholder = "Введите название ресурса",
        CreateItemText = "Создать ресурс",
    };
    private readonly ClayTextBoxOptions _modelNameOptions = new() { Required = true, RequiredError = "Укажите модель", MaxLength = 100 };
    private readonly ClayTextBoxOptions _partNumberOptions = new() { MaxLength = 50 };
    private readonly ClayTextBoxOptions _cpuOptions = new() { MaxLength = 50 };
    private readonly ClayTextBoxOptions _networkNameOptions = new() { MaxLength = 50 };
    private readonly ClayTextBoxOptions _lifeTimeOptions = new() { MaxLength = 50 };
    private readonly ClayTextAreaOptions _driverOptions = new() { MaxLength = 300, Lines = 3 };
    private readonly ClayTextAreaOptions _descriptionOptions = new() { MaxLength = 300, Lines = 2 };
    private readonly ClayNumberOptions _monitorSizeOptions = new() { Precision = 1, Min = 0, Step = 0.1m };
    private readonly ClayNumberOptions _memoryBanksOptions = new() { Precision = 0, Min = 0 };
    private readonly ClayDropDownListOptions _networkSelectOptions = new();
    private readonly ClayDropDownListOptions _memoryTypeOptions = new();
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
    private string _originalSnapshot = string.Empty;
    private int _originalTypeId;
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
    private bool ShowPhoneTemplate => _model?.Id > 0 && _selectedType?.Id == PhoneEquipmentTypeId && _isDutyEngineer;
    private bool IsModified => _loaded && _model is not null && _originalSnapshot != Snapshot(_model);
    private bool CanSave => IsModified && _canEdit && !_loading && !_saving;
    private decimal? MonitorSize => _model is null || _model.MonitorSize == 0 ? null : (decimal)_model.MonitorSize;
    private string UsageText => $"В базе данных сохранено {_usage.Total} единиц оборудования данной модели" +
                                (_usage.WrittenOff > 0 ? $", из них списано {_usage.WrittenOff}." : ".");
    private string? ChangedByUrl => _model?.ChangedById is > 0 && !string.IsNullOrWhiteSpace(Configuration["URI_user_form"])
        ? AppendQuery(Configuration["URI_user_form"]!, $"id={_model.ChangedById}")
        : null;
    private Func<ClaySelectAdvancedSearchContext, Task<ClaySelectExternalResult<ResourceLookup, int?>?>>? ResourceAdvancedSearch =>
        !string.IsNullOrWhiteSpace(Configuration["URI_resource_search"]) ? OpenResourceSearchAsync : null;
    private Func<ClaySelectAdvancedSearchContext, Task<ClaySelectExternalResult<ResourceLookup, int?>?>>? ResourceCreateItem =>
        !string.IsNullOrWhiteSpace(Configuration["URI_resource_form"]) ? OpenResourceCreateAsync : null;
    private Func<ResourceLookup, Task>? ResourceViewItem =>
        !string.IsNullOrWhiteSpace(Configuration["URI_resource_form"]) ? OpenResourceViewAsync : null;

    /// <summary>Загружает запись после рендера и повторно загружает её при смене параметров URL.</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var key = (Id: Id, InitialType: Id > 0 ? 0 : InitialTypeId);
        if (_loadedKey == key)
            return;
        _loadedKey = key;
        await LoadCurrentAsync(key.Id, key.InitialType);
    }

    private async Task LoadCurrentAsync(int id, int initialTypeId)
    {
        var version = ++_loadVersion;
        _loading = true;
        _loaded = false;
        _notFound = false;
        _model = null;
        _selectedType = null;
        _equipmentTypeId = null;
        StateHasChanged();
        await Task.Delay(100);
        try
        {
            await LoadDataAsync(id, initialTypeId, version);
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

    private async Task LoadDataAsync(int id, int initialTypeId, int version)
    {
        var model = id > 0
            ? await Service.GetModelAsync(id)
            : new EquipmentModel { EquipmentTypeId = initialTypeId };
        if (version != _loadVersion || _disposed)
            return;
        if (model is null)
        {
            _notFound = true;
            return;
        }

        var type = model.EquipmentTypeId > 0
            ? await EquipmentTypeSource.GetByIdAsync(model.EquipmentTypeId, _pageCts.Token)
            : null;
        if (version != _loadVersion || _disposed)
            return;
        var memoryTypes = await MemoryTypeSource.GetAllAsync(_pageCts.Token);
        if (version != _loadVersion || _disposed)
            return;
        var canEdit = await Service.HasRoleAsync(InventorySpecialistRoleId);
        if (version != _loadVersion || _disposed)
            return;
        var isDutyEngineer = await Service.HasRoleAsync(DutyEngineerRoleId);
        if (version != _loadVersion || _disposed)
            return;
        var usage = id > 0 ? await Service.GetUsageAsync(id) : new EquipmentModelUsage();
        if (version != _loadVersion || _disposed)
            return;

        _model = model;
        _selectedType = type;
        _equipmentTypeId = model.EquipmentTypeId > 0 ? model.EquipmentTypeId : null;
        _memoryTypes = [new MemoryType { Id = 0, Name = "—" }, .. memoryTypes.Where(item => item.Id != 0)];
        _canEdit = canEdit;
        _isDutyEngineer = isDutyEngineer;
        _usage = usage;
        _originalTypeId = model.EquipmentTypeId;
        _originalSnapshot = Snapshot(model);
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
            if (_selectedType?.Id != PhoneEquipmentTypeId)
                _model.PhoneTemplate = string.Empty;
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
        if (_model is null || _saving || !_canEdit)
            return;
        if (!IsModified)
        {
            if (closeAfterSave)
                await CloseAsync();
            return;
        }

        var model = _model;
        var version = ++_loadVersion;
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
            if (!ValidateBusinessRules(model))
                return;
            if (await Service.HasDuplicateAsync(model))
            {
                if (IsCurrentSave(model, version))
                    _validationMessage = "Модель с таким наименованием и PN уже существует.";
                return;
            }
            if (!IsCurrentSave(model, version))
                return;
            if (model.Id > 0 && _originalTypeId != model.EquipmentTypeId)
            {
                var usage = await Service.GetUsageAsync(model.Id, model.EquipmentTypeId);
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
                }
            }

            var isNew = model.Id == 0;
            var id = await Service.SaveAsync(model);
            if (!IsCurrentSave(model, version))
                return;
            if (id <= 0)
            {
                _validationMessage = "База данных не вернула идентификатор созданной модели.";
                return;
            }
            await JS.InvokeVoidAsync("clayModelEquipmentForm.refreshOpener");
            if (!IsCurrentSave(model, version))
                return;
            if (closeAfterSave)
            {
                await CloseAsync();
                return;
            }
            var reloadVersion = ++_loadVersion;
            await LoadDataAsync(id, 0, reloadVersion);
            if (_disposed || reloadVersion != _loadVersion || !_loaded)
                return;
            if (isNew)
            {
                _allowNavigation = true;
                _loadedKey = (id, 0);
                Navigation.NavigateTo($"/ModelEquipmentForm?id={id}", replace: true);
            }
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (IsCurrentSave(model, version))
                _validationMessage = "Модель не сохранена. Подробности — в сообщении базы данных.";
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

    private bool IsCurrentSave(EquipmentModel model, int version) =>
        !_disposed && version == _loadVersion && ReferenceEquals(model, _model);

    private bool ValidateBusinessRules(EquipmentModel model)
    {
        if (model.EquipmentTypeId <= 0 || string.IsNullOrWhiteSpace(model.Name))
        {
            _validationMessage = "Выберите тип и укажите наименование модели.";
            return false;
        }
        if ((model.EquipmentTypeId == 1 || model.EquipmentTypeId == 2) && model.PartNumber.Trim().Length < 3)
        {
            _validationMessage = "Для типов 1 и 2 PN должен содержать не менее 3 символов.";
            return false;
        }
        if (model.NetworkInterfaces > 0 && model.NetworkName.Trim().Length < 3)
        {
            _validationMessage = "При Net больше 0 название Net должно содержать не менее 3 символов.";
            return false;
        }
        return true;
    }

    private async Task RefreshAsync()
    {
        if (_model is null || _loading || _saving)
            return;
        if (IsModified && !await ConfirmAsync("Обновление", "Отменить несохранённые изменения и загрузить запись повторно?"))
            return;
        _loadedKey = (Id, Id > 0 ? 0 : InitialTypeId);
        await LoadCurrentAsync(Id, InitialTypeId);
    }

    private async Task CancelAsync()
    {
        if (IsModified && !await ConfirmAsync("Закрытие формы", "Закрыть форму без сохранения изменений?"))
            return;
        await CloseAsync();
    }

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
        await JS.InvokeVoidAsync("open", AppendQuery(GetConfiguredUrl("URI_resource_form"), $"id={resource.Id}"), "_blank");

    private async Task OpenPhoneTemplateAsync()
    {
        if (_model is not null)
            await JS.InvokeVoidAsync("open", $"/PhoneTemplateForm?id={_model.Id}", "_blank");
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

    private static string Snapshot(EquipmentModel model) => JsonSerializer.Serialize(model);

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
}
