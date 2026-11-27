using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Entities.TerritorialEntity;
using Clayzor.Lib.Web.Controls.Components.Tree.Models;

namespace Kesco.App.Web.Inventory.Components.Pages.TerritorialEntity;

public partial class TerritorialEntitySearch
{
    private bool _moving;

    private async Task<bool> ReparentNodeAsync(ClayTreeNode source, ClayTreeNode? target)
    {
        if (_moving || CurrentUser is not { } actor || _total.HasValue)
            return false;

        _moving = true;
        var generation = _generation;
        try
        {
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

            await TerritorialEntityData.ReparentAsync(Db, principal, actor.Id, from, to, _lifetime.Token);
            if (IsCurrent(generation))
            {
                _message = null;
                await RefreshViewAsync(generation, reloadTree: false);
            }
            return true;
        }
        catch (Exception error) when (IsCurrent(generation) && error is TerritorialEntityRuleException or UnauthorizedAccessException)
        {
            _message = error.Message;
            return false;
        }
        catch (Exception error) when (IsCurrent(generation) &&
            error is Microsoft.Data.SqlClient.SqlException or IOException or TimeoutException or OverflowException)
        {
            _message = "Не удалось перенести территорию. Обновите дерево и повторите операцию.";
            return false;
        }
        finally
        {
            _moving = false;
            if (IsCurrent(generation)) await InvokeAsync(StateHasChanged);
        }
    }
}
