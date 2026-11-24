using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>Чтение почтовых списков, подготовка и сверка черновика.</summary>
public partial class SlBlForm : IAsyncDisposable
{
    private const string DraftScope = "main";
    private readonly string _draftOwner = Guid.NewGuid().ToString("N");
    private readonly ClayEntityFormOptions _formOptions = new() { MaxWidth = "100%" };
    private string DraftRootSelector => $".slbl-draft-{_draftOwner}";
    private static readonly ClayToolbarButtonOptions SaveButton = new() { Text = "Сохранить", Icon = Icons.Material.Filled.Save };
    private static readonly ClayToolbarButtonOptions RefreshButton = new() { Text = "Обновить", Icon = Icons.Material.Filled.Refresh };
    private static readonly ClayToolbarButtonOptions CloseButton = new() { Text = "Отмена", Icon = Icons.Material.Filled.Close };
    private readonly ClaySelectOptions AddressOptions = new() { Label = "E-mail адрес", Required = true, AutoSelectSingle = true };
    private readonly ClayTextAreaOptions TextOptions = new() { Label = "Список адресов", Lines = 12, DraftKey = "slbl.text" };
    private static readonly ClayRadioOption<SlBlKind?>[] Kinds =
    [
        new(SlBlKind.Safe, "Список безопасных адресов (письма с этих адресов не будут попадать в спам-карантин)"),
        new(SlBlKind.Blocked, "Список заблокированных адресов (письма с этих адресов всегда будут попадать в спам-карантин)"),
    ];

    /// <summary>Назначает уникальный CSS-корень для изоляции черновика формы.</summary>
    public SlBlForm() => _formOptions.Class = $"slbl-draft-{_draftOwner}";

    /// <summary>Необязательный составной ключ ящика и списка.</summary>
    [SupplyParameterFromQuery(Name = "id")] public string? Id { get; set; }

    [Inject] private DbManager Db { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private ClayDraftService Drafts { get; set; } = null!;
    [Inject] private IDialogService Dialogs { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;

    private readonly CancellationTokenSource _lifetime = new();
    private Employee? CurrentUser { get; set; }
    private SlBlAddressSelectDataSource? _source;
    private SlBlLists? _lists;
    private SlBlKind? _kind;
    private string? _address;
    private string _text = "";
    private string _original = "";
    private string? _error;
    private bool _busy = true;
    private bool _disposed;
    private bool _allowNavigation;
    private bool _restoreRaw;
    private SlBlDifferences? _differences;
    private ClayDraftRegistration? _draft;
    private bool Dirty => !string.Equals(_text, _original, StringComparison.Ordinal);

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            if (!_restoreRaw || _draft is null) return;
            _restoreRaw = false;
            try { await _draft.ApplyRawAsync(_lifetime.Token); }
            catch (Exception e) when (e is JSException or InvalidOperationException)
            {
                _error = "Часть ввода не удалось восстановить.";
                if (!_disposed) StateHasChanged();
            }
            return;
        }
        try
        {
            var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
            CurrentUser = await Employee.GetCurrentAsync(Db, identity?.IsAuthenticated == true ? identity.Name : null, _lifetime.Token);
            if (CurrentUser is null) { _error = "Не удалось подтвердить текущего сотрудника."; return; }
            _source = new(Db);
            if (!SlBlKey.TryParse(Id, out var key)) { _error = "Некорректный адрес или вид списка в ссылке."; return; }
            if (key is not null) await LoadAddressAsync(key.Address, key.Kind);
        }
        catch (Exception e) when (e is not OperationCanceledException) { _error = "Не удалось загрузить почтовые списки."; }
        finally { _busy = false; if (!_disposed) StateHasChanged(); }
    }

    private async Task ChangeAddressAsync(string? value)
    {
        if (_busy || value == _address) return;
        _busy = true;
        try { if (await CanDiscardAsync()) await LoadAddressAsync(value, null); }
        catch (Exception e) when (e is not OperationCanceledException) { _error = "Не удалось загрузить списки выбранного адреса."; }
        finally { _busy = false; }
    }

    private async Task LoadAddressAsync(string? address, SlBlKind? preferredKind)
    {
        var key = address is not null ? await SlBlData.FindAddressAsync(Db, address, _lifetime.Token) : null;
        if (address is not null && key is null) { _error = "Почтовый адрес не найден или недоступен."; return; }
        var lists = key is not null ? await SlBlData.ReadAsync(Db, key, _lifetime.Token) : null;
        await DiscardAsync();
        _address = key;
        _lists = lists;
        _error = null;
        await SetKindAsync(preferredKind ?? lists?.InitialKind);
    }

    private async Task ChangeKindAsync(SlBlKind? value)
    {
        if (_busy || value == _kind) return;
        _busy = true;
        try
        {
            if (!await CanDiscardAsync()) return;
            await DiscardAsync();
            await SetKindAsync(value);
        }
        finally { _busy = false; }
    }

    private async Task SetKindAsync(SlBlKind? kind)
    {
        _kind = kind;
        _original = _text = kind is { } selected ? _lists?.GetText(selected) ?? "" : "";
        _differences = null;
        _restoreRaw = false;
        if (_address is null || kind is null || CurrentUser is null) return;
        try
        {
            _draft = await Drafts.RegisterHandleAsync("slbl", $"{_address}:{(byte)kind}",
                CurrentUser.Id.ToString(CultureInfo.InvariantCulture), 1, DraftRootSelector, _lifetime.Token);
            if (_draft.Restore.TypedScopes?.TryGetValue(DraftScope, out var json) == true)
                _text = JsonSerializer.Deserialize<string>(json) ?? _original;
            _restoreRaw = _draft.Restore.HasDraft;
        }
        catch (Exception e) when (e is JSException or InvalidOperationException or JsonException)
        {
            _error = "Не удалось восстановить черновик в браузере.";
        }
    }

    private async Task ChangeTextAsync(string? value)
    {
        if (_busy) return;
        _text = value ?? "";
        _differences = null;
        try
        {
            if (_draft is not null)
                await _draft.SaveTypedScopeAsync(DraftScope, JsonSerializer.Serialize(_text), _lifetime.Token);
        }
        catch (Exception e) when (e is JSException or InvalidOperationException) { _error = "Не удалось сохранить черновик в браузере."; }
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        if (_address is null || _kind is not { } kind)
        {
            if (!Dirty) Navigation.NavigateTo(Navigation.Uri, forceLoad: true);
            return;
        }
        _busy = true;
        try
        {
            var latest = await SlBlData.ReadAsync(Db, _address, _lifetime.Token);
            if (latest is null || !string.Equals(latest.GetText(kind).Trim(), _text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                if (SlBlData.Validate(_text) is null)
                    _differences = await SlBlData.CompareAsync(Db, new(_address, kind), _text, _lifetime.Token);
                if (!await ConfirmAsync("Изменения не сохранены! Вы хотите заново загрузить данные?")) return;
            }
            await DiscardAsync();
            _lists = latest;
            await SetKindAsync(kind);
        }
        catch (Exception e) when (e is not OperationCanceledException) { _error = "Не удалось перечитать или сверить фактический список."; }
        finally { _busy = false; }
    }

    private Task<bool> CanDiscardAsync() => Dirty ? ConfirmAsync("Отказаться от несохранённых изменений?") : Task.FromResult(true);

    private async Task<bool> ConfirmAsync(string message)
    {
        var dialog = await Dialogs.ShowAsync<ConfirmDialog>("Подтверждение", new DialogParameters<ConfirmDialog>
        {
            { dialog => dialog.Message, message }, { dialog => dialog.ConfirmText, "Да" }, { dialog => dialog.CancelText, "Нет" },
        });
        return await dialog.Result is { Canceled: false };
    }

    private async Task CloseAsync()
    {
        if (!await CanDiscardAsync()) return;
        await DiscardAsync();
        _text = _original;
        await JS.InvokeVoidAsync("clayModelEquipmentForm.close", _lifetime.Token);
    }

    private async Task BeforeNavigationAsync(LocationChangingContext context)
    {
        if (_allowNavigation) { _allowNavigation = false; return; }
        if (_busy) { context.PreventNavigation(); return; }
        if (!Dirty) return;
        context.PreventNavigation();
        if (!await CanDiscardAsync()) return;
        await DiscardAsync();
        _allowNavigation = true;
        Navigation.NavigateTo(context.TargetLocation);
    }

    private async Task DiscardAsync()
    {
        if (_draft is null) return;
        await _draft.DiscardAsync(_lifetime.Token);
        await _draft.DisposeAsync();
        _draft = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync();
        if (_draft is not null) await _draft.DisposeAsync();
        _lifetime.Dispose();
    }
}
