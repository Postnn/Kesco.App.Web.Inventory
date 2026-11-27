using System.Globalization;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Presence;

namespace Kesco.App.Web.Inventory.Components.Pages.Duty;

public partial class DutyEditor
{
    private ClayPresenceTarget? _presenceTarget;
    private int _presenceEmployeeId;

    private bool IsCurrentPresenceDate(DateOnly date) =>
        !_disposed && !_inputClosed && !_completed && _freshReadError is null &&
        _routeDate == date && RouteId is { } route && DutyKey.TryParse(route, out var routeDate) &&
        routeDate == date && _original is { } original && DateOnly.FromDateTime(original.DutyDate) == date;

    private ClayPresenceTarget? GetPresenceTarget()
    {
        if (_routeDate is not { } date || !IsCurrentPresenceDate(date) ||
            CurrentUser is not { Id: > 0 } employee)
        {
            _presenceTarget = null;
            return null;
        }

        var employeeId = employee.Id;
        var key = ClayPresenceKey.Create("inventory", "duty", date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        if (_presenceEmployeeId == employeeId && _presenceTarget?.Key == key)
            return _presenceTarget;

        _presenceEmployeeId = employeeId;
        ClayPresenceTarget? target = null;
        bool Current() => ReferenceEquals(_presenceTarget, target) &&
            CurrentUser?.Id == employeeId && IsCurrentPresenceDate(date);
        target = new(key, async (principal, token) =>
        {
            if (!Current() || principal.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } login })
                return null;

            using var db = new DbManager(Db.ConnectionString);
            var actor = await Employee.GetCurrentAsync(db, login, token);
            if (actor?.Id != employeeId)
                return null;

            var context = await DutyData.ReadContextAsync(db, token);
            var row = await DutyData.ReadAsync(db, date, token);
            if (!Current() || row is null || DateOnly.FromDateTime(row.DutyDate) != date)
                return null;

            return new(actor.Id, actor.Name, row.Closed == 0 && context.CanUpdate
                ? ClayPresenceActivity.Editing : ClayPresenceActivity.Viewing);
        });
        return _presenceTarget = target;
    }
}