using System.Data.SqlTypes;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.TerritorialEntity;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.TerritorialEntity;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;

/// <summary>Карточка телефонного кода со строковым неизменяемым ключом существующей записи.</summary>
public partial class PhoneCodeEntityForm
{
    /// <summary>Черновик сохраняет даже ещё не разобранную длину области.</summary>
    public sealed record FormState(int? TerritoryId, string Code, string? Length, int? TypeId, string Comment)
    {
        /// <summary>Идентификатор привязки поиска к текущей редакции формы.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SearchEpoch { get; init; }
    }

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
    private bool _deleted;

    protected override string FormKey => "phone-code-form";
    protected override string EntityKey => _original is { } original ? $"existing:{original.Code}" : RouteKey;
    protected override string RouteKey => QueryId is null ? $"new:{QueryTerritory}" : $"existing:{QueryId}";
    protected override bool Editable => !_deleted && (_original is null ? string.IsNullOrEmpty(QueryId) && Access.PhoneInsert : Access.PhoneUpdate);
    private bool CanDelete => !_deleted && _original is not null && Access.PhoneDelete;
    protected override bool CanRegisterDraft => Editable || CanDelete;
    /// <inheritdoc />
    protected override bool SupportsEditorCancellation => true;

    /// <inheritdoc />
    protected override bool HasEditorTransientChanges => _activeSearch is not null && Draft?.SearchEpoch is not null;

    /// <inheritdoc />
    protected override DraftRowVersion? ReadDraftRowVersion() => _original is { } row ? VersionOf(row) : null;

    private static DraftRowVersion VersionOf(PhoneCodeEntityRecord row) =>
        new(row.Code, row.ChangedBy, row.ChangedByName, row.ChangedAt);

    /// <inheritdoc />
    protected override bool StatesEqual(FormState? left, FormState? right) =>
        base.StatesEqual(left is null ? null : ForBusinessIntent(left), right is null ? null : ForBusinessIntent(right));

    /// <inheritdoc />
    protected override FormState ForBusinessIntent(FormState state) =>
        state.SearchEpoch is null ? state : state with { SearchEpoch = null };

    /// <inheritdoc />
    protected override async Task<FreshEditorState?> ReadFreshEditorAsync(Employee actor, CancellationToken token)
    {
        var code = QueryId;
        var queryTerritory = QueryTerritory;
        var access = await TerritorialEntityData.ReadAccessAsync(Db, actor, token);
        if (code is null ? !access.PhoneInsert : !access.PhoneUpdate) return null;

        PhoneCodeEntityRecord? original = null;
        int? defaultTerritory = null;
        var territoryConflict = false;
        if (code is not null)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Length > 20 || _original?.Code != code) return null;
            original = await TerritorialEntityData.ReadPhoneAsync(Db, code, token);
            if (original is null || original.Code != code || original.ChangedAt < SqlDateTime.MinValue.Value ||
                original.ChangedAt > SqlDateTime.MaxValue.Value)
                return null;
            var territory = await _territorySource.GetByIdAsync(original.TerritoryId, token);
            territoryConflict = territory?.IsPhoneCodeTerritory != true;
        }
        else if (queryTerritory is not null)
        {
            if (!TerritorialEntityRoutes.TryPositiveId(queryTerritory, out var id)) return null;
            var territory = await _territorySource.GetByIdAsync(id, token);
            if (territory?.IsPhoneCodeTerritory != true) return null;
            defaultTerritory = territory.Id;
        }

        var types = (await TerritorialEntityData.ReadPhoneTypesAsync(Db, token)).ToArray();
        token.ThrowIfCancellationRequested();
        var state = original is null ? new FormState(defaultTerritory, "", null, null, "") : ToState(original);
        return new FreshEditorState(state, access, original is null ? null : VersionOf(original), () =>
        {
            _original = original;
            _types = types;
            _territoryConflict = territoryConflict;
            UpdateAccess();
        });
    }

    protected override IReadOnlyList<DraftMergeField> MergeFields =>
    [
        .. (QueryId is null ? new DraftMergeField[] { new("Code", "Телефонный код", "phone-code.code") } : []),
        new("TerritoryId", "Территория", "phone-code.territory"),
        new("Length", "Длина кода области", "phone-code.length"),
        new("TypeId", "Тип телефонной связи", "phone-code.type"),
        new("Comment", "Комментарий", "phone-code.comment"),
    ];
    protected override bool CanReadMissingDraft => QueryId is { Length: > 0 and <= 20 } code &&
        !string.IsNullOrWhiteSpace(code) && (Access.PhoneUpdate || Access.PhoneDelete);

    protected override async Task<CompareRead> ReadForComparisonAsync(FormState proposed, Employee actor, CancellationToken token)
    {
        var code = QueryId ?? proposed.Code;
        if (string.IsNullOrWhiteSpace(code) || code.Length > 20) return new(null);
        var found = await TerritorialEntityData.ReadPhoneAsync(Db, code, token);
        return new(found is null ? null : ToState(found));
    }

    protected override async Task<FormState?> LoadCardAsync(CancellationToken routeToken)
    {
        _original = null;
        _deleted = false;
        _territoryConflict = false;
        var source = _territorySource = new TerritorialEntitySelectDataSource(Db);
        var code = QueryId;
        var territory = QueryTerritory;
        int? defaultTerritory = null;
        PhoneCodeEntityRecord? original = null;
        var territoryConflict = false;
        if (!string.IsNullOrEmpty(code))
        {
            if (code.Length > 20 || string.IsNullOrWhiteSpace(code))
            {
                Message = "Некорректный телефонный код.";
                return null;
            }
            original = await TerritorialEntityData.ReadPhoneAsync(Db, code, routeToken);
            routeToken.ThrowIfCancellationRequested();
            if (original is null)
            {
                UpdateAccess();
                Message = $"Телефонный код {code} не найден или недоступен!";
                return null;
            }
            var savedTerritory = await source.GetByIdAsync(original.TerritoryId, routeToken);
            routeToken.ThrowIfCancellationRequested();
            territoryConflict = savedTerritory?.IsPhoneCodeTerritory != true;
        }
        else if (territory is not null)
        {
            if (TerritorialEntityRoutes.TryPositiveId(territory, out var id) &&
                (await source.GetByIdAsync(id, routeToken))?.IsPhoneCodeTerritory == true)
                defaultTerritory = id;
            routeToken.ThrowIfCancellationRequested();
            if (defaultTerritory is null)
                Message = "Переданная территория не найдена или не подходит для телефонного кода. Выберите другую.";
        }
        var types = await TerritorialEntityData.ReadPhoneTypesAsync(Db, routeToken);
        routeToken.ThrowIfCancellationRequested();
        _original = original;
        _territoryConflict = territoryConflict;
        _types = types.ToArray();
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

    private const string TerritorySearchScope = "phone-code.territory-search";
    private CancellationTokenSource? _activeSearch;
    private int _activeSearchGeneration;
    private ClayDraftRegistration? _resumedSearch;
    private string? _searchRoute;

    protected override void OnParametersSet()
    {
        if (_searchRoute != RouteKey)
        {
            _activeSearch?.Cancel();
            _searchRoute = RouteKey;
        }
        base.OnParametersSet();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        if (_activeSearch is not null && _activeSearchGeneration != EditorGeneration)
            _activeSearch.Cancel();
        if (!CanSearchTerritory || _activeSearch is not null || EditorDraftRegistration is not { } registration ||
            ReferenceEquals(_resumedSearch, registration))
            return;
        _resumedSearch = registration;
        if (registration.Restore.TypedScopes?.TryGetValue(TerritorySearchScope, out var payload) != true)
            return;
        TerritorySearchLease? lease;
        try { lease = JsonSerializer.Deserialize<TerritorySearchLease>(payload!); }
        catch (JsonException) { return; }
        if (lease is { Version: 1 } && lease.Actor == CurrentUser?.Id && lease.Route == RouteKey &&
            lease.PreviousTerritory == Draft?.TerritoryId && lease.Epoch is { } epoch && Draft?.SearchEpoch == epoch)
            await RunTerritorySearchAsync(registration, null, lease);
    }

    private bool CanSearchTerritory => Editable && CanCommit && !DraftInputSuppressed && Draft is not null;

    private async Task<ClaySelectExternalResult<TerritorialEntityLookup, int?>?> FindTerritoryAsync(ClaySelectAdvancedSearchContext context)
    {
        if (CanSearchTerritory && _activeSearch is null && EditorDraftRegistration is { } registration)
            await RunTerritorySearchAsync(registration, context, null);
        return null;
    }

    private async Task RunTerritorySearchAsync(ClayDraftRegistration registration,
        ClaySelectAdvancedSearchContext? context, TerritorySearchLease? restored)
    {
        if (CurrentUser is not { } actor) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        _activeSearch = request;
        var ct = request.Token;
        var route = RouteKey;
        var generation = _activeSearchGeneration = EditorGeneration;
        var source = _territorySource;
        var previous = Draft?.TerritoryId;
        var url = Navigation.ToAbsoluteUri(TerritorialEntityRoutes.Search).AbsoluteUri;
        var proof = restored?.Proof ?? Guid.NewGuid().ToString("N");
        var epoch = restored?.Epoch ?? Guid.NewGuid().ToString("N");
        bool EditorCurrent() => !ct.IsCancellationRequested && CanSearchTerritory && CurrentUser?.Id == actor.Id &&
            route == RouteKey && generation == EditorGeneration && ReferenceEquals(registration, EditorDraftRegistration) &&
            ReferenceEquals(source, _territorySource) && Draft?.TerritoryId == previous;
        bool Current() => EditorCurrent() && Draft?.SearchEpoch == epoch;

        async Task<bool> Ready(CancellationToken token)
        {
            var ready = false;
            await InvokeAsync(() => { ready = !token.IsCancellationRequested && Current(); });
            return ready;
        }

        async Task<ClaySelectReturnDecision> Apply(JsonElement value, CancellationToken token)
        {
            var decision = ClaySelectReturnDecision.Stale;
            await InvokeAsync(async () =>
            {
                if (!Current()) return;
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var id) || id <= 0)
                {
                    decision = ClaySelectReturnDecision.Rejected;
                    return;
                }

                TerritorialEntityLookup? item;
                try { item = await source.GetByIdAsync(id, token); }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    Message = "Не удалось проверить выбранную территорию. Повторите выбор в окне поиска.";
                    StateHasChanged();
                    decision = ClaySelectReturnDecision.Rejected;
                    return;
                }

                if (!Current()) return;
                if (item?.IsPhoneCodeTerritory != true)
                {
                    Message = "Выбранная территория недоступна или не подходит для телефонного кода.";
                    StateHasChanged();
                    decision = ClaySelectReturnDecision.Rejected;
                    return;
                }

                if (context is null)
                {
                    await ChangeTerritoryAsync(item.Id);
                    StateHasChanged();
                    decision = ClaySelectReturnDecision.Accepted;
                }
                else
                    decision = await context.ApplyAsync(
                        ClaySelectExternalResult<TerritorialEntityLookup, int?>.FromItem(item), token);

                if (decision == ClaySelectReturnDecision.Accepted &&
                    (token.IsCancellationRequested || !ReferenceEquals(registration, EditorDraftRegistration) ||
                     route != RouteKey || Draft?.TerritoryId != item.Id || StorageBlocked))
                    decision = ClaySelectReturnDecision.Indeterminate;
            });
            return decision;
        }

        async Task SaveRequest(string requestId, CancellationToken token)
        {
            if (!EditorCurrent() || Draft is not { } state) throw new OperationCanceledException(token);
            await UpdateDraftAsync(state with { SearchEpoch = epoch });
            if (!Current()) throw new OperationCanceledException(token);
            var lease = new TerritorySearchLease(1, actor.Id, route, previous, requestId, proof, epoch);
            await registration.OpenScopeAsync(TerritorySearchScope, token);
            if (await registration.SaveTypedScopeAsync(TerritorySearchScope, JsonSerializer.Serialize(lease), token) <= 0)
                throw new InvalidOperationException("Не удалось подтвердить контекст поиска.");
            if (!Current()) throw new OperationCanceledException(token);
        }

        try
        {
            var accepted = restored is null
                ? await TerritorySearch.OpenRecoverableAsync(url, false, proof, Ready, SaveRequest, Apply, ct)
                : await TerritorySearch.ResumeUndispatchedAsync(url, false, restored.RequestId, proof, Ready, Apply, ct);
            if (accepted && !ct.IsCancellationRequested && ReferenceEquals(registration, EditorDraftRegistration))
            {
                var state = await registration.ReadEditorScopeAsync(TerritorySearchScope, ct);
                await registration.ReplaceEditorScopeAsync(TerritorySearchScope, state.Revision, null, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is InvalidOperationException or ArgumentException or JSException or JSDisconnectedException)
        {
            Message = "Не удалось завершить поиск территории.";
            StateHasChanged();
        }
        finally { if (ReferenceEquals(_activeSearch, request)) _activeSearch = null; }
    }

    private sealed record TerritorySearchLease(int Version, int Actor, string Route, int? PreviousTerritory, string RequestId, string Proof, string? Epoch);

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
        await AfterDraftCleanupAsync(async () =>
        {
            await RefreshOpenerAsync();
            if (close) await CloseAsync();
            else if (inserting)
                Navigation.NavigateTo(Navigation.ToAbsoluteUri(TerritorialEntityRoutes.PhoneEdit(_original.Code)).AbsoluteUri, replace: true);
        });
    }

    private async Task DeleteAsync()
    {
        if (_original is not { } original || !CanDelete || !CanCommit ||
            !await ConfirmAsync("Удаление", "Вы уверены, что хотите удалить информацию о телефонном коде?")) return;
        if (!await ExecuteWriteAsync("phone-code.delete", principal => TerritorialEntityData.DeletePhoneAsync(Db, principal, original, Lifetime))) return;
        _deleted = true;
        Accept(ToState(original));
        UpdateAccess();
        await AfterDraftCleanupAsync(async () =>
        {
            Message = "Телефонный код удалён.";
            await RefreshOpenerAsync();
            await CloseAsync();
        });
    }

    private static FormState ToState(PhoneCodeEntityRecord value) => new(value.TerritoryId, value.Code,
        value.Length.ToString(CultureInfo.InvariantCulture), value.TypeId, value.Comment);
}
