using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>Карточка строкового префикса FMTN.</summary>
public partial class SimFmtnForm
{
    private readonly ClayTextBoxOptions _prefix = new() { Label = "Префикс FMTN", Required = true, MaxLength = 11, DraftKey = "fmt.prefix" };
    private readonly ClayTextBoxOptions _shortNumber = new() { Label = "Короткий номер", MaxLength = 20, DraftKey = "fmt.short" };
    private readonly ClayTextBoxOptions _description = new() { Label = "Описание", MaxLength = 50, DraftKey = "fmt.description" };
    private SimFmtn? _original;
    private SimFmtnAccess _access = new();
    private ElementReference _fields;
    private bool _focusPending;
    private bool _deleted;
    private string? _savedKeyForNavigation;
    private bool ChangingIdentity => _savedKeyForNavigation is { } key && Id != key;

    /// <summary>Необязательный исходный ключ из ссылки.</summary>
    [SupplyParameterFromQuery(Name = "id")] public string? Id { get; set; }
    /// <summary>Legacy PageId родительского окна.</summary>
    [SupplyParameterFromQuery(Name = "parentidp")] public string? ParentPage { get; set; }
    /// <summary>Legacy ItemId родительского окна.</summary>
    [SupplyParameterFromQuery(Name = "parentId")] public string? ParentId { get; set; }
    /// <summary>Legacy ItemName родительского окна.</summary>
    [SupplyParameterFromQuery(Name = "parentName")] public string? ParentName { get; set; }

    protected override string FormKey => "sim-fmtn";
    protected override string EntityKey => Id ?? "new";
    protected override string RouteKey => EntityKey;
    protected override bool Editable => !_deleted && (Id is null ? _access.CanInsert : _original is not null && _access.CanUpdate);
    private bool CanDelete => _original?.Fields.FitsWritePolicy == true && _access.CanDelete;
    protected override bool CanRegisterDraft => Editable || CanDelete;
    protected override bool IsDomainRejection(Exception exception) => exception is SimFmtnConflict;
    protected override async Task LoadAccessAsync(Employee user) => _access = await SimFmtnData.ReadAccessAsync(Db, user, Lifetime);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var focus = _focusPending && !Loading;
        await base.OnAfterRenderAsync(firstRender);
        if (_savedKeyForNavigation is { } target && Id == target && !Loading)
        {
            _savedKeyForNavigation = null;
            if (_original?.Prefix == target && !StorageBlocked && !Dirty) await CloseAsync();
            return;
        }
        if (!focus || Draft is null || !Editable) return;
        _focusPending = false;
        await JS.InvokeVoidAsync("claySimFmtn.focusPrefix", Lifetime, _fields);
    }

    protected override async Task<SimFmtnFields?> LoadCardAsync()
    {
        _original = null;
        _deleted = false;
        if (!_access.CanRead) { Message = "Нет доступа к справочнику FMTN."; return null; }
        if (Id is { } key)
        {
            if (key.Length == 0 || key.Length > 20 || key.Any(c => !char.IsAsciiDigit(c) && c is not ('(' or ')' or '_')))
            {
                Message = "Некорректный префикс FMTN в ссылке.";
                return null;
            }
            _original = await SimFmtnData.ReadAsync(Db, key, Lifetime);
            if (_original is null) { Message = "Запись FMTN не найдена."; return null; }
        }
        _prefix.ReadOnly = _shortNumber.ReadOnly = _description.ReadOnly = !Editable;
        _focusPending = Editable;
        return _original?.Fields ?? new("", "", "");
    }

    private async Task SaveAsync()
    {
        if (Busy || Loading || StorageBlocked || ChangingIdentity || _deleted || Draft is not { } fields) return;
        Validation = fields.Validate();
        if (Validation.Count > 0) return;
        if (!Dirty) { await CloseAsync(); return; }
        if (!Editable) return;

        if (!await ExecuteWriteAsync("save", async principal =>
        {
            _original = await SimFmtnData.SaveAsync(Db, principal, _original, fields, Lifetime);
            Accept(_original.Fields);
        })) return;

        if (StorageBlocked) return;
        if (Id != _original!.Prefix)
        {
            _savedKeyForNavigation = _original.Prefix;
            try
            {
                if (ParentPage is null && ParentId is null && ParentName is null) await RefreshOpenerAsync();
            }
            catch (JSException) { Message = "Запись сохранена. Обновить родительское окно не удалось."; }
            Navigation.NavigateTo(Navigation.GetUriWithQueryParameter("id", _original.Prefix), replace: true);
            return;
        }
        await CompleteAsync();
    }

    private async Task DeleteAsync()
    {
        if (ChangingIdentity || !CanCommit || !CanDelete || _original is not { } original ||
            !await ConfirmAsync("Удаление", "Вы уверены, что хотите удалить информацию о FMTN?")) return;
        if (!await ExecuteWriteAsync("delete", async principal =>
        {
            await SimFmtnData.DeleteAsync(Db, principal, original, Lifetime);
            Accept(original.Fields);
        })) return;
        _original = null;
        _deleted = true;
        _prefix.ReadOnly = _shortNumber.ReadOnly = _description.ReadOnly = true;
        Message = "Запись FMTN удалена.";
        await CompleteAsync();
    }

    private async Task CompleteAsync()
    {
        if (ParentPage is null && ParentId is null && ParentName is null) await RefreshOpenerAsync();
        await CloseAsync();
    }
}
