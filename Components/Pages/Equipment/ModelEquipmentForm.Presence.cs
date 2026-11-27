using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Presence;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class ModelEquipmentForm
{
    private ClayPresenceTarget? _presenceTarget;
    private int _presenceEmployeeId;

    private ClayPresenceTarget? GetPresenceTarget()
    {
        if (_disposed || _modelDraftClosed || !_loaded || _notFound ||
            _model is not { Id: > 0 } currentModel || currentModel.Id != Id ||
            CurrentUser is not { Id: > 0 } employee)
        {
            _presenceTarget = null;
            return null;
        }

        var entityId = currentModel.Id;
        var employeeId = employee.Id;
        var key = ClayPresenceKey.Create("inventory", "equipment-model", entityId);
        if (_presenceEmployeeId == employeeId && _presenceTarget?.Key == key)
            return _presenceTarget;

        _presenceEmployeeId = employeeId;
        ClayPresenceTarget? target = null;
        bool Current() => ReferenceEquals(_presenceTarget, target) && !_disposed && !_modelDraftClosed && _loaded && !_notFound &&
            Id == entityId && _model?.Id == entityId && CurrentUser?.Id == employeeId;
        target = new(key, async (principal, token) =>
        {
            if (!Current() || principal.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } login })
                return null;

            using var db = new DbManager(Db.ConnectionString);
            var actor = await Employee.GetCurrentWithRolesAsync(db, login, token);
            if (actor?.Id != employeeId)
                return null;

            var model = await EquipmentModel.GetForEditAsync(db, entityId, token);
            if (model is null || model.Id != entityId || !Current())
                return null;

            return new(actor.Id, actor.Name, _canEdit && actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования)
                ? ClayPresenceActivity.Editing : ClayPresenceActivity.Viewing);
        });
        return _presenceTarget = target;
    }
}
