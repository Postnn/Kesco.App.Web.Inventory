using System.Globalization;
using Clayzor.Lib.Entities.TerritorialEntity;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.TerritorialEntity;
using Microsoft.AspNetCore.Components;

namespace Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;

/// <summary>Карточка телефонного кода со строковым неизменяемым ключом существующей записи.</summary>
public partial class PhoneCodeEntityForm
{
    /// <summary>Черновик сохраняет даже ещё не разобранную длину области.</summary>
    public sealed record FormState(int? TerritoryId, string Code, string? Length, int? TypeId, string Comment);

    /// <summary>Строковый код, который передал query parser, включая ведущие нули.</summary>
    [SupplyParameterFromQuery(Name = "id")] public string? QueryId { get; set; }

    /// <summary>Предлагаемая территория при открытии из карточки сохранённой территории.</summary>
    [SupplyParameterFromQuery(Name = "areaid")] public string? QueryTerritory { get; set; }

    [Inject] private ClayFormWindowService WindowService { get; set; } = null!;

    [Inject] private ClaySelectAdvancedSearchService TerritorySearch { get; set; } = null!;

    private readonly ClaySelectOptions _territoryOptions = new() { Label = "Территория", Required = true, AutoSelectSingle = true, AlwaysShowAdvancedSearch = true, DraftKey = "phone-code.territory" };
    private readonly ClayTextBoxOptions _codeOptions = new() { Label = "Телефонный код", Required = true, MaxLength = 20, DraftKey = "phone-code.code" };
    private readonly ClayTextBoxOptions _lengthOptions = new() { Label = "Длина кода области", Required = true, DraftKey = "phone-code.length" };
    private readonly ClayDropDownListOptions _typeOptions = new() { Label = "Тип телефонной связи", Required = true, DraftKey = "phone-code.type" };
    private readonly ClayTextBoxOptions _commentOptions = new() { Label = "Комментарий", MaxLength = 100, DraftKey = "phone-code.comment" };
    private TerritorialEntitySelectDataSource _territorySource = null!;
    private TerritorialEntityOption[] _types = [];
    private PhoneCodeEntityRecord? _original;
    private bool _territoryConflict;

    protected override string FormKey => "phone-code-form";
    protected override string EntityKey => _original is null ? $"new:{QueryTerritory ?? ""}" : $"existing:{_original.Code}";
    protected override string RouteKey => QueryId is null ? $"new:{QueryTerritory}" : $"existing:{QueryId}";
    protected override bool Editable => _original is null ? string.IsNullOrEmpty(QueryId) && Access.PhoneInsert : Access.PhoneUpdate;
    protected override bool CanRegisterDraft => Editable || _original is not null && Access.PhoneDelete;

    protected override async Task<FormState?> LoadCardAsync()
    {
        _original = null;
        _territoryConflict = false;
        _territorySource = new(Db);
        int? defaultTerritory = null;
        if (!string.IsNullOrEmpty(QueryId))
        {
            if (QueryId.Length > 20 || string.IsNullOrWhiteSpace(QueryId))
            {
                Message = "Некорректный телефонный код.";
                return null;
            }
            _original = await TerritorialEntityData.ReadPhoneAsync(Db, QueryId, Lifetime);
            if (_original is null)
            {
                Message = $"Телефонный код {QueryId} не найден или недоступен!";
                return null;
            }
            var savedTerritory = await _territorySource.GetByIdAsync(_original.TerritoryId, Lifetime);
            _territoryConflict = savedTerritory?.IsPhoneCodeTerritory != true;
        }
        else if (QueryTerritory is not null)
        {
            if (!TerritorialEntityRoutes.TryPositiveId(QueryTerritory, out var id) ||
                (await _territorySource.GetByIdAsync(id, Lifetime))?.IsPhoneCodeTerritory != true)
                Message = "Переданная территория не найдена или не подходит для телефонного кода. Выберите другую.";
            else defaultTerritory = id;
        }
        _types = (await TerritorialEntityData.ReadPhoneTypesAsync(Db, Lifetime)).ToArray();
        UpdateAccess();
        return _original is null ? new(defaultTerritory, "", null, null, "") : ToState(_original);
    }

    private void UpdateAccess()
    {
        _territoryOptions.ReadOnly = !Editable;
        _codeOptions.ReadOnly = _original is not null || !Editable;
        _lengthOptions.ReadOnly = _typeOptions.ReadOnly = _commentOptions.ReadOnly = !Editable;
    }

    private async Task ChangeTerritoryAsync(int? value)
    {
        if (Draft is null) return;
        _territoryConflict = false;
        await UpdateDraftAsync(Draft with { TerritoryId = value });
    }

    private async Task<ClaySelectExternalResult<TerritorialEntityLookup, int?>?> FindTerritoryAsync(ClaySelectAdvancedSearchContext context)
    {
        if (!Editable || Busy || Loading || Draft is null) return null;
        var route = RouteKey;
        var source = _territorySource;
        var previous = Draft.TerritoryId;
        bool Current() => !Lifetime.IsCancellationRequested && Editable && !Busy && !Loading && route == RouteKey &&
            ReferenceEquals(source, _territorySource) && Draft?.TerritoryId == previous;
        try
        {
            var result = await TerritorySearch.OpenAsync<TerritorialEntityLookup, int?>(
                Navigation.ToAbsoluteUri(TerritorialEntityRoutes.Search).AbsoluteUri, Lifetime);
            if (result?.Value is not int id || !Current()) return null;
            var item = id > 0 ? await source.GetByIdAsync(id, Lifetime) : null;
            if (!Current()) return null;
            if (item?.IsPhoneCodeTerritory == true) return ClaySelectExternalResult<TerritorialEntityLookup, int?>.FromItem(item);
            Message = "Выбранная территория недоступна или не подходит для телефонного кода.";
            StateHasChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (!Lifetime.IsCancellationRequested) { Message = "Не удалось завершить поиск территории."; StateHasChanged(); }
        return null;
    }

    private async Task ViewSavedTerritoryAsync(int id)
    {
        var item = await _territorySource.GetByIdAsync(id, Lifetime);
        if (item is not null) await OpenTerritoryAsync(item);
        else Message = "Территория недоступна.";
    }

    private Task OpenTerritoryAsync(TerritorialEntityLookup item) => WindowService.OpenAsync(
        Navigation.ToAbsoluteUri(TerritorialEntityRoutes.TerritoryEdit(item.Id)).AbsoluteUri, "phone-code-territory", Lifetime);

    private async Task SaveAsync(bool close)
    {
        if (Busy || Draft is not { } draft) return;
        if (!Dirty && _original is not null)
        {
            if (close) await CloseAsync();
            return;
        }
        if (!Editable) return;
        _ = TerritorialEntityRules.TryParseAreaLength(draft.Length, out var length);
        var value = (_original ?? new PhoneCodeEntityRecord()) with
        {
            Code = draft.Code, TerritoryId = draft.TerritoryId ?? 0, Length = length, TypeId = draft.TypeId ?? -1, Comment = draft.Comment,
        };
        Validation = TerritorialEntityRules.Validate(value);
        if (Validation.Count != 0) return;
        var inserting = _original is null;
        PhoneCodeEntityRecord? saved = null;
        if (!await ExecuteWriteAsync(inserting ? "phone-code.insert" : "phone-code.update", async principal =>
                saved = await TerritorialEntityData.SavePhoneAsync(Db, principal, _original, value, Lifetime))) return;
        _original = saved!;
        Accept(ToState(_original));
        UpdateAccess();
        await RefreshOpenerAsync();
        if (close)
        {
            await CloseAsync();
            return;
        }
        if (inserting)
            Navigation.NavigateTo(Navigation.ToAbsoluteUri(TerritorialEntityRoutes.PhoneEdit(_original.Code)).AbsoluteUri, replace: true);
    }

    private async Task DeleteAsync()
    {
        if (_original is not { } original || !Access.PhoneDelete || !CanCommit ||
            !await ConfirmAsync("Удаление", "Вы уверены, что хотите удалить информацию о телефонном коде?")) return;
        if (!await ExecuteWriteAsync("phone-code.delete", principal => TerritorialEntityData.DeletePhoneAsync(Db, principal, original, Lifetime))) return;
        await RefreshOpenerAsync();
        await CloseAsync();
    }

    private static FormState ToState(PhoneCodeEntityRecord value) => new(value.TerritoryId, value.Code,
        value.Length.ToString(CultureInfo.InvariantCulture), value.TypeId, value.Comment);
}
