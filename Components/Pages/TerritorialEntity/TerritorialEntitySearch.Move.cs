using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.TerritorialEntity;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;

public partial class TerritorialEntitySearch
{
    private const string MoveOperation = "territory.move";
    private const string MoveForm = "territory-tree-move";
    private const string MoveEntity = "unrestricted";
    private const string MoveResultScope = "move-result";

    [Inject] private ClayDraftService MoveDrafts { get; set; } = null!;

    private ClayDraftRegistration? _moveRegistration;
    private (long Generation, int EmployeeId, Guid Instance)? _moveRamOwner;
    private long? _moveCloseRevision;
    private bool _moveClosing;
    private long? _moveOpeningGeneration;
    private bool _moveJournalReady;
    private bool _moveJournalEnabled;
    private bool _legacyMoveBlocked;
    private string? _moveNotSentAttemptId;
    private PendingMove? _pendingMove;
    private ClayDraftOperation? _moveAttempt;
    private KnownMoveCleanup? _knownMoveCleanup;
    private TerritorialEntityData.MoveOutcome? _latestOwnMove;
    private bool _moveOutcomeReadFailed;
    private long _moveOutcomeRead;
    private string? _moveComparison;
    private bool _moving;

    private sealed record PendingMove(TerritorialEntityData.MoveNode From, TerritorialEntityData.MoveNode? To, Guid? AttemptId = null);
    private sealed record MoveReceipt(string AttemptId, long Revision, string? Rejection);
    private sealed record KnownMoveCleanup(long Generation, int EmployeeId, ClayDraftOperation Attempt, MoveReceipt Receipt, bool Recorded)
    {
        internal string? Rejection => Receipt.Rejection;
    }

    private async Task OpenMoveJournalAsync(Employee actor, long generation)
    {
        ClayDraftRegistration? registration = null;
        _moveOpeningGeneration = generation;
        try
        {
            var token = _lifetime.Token;
            bool Current() => IsCurrent(generation) && CurrentUser?.Id == actor.Id && !token.IsCancellationRequested;
            var userKey = actor.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var legacy = await JS.InvokeAsync<string?>("sessionStorage.getItem", token, $"territory-move:{userKey}");
            if (!Current()) return;
            _legacyMoveBlocked = legacy is not null;
            if (_legacyMoveBlocked)
            {
                _message = "Найдена старая попытка переноса без подтверждённого идентификатора. Перенос заблокирован до сверки.";
                return;
            }

            registration = await MoveDrafts.TransferLegacyAndRegisterRamAsync(MoveForm, MoveEntity, userKey, async (evidence, ct) =>
            {
                if (evidence.Count > 0)
                {
                    var attempts = evidence.Select(ParseMoveEvidence).ToArray();
                    var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
                    if (!Current()) throw new InvalidOperationException("Контекст переноса изменился.");
                    await TerritorialEntityData.ImportLegacyMoveOperationsAsync(Db,
                        identity?.IsAuthenticated == true ? identity.Name : null, actor.Id, attempts, ct);
                }
                if (!Current()) throw new InvalidOperationException("Контекст переноса изменился.");
                if (_moveRamOwner is not { } owner || owner.Generation != generation || owner.EmployeeId != actor.Id)
                {
                    owner = (generation, actor.Id, Guid.NewGuid());
                    _moveRamOwner = owner;
                }
                return await MoveDrafts.RegisterRamHandleAsync(MoveForm, MoveEntity, userKey, 1,
                    $"[data-territory-move-owner='{_registrationId}']", owner.Instance, ct);
            }, Current, token);

            if (!Current()) return;
            _moveRegistration = registration;
            registration = null;
            await RestoreMoveJournalAsync(_moveRegistration, actor.Id, generation);
            await ScanMoveJournalAsync(_moveRegistration, generation);
            if (_knownMoveCleanup is { } cleanup)
                await CompleteMoveCleanupAsync(cleanup);
            else
                await ReadOwnMoveOutcomeAsync(_moveRegistration, generation);
        }
        catch (Exception ex) when (IsMoveStorageError(ex))
        {
            if (IsCurrent(generation))
            {
                _moveJournalReady = false;
                _message = "Журнал переноса недоступен. Команда заблокирована.";
            }
        }
        finally
        {
            if (_moveOpeningGeneration == generation) _moveOpeningGeneration = null;
            if (registration is not null) await registration.DisposeAsync();
        }
    }

    private static (Guid AttemptId, TerritorialEntityData.MoveNode From, TerritorialEntityData.MoveNode? To)
        ParseMoveEvidence(ClayLegacyDraftOperationEvidence entry)
    {
        if (entry is null || entry.Kind != MoveOperation || entry.Data is not { Length: <= 512 } ||
            !Guid.TryParse(entry.AttemptId, out var id) || id == Guid.Empty)
            throw new InvalidOperationException("Не удалось проверить сведения о попытке переноса.");

        using var payload = JsonDocument.Parse(entry.Data);
        var root = payload.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
            !root.TryGetProperty("From", out var fromValue) || !root.TryGetProperty("To", out var toValue))
            throw new InvalidOperationException("Не удалось проверить сведения о попытке переноса.");
        var from = ReadLegacyMoveNode(fromValue);
        var to = toValue.ValueKind == JsonValueKind.Null ? null : ReadLegacyMoveNode(toValue);
        if (to?.Id == from.Id)
            throw new InvalidOperationException("Не удалось проверить сведения о попытке переноса.");
        return (id, from, to);
    }

    private static TerritorialEntityData.MoveNode ReadLegacyMoveNode(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object || node.EnumerateObject().Count() != 4 ||
            !ReadInt("Id", out var id) || id <= 0 || !ReadInt("Left", out var left) ||
            !ReadInt("Right", out var right) || left < 0 || right <= left ||
            !node.TryGetProperty("ParentId", out var parent))
            throw new InvalidOperationException("Прежняя попытка переноса требует сверки.");
        int? parentId = null;
        if (parent.ValueKind != JsonValueKind.Null)
        {
            if (parent.ValueKind != JsonValueKind.Number || !parent.TryGetInt32(out var value) || value <= 0)
                throw new InvalidOperationException("Прежняя попытка переноса требует сверки.");
            parentId = value;
        }
        return new(id, parentId, left, right);

        bool ReadInt(string name, out int value)
        {
            value = 0;
            return node.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
        }
    }


    private async Task RestoreMoveJournalAsync(ClayDraftRegistration registration, int employeeId, long generation)
    {
        var restored = registration.Restore;
        if (restored.OperationSnapshot is not { } attempt) return;
        _moveJournalReady = false;
        if (attempt.Kind != MoveOperation ||
            JsonSerializer.Deserialize<PendingMove>(attempt.Data) is not { } pending ||
            pending.From is not { Id: > 0 } || pending.AttemptId?.ToString("N") != attempt.AttemptId)
            throw new InvalidOperationException("Исходный контекст переноса недоступен для сверки.");

        _pendingMove = pending;
        _moveAttempt = attempt;
        await VerifyMoveReceiptAsync(registration, employeeId, generation, pending, attempt);
    }

    private async Task<bool> VerifyMoveReceiptAsync(ClayDraftRegistration registration, int employeeId, long generation,
        PendingMove pending, ClayDraftOperation attempt)
    {
        if (pending.AttemptId is not { } id || id == Guid.Empty || pending.From is not { Id: > 0 }) return false;
        var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
        var principal = identity?.IsAuthenticated == true ? identity.Name : null;
        var proof = await TerritorialEntityData.ReadMoveReceiptAsync(Db, principal, employeeId, id, pending.From, pending.To,
            _lifetime.Token);
        if (!IsCurrent(generation) || CurrentUser?.Id != employeeId || !ReferenceEquals(registration, _moveRegistration) ||
            !ReferenceEquals(pending, _pendingMove) || !ReferenceEquals(attempt, _moveAttempt))
            return false;
        if (proof is null || proof.State != InventoryOperationState.Rejected &&
            !(proof.State == InventoryOperationState.Confirmed && proof.ResultEntityId == pending.From.Id))
            return false;
        var rejection = proof.State == InventoryOperationState.Rejected ? "Серверный журнал подтвердил отказ в переносе." : null;

        var recorded = attempt.Status == ClayDraftOperationStatus.Confirmed &&
            registration.Restore.TypedScopes?.TryGetValue(MoveResultScope, out var json) == true &&
            JsonSerializer.Deserialize<MoveReceipt>(json) is { } local &&
            local.Revision == attempt.Revision && local.AttemptId == attempt.AttemptId;
        var receipt = new MoveReceipt(attempt.AttemptId, attempt.Revision, rejection);
        _knownMoveCleanup = new KnownMoveCleanup(generation, employeeId, attempt, receipt, recorded);
        return true;
    }


    private async Task ScanMoveJournalAsync(ClayDraftRegistration registration, long generation)
    {
        var health = await registration.GetHealthAsync(_lifetime.Token);
        var serverPending = await TerritorialEntityData.IsMovePendingAsync(Db, _lifetime.Token);
        if (!IsCurrent(generation) || !ReferenceEquals(registration, _moveRegistration)) return;
        _moveJournalReady = !serverPending && health.Healthy && !health.HasOperation && _pendingMove is null &&
            !_legacyMoveBlocked && _moveCloseRevision is null;
    }

    private async Task ReadOwnMoveOutcomeAsync(ClayDraftRegistration? registration, long generation)
    {
        if (registration is null || _pendingMove is not null || _moveClosing || _moveCloseRevision is not null ||
            CurrentUser is not { } actor)
            return;
        var request = ++_moveOutcomeRead;
        bool Current() => IsCurrent(generation) && request == _moveOutcomeRead && CurrentUser?.Id == actor.Id &&
            ReferenceEquals(registration, _moveRegistration) && _pendingMove is null && !_moveClosing;
        try
        {
            var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
            if (!Current()) return;
            var outcome = await TerritorialEntityData.ReadLatestMoveOutcomeAsync(Db,
                identity?.IsAuthenticated == true ? identity.Name : null, actor.Id, _lifetime.Token);
            if (!Current()) return;
            _latestOwnMove = outcome;
            _moveOutcomeReadFailed = false;
            if (outcome?.State is InventoryOperationState.Reserved or InventoryOperationState.Dispatching or InventoryOperationState.Unknown)
                _moveJournalReady = false;
        }
        catch (Exception error) when (IsMoveStorageError(error))
        {
            if (!Current()) return;
            _latestOwnMove = null;
            _moveOutcomeReadFailed = true;
            if (error is UnauthorizedAccessException) _moveJournalReady = false;
        }
    }

    private static string DescribeMoveOutcome(TerritorialEntityData.MoveOutcome outcome)
    {
        var target = outcome.TargetId is int id ? $"под №{id}" : "в корень";
        var move = $"перенос территории №{outcome.SourceId} {target}";
        return outcome.State switch
        {
            InventoryOperationState.Confirmed => $"Последний {move} подтверждён.",
            InventoryOperationState.Rejected => $"Последний {move} отклонён.",
            _ => $"Сервер пока не подтвердил {move}.",
        };
    }

    private async Task CompareMoveAsync()
    {
        if (_moving || _moveClosing || _moveCloseRevision is not null || _knownMoveCleanup is not null || _pendingMove is not { } attempt || CurrentUser is not { } actor) return;
        _moving = true;
        var generation = _generation;
        try
        {
            if (_moveRegistration is { } registration && _moveAttempt is { } marker &&
                await VerifyMoveReceiptAsync(registration, actor.Id, generation, attempt, marker) &&
                _knownMoveCleanup is { } cleanup)
            {
                if (await CompleteMoveCleanupAsync(cleanup) && cleanup.Rejection is null)
                    _message = "Серверный журнал подтвердил перенос.";
                return;
            }
            var actual = await TerritorialEntityData.ReadTerritoryAsync(Db, attempt.From.Id, _lifetime.Token);
            if (!IsCurrent(generation)) return;
            static string ParentLabel(int? id) => id is int value ? $"#{value}" : "корень";
            _moveComparison = actual is null ? "Территория удалена или недоступна." :
                $"Исходный родитель: {ParentLabel(attempt.From.ParentId)}. Запрошенный: {ParentLabel(attempt.To?.Id)}. Сейчас: {ParentLabel(actual.ParentId)}.";
        }
        catch (Exception) when (IsCurrent(generation)) { _moveComparison = "Не удалось перечитать территорию."; }
        finally { _moving = false; }
    }

    private async Task<bool> ReparentNodeAsync(ClayTreeNode source, ClayTreeNode? target)
    {
        if (_moving || _moveClosing || _moveCloseRevision is not null || !_moveJournalReady || _pendingMove is not null || CurrentUser is not { } actor ||
            _moveRegistration is not { } registration || !_moveJournalEnabled || _total.HasValue)
            return false;

        _moving = true;
        var generation = _generation;
        var dispatched = false;
        ++_moveOutcomeRead;
        _latestOwnMove = null;
        _moveOutcomeReadFailed = false;
        ClayDraftOperation? operation = null;
        try
        {
            await ScanMoveJournalAsync(registration, generation);
            if (!IsCurrent(generation) || !_moveJournalReady) return false;
            await registration.EnsureCommandAllowedAsync(_lifetime.Token);
            static TerritorialEntityData.MoveNode Snapshot(ClayTreeNode node) =>
                new((int)node.RawId!, node.Parent?.RawId as int?, checked((int)node.Left!.Value), checked((int)node.Right!.Value));
            var from = Snapshot(source);
            var to = target is null ? null : Snapshot(target);
            var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
            var principal = identity?.IsAuthenticated == true ? identity.Name : null;
            var currentEmployee = await Employee.GetCurrentAsync(Db, principal, _lifetime.Token);
            if (!IsCurrent(generation)) return false;
            if (currentEmployee?.Id != actor.Id)
            {
                _message = "Сотрудник для переноса не подтверждён. Запись не начата.";
                return false;
            }

            var id = Guid.NewGuid();
            var attemptId = id.ToString("N");
            var pending = new PendingMove(from, to, id);
            var data = JsonSerializer.Serialize(pending);
            _moveNotSentAttemptId = attemptId;
            operation = await registration.BeginOperationAsync(
                new ClayDraftOperationIntent(MoveOperation, data, AttemptId: attemptId), _lifetime.Token);
            if (!IsCurrent(generation)) return false;
            if (operation.AttemptId != attemptId || operation.Kind != MoveOperation || operation.Data != data ||
                operation.Status != ClayDraftOperationStatus.Running || operation.Scopes.Count != 0)
                throw new InvalidOperationException("Журнал не подтвердил перенос.");

            _moveNotSentAttemptId = null;
            _moveJournalReady = false;
            _pendingMove = pending;
            _moveAttempt = operation;
            dispatched = true;
            string? rejection = null;
            try
            {
                await TerritorialEntityData.ReparentAsync(Db, principal, actor.Id, id, from, to, _lifetime.Token);
            }
            catch (Exception error) when (error is TerritorialEntityRuleException or UnauthorizedAccessException or InventoryOperationReservationException)
            {
                rejection = error.Message;
            }
            if (IsCurrent(generation))
            {
                var receipt = new MoveReceipt(operation.AttemptId, operation.Revision, rejection);
                _knownMoveCleanup = new KnownMoveCleanup(generation, actor.Id, operation, receipt, Recorded: false);
                _message = rejection;
                await CompleteMoveCleanupAsync(_knownMoveCleanup);
                if (rejection is null) await RefreshViewAsync(generation, reloadTree: false);
            }
            return rejection is null;
        }
        catch (Exception ex) when (IsMoveStorageError(ex) || ex is Microsoft.Data.SqlClient.SqlException or IOException or TimeoutException or OverflowException)
        {
            if (IsCurrent(generation))
            {
                _moveJournalReady = false;
                _message = dispatched ? "Перенос не подтверждён. Обновите дерево и сверьте расположение узла."
                    : "Не удалось подготовить перенос территории. Запись не начата.";
                if (dispatched && operation is not null)
                {
                    try { await registration.UnknownOperationAsync(operation.Revision, _lifetime.Token); }
                    catch (Exception failure) when (IsMoveStorageError(failure)) { }
                }
            }
            return false;
        }
        finally
        {
            try
            {
                if (IsCurrent(generation) && _knownMoveCleanup is { } cleanup)
                    await CompleteMoveCleanupAsync(cleanup);
            }
            finally { _moving = false; }
            if (IsCurrent(generation))
            {
                if (_knownMoveCleanup is null && _moveNotSentAttemptId is not null)
                    await RetryMoveJournalAsync();
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private bool CanCompleteMoveCleanup(KnownMoveCleanup cleanup) =>
        IsCurrent(cleanup.Generation) && CurrentUser?.Id == cleanup.EmployeeId &&
        ReferenceEquals(_knownMoveCleanup, cleanup);

    private async Task<bool> CompleteMoveCleanupAsync(KnownMoveCleanup cleanup)
    {
        if (!CanCompleteMoveCleanup(cleanup) || _moveRegistration is not { } registration) return false;
        try
        {
            if (!cleanup.Recorded)
            {
                await registration.OpenScopeAsync(MoveResultScope, _lifetime.Token);
                await registration.SaveTypedScopeAsync(MoveResultScope, JsonSerializer.Serialize(cleanup.Receipt), _lifetime.Token);
                await registration.RecordConfirmedOperationAsync(cleanup.Attempt.Revision, _lifetime.Token);
                if (!CanCompleteMoveCleanup(cleanup)) return false;
                _knownMoveCleanup = cleanup = cleanup with { Recorded = true };
            }
            await registration.CommitOperationAsync(cleanup.Attempt.Revision, cancellationToken: _lifetime.Token);
            if (!CanCompleteMoveCleanup(cleanup)) return false;
            _knownMoveCleanup = null;
            _pendingMove = null;
            _moveAttempt = null;
            _moveComparison = null;
            _message = cleanup.Rejection;
            try
            {
                await registration.DiscardScopeAsync(MoveResultScope, _lifetime.Token);
                await registration.OpenScopeAsync(MoveResultScope, _lifetime.Token);
                await registration.OpenScopeAsync("main", _lifetime.Token);
            }
            catch (Exception ex) when (IsMoveStorageError(ex))
            {
                // The operation is already completed. Any leftover result is ignored without its marker.
            }
            await ScanMoveJournalAsync(registration, cleanup.Generation);
            return true;
        }
        catch (Exception ex) when (IsMoveStorageError(ex)) { return false; }
    }

    private async Task RetryMoveJournalAsync()
    {
        if (_moving || _moveClosing || _moveCloseRevision is not null || !_moveJournalEnabled || _pendingMove is not null || _legacyMoveBlocked || CurrentUser is not { } actor)
            return;
        _moving = true;
        var generation = _generation;
        try
        {
            if (_moveRegistration is not { } registration)
            {
                await OpenMoveJournalAsync(actor, generation);
                return;
            }
            var pending = await registration.RetryStorageAsync(_moveNotSentAttemptId, _lifetime.Token);
            if (!IsCurrent(generation)) return;
            _moveNotSentAttemptId = null;
            if (pending is null)
                await ScanMoveJournalAsync(registration, generation);
            else if (pending.Kind == MoveOperation)
            {
                _moveAttempt = pending;
                _pendingMove = JsonSerializer.Deserialize<PendingMove>(pending.Data);
            }
        }
        catch (Exception ex) when (IsMoveStorageError(ex))
        {
            if (IsCurrent(generation)) _message = "Журнал переноса пока недоступен. Запись заблокирована.";
        }
        finally { _moving = false; }
    }

    private async Task BeforeMoveNavigationAsync(LocationChangingContext context)
    {
        if (_moveRegistration is null && !_moving && !_moveClosing && _moveOpeningGeneration != _generation) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _lifetime.Token);
        if (!await CloseMoveRegistrationAsync(request.Token))
            context.PreventNavigation();
    }

    private async Task<bool> CloseMoveRegistrationAsync(CancellationToken token)
    {
        if (_moving || _moveClosing || _moveOpeningGeneration == _generation || token.IsCancellationRequested) return false;
        if (_moveRegistration is not { } registration) return true;
        var generation = _generation;
        var employeeId = CurrentUser?.Id;
        bool Current() => IsCurrent(generation) && CurrentUser?.Id == employeeId &&
            ReferenceEquals(registration, _moveRegistration);

        _moveClosing = true;
        try
        {
            var revision = _moveCloseRevision;
            if (revision is null)
            {
                var scope = await registration.ReadEditorScopeAsync("main", token);
                if (!Current()) return false;
                if (scope.HasOperation)
                {
                    var evidence = ParseMoveEvidence(await registration.ReadCloseOperationEvidenceAsync(scope.Revision, token));
                    if (evidence.AttemptId.ToString("N") != _moveNotSentAttemptId)
                    {
                        var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
                        if (!Current() || employeeId is not int employee) return false;
                        await TerritorialEntityData.ImportLegacyMoveOperationsAsync(Db,
                            identity?.IsAuthenticated == true ? identity.Name : null, employee, [evidence], token);
                    }
                }
                else if (_pendingMove is not null && _knownMoveCleanup is null)
                {
                    _message = "Результат переноса требует сверки перед закрытием.";
                    return false;
                }
                revision = scope.Revision;
            }
            if (!Current()) return false;
            token.ThrowIfCancellationRequested();
            _moveCloseRevision = revision;
            if (!await registration.CloseRamIfRevisionAsync(revision.Value, token))
            {
                if (Current())
                {
                    _moveCloseRevision = null;
                    _message = "Состояние переноса изменилось. Повторите переход.";
                }
                return false;
            }
            if (!Current()) return false;
            _moveRegistration = null;
            _moveRamOwner = null;
            _moveCloseRevision = null;
            _moveJournalReady = false;
            return true;
        }
        catch (Exception error) when (IsMoveStorageError(error))
        {
            if (Current())
                _message = "Не удалось удалить временные данные переноса. Повторите переход.";
            return false;
        }
        finally
        {
            _moveClosing = false;
            if (Current()) await InvokeAsync(StateHasChanged);
        }
    }


    private async Task ReleaseMoveJournalAsync()
    {
        var registration = _moveRegistration;
        _moveRegistration = null;
        _moveCloseRevision = null;
        _moveAttempt = null;
        if (registration is null) return;
        try { await registration.DisposeAsync(); }
        catch (Exception ex) when (IsMoveStorageError(ex)) { }
    }

    private static bool IsMoveStorageError(Exception ex) =>
        ex is JSException or JSDisconnectedException or InvalidOperationException or JsonException or OperationCanceledException or
            Microsoft.Data.SqlClient.SqlException or UnauthorizedAccessException or TimeoutException;
}
