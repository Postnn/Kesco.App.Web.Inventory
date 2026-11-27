using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;

namespace Kesco.App.Web.Inventory.Components.Pages.Maintenance;

public partial class MaintenancePage
{
    private bool HasUnsentConfirmation => _pendingConfirmation is > 0 && !RecoveryRequired;

    private async Task CancelPendingAsync()
    {
        if (!_disposed) await ResolveUnsentConfirmationAsync(_lifetime.Token);
    }

    private async Task BeforeMaintenanceNavigationAsync(LocationChangingContext context)
    {
        if (_disposed) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _lifetime.Token);
        if (IsBusy || HasUnsentConfirmation && !await ResolveUnsentConfirmationAsync(request.Token))
        {
            context.PreventNavigation();
            return;
        }

        if (_registration is not { } registration) return;
        _closingDraft = true;
        try
        {
            await registration.CloseRamAsync(request.Token);
            if (ReferenceEquals(registration, _registration))
            {
                _registration = null;
                _draftInstance = Guid.NewGuid();
                _closingDraft = false;
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            context.PreventNavigation();
        }
        catch (Exception error) when (error is JSException or InvalidOperationException)
        {
            context.PreventNavigation();
            Report("Не удалось удалить временные данные списка. Повторите переход.");
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    private async Task<bool> ResolveUnsentConfirmationAsync(CancellationToken token)
    {
        if (_disposed || IsBusy || !HasUnsentConfirmation || _registration is not { } registration ||
            _pendingConfirmation is not { } id || token.IsCancellationRequested)
            return false;

        var employeeId = _registrationEmployee;
        var uri = Navigation.Uri;
        bool Current() => !_disposed && !token.IsCancellationRequested && HasUnsentConfirmation &&
            _pendingConfirmation == id && _registrationEmployee == employeeId && ReferenceEquals(_registration, registration) &&
            string.Equals(Navigation.Uri, uri, StringComparison.Ordinal);

        _busy = true;
        try
        {
            var scope = await registration.ReadEditorScopeAsync(ConfirmationScope, token);
            if (!Current()) return false;
            if (scope.HasOperation) return true;

            var dialog = await Dialogs.ShowExAsync<ClayEditorCancelDialog>("Отмена вопроса", new DialogParameters(),
                new DialogOptionsEx { DragMode = MudDialogDragMode.Simple, CloseButton = true, CloseOnEscapeKey = true });
            DialogResult? result;
            try { result = await dialog.Result.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                dialog.Close(DialogResult.Cancel());
                return false;
            }
            if (!Current() || result is not { Canceled: false, Data: ClayEditorCancelChoice choice } ||
                choice == ClayEditorCancelChoice.Continue)
                return false;

            var actor = await Employee.GetCurrentWithRolesAsync(Db, await PrincipalAsync(), token);
            if (!Current()) return false;
            if (actor is null || actor.Id != employeeId)
            {
                Report("Не удалось подтвердить владельца вопроса. Черновик сохранён.");
                return false;
            }

            MaintenanceList? fresh = null;
            if (choice == ClayEditorCancelChoice.ResetAndStay)
            {
                if (!await MaintenanceData.CanCompleteAsync(Db, actor, token))
                {
                    if (Current()) Report("У вас нет права подтверждать выполнение. Черновик сохранён.");
                    return false;
                }

                fresh = await ReadListAsync(token);
                var readable = fresh.Rows.Any(row => row.Id == id && row.IsApproved);
                if (!readable && _filter is not null && Current())
                    readable = (await MaintenanceData.ReadAsync(Db, token)).Rows.Any(row => row.Id == id && row.IsApproved);
                if (!Current()) return false;
                if (!readable)
                {
                    Report("Работа больше не доступна для подтверждения. Черновик сохранён.");
                    return false;
                }
            }

            if (!Current()) return false;
            if (!await registration.ReplaceEditorScopeAsync(ConfirmationScope, scope.Revision, null, token))
            {
                if (Current()) Report("Вопрос изменился. Подтвердите отмену ещё раз.");
                return false;
            }
            if (!Current()) return false;

            _pendingConfirmation = null;
            _message = null;
            if (fresh is null) return true;

            CurrentUser = actor;
            _list = fresh;
            _loadFailed = false;
            _canComplete = true;
            return false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception error) when (error is JSException or SqlException or InvalidOperationException or TimeoutException)
        {
            if (Current()) Report("Не удалось завершить отмену вопроса. Черновик сохранён.");
            return false;
        }
        finally
        {
            _busy = false;
            await ApplyPendingQueryAsync();
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }
}
