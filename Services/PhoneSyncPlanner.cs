using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Планирует только необходимые операции синхронизации АТС.</summary>
public static class PhoneSyncPlanner
{
    /// <summary>Создаёт план по исходной и новой станции и фактическому diff оборудования.</summary>
    public static IReadOnlyList<PhoneSyncOperation> Build(PhoneSaveRequest request,
        PhoneSyncStation originalStation, PhoneSyncStation currentStation, bool deleting = false)
    {
        var operations = new List<PhoneSyncOperation>();
        var mainChanged = deleting || AtsMainChanged(request.OriginalPhone, request.Phone) ||
            GroupChanged(request.OriginalRelations.GroupMembers, request.Relations.GroupMembers) ||
            ParallelCallsChanged(request.OriginalRelations.ParallelCalls, request.Relations.ParallelCalls);
        var equipmentChanged = deleting || EquipmentChanged(request.OriginalRelations.Equipment, request.Relations.Equipment);
        if (mainChanged || equipmentChanged)
        {
            AddStationOperations(operations, originalStation, request.OriginalPhone.AtsPort,
                deleting || originalStation.Id != currentStation.Id, currentStation);
            AddStationOperations(operations, currentStation, request.Phone.AtsPort, deleting, currentStation);
        }
        var ccmRequired = equipmentChanged || mainChanged &&
            (request.OriginalRelations.Equipment.Count > 0 || request.Relations.Equipment.Count > 0);
        if (ccmRequired && (originalStation.Id == (int)Clayzor.Lib.Entities.ТелефонныеСтанции.CCM ||
                            currentStation.Id == (int)Clayzor.Lib.Entities.ТелефонныеСтанции.CCM))
        {
            var equipmentIds = request.OriginalRelations.Equipment.SelectMany(EquipmentIds)
                .Concat(request.Relations.Equipment.SelectMany(EquipmentIds)).Distinct().OrderBy(id => id);
            operations.AddRange(equipmentIds.Select(id => new PhoneSyncOperation(PhoneSyncKind.Ccm, id.ToString())));
        }
        return operations.Distinct().ToList();
    }

    private static void AddStationOperations(List<PhoneSyncOperation> operations, PhoneSyncStation station,
        string port, bool deleting, PhoneSyncStation currentStation)
    {
        if (station.Id == (int)Clayzor.Lib.Entities.ТелефонныеСтанции.CCM && station.Id != currentStation.Id) return;
        if (station.Id == (int)Clayzor.Lib.Entities.ТелефонныеСтанции.Meridian && !string.IsNullOrWhiteSpace(port))
            operations.Add(new PhoneSyncOperation(PhoneSyncKind.Meridian, port, deleting));
        else if (!string.IsNullOrWhiteSpace(station.XorcomName))
            operations.Add(new PhoneSyncOperation(PhoneSyncKind.Xorcom, station.XorcomName, deleting));
    }

    private static IEnumerable<int> EquipmentIds(PhoneEquipmentLink link)
    {
        yield return link.EquipmentId;
        if (link.ConnectionEquipmentId is > 0) yield return link.ConnectionEquipmentId.Value;
    }

    private static bool EquipmentChanged(IReadOnlyCollection<PhoneEquipmentLink> original,
        IReadOnlyCollection<PhoneEquipmentLink> current) =>
        original.Select(item => item.Key).ToHashSet().SetEquals(current.Select(item => item.Key)) == false;

    private static bool GroupChanged(IReadOnlyCollection<PhoneGroupMember> original,
        IReadOnlyCollection<PhoneGroupMember> current) =>
        !original.Select(item => item.PhoneId).ToHashSet().SetEquals(current.Select(item => item.PhoneId));

    private static bool ParallelCallsChanged(IReadOnlyCollection<PhoneParallelCall> original,
        IReadOnlyCollection<PhoneParallelCall> current) =>
        !original.Select(item => (item.CallNumber, item.NotUsed, item.DelayBeforeCall, item.DelayTooQuickResponse)).ToHashSet()
            .SetEquals(current.Select(item => (item.CallNumber, item.NotUsed, item.DelayBeforeCall, item.DelayTooQuickResponse)));

    private static bool AtsMainChanged(Phone old, Phone current) =>
        old.Id != current.Id || old.Number != current.Number || old.FullNumber != current.FullNumber ||
        old.PhoneTypeId != current.PhoneTypeId || old.StationId != current.StationId || old.AtsPort != current.AtsPort ||
        old.DrivenById != current.DrivenById || old.DisplayedFromId != current.DisplayedFromId ||
        old.EmployeeId != current.EmployeeId || old.CustomerId != current.CustomerId || old.Connected != current.Connected ||
        old.AtsDescription != current.AtsDescription || old.IsDefault != current.IsDefault ||
        old.ServicesKind != current.ServicesKind || old.ViewInDirectory != current.ViewInDirectory ||
        old.PickupGroup != current.PickupGroup || old.QuickCall != current.QuickCall || old.AutoDialNumber != current.AutoDialNumber;
}
