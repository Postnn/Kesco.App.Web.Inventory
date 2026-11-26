using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;

namespace Kesco.App.Web.Inventory.Components.Pages.Net;

public partial class Net
{
    private const string NetInputScope = "net-input";
    private int _inputViewRevision;
    private bool RawInputBlocked => _operationContext is not { InputReady: true, InputApplying: false, InputFailed: false };

    private bool InputBlocked => RawInputBlocked || _operationContext?.InputConflict == true;

    private string? InputNotice => _operationContext is not { } context ? null
        : !context.InputReady && context.InputFailed ? "Не удалось восстановить ввод."
        : !context.InputReady ? "Восстановление ввода ещё не завершено."
        : context.InputFailed ? "Не удалось подтвердить сохранение черновика."
        : context.InputConflict ? "Исходная версия черновика отличается от текущей записи или отсутствует. Ввод сохранён для сверки." : null;
    private NetDraft RestoreNetInput(NetOperationContext context, NetDraft fresh, DateTime freshVersion)
    {
        context.InputBaseline = fresh;
        context.InputVersion = freshVersion;
        context.InputReady = !context.Registration.Restore.HasDraft;
        context.InputRestoreStarted = context.InputReady;
        context.InputConflict = false;
        context.InputFailed = false;
        context.InputReview = null;
        context.LastSavedInput = null;
        if (context.Registration.Restore.TypedScopes?.TryGetValue(NetInputScope, out var json) != true)
            return fresh;

        try
        {
            var input = JsonSerializer.Deserialize<NetEditorInput>(json);
            if (input?.Original is null || input.Current is null || input.PortId != context.PortId)
                throw new InvalidOperationException("Снимок черновика порта повреждён.");
            context.LastSavedInput = input;
            context.InputBaseline = input.Original;
            context.InputVersion = input.ChangedAt;
            context.InputConflict = input.Original != fresh || input.ChangedAt != freshVersion;
            return input.Current;
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            context.InputConflict = true;
            return fresh;
        }
    }

    private static bool NetScopeReady(ClayDraftRestoreReadiness? state) =>
        state is not null && !state.PendingScopes.Contains(NetInputScope) && !state.FailedScopes.Contains(NetInputScope);

    private async Task<bool> VerifyNetInputReadyAsync(NetOperationContext context)
    {
        if (!IsCurrentInput(context) || RawInputBlocked)
            return false;

        try
        {
            var readiness = await context.Registration.GetRestoreReadinessAsync(_pageCts.Token);
            if (!IsCurrentInput(context)) return false;
            if (NetScopeReady(readiness)) return !RawInputBlocked;
            context.InputReady = false;
            context.InputFailed = readiness is null || readiness.FailedScopes.Contains(NetInputScope);
        }
        catch (Exception error) when (IsJournalError(error))
        {
            if (!IsCurrentInput(context)) return false;
            context.InputReady = false;
            context.InputFailed = true;
        }
        StateHasChanged();
        return false;
    }

    private async Task RestoreNetRawAsync(NetOperationContext context, bool retry = false)
    {
        if (context.InputReady || context.InputApplying || _loading || !IsCurrentInput(context))
            return;
        context.InputApplying = true;
        try
        {
            var scope = await context.Registration.ReadEditorScopeAsync(NetInputScope, _pageCts.Token);
            var readiness = await context.Registration.GetRestoreReadinessAsync(_pageCts.Token);
            if (!IsCurrentInput(context))
                return;
            if (scope.HasRaw && context.LastSavedInput is null && !context.VerifiedAfterWrite)
                context.InputConflict = true;

            var hasPending = false;
            if (scope.HasRaw || !NetScopeReady(readiness))
            {
                var applied = retry
                    ? await context.Registration.ApplyPendingRawAsync(NetInputScope, _pageCts.Token)
                    : await context.Registration.ApplyRawWithResultAsync(NetInputScope, _pageCts.Token);
                hasPending = applied?.Pending is not { Count: 0 };
                readiness = await context.Registration.GetRestoreReadinessAsync(_pageCts.Token);
            }
            if (!IsCurrentInput(context))
                return;
            context.InputReady = !hasPending && NetScopeReady(readiness);
            context.InputFailed = readiness is null || readiness.FailedScopes.Contains(NetInputScope);
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            if (IsCurrentInput(context))
            {
                context.InputReady = false;
                context.InputFailed = true;
            }
        }
        finally
        {
            context.InputApplying = false;
            if (IsCurrentInput(context))
                StateHasChanged();
        }
    }

    private async Task TrackNetInputAsync()
    {
        if (!_loaded || _loading || _cancellingInput || _operationContext is not { } context || !IsCurrentInput(context))
            return;
        if (!context.InputRestoreStarted && !_saving)
        {
            context.InputRestoreStarted = true;
            await RestoreNetRawAsync(context);
            if (!context.InputReady)
                await RestoreNetRawAsync(context, retry: true);
        }
        else if (context.InputReady && !context.InputConflict)
        {
            await PersistNetInputAsync(context);
        }
    }

    private async Task<long> PersistNetInputAsync(NetOperationContext context, NetDraft? submitted = null)
    {
        var acquired = false;
        try
        {
            await context.InputGate.WaitAsync(_pageCts.Token);
            acquired = true;
            if (!IsCurrentInput(context) || _model is not { } model || context.InputBaseline is null ||
                !context.InputReady || context.InputConflict || _loading || _cancellingInput)
                return 0;
            var current = Snapshot(model);
            if (submitted is not null && submitted != current)
            {
                _validationMessage = "Ввод изменился до подтверждения команды. Проверьте данные и примените ещё раз.";
                return 0;
            }

            var input = new NetEditorInput(context.PortId, context.InputVersion, context.InputBaseline, current);
            if (input == context.LastSavedInput && (submitted is null || context.LastInputRevision > 0))
                return context.LastInputRevision;
            if (submitted is null && context.LastSavedInput is null && input.Current == input.Original)
                return 0;

            var revision = await context.Registration.SaveTypedScopeAsync(NetInputScope,
                JsonSerializer.Serialize(input), _pageCts.Token);
            if (!IsCurrentInput(context))
                return 0;
            context.LastSavedInput = input;
            context.LastInputRevision = revision;
            context.InputFailed = false;
            return revision;
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            if (IsCurrentInput(context))
            {
                context.InputFailed = true;
                StateHasChanged();
            }
            if (submitted is not null)
                throw;
            return 0;
        }
        finally { if (acquired) context.InputGate.Release(); }
    }

    private void ApplyRecoveredNetInput(NetOperationContext context)
    {
        if (!IsCurrentInput(context) || _model is not { } port || _originalSnapshot is not { } fresh)
            return;
        if (IsModified)
        {
            context.InputConflict = true;
            return;
        }

        var visible = RestoreNetInput(context, fresh, port.ChangedAtUtc);
        ApplyVisibleNetInput(port, visible);
        _originalSnapshot = context.InputBaseline;
        context.InputRestoreStarted = false;
    }

    private void ApplyVisibleNetInput(LocalNetworkPort port, NetDraft visible)
    {
        if (visible.EmployeeId != _model?.EmployeeId)
        {
            _employeeOrganizationId = null;
            _employeeOrganizationName = null;
            _employeeVersion++;
        }
        if (visible.SocketId != _model?.SocketId)
        {
            _socketNotes = null;
            _socketVersion++;
        }
        if (visible.Connected != _model?.Connected)
            _connectedLinks = [];
        if (visible.Description != _model?.Description)
            _descriptionLinks = [];
        visible.ApplyTo(port);
        SocketSource.CurrentSocketId = port.SocketId;
        _switchToId = visible.SwitchToId;
        _exchangeWithId = visible.ExchangeWithId;
        _hasReturnDate = visible.HasReturnDate;
    }

    private bool IsCurrentInput(NetOperationContext context) => !_disposed && !context.Retired &&
        ReferenceEquals(_operationContext, context) && Id == context.PortId &&
        CurrentUser?.Id == context.EmployeeId && _model?.Id == context.PortId;

    private async Task<bool> HasLateRawAsync(NetOperationContext context)
    {
        try
        {
            var scope = await context.Registration.ReadEditorScopeAsync(NetInputScope, _pageCts.Token);
            return !IsCurrentInput(context) || scope.HasRaw;
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            if (IsCurrentInput(context))
                _validationMessage = "Запись подтверждена. Проверка более позднего ввода недоступна, форма оставлена открытой.";
            return true;
        }
    }

    private sealed record NetEditorInput(int PortId, DateTime ChangedAt, NetDraft Original, NetDraft Current);
}
