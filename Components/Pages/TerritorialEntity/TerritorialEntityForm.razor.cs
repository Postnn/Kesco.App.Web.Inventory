using Clayzor.Lib.Web.Controls.Components.Presence;
using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
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

    private readonly ClayDropDownListOptions _typeOptions = new() { Label = "Тип", Required = true, DraftKey = "territory.type", Class = "territorial-field-type", AdvanceFocusOnSelection = true };
    private readonly ClayTextBoxOptions _classificationOptions = new() { Label = "Код ОКСМ", DraftKey = "territory.oksm", Class = "territorial-field-short" };
    private readonly ClayTextBoxOptions _nameOptions = new() { Label = "Название территории на русском языке", Required = true, MaxLength = 80, DraftKey = "territory.name" };
    private readonly ClayTextBoxOptions _alternateNameOptions = new() { Label = "Альтернативное название территории на русском языке", MaxLength = 80, DraftKey = "territory.alternate-name" };
    private readonly ClayTextBoxOptions _captionOptions = new() { Label = "Международное название территории на английском языке", MaxLength = 50, DraftKey = "territory.caption" };
    private readonly ClayTextBoxOptions _alternateCaptionOptions = new() { Label = "Альтернативное международное название территории на английском языке", MaxLength = 50, DraftKey = "territory.alternate-caption" };
    private readonly ClayTextBoxOptions _abbreviationOptions = new() { Label = "Аббревиатура", MaxLength = 4, DraftKey = "territory.abbreviation", Class = "territorial-field-short" };
    private readonly ClayTextBoxOptions _countryOptions = new() { Label = "Тел. код страны", Required = true, MaxLength = 20, DraftKey = "territory.country-code", Class = "territorial-field-phone" };
    private readonly ClayTextBoxOptions _directionOptions = new() { Label = "Направление", Required = true, MaxLength = 300, DraftKey = "territory.direction" };
    private TerritorialEntityOption[] _types = [];
    private int _id;
    private bool _recordAvailable;
    private bool _treeWritePending;
    private int _phoneListVersion;
    private (int Generation, int EmployeeId, int Key, bool Editing)? _presenceContext;
    private ClayPresenceTarget? _presenceTarget;

    private ClayPresenceTarget? TerritoryPresence()
    {
        if (Loading || !_recordAvailable || _id <= 0 || Clean?.Record.Id != _id || CurrentUser is not { Id: > 0 } actor)
            return null;
        var context = (Generation: EditorGeneration, EmployeeId: actor.Id, Key: _id, Editing: Editable);
        if (_presenceContext != context)
        {
            _presenceContext = context;
            var lifetime = Lifetime;
            _presenceTarget = TerritorialCardPresence.Territory(Db, context.EmployeeId, context.Key, context.Editing,
                () => !lifetime.IsCancellationRequested && !Loading && _recordAvailable && EditorGeneration == context.Generation &&
                    _id == context.Key && Clean?.Record.Id == context.Key && CurrentUser?.Id == context.EmployeeId &&
                    Editable == context.Editing);
        }
        return _presenceTarget;
    }

    private string PhoneListUrl => Navigation.ToAbsoluteUri(TerritorialEntityRoutes.Grid(Запросы.Телефонные_коды,
        _id, TerritorialEntityRoutes.PhoneListContext)).AbsoluteUri;

    protected override string FormKey => "territory-form";
    protected override string EntityKey => _id > 0 ? _id.ToString(CultureInfo.InvariantCulture)
        : $"new:{(TerritorialEntityRoutes.TryPositiveId(QueryParent, out var parent) ? parent : 0)}";
    protected override string RouteKey => $"{QueryId}:{QueryParent}";
    protected override bool Editable => _recordAvailable && (_id == 0 ? Access.TerritoryInsert : Access.TerritoryUpdate);
    private bool CanDeleteTerritory => _recordAvailable && _id > 0 && Access.TerritoryDelete;
    protected override bool CanRegisterDraft => Editable || CanDeleteTerritory;
    protected override bool SupportsEditorCancellation => true;
    /// <inheritdoc />
    protected override bool UsesRamDraft => true;

    protected override DraftRowVersion? ReadDraftRowVersion() => Clean?.Record is { Id: > 0 } current ? VersionOf(current) : null;

    protected override async Task<FreshEditorState?> ReadFreshEditorAsync(
        Clayzor.Lib.Entities.Inventory.Employee actor, CancellationToken token)
    {
        if (!TerritorialEntityRoutes.TryTerritoryId(QueryId, out var id) || id != _id) return null;
        var access = await TerritorialEntityData.ReadAccessAsync(Db, actor, token);
        var record = id == 0
            ? await ReadNewTerritoryAsync(QueryParent, access, token)
            : await TerritorialEntityData.ReadTerritoryAsync(Db, id, token);
        if (record is null) return null;
        var types = (await TerritorialEntityData.ReadTerritoryTypesAsync(Db, token)).ToArray();
        token.ThrowIfCancellationRequested();
        return new FreshEditorState(ToFormState(record), access, id > 0 ? VersionOf(record) : null,
            () => ApplyTypes(types));
    }

    private static FormState ToFormState(TerritorialEntityRecord record) =>
        new(record, record.CountryClassification?.ToString(CultureInfo.InvariantCulture));

    private static DraftRowVersion VersionOf(TerritorialEntityRecord record) =>
        new(record.Id.ToString(CultureInfo.InvariantCulture), record.ChangedBy, record.ChangedByName, record.ChangedAt);
    protected override IReadOnlyList<DraftMergeField> MergeFields =>
    [
        new("Record.Type", "Тип", "territory.type"),
        new("Classification", "Код ОКСМ", "territory.oksm"),
        new("Record.Name", "Название территории на русском языке", "territory.name"),
        new("Record.AlternateName", "Альтернативное название на русском языке", "territory.alternate-name"),
        new("Record.Caption", "Международное название на английском языке", "territory.caption"),
        new("Record.AlternateCaption", "Альтернативное международное название", "territory.alternate-caption"),
        new("Record.Abbreviation", "Аббревиатура", "territory.abbreviation"),
        new("Record.CountryCode", "Тел. код страны", "territory.country-code"),
        new("Record.Direction", "Направление", "territory.direction"),
    ];
    protected override bool CanReadMissingDraft => (Access.TerritoryUpdate || Access.TerritoryDelete) && TerritorialEntityRoutes.TryPositiveId(QueryId, out _);

    protected override async Task<CompareRead> ReadForComparisonAsync(FormState proposed, Clayzor.Lib.Entities.Inventory.Employee actor, CancellationToken token)
    {
        if (!TerritorialEntityRoutes.TryPositiveId(QueryId, out var id)) return new(null);
        var record = await TerritorialEntityData.ReadTerritoryAsync(Db, id, token);
        return new(record is null ? null : new FormState(record, record.CountryClassification?.ToString(CultureInfo.InvariantCulture)));
    }

    protected override async Task<FormState?> LoadCardAsync(CancellationToken routeToken)
    {
        _id = 0;
        _recordAvailable = false;
        _treeWritePending = false;
        var queryParent = QueryParent;
        if (!TerritorialEntityRoutes.TryTerritoryId(QueryId, out var id))
        {
            Message = QueryId is null ? "Код территории не передан." : "Некорректный код территории.";
            return null;
        }
        routeToken.ThrowIfCancellationRequested();
        _id = id;
        var record = id == 0
            ? await ReadNewTerritoryAsync(queryParent, Access, routeToken)
            : await TerritorialEntityData.ReadTerritoryAsync(Db, id, routeToken);
        routeToken.ThrowIfCancellationRequested();
        if (record is null)
        {
            Message ??= "Территория не найдена или недоступна.";
            return null;
        }
        var types = await TerritorialEntityData.ReadTerritoryTypesAsync(Db, routeToken);
        routeToken.ThrowIfCancellationRequested();
        ApplyTypes(types.ToArray());
        if (!Editable) Message = "Карточка открыта для просмотра.";
        return ToFormState(record);
    }

    private async Task<TerritorialEntityRecord?> ReadNewTerritoryAsync(
        string? queryParent, TerritorialEntityAccess access, CancellationToken token)
    {
        int? parent = null;
        if (queryParent is not null)
        {
            if (!TerritorialEntityRoutes.TryPositiveId(queryParent, out var id) ||
                await TerritorialEntityData.FindTerritoryAsync(Db, id, token) is null)
            {
                token.ThrowIfCancellationRequested();
                Message = "Родительская территория не найдена или недоступна.";
                return null;
            }
            parent = id;
        }
        if (access.TerritoryInsert)
            return new TerritorialEntityRecord { ParentId = parent };
        Message = "Создание территории сейчас запрещено.";
        return null;
    }

    private void ApplyTypes(TerritorialEntityOption[] types)
    {
        _recordAvailable = true;
        _types = types;
        foreach (var option in new ClayInputOptions[] { _typeOptions, _classificationOptions, _nameOptions, _alternateNameOptions,
            _captionOptions, _alternateCaptionOptions, _abbreviationOptions, _countryOptions, _directionOptions })
            option.ReadOnly = !Editable;
    }

    private Task ChangeAsync(int generation, Func<TerritorialEntityRecord, TerritorialEntityRecord> update) =>
        generation == EditorGeneration && Draft is { } current
            ? UpdateDraftAsync(current with { Record = update(current.Record) })
            : Task.CompletedTask;

    private Task ChangeTypeAsync(int generation, int? value) =>
        ChangeAsync(generation, record => record with { Type = (ТипыТерриитории)(value ?? 0) });

    private Task ChangeClassificationAsync(int generation, string? value) =>
        generation == EditorGeneration && Draft is { } current
            ? UpdateDraftAsync(current with { Classification = value })
            : Task.CompletedTask;

    private async Task SaveAsync(bool close)
    {
        if (Busy || Draft is not { } draft || Clean is not { } original || CurrentUser is not { } actor) return;
        if (!Dirty)
        {
            if (close) await CloseAsync();
            return;
        }
        if (!Editable || !CanCommit) return;
        var parsed = TerritorialEntityRules.TryParseCountryClassification(draft.Classification, out var classification);
        var record = draft.Record with { CountryClassification = classification };
        var errors = TerritorialEntityRules.Validate(record).ToList();
        if (!parsed) errors.Add("Код ОКСМ должен быть положительным целым числом.");
        Validation = errors;
        if (errors.Count != 0) return;
        var creating = _id == 0;
        TerritorialEntityRecord? saved = null;
        if (!await ExecuteWriteAsync(creating ? "territory.create" : "territory.update", async principal =>
            saved = creating
                ? await TerritorialEntityData.CreateTerritoryAsync(Db, principal, actor.Id, record, Lifetime)
                : await TerritorialEntityData.UpdateTerritoryAsync(Db, principal, actor.Id, original.Record, record, Lifetime),
            creating ? () => saved!.Id : null))
            return;
        AcceptCommitted(draft, ToFormState(saved!));
        await AfterDraftCleanupAsync(() => CompleteWriteAsync(creating && !close ? saved!.Id : null, close, preserveLaterInput: true));
    }

    private async Task DeleteTerritoryAsync()
    {
        if (!CanDeleteTerritory || !CanCommit || Dirty || Clean is not { } original ||
            CurrentUser is not { } actor) return;
        if (!await ConfirmAsync("Удаление территории", $"Удалить территорию «{original.Record.Name}»?") ||
            !CanDeleteTerritory || !CanCommit || Dirty || !ReferenceEquals(Clean, original))
            return;

        if (!await ExecuteWriteAsync("territory.delete", async principal =>
            await TerritorialEntityData.DeleteTerritoryAsync(Db, principal, actor.Id, original.Record, Lifetime),
            () => original.Record.Id))
            return;
        _recordAvailable = false;
        await AfterDraftCleanupAsync(() => CompleteWriteAsync(null, close: true, preserveLaterInput: true));
    }

    /// <inheritdoc />
    protected override bool IsDomainRejection(Exception exception) => exception is TerritorialEntityRuleException;

    private async Task CompleteWriteAsync(int? createdId, bool close, bool preserveLaterInput = false)
    {
        var generation = EditorGeneration;
        await RefreshOpenerAsync();
        if (generation != EditorGeneration) return;
        if (preserveLaterInput && (close || createdId is not null) && EditorDraftRegistration is { } registration)
        {
            var scope = await registration.ReadEditorScopeAsync("main", Lifetime);
            if (generation != EditorGeneration || !ReferenceEquals(EditorDraftRegistration, registration)) return;
            if (Dirty || scope.HasRaw)
            {
                Message = "Запись подтверждена. Более поздний ввод остался в открытой карточке.";
                return;
            }
        }
        if (close)
            await CloseAsync();
        else if (createdId is int id)
            NavigateAfterConfirmedWrite(TerritorialEntityRoutes.TerritoryEdit(id));
    }

    [Inject] private IConfiguration Configuration { get; set; } = null!;

    private string? ExportUrl(int id)
    {
        var link = Kesco.App.Web.Inventory.Services.InventoryMenuUrlBuilder.BuildCardLink("@URI_export_dir",
            id.ToString(CultureInfo.InvariantCulture), Configuration, Navigation.Uri);
        if (link.Href is null) return null;
        var url = new UriBuilder(link.Href);
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        query["DirOU"] = "15";
        url.Query = query.ToString();
        return url.Uri.AbsoluteUri;
    }

    private async Task OpenExportAsync()
    {
        if (Busy || Loading || Clean?.Record.Id is not int id || id <= 0 || ExportUrl(id) is not { } url) return;
        try
        {
            var actor = await Clayzor.Lib.Entities.Inventory.Employee.GetCurrentAsync(Db, await ReadPrincipalAsync(), Lifetime);
            if (actor is null || await TerritorialEntityData.ReadTerritoryAsync(Db, id, Lifetime) is null)
            {
                Message = "Территория недоступна для экспорта.";
                return;
            }
            if (Clean?.Record.Id == id && !await WindowService.OpenAsync(url, $"territory-export-{id}", Lifetime))
                Message = "Браузер заблокировал открытие экспорта.";
        }
        catch (Exception e) when (e is not OperationCanceledException) { Message = "Не удалось открыть экспорт территории."; }
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
