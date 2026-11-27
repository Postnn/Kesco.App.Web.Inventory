using Clayzor.Lib.Web.Controls.Components.Presence;
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

    private readonly ClaySelectOptions _territoryOptions = new() { Label = "Территория", Required = true, AutoSelectSingle = true, AlwaysShowAdvancedSearch = true, DraftKey = "phone-code.territory", AdvanceFocusOnSelection = true };
    private readonly ClayTextBoxOptions _codeOptions = new() { Label = "Телефонный код", Required = true, MaxLength = 20, DraftKey = "phone-code.code" };
    private readonly ClayTextBoxOptions _lengthOptions = new() { Label = "Длина кода области", Required = true, DraftKey = "phone-code.length" };
    private readonly ClayDropDownListOptions _typeOptions = new() { Label = "Тип телефонной связи", Required = true, DraftKey = "phone-code.type", AdvanceFocusOnSelection = true };
    private readonly ClayTextBoxOptions _commentOptions = new() { Label = "Комментарий", MaxLength = 100, DraftKey = "phone-code.comment" };
    private TerritorialEntitySelectDataSource _territorySource = null!;
    private TerritorialEntityOption[] _types = [];
    private PhoneCodeEntityRecord? _original;
    private bool _territoryConflict;
    private bool _deleted;
    private bool _serverJournalReady;
    private bool _serverWritePending;
    private string? _lastServerCode;

    private (int Generation, int EmployeeId, string Key, bool Editing)? _presenceContext;
    private ClayPresenceTarget? _presenceTarget;

    private ClayPresenceTarget? PhoneCodePresence()
    {
        if (Loading || _deleted || string.IsNullOrEmpty(QueryId) ||
            _original is not { Code: { Length: > 0 } code } || CurrentUser is not { Id: > 0 } actor)
            return null;
        var context = (Generation: EditorGeneration, EmployeeId: actor.Id, Key: code, Editing: Editable);
        if (_presenceContext != context)
        {
            _presenceContext = context;
            var lifetime = Lifetime;
            _presenceTarget = TerritorialCardPresence.PhoneCode(Db, context.EmployeeId, context.Key, context.Editing,
                () => !lifetime.IsCancellationRequested && !Loading && !_deleted && !string.IsNullOrEmpty(QueryId) &&
                    EditorGeneration == context.Generation && _original?.Code == context.Key &&
                    CurrentUser?.Id == context.EmployeeId && Editable == context.Editing);
        }
        return _presenceTarget;
    }

    protected override string FormKey => "phone-code-form";
    protected override string EntityKey => !string.IsNullOrEmpty(QueryId) && _original is { } original
        ? $"existing:{original.Code}" : RouteKey;
    protected override string RouteKey => string.IsNullOrEmpty(QueryId) ? $"new:{QueryTerritory}" : $"existing:{QueryId}";
    /// <inheritdoc />
    protected override string LegacyEvidenceEntityKey => string.IsNullOrEmpty(QueryId) ? "new" : EntityKey;
    /// <inheritdoc />
    protected override bool UsesRamDraft => true;
    protected override bool Editable => !_deleted && (_original is null ? string.IsNullOrEmpty(QueryId) && Access.PhoneInsert : Access.PhoneUpdate);
    private bool CanDelete => !_deleted && _original is not null && Access.PhoneDelete;
    private bool CanCommitPhone => CanCommit && _serverJournalReady && !_serverWritePending;
    protected override bool CanRegisterDraft => Editable || CanDelete;
    /// <inheritdoc />
    protected override bool RequiresVerifiedWriteReceipt => true;
    /// <inheritdoc />
    protected override bool IsDomainRejection(Exception exception) =>
        exception is InventoryOperationPendingException or InventoryOperationReservationException || base.IsDomainRejection(exception);
    /// <inheritdoc />
    protected override bool SupportsEditorCancellation => true;

    /// <inheritdoc />
    protected override bool HasEditorTransientChanges => _activeSearch is not null && Draft?.SearchEpoch is not null;

    /// <inheritdoc />
    protected override DraftRowVersion? ReadDraftRowVersion() => _original is { } row ? VersionOf(row) : null;

    private static bool AreaLengthChanged(string? baseline, string? current) =>
        TerritorialEntityRules.TryParseAreaLength(baseline, out var original) &&
        TerritorialEntityRules.TryParseAreaLength(current, out var proposed)
            ? original != proposed
            : (baseline ?? string.Empty) != (current ?? string.Empty);

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
        var code = string.IsNullOrEmpty(QueryId) ? null : QueryId;
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
    /// <inheritdoc />
    protected override async Task ImportLegacyEvidenceAsync(Employee actor, string entityKey,
        IReadOnlyList<ClayLegacyDraftOperationEvidence> evidence, CancellationToken token)
    {
        if (evidence.Count == 0) return;
        var creating = string.IsNullOrEmpty(QueryId);
        var sourceCode = creating ? null : _original?.Code;
        if (entityKey != EntityKey || !creating && sourceCode is null)
            throw new InvalidOperationException("Не удалось подтвердить открытый телефонный код.");
        var attempts = new Dictionary<Guid, (string Kind, string? Source, string Target)>();
        foreach (var item in evidence)
        {
            if (item is null || !Guid.TryParseExact(item.AttemptId, "N", out var id) || id == Guid.Empty ||
                item.Data is not { Length: <= 512 })
                throw new InvalidOperationException("Прежняя попытка не соответствует открытому телефонному коду.");
            using var json = JsonDocument.Parse(item.Data);
            var row = json.RootElement;
            if (row.ValueKind != JsonValueKind.Object || row.EnumerateObject().Count() != 2 ||
                !row.TryGetProperty("Source", out var source) || source.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
                !row.TryGetProperty("Target", out var target) || target.ValueKind != JsonValueKind.String ||
                target.GetString() is not { Length: > 0 and <= 20 } targetCode ||
                !string.Equals(source.GetString(), sourceCode, StringComparison.Ordinal))
                throw new InvalidOperationException("Прежняя попытка относится к другому телефонному коду.");

            var value = (item.Kind, source.GetString(), targetCode);
            if (attempts.TryGetValue(id, out var previous) && previous != value)
                throw new InvalidOperationException("Сохранены разные цели одной прежней попытки.");
            attempts[id] = value;
        }
        var principal = await ReadPrincipalAsync();
        token.ThrowIfCancellationRequested();
        if (entityKey != EntityKey || CurrentUser?.Id != actor.Id)
            throw new InvalidOperationException("Контекст телефонного кода изменился.");
        await TerritorialEntityData.ImportLegacyPhoneOperationsAsync(Db, principal, actor.Id, sourceCode,
            attempts.Select(pair => (pair.Key, pair.Value.Kind, pair.Value.Source, pair.Value.Target)).ToArray(), token);
        token.ThrowIfCancellationRequested();
        await ReadServerJournalAsync(sourceCode, token);
    }

    protected override async Task<CompareRead> ReadForComparisonAsync(FormState proposed, Employee actor, CancellationToken token)
    {
        var code = QueryId ?? proposed.Code;
        if (string.IsNullOrWhiteSpace(code) || code.Length > 20) return new(null);
        var access = await TerritorialEntityData.ReadAccessAsync(Db, actor, token);
        if (!(access.PhoneInsert || access.PhoneUpdate || access.PhoneDelete)) return new(null);
        var found = await TerritorialEntityData.ReadPhoneAsync(Db, code, token);
        return new(found is null ? null : ToState(found));
    }

    /// <inheritdoc />
    protected override async Task<InventoryOperationReceipt?> ReadVerifiedReceiptAsync(string kind, Guid attemptId,
        FormState? original, FormState proposed, Employee actor, CancellationToken token)
    {
        var routeCode = string.IsNullOrEmpty(QueryId) ? null : QueryId;
        var code = kind == "phone-code.insert" ? routeCode : _original?.Code ?? routeCode ?? original?.Code;
        if (kind == "phone-code.insert" ? !string.IsNullOrEmpty(code) :
            string.IsNullOrEmpty(code) || original?.Code != code)
            return null;
        return await TerritorialEntityData.ReadPhoneWriteReceiptAsync(Db, await ReadPrincipalAsync(), actor.Id,
            kind, attemptId, string.IsNullOrEmpty(code) ? null : code, proposed.Code, token);
    }

    protected override async Task<FormState?> LoadCardAsync(CancellationToken routeToken)
    {
        _original = null;
        _deleted = false;
        _territoryConflict = false;
        _serverJournalReady = _serverWritePending = false;
        _lastServerCode = null;
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
                await ReadServerJournalAsync(code, routeToken);
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
        await ReadServerJournalAsync(original?.Code, routeToken);
        return _original is null ? new(defaultTerritory, "", null, null, "") : ToState(_original);
    }

    private async Task ReadServerJournalAsync(string? code, CancellationToken ct)
    {
        if (CurrentUser is not { } actor || !(Access.PhoneInsert || Access.PhoneUpdate || Access.PhoneDelete)) return;
        var generation = EditorGeneration;
        var route = RouteKey;
        bool Current() => !ct.IsCancellationRequested && generation == EditorGeneration &&
            route == RouteKey && CurrentUser?.Id == actor.Id;
        try
        {
            var state = await TerritorialEntityData.ReadPhoneWriteStateAsync(Db, await ReadPrincipalAsync(), actor.Id, code, ct);
            if (!Current()) return;
            _serverJournalReady = true;
            _serverWritePending = state.HasPending;
            _lastServerCode = state.LastConfirmedCode;
        }
        catch (Exception error) when (error is Microsoft.Data.SqlClient.SqlException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (Current()) _serverJournalReady = false;
        }
    }

    private async Task RefreshPhoneAsync()
    {
        if (!CanRefresh) return;
        var generation = EditorGeneration;
        await ReadServerJournalAsync(_original?.Code ?? QueryId, Lifetime);
        if (generation == EditorGeneration)
            await RefreshAsync();
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

    private string PhoneCodeEditLink(string code) =>
        Navigation.ToAbsoluteUri(TerritorialEntityRoutes.PhoneEdit(code)).AbsoluteUri;

    private async Task SaveAsync(bool close)
    {
        if (Busy || Draft is not { } draft) return;
        if (!Dirty && _original is not null)
        {
            if (close) await CloseAsync();
            return;
        }
        if (!Editable || !CanCommitPhone || CurrentUser is not { } actor) return;
        _ = TerritorialEntityRules.TryParseAreaLength(draft.Length, out var length);
        var value = (_original ?? new PhoneCodeEntityRecord()) with
        {
            Code = draft.Code, TerritoryId = draft.TerritoryId ?? 0, Length = length, TypeId = draft.TypeId ?? -1, Comment = draft.Comment,
        };
        Validation = TerritorialEntityRules.Validate(value);
        if (Validation.Count != 0) return;
        var original = _original;
        var inserting = original is null;
        var serverAttemptId = Guid.NewGuid();
        PhoneCodeEntityRecord? saved = null;
        if (!await ExecuteWriteAsync(inserting ? "phone-code.insert" : "phone-code.update", async principal =>
            {
                try { saved = await TerritorialEntityData.SavePhoneAsync(Db, principal, actor.Id, serverAttemptId, original, value, Lifetime); }
                catch (Exception error) when (error is InventoryOperationPendingException or InventoryOperationReservationException)
                {
                    _serverWritePending = true;
                    throw;
                }
            }, serverAttemptId: serverAttemptId)) return;
        _lastServerCode = value.Code;
        if (saved is null)
        {
            Message = "Запись подтверждена сервером. Не удалось перечитать сохранённый код.";
            return;
        }
        _original = saved;
        AcceptCommitted(draft, ToState(saved));
        UpdateAccess();
        await AfterDraftCleanupAsync(async () =>
        {
            await RefreshOpenerAsync();
            if (Dirty) return;
            if (close) await CloseAsync();
            else if (inserting)
                NavigateAfterConfirmedWrite(PhoneCodeEditLink(saved.Code));
        });
    }

    private async Task DeleteAsync()
    {
        if (_original is not { } original || !CanDelete || !CanCommitPhone ||
            !await ConfirmAsync("Удаление", "Вы уверены, что хотите удалить информацию о телефонном коде?")) return;
        if (!CanCommitPhone || !ReferenceEquals(_original, original) || Draft is not { } submitted || CurrentUser is not { } actor)
            return;
        var serverAttemptId = Guid.NewGuid();
        if (!await ExecuteWriteAsync("phone-code.delete", async principal =>
            {
                try { await TerritorialEntityData.DeletePhoneAsync(Db, principal, actor.Id, serverAttemptId, original, Lifetime); }
                catch (Exception error) when (error is InventoryOperationPendingException or InventoryOperationReservationException)
                {
                    _serverWritePending = true;
                    throw;
                }
            }, serverAttemptId: serverAttemptId)) return;
        _deleted = true;
        _lastServerCode = original.Code;
        AcceptCommitted(submitted, ToState(original));
        UpdateAccess();
        await AfterDraftCleanupAsync(async () =>
        {
            Message = "Телефонный код удалён.";
            await RefreshOpenerAsync();
            if (!Dirty) await CloseAsync();
        });
    }

    private static FormState ToState(PhoneCodeEntityRecord value) => new(value.TerritoryId, value.Code,
        value.Length.ToString(CultureInfo.InvariantCulture), value.TypeId, value.Comment);
}
