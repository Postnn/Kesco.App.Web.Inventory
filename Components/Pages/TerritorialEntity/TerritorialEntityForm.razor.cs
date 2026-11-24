using System.Globalization;
using Clayzor.Lib.Entities.TerritorialEntity;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;

/// <summary>Карточка территории с отдельным контекстом родителя и телефонных кодов.</summary>
public partial class TerritorialEntityForm
{
    /// <summary>Черновик карточки с исходным представлением ОКСМ.</summary>
    public sealed record FormState(TerritorialEntityRecord Record, string? Classification);

    /// <summary>Строковый параметр маршрута, где 0 означает явное создание.</summary>
    [SupplyParameterFromQuery(Name = "id")] public string? QueryId { get; set; }

    /// <summary>Параметр контекста нового дочернего узла.</summary>
    [SupplyParameterFromQuery(Name = "parent")] public string? QueryParent { get; set; }

    [Inject] private ClayFormWindowService WindowService { get; set; } = null!;

    private readonly ClayEntityFormOptions _formOptions = new() { MaxWidth = "100%" };
    private readonly ClayDropDownListOptions _typeOptions = new() { Label = "Тип", Required = true, DraftKey = "territory.type" };
    private readonly ClayTextBoxOptions _classificationOptions = new() { Label = "Код ОКСМ", DraftKey = "territory.oksm" };
    private readonly ClayTextBoxOptions _nameOptions = new() { Label = "Название территории на русском языке", Required = true, MaxLength = 80, DraftKey = "territory.name" };
    private readonly ClayTextBoxOptions _alternateNameOptions = new() { Label = "Альтернативное название территории на русском языке", MaxLength = 80, DraftKey = "territory.alternate-name" };
    private readonly ClayTextBoxOptions _captionOptions = new() { Label = "Международное название территории на английском языке", MaxLength = 50, DraftKey = "territory.caption" };
    private readonly ClayTextBoxOptions _alternateCaptionOptions = new() { Label = "Альтернативное международное название территории на английском языке", MaxLength = 50, DraftKey = "territory.alternate-caption" };
    private readonly ClayTextBoxOptions _abbreviationOptions = new() { Label = "Аббревиатура", MaxLength = 4, DraftKey = "territory.abbreviation" };
    private readonly ClayTextBoxOptions _countryOptions = new() { Label = "Тел. код страны", Required = true, MaxLength = 20, DraftKey = "territory.country-code" };
    private readonly ClayTextBoxOptions _directionOptions = new() { Label = "Направление", Required = true, MaxLength = 300, DraftKey = "territory.direction" };
    private TerritorialEntityOption[] _types = [];
    private int _id;
    private int _phoneListVersion;
    private string PhoneListUrl => Navigation.ToAbsoluteUri(TerritorialEntityRoutes.Grid(Запросы.Телефонные_коды,
        _id, TerritorialEntityRoutes.PhoneListContext)).AbsoluteUri;

    protected override string FormKey => "territory-form";
    protected override string EntityKey => _id.ToString(CultureInfo.InvariantCulture);
    protected override string RouteKey => $"{QueryId}:{QueryParent}";
    protected override bool Editable => _id > 0 && Access.TerritoryUpdate;

    protected override async Task<FormState?> LoadCardAsync()
    {
        if (!TerritorialEntityRoutes.TryTerritoryId(QueryId, out _id))
        {
            Message = QueryId is null ? "Код территории не передан." : "Некорректный код территории.";
            return null;
        }
        if (_id == 0 && QueryParent is not null &&
            (!TerritorialEntityRoutes.TryPositiveId(QueryParent, out var parent) ||
             await TerritorialEntityData.FindTerritoryAsync(Db, parent, Lifetime) is null))
        {
            Message = "Родительская территория не найдена или недоступна.";
            return null;
        }
        var record = _id == 0 ? new TerritorialEntityRecord { ParentId = int.TryParse(QueryParent, out var parentId) ? parentId : null }
            : await TerritorialEntityData.ReadTerritoryAsync(Db, _id, Lifetime);
        if (record is null)
        {
            Message = "Территория не найдена или недоступна.";
            return null;
        }
        _types = (await TerritorialEntityData.ReadTerritoryTypesAsync(Db, Lifetime)).ToArray();
        foreach (var option in new ClayInputOptions[] { _typeOptions, _classificationOptions, _nameOptions, _alternateNameOptions,
            _captionOptions, _alternateCaptionOptions, _abbreviationOptions, _countryOptions, _directionOptions })
            option.ReadOnly = !Editable;
        if (_id == 0) Message = "Создание территории пока недоступно: штатная вставка не возвращает её идентификатор.";
        else if (!Editable) Message = "Карточка открыта для просмотра.";
        return new(record, record.CountryClassification?.ToString(CultureInfo.InvariantCulture));
    }

    private Task ChangeAsync(TerritorialEntityRecord value) => UpdateDraftAsync(Draft! with { Record = value });
    private Task ChangeTypeAsync(int? value) => ChangeAsync(Draft!.Record with { Type = (ТипыТерриитории)(value ?? 0) });
    private Task ChangeClassificationAsync(string? value) => UpdateDraftAsync(Draft! with { Classification = value });

    private async Task SaveAsync(bool close)
    {
        if (Busy || Draft is not { } draft || Clean is not { } original) return;
        if (!Dirty)
        {
            if (close) await CloseAsync();
            return;
        }
        if (!Editable) return;
        var parsed = TerritorialEntityRules.TryParseCountryClassification(draft.Classification, out var classification);
        var record = draft.Record with { CountryClassification = classification };
        var errors = TerritorialEntityRules.Validate(record).ToList();
        if (!parsed) errors.Add("Код ОКСМ должен быть положительным целым числом.");
        Validation = errors;
        if (errors.Count != 0) return;
        TerritorialEntityRecord? saved = null;
        if (!await ExecuteWriteAsync("territory.update", async principal =>
                saved = await TerritorialEntityData.UpdateTerritoryAsync(Db, principal, original.Record, record, Lifetime))) return;
        Accept(new(saved!, saved!.CountryClassification?.ToString(CultureInfo.InvariantCulture)));
        await RefreshOpenerAsync();
        if (close) await CloseAsync();
    }

    private async Task OpenPhoneAsync()
    {
        if (_id <= 0 || !Access.PhoneInsert) return;
        await JS.InvokeVoidAsync("clayFormWindow.registerRefresh", Lifetime, FormKey, _refreshReference ??= DotNetObjectReference.Create(this));
        if (!await WindowService.OpenAsync(Navigation.ToAbsoluteUri(TerritorialEntityRoutes.PhoneCreate(_id)).AbsoluteUri,
                "territory-phone-code", Lifetime))
            Message = "Браузер заблокировал окно телефонного кода.";
    }

    private DotNetObjectReference<TerritorialEntityForm>? _refreshReference;

    /// <summary>Перечитывает только телефонные коды после сохранения дочерней карточки.</summary>
    [JSInvokable]
    public Task RefreshFromChildAsync() => InvokeAsync(() => { _phoneListVersion++; StateHasChanged(); });

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_refreshReference is not null)
        {
            try { await JS.InvokeVoidAsync("clayFormWindow.unregisterRefresh", FormKey); }
            catch (JSDisconnectedException) { }
            _refreshReference.Dispose();
        }
        await base.DisposeAsync();
    }
}
