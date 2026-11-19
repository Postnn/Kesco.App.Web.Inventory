using System.Data;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Чтение, серверная валидация, сохранение, удаление и синхронизация телефонных номеров.</summary>
public class PhoneFormService
{
    private readonly DbManager _db;

    /// <summary>Создаёт сервис с подключением текущего Blazor circuit.</summary>
    public PhoneFormService(DbManager db) => _db = db;

    /// <summary>Загружает основную запись и связанные наборы одним batch.</summary>
    public virtual async Task<PhoneFormData?> GetAsync(int id, bool copy = false)
    {
        var canEdit = await HasRoleAsync((int)Роли.Специалист_по_инвентаризации_оборудования);
        var result = await _db.RunAsync(async connection =>
        {
            var sql = string.Join("\n", SQLQueries.SELECT_ТелефонДляФормы,
                SQLQueries.SELECT_ТелефонЧленыГруппы, SQLQueries.SELECT_ТелефонВходитВГруппы,
                SQLQueries.SELECT_ТелефонПараллельныеВызовы, SQLQueries.SELECT_ТелефонОборудование,
                SQLQueries.SELECT_ТелефонДругиеНомераОборудования);
            using var reader = await connection.QueryMultipleAsync(sql, new { Id = id });
            var phone = await reader.ReadFirstOrDefaultAsync<Phone>();
            if (phone is null) return null;
            var members = (await reader.ReadAsync<PhoneGroupMember>()).ToList();
            var memberships = (await reader.ReadAsync<PhoneGroupMembership>()).ToList();
            var calls = (await reader.ReadAsync<PhoneParallelCall>()).ToList();
            var equipment = (await reader.ReadAsync<PhoneEquipmentLink>()).ToList();
            var otherPhones = (await reader.ReadAsync<(int EquipmentId, string ДругиеНомера)>())
                .ToDictionary(item => item.EquipmentId, item => item.ДругиеНомера);
            foreach (var item in equipment)
                item.OtherPhones = otherPhones.GetValueOrDefault(item.EquipmentId, string.Empty);
            if (copy)
            {
                phone = phone.CopyForNew();
                members.Clear();
                equipment.Clear();
                memberships.Clear();
            }
            return new PhoneFormData
            {
                Phone = phone,
                Relations = new PhoneRelationsSnapshot { GroupMembers = members, Equipment = equipment, ParallelCalls = calls },
                Memberships = memberships,
                CanEdit = canEdit,
            };
        });
        return result;
    }

    /// <summary>Проверяет роль текущего пользователя с учётом замещений.</summary>
    public virtual async Task<bool> HasRoleAsync(int roleId) =>
        await _db.ExecuteScalarAsync<int>(SQLQueries.SELECT_ТекущийПользовательИмеетРоль,
            new { RoleId = roleId }, commandType: CommandType.Text) == 1;

    /// <summary>Вычисляет порты и автонабор без запросов при рендере.</summary>
    public virtual async Task<PhoneCapabilities> GetCapabilitiesAsync(int? stationId, int? phoneTypeId, int phoneId)
    {
        if (stationId is null || phoneTypeId is null) return new PhoneCapabilities();
        return await _db.RunAsync(async connection =>
        {
            var parameters = new { StationId = stationId.Value, PhoneTypeId = phoneTypeId.Value, PhoneId = phoneId };
            var sql = string.Join("\n", SQLQueries.SELECT_ТелефонСуществуютПорты,
                SQLQueries.SELECT_ТелефонАвтонаборДоступен, SQLQueries.SELECT_ТелефонСвободныеПорты);
            using var reader = await connection.QueryMultipleAsync(sql, parameters);
            var hasPorts = await reader.ReadFirstOrDefaultAsync<int>() != 0;
            var autoDial = await reader.ReadFirstOrDefaultAsync<int>() != 0;
            var ports = (await reader.ReadAsync<string>()).ToList();
            return new PhoneCapabilities { HasPorts = hasPorts, AutoDialAvailable = autoDial, AvailablePorts = ports };
        });
    }

    /// <summary>Возвращает организацию сотрудника для бизнес-подстановки заказчика.</summary>
    public virtual async Task<int?> GetEmployeeOrganizationAsync(int employeeId) =>
        await _db.ExecuteScalarAsync<int?>(SQLQueries.SELECT_ТелефонОрганизацияСотрудника,
            new { EmployeeId = employeeId }, commandType: CommandType.Text);

    /// <summary>Строит описание АТС английским именем либо подтверждённой fn_TransLit.</summary>
    public virtual async Task<string> GetAtsDescriptionAsync(int employeeId, bool upperCase)
    {
        var value = await _db.ExecuteScalarAsync<string>(SQLQueries.SELECT_ТелефонОписаниеАТССотрудника,
            new { EmployeeId = employeeId }, commandType: CommandType.Text) ?? string.Empty;
        return upperCase ? value.ToUpperInvariant() : value;
    }

    /// <summary>Возвращает текущий основной номер сотрудника для информационной ссылки.</summary>
    public virtual async Task<(int Id, string Number)?> GetDefaultEmployeePhoneAsync(int employeeId)
    {
        var rows = await _db.QueryAsync<PhoneDirectoryItem>(SQLQueries.SELECT_ТелефонОсновнойНомерСотрудника,
            new { EmployeeId = employeeId });
        var item = rows.FirstOrDefault();
        return item is null ? null : (item.Id, item.Number);
    }

    /// <summary>Повторно выполняет локальную и серверную валидацию, затем атомарно сохраняет diff.</summary>
    public virtual async Task<PhoneSaveResult> SaveAsync(PhoneSaveRequest request)
    {
        await EnsureWriteAccessAsync();
        var issues = PhoneFormValidator.Validate(request).ToList();
        issues.AddRange(await ValidateServerAsync(request));
        if (issues.Any(item => item.Severity == PhoneValidationSeverity.Blocking))
            return new PhoneSaveResult { PhoneId = request.Phone.Id, Issues = issues };
        var command = PhoneSaveBuilder.Build(request);
        var id = await _db.ExecuteWriteScalarAsync<int>(command.Sql, command.Parameters);
        if (id <= 0) throw new InvalidOperationException("База данных не вернула идентификатор телефонного номера.");
        return new PhoneSaveResult { PhoneId = id, Saved = true, Issues = issues };
    }

    /// <summary>Выполняет параметризованные проверки номера, типа, порта, зависимостей и legacy-предупреждений.</summary>
    public virtual async Task<IReadOnlyList<PhoneValidationIssue>> ValidateServerAsync(PhoneSaveRequest request)
    {
        var phone = request.Phone;
        var issues = new List<PhoneValidationIssue>();
        if (phone.PhoneTypeId is null || phone.StationId is null || string.IsNullOrEmpty(phone.Number)) return issues;
        phone.FullNumber = await _db.ExecuteWriteScalarAsync<string>(SQLQueries.SELECT_ТелефонАвторитетныйПолныйНомер,
            new { phone.Number, phone.StationId, phone.DrivenById, phone.DisplayedFromId })
            ?? throw new InvalidOperationException("База данных не вернула полный номер. Сохранение отменено.");
        var statements = new List<string>
        {
            SQLQueries.SELECT_ТелефонТипДоступенНаСтанции,
            SQLQueries.SELECT_ТелефонДубликатНаСтанции,
            SQLQueries.SELECT_ТелефонСуществуютПорты,
            SQLQueries.SELECT_ТелефонПортДоступен,
        };
        if (phone.DrivenById is null && phone.DisplayedFromId is null)
            statements.Add(SQLQueries.SELECT_ТелефонДубликатПолногоНомера);
        statements.Add(SQLQueries.SELECT_ТелефонПересеченияПолногоНомера);
        if (phone.DrivenById is not null || phone.DisplayedFromId is not null)
            statements.Add(SQLQueries.SELECT_ТелефонНедопустимыеЗависимости);
        if (phone.PhoneTypeId == (int)ТипыТелефонныхНомеров.ГруппаИлиФиктивный && phone.EmployeeId is not null)
            statements.Add(SQLQueries.SELECT_ТелефонСотрудникГрупповойРаботы);
        if (phone.PhoneTypeId == (int)ТипыТелефонныхНомеров.Короткий_номер_мобильного && phone.CustomerId is not null && !string.IsNullOrEmpty(phone.QuickCall))
            statements.Add(SQLQueries.SELECT_ТелефонЗаказчикиКороткогоМобильного);
        if (phone.CustomerId is not null)
            statements.Add(SQLQueries.SELECT_ТелефонЗаказчикиНаРасположенииСтанции);
        if (phone.EmployeeId is not null) statements.Add(SQLQueries.SELECT_ТелефонРаботодателиСотрудника);
        if (!string.IsNullOrEmpty(phone.QuickCall)) statements.Add(SQLQueries.SELECT_ТелефонПереадресацияДругогоСотрудника);
        if (phone.Id > 0 && request.Relations.ParallelCalls.Count == 0 && phone.ViewInDirectory != 0)
            statements.Add(SQLQueries.SELECT_ТелефонУчастникПараллельногоВызова);
        var parameters = new DynamicParameters();
        parameters.Add("@Id", phone.Id);
        parameters.Add("@PhoneId", phone.Id);
        parameters.Add("@Number", phone.Number);
        parameters.Add("@StationId", phone.StationId.Value);
        parameters.Add("@PhoneTypeId", phone.PhoneTypeId.Value);
        parameters.Add("@FullNumber", phone.FullNumber);
        parameters.Add("@FullNumberEscaped", PhoneFormValidator.EscapeLike(phone.FullNumber));
        parameters.Add("@AtsPort", phone.AtsPort);
        parameters.Add("@DrivenById", phone.DrivenById);
        parameters.Add("@DisplayedFromId", phone.DisplayedFromId);
        parameters.Add("@EmployeeId", phone.EmployeeId);
        parameters.Add("@CustomerId", phone.CustomerId);
        parameters.Add("@QuickCall", phone.QuickCall);
        parameters.Add("@CallNumber", phone.Number);
        parameters.Add("@SocketId", phone.SocketId);
        var relationChecks = new List<Action<int>>();
        var relationIndex = 0;
        foreach (var member in request.Relations.GroupMembers)
        {
            var parameter = $"@MemberPhoneId{relationIndex++}";
            parameters.Add(parameter, member.PhoneId);
            statements.Add(SQLQueries.SELECT_ТелефонЧленГруппыНедопустим.Replace("@MemberPhoneId", parameter));
            relationChecks.Add(result =>
            {
                if (result > 0) issues.Add(new PhoneValidationIssue("GroupMembers",
                    $"Номер #{member.PhoneId} отсутствует или принадлежит другой станции."));
            });
        }
        relationIndex = 0;
        foreach (var link in request.Relations.Equipment)
        {
            var equipmentParameter = $"@EquipmentId{relationIndex}";
            var connectionParameter = $"@ConnectionEquipmentId{relationIndex++}";
            parameters.Add(equipmentParameter, link.Key.EquipmentId);
            parameters.Add(connectionParameter, link.Key.ConnectionEquipmentId);
            statements.Add(SQLQueries.SELECT_ТелефонОборудованиеДопустимо
                .Replace("@EquipmentId", equipmentParameter).Replace("@ConnectionEquipmentId", connectionParameter));
            relationChecks.Add(result =>
            {
                if (result == 0) issues.Add(new PhoneValidationIssue("Equipment",
                    $"Связь оборудования #{link.EquipmentId} недопустима для текущих типа, станции, сотрудника или розетки."));
            });
        }
        if (phone.SocketId is not null)
        {
            var locationIds = request.Relations.Equipment.Select(item => item.LocationId).Where(id => id > 0).Distinct().ToList();
            if (request.Relations.Equipment.Count > 0 && locationIds.Count == 0)
                issues.Add(new PhoneValidationIssue("SocketId", "Нельзя проверить розетку: у выбранного оборудования не указано расположение."));
            else
            {
                var socketSql = SQLQueries.SELECT_ТелефонРозеткаДопустимаВОфисе;
                if (locationIds.Count > 0)
                {
                    var names = locationIds.Select((id, index) =>
                    {
                        var name = $"@LocationId{index}";
                        parameters.Add(name, id);
                        return name;
                    });
                    socketSql = SQLQueries.SELECT_ТелефонРозеткаДопустимаОборудования
                        .Replace("@LocationIds", string.Join(",", names));
                }
                statements.Add(socketSql);
                relationChecks.Add(result =>
                {
                    if (result == 0) issues.Add(new PhoneValidationIssue("SocketId",
                        "Розетка занята, не работает или недоступна для текущего расположения."));
                });
            }
        }
        await _db.RunAsync<bool>(async connection =>
        {
            using var reader = await connection.QueryMultipleAsync(string.Join("\n", statements), parameters);
            if (await reader.ReadFirstOrDefaultAsync<int>() == 0)
                issues.Add(new PhoneValidationIssue("PhoneTypeId", "Выбранный тип номера недоступен на этой станции."));
            var duplicateId = await reader.ReadFirstOrDefaultAsync<int?>();
            if (duplicateId is not null)
                issues.Add(new PhoneValidationIssue("Number", $"Номер уже используется на станции (запись #{duplicateId})."));
            var hasPorts = await reader.ReadFirstOrDefaultAsync<int>() != 0;
            var portAvailable = await reader.ReadFirstOrDefaultAsync<int>() != 0;
            if (hasPorts && string.IsNullOrEmpty(phone.AtsPort))
                issues.Add(new PhoneValidationIssue("AtsPort", "Выберите порт АТС."));
            if (hasPorts && !string.IsNullOrEmpty(phone.AtsPort) && !portAvailable)
                issues.Add(new PhoneValidationIssue("AtsPort", "Выбранный порт занят или недоступен для текущих типа и станции."));
            if (phone.DrivenById is null && phone.DisplayedFromId is null &&
                await reader.ReadFirstOrDefaultAsync<int>() > 0)
                issues.Add(new PhoneValidationIssue("Number", "Полный номер уже используется."));
            if (await reader.ReadFirstOrDefaultAsync<int>() > 0)
                issues.Add(new PhoneValidationIssue("Number", "Полный номер пересекается по префиксу с другим номером."));
            if (phone.DrivenById is not null || phone.DisplayedFromId is not null)
            {
                if (await reader.ReadFirstOrDefaultAsync<int>() > 0)
                    issues.Add(new PhoneValidationIssue("DrivenById", "Зависимые номера содержат ссылку на себя, цикл или другую станцию."));
            }
            if (phone.PhoneTypeId == (int)ТипыТелефонныхНомеров.ГруппаИлиФиктивный && phone.EmployeeId is not null && await reader.ReadFirstOrDefaultAsync<int>() == 0)
                issues.Add(new PhoneValidationIssue("EmployeeId", "Для PBX-группы выберите сотрудника, являющегося группой посменной работы.", PhoneValidationSeverity.WarningAllowed));
            if (phone.PhoneTypeId == (int)ТипыТелефонныхНомеров.Короткий_номер_мобильного && phone.CustomerId is not null && !string.IsNullOrEmpty(phone.QuickCall))
            {
                var customers = (await reader.ReadAsync<string>()).ToList();
                if (customers.Count > 0)
                    issues.Add(new PhoneValidationIssue("CustomerId", "Заказчик не соответствует договору связи: " + string.Join(", ", customers), PhoneValidationSeverity.WarningAllowed));
            }
            if (phone.CustomerId is not null)
            {
                var customers = (await reader.ReadAsync<(int КодЛица, string Кличка)>()).ToList();
                if (customers.Count > 0)
                    issues.Add(new PhoneValidationIssue("CustomerId", "На расположении станции есть другие допустимые заказчики: " + string.Join(", ", customers.Select(item => item.Кличка)), PhoneValidationSeverity.WarningAllowed));
            }
            if (phone.EmployeeId is not null)
            {
                var employers = (await reader.ReadAsync<(int КодЛица, string Работодатель)>()).ToList();
                if (employers.Count > 0 && phone.CustomerId is not null && employers.All(item => item.КодЛица != phone.CustomerId.Value))
                    issues.Add(new PhoneValidationIssue("CustomerId",
                        "Заказчик не соответствует работодателю сотрудника: " + string.Join(", ", employers.Select(item => item.Работодатель)),
                        PhoneValidationSeverity.WarningAllowed));
            }
            if (!string.IsNullOrEmpty(phone.QuickCall))
            {
                var employees = (await reader.ReadAsync<string>()).ToList();
                if (employees.Count > 0)
                    issues.Add(new PhoneValidationIssue("QuickCall",
                        "Номер переадресации выдан другому сотруднику: " + string.Join(", ", employees),
                        PhoneValidationSeverity.WarningAllowed));
            }
            if (phone.Id > 0 && request.Relations.ParallelCalls.Count == 0 && phone.ViewInDirectory != 0 &&
                await reader.ReadFirstOrDefaultAsync<int>() > 0)
                issues.Add(new PhoneValidationIssue("ViewInDirectory", "Номер участвует в чужом параллельном вызове; отображение в справочнике допустимо, но не обязательно.", PhoneValidationSeverity.Info));
            foreach (var check in relationChecks)
                check(await reader.ReadFirstOrDefaultAsync<int>());
            return true;
        });
        return issues;
    }

    /// <summary>Проверяет зависимости и удаляет телефон со связями в короткой транзакции.</summary>
    public virtual async Task DeleteAsync(int id)
    {
        await EnsureWriteAccessAsync();
        if (await _db.ExecuteScalarAsync<int>(SQLQueries.SELECT_ТелефонЗависимыеПриУдалении,
                new { Id = id }, commandType: CommandType.Text) > 0)
            throw new InvalidOperationException("Номер нельзя удалить: от него зависят ведомые или отображаемые номера.");
        var sql = "SET XACT_ABORT ON; BEGIN TRY BEGIN TRANSACTION;\n" + SQLQueries.DELETE_ТелефонДляФормы +
                  "\nCOMMIT TRANSACTION; END TRY BEGIN CATCH IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; " +
                  "DECLARE @ErrorMessage nvarchar(4000) = ERROR_MESSAGE(), @ErrorSeverity int = ERROR_SEVERITY(), @ErrorState int = ERROR_STATE(); " +
                  "RAISERROR(@ErrorMessage, @ErrorSeverity, @ErrorState); END CATCH;";
        await _db.ExecuteAsync(sql, new { Id = id }, commandType: CommandType.Text);
    }

    /// <summary>Выполняет процедуры АТС и возвращает независимый результат каждой операции.</summary>
    public virtual async Task<IReadOnlyList<PhoneSyncResult>> SynchronizeAsync(int phoneId, IEnumerable<PhoneSyncOperation> operations)
    {
        var results = new List<PhoneSyncResult>();
        foreach (var operation in operations.Distinct())
        {
            string? error = null;
            try
            {
                switch (operation.Kind)
                {
                    case PhoneSyncKind.Meridian:
                        await ExecuteReturnCodeAsync(SQLQueries.SP_ТелефонСинхронизацияMeridian, new DynamicParameters(),
                            parameters =>
                            {
                                parameters.Add("@ПортАТС", operation.Key);
                                parameters.Add("@ОткрытьСоединение", 1);
                                parameters.Add("@ЗакрытьСоединение", 1);
                                parameters.Add("@showTrace", 2);
                            });
                        break;
                    case PhoneSyncKind.Xorcom:
                        var hasDelete = await _db.ExecuteScalarAsync<int>(SQLQueries.SELECT_ПроцедураИмеетПараметр,
                            new { ProcedureName = SQLQueries.SP_PhoneSync_Xorcom, ParameterName = "@Удаление" },
                            commandType: CommandType.Text) > 0;
                        await ExecuteReturnCodeAsync(SQLQueries.SP_PhoneSync_Xorcom, new DynamicParameters(), parameters =>
                        {
                            parameters.Add("@КодТелефонногоНомера", phoneId);
                            if (hasDelete) parameters.Add("@Удаление", operation.Deleting);
                        });
                        break;
                    case PhoneSyncKind.Ccm:
                        await ExecuteReturnCodeAsync(SQLQueries.SP_PhoneSync_Ccm, new DynamicParameters(),
                            parameters => parameters.Add("@КодОборудования", int.Parse(operation.Key)));
                        break;
                }
            }
            catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
            {
                error = ex.Message;
            }
            results.Add(new PhoneSyncResult(operation, error));
        }
        return results;
    }

    private async Task ExecuteReturnCodeAsync(string procedure, DynamicParameters parameters, Action<DynamicParameters> fill)
    {
        fill(parameters);
        parameters.Add("@RETURN_VALUE", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);
        await _db.ExecuteAsync(procedure, parameters, commandType: CommandType.StoredProcedure);
        var code = parameters.Get<int>("@RETURN_VALUE");
        if (code != 0) throw new InvalidOperationException($"Процедура вернула код {code}.");
    }

    private async Task EnsureWriteAccessAsync()
    {
        if (!await HasRoleAsync((int)Роли.Специалист_по_инвентаризации_оборудования))
            throw new UnauthorizedAccessException("Недостаточно прав для изменения телефонного номера (роль 43).");
    }
}
