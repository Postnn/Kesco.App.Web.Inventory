using Clayzor.Lib.Web.Controls.Components.Presence;
using Kesco.App.Web.Inventory.Services;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentForm
{
    private ClayPresenceTarget? _presenceTarget;
    private int _presenceEmployeeId;

    private ClayPresenceTarget? BindChildPresence(EquipmentChildPresence? presence, EquipmentEditorOpening opening)
    {
        if (presence is null || opening.EquipmentId <= 0 || CurrentUser is not { Id: > 0 } employee)
            return null;

        var employeeId = employee.Id;
        bool Current() => opening.EditorContext is { IsCurrent: true } context &&
            ReferenceEquals(context, _activeEditorContext) && CurrentUser?.Id == employeeId;

        return new(presence.Key, async (principal, token) =>
        {
            if (!Current() || principal.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } login })
                return null;

            var access = await Service.ReadChildPresenceAccessAsync(presence, login, employeeId, token);
            token.ThrowIfCancellationRequested();
            return Current() && access?.EmployeeId == employeeId ? access : null;
        });
    }

    private ClayPresenceTarget? GetPresenceTarget()
    {
        if (_disposed || _mainDraftCancelled || !_loaded || _notFound ||
            _model is not { Id: > 0 } currentModel || currentModel.Id != Id ||
            CurrentUser is not { Id: > 0 } employee)
        {
            _presenceTarget = null;
            return null;
        }

        var entityId = currentModel.Id;
        var employeeId = employee.Id;
        var key = ClayPresenceKey.Create("inventory", "equipment", entityId);
        if (_presenceEmployeeId == employeeId && _presenceTarget?.Key == key)
            return _presenceTarget;

        _presenceEmployeeId = employeeId;
        ClayPresenceTarget? target = null;
        bool Current() => ReferenceEquals(_presenceTarget, target) && !_disposed && !_mainDraftCancelled && _loaded && !_notFound &&
            Id == entityId && _model?.Id == entityId && CurrentUser?.Id == employeeId;
        target = new(key, async (principal, token) =>
        {
            if (!Current() || principal.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } login })
                return null;

            var access = await Service.ReadPresenceAccessAsync(login, employeeId, entityId, token);
            if (!Current() || access is null) return null;
            return _canEdit ? access : access with { Activity = ClayPresenceActivity.Viewing };
        });
        return _presenceTarget = target;
    }
}
