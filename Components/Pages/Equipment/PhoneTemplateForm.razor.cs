using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>Отдельный редактор шаблона настройки IP-телефона.</summary>
public partial class PhoneTemplateForm : IDisposable
{
    private const int DutyEngineerRoleId = 102;
    private const int PhoneEquipmentTypeId = 30;

    private readonly ClayEntityFormOptions _entityFormOptions = new() { Class = "model-equipment-form" };
    private readonly ClayTextAreaOptions _templateOptions = new() { Lines = 24, Class = "phone-template-editor" };
    private readonly ClayToolbarButtonOptions _okOptions = new()
    {
        Text = "OK", Icon = Icons.Material.Filled.Check, Title = "Сохранить и закрыть",
        Color = Color.Primary, Variant = Variant.Filled,
    };
    private readonly ClayToolbarButtonOptions _applyOptions = new()
    {
        Text = "Применить", Icon = Icons.Material.Filled.Save, Title = "Сохранить без закрытия",
    };
    private readonly ClayToolbarButtonOptions _loadOptions = new()
    {
        Text = "Из файла", Icon = Icons.Material.Filled.FolderOpen, Title = "Загрузить шаблон из файла",
    };
    private readonly ClayToolbarButtonOptions _refreshOptions = new()
    {
        Text = "Обновить", Icon = Icons.Material.Filled.Refresh, Title = "Загрузить шаблон повторно",
    };
    private readonly ClayToolbarButtonOptions _cancelOptions = new()
    {
        Text = "Отмена", Icon = Icons.Material.Filled.Close, Title = "Закрыть форму",
    };

    private EquipmentModel? _model;
    private string _originalTemplate = string.Empty;
    private bool _canEdit;
    private bool _loaded;
    private bool _notFound;
    private bool _loading = true;
    private bool _saving;
    private bool _allowNavigation;
    private bool _disposed;
    private int _loadedId = -1;
    private int _loadVersion;
    private string? _errorMessage;

    /// <summary>Идентификатор модели IP-телефона.</summary>
    [SupplyParameterFromQuery(Name = "id")]
    public int Id { get; set; }

    private string Title => _model is null ? "Шаблон телефона" : $"Шаблон телефона — {_model.Name} #{_model.Id}";
    private bool IsModified => _loaded && _model is not null && _model.PhoneTemplate != _originalTemplate;
    private bool CanSave => IsModified && _canEdit && !_loading && !_saving;
    private string? ChangedByUrl => _model?.ChangedById is > 0 && !string.IsNullOrWhiteSpace(Configuration["URI_user_form"])
        ? $"{Configuration["URI_user_form"]}{(Configuration["URI_user_form"]!.Contains('?') ? '&' : '?')}id={_model.ChangedById}"
        : null;

    /// <summary>Загружает модель после рендера и при изменении идентификатора.</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_loadedId == Id)
            return;
        _loadedId = Id;
        await LoadCurrentAsync();
    }

    private async Task LoadCurrentAsync()
    {
        var version = ++_loadVersion;
        _loading = true;
        _loaded = false;
        _notFound = false;
        _model = null;
        StateHasChanged();
        await Task.Delay(100);
        try
        {
            var model = Id > 0 ? await Service.GetModelAsync(Id) : null;
            if (version != _loadVersion)
                return;
            if (model is null || model.EquipmentTypeId != PhoneEquipmentTypeId)
            {
                _notFound = true;
                return;
            }
            var canEdit = await Service.HasRoleAsync(DutyEngineerRoleId);
            if (version != _loadVersion)
                return;
            _model = model;
            _originalTemplate = model.PhoneTemplate;
            _canEdit = canEdit;
            _templateOptions.ReadOnly = !canEdit;
            _loaded = true;
        }
        finally
        {
            if (version == _loadVersion)
            {
                _loading = false;
                StateHasChanged();
            }
        }
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
        _errorMessage = null;
        StateHasChanged();
        try
        {
            await Task.Delay(100);
            if (!IsCurrentSave(model, version))
                return;
            if (!string.IsNullOrWhiteSpace(_originalTemplate) && string.IsNullOrWhiteSpace(model.PhoneTemplate) &&
                !await ConfirmAsync("Очистка шаблона", "Вы уверены, что хотите очистить шаблон телефона для данной модели?"))
                return;
            if (!IsCurrentSave(model, version))
                return;
            await Service.SavePhoneTemplateAsync(model);
            if (!IsCurrentSave(model, version))
                return;
            await JS.InvokeVoidAsync("clayModelEquipmentForm.refreshOpener");
            if (!IsCurrentSave(model, version))
                return;
            if (closeAfterSave)
            {
                await CloseAsync();
                return;
            }
            await LoadCurrentAsync();
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (IsCurrentSave(model, version))
                _errorMessage = "Шаблон не сохранён. Подробности — в сообщении базы данных.";
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

    private async Task LoadFileAsync(InputFileChangeEventArgs args)
    {
        if (_model is null || !_canEdit)
            return;
        await using var stream = args.File.OpenReadStream(maxAllowedSize: 10_000_000);
        using var reader = new StreamReader(stream);
        _model.PhoneTemplate = await reader.ReadToEndAsync();
    }

    private async Task BrowseFileAsync() => await JS.InvokeVoidAsync("clayModelEquipmentForm.browseFile", "phoneTemplateFile");

    private async Task RefreshAsync()
    {
        if (_model is null || _loading || _saving)
            return;
        if (IsModified && !await ConfirmAsync("Обновление", "Отменить несохранённые изменения и загрузить шаблон повторно?"))
            return;
        await LoadCurrentAsync();
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

    /// <summary>Блокирует продолжение сохранения и JS-вызовы после ухода со страницы.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _loadVersion++;
    }
}
