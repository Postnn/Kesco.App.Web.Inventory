using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.TerritorialEntity;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;

public partial class TerritorialEntitySearch
{
    private const string MoveOperation = "territory.move";
    private const string MoveResultScope = "move-result";

    [Inject] private ClayDraftService MoveDrafts { get; set; } = null!;

    private ClayDraftRegistration? _moveRegistration;
    private IReadOnlyList<ClayDraftRecoveryCandidate> _moveCandidates = [];
    private bool _moveJournalReady;
    private bool _moveJournalEnabled;
    private bool _legacyMoveBlocked;
    private string? _moveNotSentAttemptId;
    private PendingMove? _pendingMove;
    private KnownMoveCleanup? _knownMoveCleanup;
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
        try
        {
            var legacy = await JS.InvokeAsync<string?>("sessionStorage.getItem", _lifetime.Token, $"territory-move:{actor.Id}");
            if (!IsCurrent(generation)) return;
            _legacyMoveBlocked = legacy is not null;
            if (legacy is not null)
            {
                _pendingMove = JsonSerializer.Deserialize<PendingMove>(legacy);
                _message = "Найден прежний журнал переноса. Перенос остаётся заблокирован до сверки.";
            }

            registration = await MoveDrafts.RegisterHandleAsync("territory-tree-move", "unrestricted",
                actor.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), 1,
                $"[data-territory-move-owner='{_registrationId}']", _lifetime.Token);
            if (!IsCurrent(generation) || CurrentUser?.Id != actor.Id) return;
            _moveRegistration = registration;
            registration = null;
            RestoreMoveJournal(_moveRegistration, actor.Id, generation);
            await ScanMoveJournalAsync(_moveRegistration, generation);
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
            if (registration is not null) await registration.DisposeAsync();
        }
    }

    private void RestoreMoveJournal(ClayDraftRegistration registration, int employeeId, long generation)
    {
        var restored = registration.Restore;
        if (restored.OperationSnapshot is not { } attempt) return;
        _moveJournalReady = false;
        if (attempt.Kind != MoveOperation ||
            JsonSerializer.Deserialize<PendingMove>(attempt.Data) is not { } pending ||
            pending.From.Id <= 0 || pending.AttemptId?.ToString("N") != attempt.AttemptId)
            throw new InvalidOperationException("Исходный контекст переноса недоступен для сверки.");

        _pendingMove = pending;
        if (attempt.Status == ClayDraftOperationStatus.Confirmed &&
            restored.TypedScopes?.TryGetValue(MoveResultScope, out var json) == true &&
            JsonSerializer.Deserialize<MoveReceipt>(json) is { } receipt &&
            receipt.Revision == attempt.Revision && receipt.AttemptId == attempt.AttemptId)
            _knownMoveCleanup = new KnownMoveCleanup(generation, employeeId, attempt, receipt, Recorded: true);
    }

    private async Task ScanMoveJournalAsync(ClayDraftRegistration registration, long generation)
    {
        var candidates = await registration.ListRecoveryCandidatesAsync(_lifetime.Token);
        var health = await registration.GetHealthAsync(_lifetime.Token);
        if (!IsCurrent(generation) || !ReferenceEquals(registration, _moveRegistration)) return;
        _moveCandidates = candidates.Where(candidate => candidate.HasOperation).ToArray();
        _moveJournalReady = health.Healthy && !health.HasOperation && _pendingMove is null &&
            !_legacyMoveBlocked && _moveCandidates.Count == 0;
    }

    private async Task CompareMoveAsync()
    {
        if (_moving || _knownMoveCleanup is not null || _pendingMove is not { } attempt || CurrentUser is null) return;
        _moving = true;
        var generation = _generation;
        try
        {
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
        if (_moving || !_moveJournalReady || _pendingMove is not null || CurrentUser is not { } actor ||
            _moveRegistration is not { } registration || !_moveJournalEnabled || _total.HasValue)
            return false;

        _moving = true;
        var generation = _generation;
        var dispatched = false;
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
            dispatched = true;
            string? rejection = null;
            try
            {
                await TerritorialEntityData.ReparentAsync(Db, principal,
                    from, to, _lifetime.Token);
            }
            catch (Exception error) when (error is TerritorialEntityRuleException or UnauthorizedAccessException)
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
        finally { _moving = false; if (!_disposed) await InvokeAsync(StateHasChanged); }
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

    private async Task RetryMoveCleanupAsync()
    {
        if (_moving || _knownMoveCleanup is not { } cleanup) return;
        _moving = true;
        try
        {
            if (await CompleteMoveCleanupAsync(cleanup) && cleanup.Rejection is null)
                await RefreshViewAsync(cleanup.Generation, reloadTree: true);
        }
        finally { _moving = false; }
    }

    private async Task RetryMoveJournalAsync()
    {
        if (_moving || !_moveJournalEnabled || _pendingMove is not null || _legacyMoveBlocked || CurrentUser is not { } actor)
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
                _pendingMove = JsonSerializer.Deserialize<PendingMove>(pending.Data);
        }
        catch (Exception ex) when (IsMoveStorageError(ex))
        {
            if (IsCurrent(generation)) _message = "Журнал переноса пока недоступен. Запись заблокирована.";
        }
        finally { _moving = false; }
    }

    private async Task RecoverMoveJournalAsync(ClayDraftRecoveryCandidate candidate)
    {
        if (_moving || _legacyMoveBlocked || _pendingMove is not null || !_moveCandidates.Contains(candidate) ||
            !candidate.CanRecover || _moveRegistration is not { } registration || CurrentUser is not { } actor)
            return;
        _moving = true;
        var generation = _generation;
        try
        {
            if (!await registration.RecoverListedDraftAsync(candidate.DraftId, _lifetime.Token) || !IsCurrent(generation))
                return;
            RestoreMoveJournal(registration, actor.Id, generation);
            await ScanMoveJournalAsync(registration, generation);
        }
        catch (Exception ex) when (IsMoveStorageError(ex))
        {
            if (IsCurrent(generation)) _message = "Сохранённый журнал переноса недоступен.";
        }
        finally { _moving = false; }
    }

    private async Task ReleaseMoveJournalAsync()
    {
        var registration = _moveRegistration;
        _moveRegistration = null;
        if (registration is null) return;
        try { await registration.DisposeAsync(); }
        catch (Exception ex) when (IsMoveStorageError(ex)) { }
    }

    private static bool IsMoveStorageError(Exception ex) =>
        ex is JSException or JSDisconnectedException or InvalidOperationException or JsonException or OperationCanceledException;
}
