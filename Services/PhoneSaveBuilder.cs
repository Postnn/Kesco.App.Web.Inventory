using System.Data;
using System.Data.SqlTypes;
using System.Text;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Строит атомарный параметризованный diff основной записи телефона и её связей.</summary>
public static class PhoneSaveBuilder
{
    internal const int VersionConflictStatus = -1;
    /// <summary>Создаёт транзакционный batch; при INSERT использует @@IDENTITY для всех связей.</summary>
    public static PhoneSaveCommand Build(PhoneSaveRequest request) => Build(request, enlisted: false);

    internal static PhoneSaveCommand BuildEnlisted(PhoneSaveRequest request) => Build(request, enlisted: true);

    private static PhoneSaveCommand Build(PhoneSaveRequest request, bool enlisted)
    {
        RequireOriginalVersion(request);
        var phone = request.Phone;
        var parameters = MainParameters(phone);
        var sql = new StringBuilder();
        if (!enlisted)
        {
            sql.AppendLine("SET XACT_ABORT ON;");
            sql.AppendLine("BEGIN TRY");
            sql.AppendLine("    BEGIN TRANSACTION;");
        }
        sql.AppendLine("    DECLARE @PhoneId int = @Id;");
        if (phone.Id <= 0)
        {
            sql.AppendLine(SQLQueries.INSERT_ТелефонДляФормы);
            AppendGroupDiff(sql, parameters, request.OriginalRelations.GroupMembers, request.Relations.GroupMembers);
        }
        else
        {
            parameters.Add("ExpectedChangedAt", request.OriginalPhone.ChangedAtUtc, DbType.DateTime);
            parameters.Add("VersionConflictStatus", VersionConflictStatus, DbType.Int32);
            sql.AppendLine(enlisted ? SQLQueries.CHECK_ТелефонИсходнаяВерсияДляСохраненияEnlisted :
                SQLQueries.CHECK_ТелефонИсходнаяВерсияДляСохранения);
            AppendGroupDiff(sql, parameters, request.OriginalRelations.GroupMembers, request.Relations.GroupMembers);
            if (!MainEquals(request.OriginalPhone, phone))
            {
                sql.AppendLine(SQLQueries.UPDATE_ТелефонДляФормы).AppendLine(";");
                sql.AppendLine(enlisted ? SQLQueries.CHECK_ТелефонОбновленаОднаСтрокаEnlisted :
                    SQLQueries.CHECK_ТелефонОбновленаОднаСтрока);
            }
        }

        AppendParallelDiff(sql, parameters, request.OriginalRelations.ParallelCalls, request.Relations.ParallelCalls);
        AppendEquipmentDiff(sql, parameters, request.OriginalRelations.Equipment, request.Relations.Equipment);
        if (!enlisted)
            sql.AppendLine("    COMMIT TRANSACTION;");
        sql.AppendLine("    SELECT @PhoneId;");
        if (!enlisted)
        {
            sql.AppendLine("END TRY");
            sql.AppendLine("BEGIN CATCH");
            sql.AppendLine("    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
            sql.AppendLine("    DECLARE @ErrorMessage nvarchar(4000) = ERROR_MESSAGE(), @ErrorSeverity int = ERROR_SEVERITY(), @ErrorState int = ERROR_STATE();");
            sql.AppendLine("    RAISERROR(@ErrorMessage, @ErrorSeverity, @ErrorState);");
            sql.AppendLine("END CATCH;");
        }
        return new PhoneSaveCommand(sql.ToString(), parameters);
    }

    internal static void RequireOriginalVersion(PhoneSaveRequest request)
    {
        var original = request.OriginalPhone;
        if (request.Phone.Id > 0 &&
            (original.Id != request.Phone.Id || original.ChangedAtUtc < SqlDateTime.MinValue.Value ||
             original.ChangedAtUtc > SqlDateTime.MaxValue.Value))
            throw new InvalidOperationException("Исходная версия номера недоступна. Перечитайте номер перед сохранением.");
    }

    /// <summary>Сравнивает только 20 сохраняемых полей, исключая технические timestamps.</summary>
    public static bool MainEquals(Phone left, Phone right) =>
        left.Number == right.Number && left.FullNumber == right.FullNumber && left.PhoneTypeId == right.PhoneTypeId &&
        left.StationId == right.StationId && left.AtsPort == right.AtsPort && left.DrivenById == right.DrivenById &&
        left.DisplayedFromId == right.DisplayedFromId && left.SocketId == right.SocketId && left.EmployeeId == right.EmployeeId &&
        left.CustomerId == right.CustomerId && left.Connected == right.Connected && left.AtsDescription == right.AtsDescription &&
        left.IsDefault == right.IsDefault && left.ReturnDate == right.ReturnDate && left.ServicesKind == right.ServicesKind &&
        left.ViewInDirectory == right.ViewInDirectory && left.PickupGroup == right.PickupGroup &&
        left.QuickCall == right.QuickCall && left.AutoDialNumber == right.AutoDialNumber && left.Notes == right.Notes;

    private static DynamicParameters MainParameters(Phone phone)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@Id", phone.Id);
        parameters.Add("@ТелефонныйНомер", phone.Number);
        parameters.Add("@ПолныйНомер", string.IsNullOrEmpty(phone.FullNumber) ? phone.Number : phone.FullNumber);
        parameters.Add("@КодТипаТелефонныхНомеров", phone.PhoneTypeId);
        parameters.Add("@КодТелефоннойСтанции", phone.StationId);
        parameters.Add("@ПортАТС", phone.AtsPort ?? string.Empty);
        parameters.Add("@ВедомыйОт", phone.DrivenById);
        parameters.Add("@ОтображаемыйОт", phone.DisplayedFromId);
        parameters.Add("@КодРозетки", phone.SocketId);
        parameters.Add("@КодСотрудника", phone.EmployeeId);
        parameters.Add("@КодЛицаЗаказчика", phone.CustomerId);
        parameters.Add("@Подключено", phone.Connected ?? string.Empty);
        parameters.Add("@ОписаниеАТС", phone.AtsDescription ?? string.Empty);
        parameters.Add("@ОсновнойНомер", phone.IsDefault ? 1 : 0);
        parameters.Add("@СрокВозврата", phone.ReturnDate);
        parameters.Add("@УслугиСвязи", phone.ServicesKind);
        parameters.Add("@ВСправочнике", phone.ViewInDirectory);
        parameters.Add("@Группа6", phone.PickupGroup ?? string.Empty);
        parameters.Add("@БыстрыйНабор", phone.QuickCall ?? string.Empty);
        parameters.Add("@НомерАвтонабора", phone.AutoDialNumber ?? string.Empty);
        parameters.Add("@Примечания", phone.Notes ?? string.Empty);
        return parameters;
    }

    private static void AppendGroupDiff(StringBuilder sql, DynamicParameters parameters,
        IReadOnlyCollection<PhoneGroupMember> original, IReadOnlyCollection<PhoneGroupMember> current)
    {
        if (current.GroupBy(item => item.PhoneId).Any(group => group.Count() > 1))
            throw new InvalidOperationException("В PBX-группе есть повторяющиеся номера.");
        var before = original.Select(item => item.PhoneId).ToHashSet();
        var after = current.Select(item => item.PhoneId).ToHashSet();
        var index = 0;
        foreach (var id in before.Where(id => !after.Contains(id)))
        {
            Add(sql, parameters, SQLQueries.DELETE_ТелефонЧленГруппы, "delGroup", index++, "@MemberPhoneId", id);
        }
        foreach (var id in after.Where(id => !before.Contains(id)))
        {
            Add(sql, parameters, SQLQueries.INSERT_ТелефонЧленГруппы, "newGroup", index++, "@MemberPhoneId", id);
        }
    }

    private static void AppendEquipmentDiff(StringBuilder sql, DynamicParameters parameters,
        IReadOnlyCollection<PhoneEquipmentLink> original, IReadOnlyCollection<PhoneEquipmentLink> current)
    {
        if (current.GroupBy(item => item.EquipmentId).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Оборудование добавлено в список повторно: это запрещено первичным ключом таблицы.");
        var before = original.ToDictionary(item => item.EquipmentId);
        var after = current.ToDictionary(item => item.EquipmentId);
        var index = 0;
        foreach (var item in before.Values.Where(item => !after.TryGetValue(item.EquipmentId, out var currentItem) ||
                     currentItem.Key != item.Key))
        {
            var prefix = $"delEquipment{index++}";
            parameters.Add($"@{prefix}EquipmentId", item.Key.EquipmentId);
            parameters.Add($"@{prefix}ConnectionEquipmentId", item.Key.ConnectionEquipmentId);
            sql.AppendLine(SQLQueries.DELETE_ТелефонОборудование
                .Replace("@EquipmentId", $"@{prefix}EquipmentId")
                .Replace("@ConnectionEquipmentId", $"@{prefix}ConnectionEquipmentId"));
        }
        foreach (var item in after.Values.Where(item => !before.TryGetValue(item.EquipmentId, out var originalItem) ||
                     originalItem.Key != item.Key))
        {
            var prefix = $"newEquipment{index++}";
            parameters.Add($"@{prefix}EquipmentId", item.Key.EquipmentId);
            parameters.Add($"@{prefix}ConnectionEquipmentId", item.Key.ConnectionEquipmentId);
            parameters.Add($"@{prefix}ReturnDate", item.ReturnDate);
            parameters.Add($"@{prefix}Notes", item.Notes ?? string.Empty);
            sql.AppendLine(SQLQueries.INSERT_ТелефонОборудование
                .Replace("@EquipmentId", $"@{prefix}EquipmentId")
                .Replace("@ConnectionEquipmentId", $"@{prefix}ConnectionEquipmentId")
                .Replace("@ReturnDate", $"@{prefix}ReturnDate")
                .Replace("@Notes", $"@{prefix}Notes"));
        }
        foreach (var item in after.Values.Where(item => before.TryGetValue(item.EquipmentId, out var old) &&
                     old.Key == item.Key && (old.ReturnDate != item.ReturnDate || old.Notes != item.Notes)))
        {
            var prefix = $"updEquipment{index++}";
            parameters.Add($"@{prefix}EquipmentId", item.Key.EquipmentId);
            parameters.Add($"@{prefix}ConnectionEquipmentId", item.Key.ConnectionEquipmentId);
            parameters.Add($"@{prefix}ReturnDate", item.ReturnDate);
            parameters.Add($"@{prefix}Notes", item.Notes ?? string.Empty);
            sql.AppendLine(SQLQueries.UPDATE_ТелефонОборудование
                .Replace("@EquipmentId", $"@{prefix}EquipmentId")
                .Replace("@ConnectionEquipmentId", $"@{prefix}ConnectionEquipmentId")
                .Replace("@ReturnDate", $"@{prefix}ReturnDate")
                .Replace("@Notes", $"@{prefix}Notes"));
        }
    }

    private static void AppendParallelDiff(StringBuilder sql, DynamicParameters parameters,
        IReadOnlyCollection<PhoneParallelCall> original, IReadOnlyCollection<PhoneParallelCall> current)
    {
        if (current.GroupBy(item => item.CallNumber, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidOperationException("В списке есть повторяющиеся параллельные вызовы.");
        var before = original.ToDictionary(item => item.CallNumber, StringComparer.Ordinal);
        var after = current.ToDictionary(item => item.CallNumber, StringComparer.Ordinal);
        var index = 0;
        foreach (var number in before.Keys.Where(number => !after.ContainsKey(number)))
            Add(sql, parameters, SQLQueries.DELETE_ТелефонПараллельныйВызов, "delParallel", index++, "@CallNumber", number);
        foreach (var item in after.Values.Where(item => !before.ContainsKey(item.CallNumber)))
            AddParallel(sql, parameters, SQLQueries.INSERT_ТелефонПараллельныйВызов, "newParallel", index++, item);
        foreach (var item in after.Values.Where(item => before.TryGetValue(item.CallNumber, out var old) &&
                     (old.NotUsed != item.NotUsed || old.DelayBeforeCall != item.DelayBeforeCall ||
                      old.DelayTooQuickResponse != item.DelayTooQuickResponse)))
            AddParallel(sql, parameters, SQLQueries.UPDATE_ТелефонПараллельныйВызов, "updParallel", index++, item);
    }

    private static void AddParallel(StringBuilder sql, DynamicParameters parameters, string statement,
        string prefix, int index, PhoneParallelCall item)
    {
        prefix += index;
        parameters.Add($"@{prefix}CallNumber", item.CallNumber);
        parameters.Add($"@{prefix}NotUsed", item.NotUsed);
        parameters.Add($"@{prefix}DelayBeforeCall", item.DelayBeforeCall);
        parameters.Add($"@{prefix}DelayTooQuickResponse", item.DelayTooQuickResponse);
        sql.AppendLine(statement.Replace("@CallNumber", $"@{prefix}CallNumber")
            .Replace("@NotUsed", $"@{prefix}NotUsed")
            .Replace("@DelayBeforeCall", $"@{prefix}DelayBeforeCall")
            .Replace("@DelayTooQuickResponse", $"@{prefix}DelayTooQuickResponse"));
    }

    private static void Add(StringBuilder sql, DynamicParameters parameters, string statement,
        string prefix, int index, string parameterName, object value)
    {
        var name = $"@{prefix}{index}{parameterName.TrimStart('@')}";
        parameters.Add(name, value);
        sql.AppendLine(statement.Replace(parameterName, name));
    }
}
