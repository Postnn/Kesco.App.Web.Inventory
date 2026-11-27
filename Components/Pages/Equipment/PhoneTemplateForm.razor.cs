using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>Отдельный редактор шаблона настройки IP-телефона.</summary>
public partial class PhoneTemplateForm : IDisposable, IAsyncDisposable
{

    private readonly ClayEntityFormOptions _entityFormOptions = new() { Class = "model-equipment-form" };
    private readonly ClayTextAreaOptions _templateOptions = new()
    {
        Lines = 24, Class = "phone-template-editor", DraftKey = "phone-template.text",
        BrowserManaged = true, BrowserLabel = "Шаблон телефона",
    };
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

    private readonly CancellationTokenSource _pageCts = new();
    private EquipmentModel? _model;
    private Employee? CurrentUser;
    private EquipmentModelWriteBaseline? _writeBaseline;
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
    private bool IsModified => _loaded && _model is not null && _model.PhoneTemplate != (_writeBaseline?.PhoneTemplate ?? string.Empty);
    private bool CanSave => IsModified && _canEdit && !_loading && !_saving && !TemplateWriteBlocked;
    private string? ChangedByUrl => _model?.ChangedById is > 0 && !string.IsNullOrWhiteSpace(Configuration["URI_user_form"])
        ? $"{Configuration["URI_user_form"]}{(Configuration["URI_user_form"]!.Contains('?') ? '&' : '?')}id={_model.ChangedById}"
        : null;

    /// <summary>Загружает модель после рендера и при изменении идентификатора.</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await ReleaseTemplateTransitionAfterRenderAsync();
        if (_loadedId == Id)
        {
            await EnsureTemplateEditorWatcherAsync();
            if (_restoreTemplateRawRequested && _editorWatchGeneration is not null)
                await RestoreTemplateRawAsync();
            return;
        }
        _loadedId = Id;
        CancelTemplateConflictReview();
        await ReleaseTemplateEditorWatcherAsync();
        await ReleaseTemplateJournalAsync();
        await LoadCurrentAsync();
    }

    private async Task LoadCurrentAsync()
    {
        var version = ++_loadVersion;
        _loading = true;
        _loaded = false;
        _notFound = false;
        CurrentUser = null;
        _errorMessage = null;
        _templateInputDiscarded = false;
        _templateConflictNeedsReview = false;
        ClearTemplateAttribute();
        _templateAttributeTypes = [];
        _model = null;
        StateHasChanged();
        await Task.Delay(100);
        try
        {
            var model = Id > 0 ? await EquipmentModel.GetForEditAsync(Db, Id, _pageCts.Token) : null;
            if (version != _loadVersion)
                return;
            if (model is null || model.EquipmentTypeId != (int)ТипыОборудования.IP_телефон)
            {
                _notFound = true;
                return;
            }
            var actor = await Employee.GetCurrentWithRolesAsync(Db, await ReadPrincipalAsync(), _pageCts.Token);
            if (version != _loadVersion)
                return;
            _model = model;
            _writeBaseline = model.CaptureWriteBaseline();
            CurrentUser = actor;
            _canEdit = actor?.HasRole(Роли.Дежурный_инженер) == true;
            _templateOptions.ReadOnly = !_canEdit || _editorWatchGeneration is null;
            _loaded = true;
            if (_templateJournal is not null && actor?.Id != _journalEmployeeId)
            {
                await ReleaseTemplateEditorWatcherAsync();
                await ReleaseTemplateJournalAsync();
            }
            if (_disposed || version != _loadVersion)
                return;
            if (_canEdit && actor is not null && _templateJournal is null)
                await InitializeTemplateJournalAsync(model.Id, actor.Id, version);
            if (_canEdit && IsCurrentSave(model, version))
                await LoadTemplateAttributeTypesAsync(model, version);
        }
        catch (OperationCanceledException) when (_disposed || version != _loadVersion) { }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _loadVersion)
                _errorMessage = "Не удалось загрузить шаблон или подтвердить текущего сотрудника.";
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
        if (_model is not { } model || _writeBaseline is not { } baseline || CurrentUser is not { } actor ||
            _saving || !_canEdit || TemplateWriteBlocked || _templateJournal is not { } journal)
            return;

        var version = ++_loadVersion;
        _saving = true;
        _errorMessage = null;
        StateHasChanged();
        var dispatched = false;
        var noEffect = false;
        try
        {
            var snapshot = await ReadCurrentTemplateAsync(_pageCts.Token);
            if (!IsCurrentSave(model, version))
                return;
            var submitted = MatchesBrowserTemplateText(model.PhoneTemplate, snapshot.Text)
                ? model.PhoneTemplate ?? string.Empty : snapshot.Text;
            if (model.PhoneTemplate != submitted)
            {
                ++_templateInputEpoch;
                model.PhoneTemplate = submitted;
            }
            if (!IsModified)
            {
                if (closeAfterSave)
                    await CloseAtEditorRevisionAsync(snapshot.Revision);
                return;
            }
            if (!string.IsNullOrWhiteSpace(baseline.PhoneTemplate) && string.IsNullOrWhiteSpace(submitted) &&
                !await ConfirmAsync("Очистка шаблона", "Вы уверены, что хотите очистить шаблон телефона для данной модели?"))
                return;
            if (!IsCurrentSave(model, version) || model.PhoneTemplate != submitted)
                return;

            var principal = await ReadPrincipalAsync();
            if (!IsCurrentSave(model, version))
                return;
            var command = new EquipmentModel { Id = model.Id, PhoneTemplate = submitted };
            var attemptId = Guid.NewGuid();
            var boundary = new EquipmentModelWriteBoundary(
                ct => BeginTemplateWriteAsync(journal, model, baseline, submitted, attemptId, version, ct),
                () =>
                {
                    if (!IsCurrentSave(model, version) || model.PhoneTemplate != submitted ||
                        !ReferenceEquals(_templateJournal, journal))
                        throw new InvalidOperationException("Ввод или контекст изменился. Запись не отправлена.");
                    dispatched = true;
                }, () => noEffect = true);
            await command.SaveJournaledPhoneTemplateAsync(Db, principal, actor.Id, baseline, attemptId, boundary, _pageCts.Token);
            if (!IsCurrentSave(model, version))
                return;

            _templateConflictNeedsReview = false;
            _knownWrite = true;
            _confirmedStored = false;
            _submittedTemplate = submitted;
            await FinishKnownTemplateWriteAsync(journal);
            await JS.InvokeVoidAsync("clayModelEquipmentForm.refreshOpener");
            if (!IsCurrentSave(model, version))
                return;
            await ReloadKnownTemplateAsync(model, version);
            if (closeAfterSave && IsCurrentSave(model, version) && !_knownWrite && !IsModified)
                await CloseAtEditorRevisionAsync(snapshot.Revision);
        }
        catch (Exception ex) when (IsJournalError(ex) || ex is IOException)
        {
            if (!IsCurrentSave(model, version))
                return;
            if (ex is InventoryOperationPendingException) _serverWritePending = true;
            if (!_knownWrite)
                await RecordTemplateFailureAsync(journal, dispatched, noEffect);
            if (!IsCurrentSave(model, version) || !ReferenceEquals(_templateJournal, journal))
                return;
            ShowTemplateJournalState();
            if (dispatched && noEffect && ex is EquipmentModel.PhoneTemplateWriteRejectedException { BaselineConflict: true })
            {
                _templateConflictNeedsReview = true;
                _inputNotice = "Сверьте исходную версию через «Обновить». Ваш текст оставлен в редакторе.";
            }
            if (noEffect || ex is UnauthorizedAccessException)
                _errorMessage = ex.Message;
            else if (!_knownWrite && !dispatched && _notSentAttempt is null)
                _errorMessage = "Не удалось подготовить сохранение. Запись не отправлена.";
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

    private async Task<string?> ReadPrincipalAsync()
    {
        var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
        return identity?.IsAuthenticated == true ? identity.Name : null;
    }

    private bool IsCurrentSave(EquipmentModel model, int version) =>
        !_disposed && model.Id == Id && version == _loadVersion && ReferenceEquals(model, _model);

    private Task LoadFileAsync(InputFileChangeEventArgs args) => ImportTemplateFileAsync(args);

    private async Task BrowseFileAsync() => await JS.InvokeVoidAsync("clayModelEquipmentForm.browseFile", "phoneTemplateFile");

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

    /// <summary>Сохраняет существующий синхронный вход освобождения редактора.</summary>
    public void Dispose() => _ = DisposeAsync();

    /// <summary>Отменяет вызовы текущей формы и освобождает журнал без удаления попытки.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _loadVersion++;
        _pageCts.Cancel();
        try
        {
            await ReleaseTemplateEditorWatcherAsync();
            await ReleaseTemplateJournalAsync();
            if (_templateTextModule is not null)
                await _templateTextModule.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        finally { _pageCts.Dispose(); }
    }
}
