using System.Data;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Dapper;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Операции чтения и сохранения основной записи оборудования.</summary>
public class EquipmentFormService
{
    private readonly DbManager _db;
    private readonly ICurrentUserPrincipalProvider? _principalProvider;
    private readonly ILogger<EquipmentFormService>? _logger;
    private readonly Dictionary<string, EquipmentPhoneSyncIntent> _confirmedPhoneIntents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PhoneSyncAttempt> _phoneSyncAttempts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _phoneSyncPlanningGate = new(1, 1);

    /// <summary>Создаёт сервис без UI-авторизации для тестов и небезопасных для ролей вызовов.</summary>
    public EquipmentFormService(DbManager db) : this(db, (ICurrentUserPrincipalProvider?)null) { }

    /// <summary>Создаёт сервис с подключением и серверной Windows-идентичностью текущего circuit.</summary>
    public EquipmentFormService(DbManager db, AuthenticationStateProvider? authenticationStateProvider, ILogger<EquipmentFormService>? logger = null)
        : this(db, authenticationStateProvider is null ? null : new CircuitCurrentUserPrincipalProvider(authenticationStateProvider), logger) { }

    /// <summary>Создаёт сервис с явным источником доверенной идентичности HTTP-запроса или circuit.</summary>
    public EquipmentFormService(DbManager db, ICurrentUserPrincipalProvider? principalProvider, ILogger<EquipmentFormService>? logger = null)
    {
        _db = db;
        _principalProvider = principalProvider;
        _logger = logger;
    }

    /// <summary>Читает секции формы; SIM требует явный business Today через перегрузку.</summary>
    public virtual Task<EquipmentFormReadResult> GetFormDataAsync(EquipmentFormReadRequest request) =>
        GetFormDataAsync(request, null);

    /// <summary>Читает независимые секции формы одним SQL batch с единым business Today для SIM.</summary>
    public virtual async Task<EquipmentFormReadResult> GetFormDataAsync(
        EquipmentFormReadRequest request,
        DateTime? businessToday)
    {
        if (request.IncludeSim && businessToday is null)
            throw new InvalidOperationException("Календарная дата клиента не задана. Чтение услуг SIM отменено.");
        if (request.IncludeAccounts)
            await EnsureAccountAccessAsync(false);

        var statements = new List<string>();
        void Add(string sql) => statements.Add(sql.TrimEnd().TrimEnd(';') + ";");

        if (request.IncludePhones) Add(SQLQueries.SELECT_ТелефонныеНомераОборудования);
        if (request.IncludeHotelPhoneCount) Add(SQLQueries.SELECT_UniquePhoneNumberInHotel);
        Add(SQLQueries.SELECT_ОборудованиеРасположение);
        Add(SQLQueries.SELECT_ОборудованиеСотрудники);
        if (request.IncludeAccounts) Add(SQLQueries.SELECT_ОборудованиеУчётныеЗаписи);
        if (request.IncludeComputer)
        {
            Add(SQLQueries.SELECT_Компьютеры_КодОборудования);
            Add(SQLQueries.SELECT_СтандартныеКонфигурации);
        }
        if (request.IncludeSim)
        {
            Add(SQLQueries.SELECT_SIMКарты_КодОборудования);
            Add(SQLQueries.SELECT_SIMКартыДопУслугиСотОператора_КодОборудования);
            Add(SQLQueries.SELECT_ТипыДопУслугСотОператора);
        }
        if (request.IncludeLicense)
        {
            Add(SQLQueries.SELECT_Soft_КодОборудования);
            Add(SQLQueries.SELECT_ТипыЛицензирования);
        }
        if (request.IncludeComputer) Add(SQLQueries.SELECT_ПО_КодОборудования);
        Add(SQLQueries.SELECT_ОборудованиеБухУчета_КодОборудования);
        Add(SQLQueries.SELECT_Routers_КодОборудования);
        Add(SQLQueries.SELECT_КодОборудования_RouterInventory);
        Add(SQLQueries.SELECT_ID_vwПодключенияUPS);
        Add(SQLQueries.SELECT_ID_РеальноеРасположениеОборудования_ПоследнееРасположение);
        Add(SQLQueries.SELECT_ID_ConnectedWorkNet);
        if (request.IncludeNetworkName && !string.IsNullOrWhiteSpace(request.NetworkName))
            Add(SQLQueries.SELECT_NAME_vwЛокальнаяСеть);
        if (request.IncludeNetworkName) Add(SQLQueries.SELECT_ОборудованиеСетевыеИмена);

        var parameters = new
        {
            EquipmentId = request.EquipmentId,
            ServerEquipmentId = request.EquipmentId,
            ShowHistory = request.ShowServiceHistory ? 1 : 0,
            ShowRequired = request.ShowCompanyRequiredServices ? 1 : 0,
            BusinessToday = businessToday?.Date,
            СетевоеИмя = request.NetworkName ?? string.Empty
        };

        return await _db.RunAsync(async connection =>
        {
            using var reader = await connection.QueryMultipleAsync(string.Join("\n", statements), parameters);
            var result = new EquipmentFormReadResult();
            if (request.IncludePhones)
                result.Phones = (await reader.ReadAsync<EquipmentPhoneLink>()).ToList();
            if (request.IncludeHotelPhoneCount)
                result.HotelPhoneCount = await reader.ReadFirstOrDefaultAsync<int>();
            result.Locations = (await reader.ReadAsync<EquipmentLocationPeriod>()).ToList();
            result.Responsibles = (await reader.ReadAsync<EquipmentResponsiblePeriod>()).ToList();
            if (request.IncludeAccounts)
                result.Accounts = (await reader.ReadAsync<EquipmentAccount>()).ToList();
            if (request.IncludeComputer)
            {
                result.ComputerHistory = (await reader.ReadAsync<EquipmentComputerSnapshot>()).ToList();
                foreach (var snapshot in result.ComputerHistory)
                    snapshot.ParseDetails();
                result.Configurations = (await reader.ReadAsync<StandardConfiguration>()).ToList();
            }
            if (request.IncludeSim)
            {
                result.SimCards = (await reader.ReadAsync<EquipmentSimCard>()).ToList();
                result.SimServices = (await reader.ReadAsync<EquipmentSimService>()).ToList();
                result.SimServiceTypes = (await reader.ReadAsync<SimServiceType>()).ToList();
            }
            if (request.IncludeLicense)
            {
                result.Licenses = (await reader.ReadAsync<EquipmentLicense>()).ToList();
                result.LicenseTypes = (await reader.ReadAsync<LicenseType>()).ToList();
            }
            if (request.IncludeComputer)
                result.SoftwareInstallations = (await reader.ReadAsync<EquipmentSoftwareInstallation>()).ToList();
            result.Accounting = (await reader.ReadAsync<EquipmentAccounting>()).ToList();
            result.RouterInfo = await reader.ReadFirstOrDefaultAsync<EquipmentRouterInfo>();
            result.RouterInventory = (await reader.ReadAsync<EquipmentRouterInventoryItem>()).ToList();
            result.UpsLinks = (await reader.ReadAsync<UpsServerLink>()).ToList();
            result.LastInventory = await reader.ReadFirstOrDefaultAsync<EquipmentActualInventory>();
            result.NetworkAttachmentCount = await reader.ReadFirstOrDefaultAsync<int>();
            if (request.IncludeNetworkName && !string.IsNullOrWhiteSpace(request.NetworkName))
                result.NetworkPorts = (await reader.ReadAsync<EquipmentNetworkPortLink>()).ToList();
            if (request.IncludeNetworkName)
                result.NetworkNameHistory = (await reader.ReadAsync<EquipmentNetworkNameHistory>()).ToList();
            return result;
        });
    }

    /// <summary>Загружает основную запись оборудования для редактирования.</summary>
    public virtual async Task<Equipment?> GetAsync(int id) =>
        await _db.RunDbAsync(async connection =>
            (await connection.QueryAsync<Equipment>(SQLQueries.SELECT_ID_Оборудование, new { Id = id }))
                .SingleOrDefault());

    /// <summary>Проверяет роль 43 и сохраняет 15 основных полей существующего оборудования.</summary>
    public virtual async Task UpdateAsync(Equipment equipment)
    {
        await EnsureInventoryEditAsync();
        await ValidateEquipmentForSaveAsync(equipment);
        await equipment.UpdateAsync(_db);
    }

    /// <summary>Загружает телефонные связи оборудования.</summary>
    public virtual async Task<IReadOnlyList<EquipmentPhoneLink>> GetPhonesAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentPhoneLink>(
            SQLQueries.SELECT_ТелефонныеНомераОборудования, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Возвращает количество номеров, если текущее расположение является гостиничным номером.</summary>
    public virtual async Task<int> GetHotelPhoneCountAsync(int equipmentId) =>
        await ExecuteRequiredScalarAsync<int>(SQLQueries.SELECT_UniquePhoneNumberInHotel,
            new { EquipmentId = equipmentId }, commandType: CommandType.Text);

    /// <summary>Определяет доступное оборудование подключения аналогового номера.</summary>
    public virtual async Task<int?> GetConnectionEquipmentIdAsync(int phoneId, int? employeeId, int locationId) =>
        (await _db.QueryRequiredAsync<int?>(SQLQueries.SELECT_ТелефонныйНомерОборудованиеПодключения,
            new { PhoneId = phoneId, EmployeeId = employeeId, LocationId = locationId })).SingleOrDefault();

    /// <summary>Загружает другое оборудование той же legacy-группы на выбранном рабочем месте без изменения данных.</summary>
    public virtual async Task<IReadOnlyList<EquipmentWorkplaceAdvisoryItem>> GetWorkplaceAdvisoryAsync(
        int equipmentId, int locationId) =>
        (await _db.QueryRequiredAsync<EquipmentWorkplaceAdvisoryItem>(
            SQLQueries.SELECT_ПредупреждениеОборудованиеНаРабочемМесте,
            new { Id = locationId, IT = 1, EquipmentId = equipmentId })).ToList();

    /// <summary>Загружает историю расположений оборудования.</summary>
    public virtual async Task<IReadOnlyList<EquipmentLocationPeriod>> GetLocationsAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentLocationPeriod>(
            SQLQueries.SELECT_ОборудованиеРасположение, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Проверяет наличие действующего ответственного.</summary>
    public virtual async Task<bool> HasCurrentResponsibleAsync(int equipmentId) =>
        await ExecuteRequiredScalarAsync<int>(
            SQLQueries.SELECT_ОборудованиеЕстьТекущийОтветственный,
            new { EquipmentId = equipmentId }, commandType: CommandType.Text) > 0;

    /// <summary>Проверяет роль 43 и добавляет расположение и при необходимости ответственного.</summary>
    public virtual Task AddLocationAsync(
        EquipmentLocationPeriod location,
        EquipmentResponsiblePeriod? responsible = null) =>
        AddLocationCheckedAsync(location, responsible, expectedActiveResponsible: null);

    /// <summary>Атомарно проверяет ожидаемое active-состояние перед добавлением расположения и выдачи.</summary>
    public virtual Task AddLocationAsync(
        EquipmentLocationPeriod location,
        EquipmentResponsiblePeriod? responsible,
        bool expectedActiveResponsible) =>
        AddLocationCheckedAsync(location, responsible, expectedActiveResponsible);

    private Task AddLocationCheckedAsync(
        EquipmentLocationPeriod location,
        EquipmentResponsiblePeriod? responsible,
        bool? expectedActiveResponsible)
    {
        var activeResponsible = false;
        return RunPeriodWriteAsync(async () =>
        {
            await EnsureInventoryEditAsync();
            ValidateLocationAddition(location, responsible);
            activeResponsible = expectedActiveResponsible ?? await HasCurrentResponsibleAsync(location.EquipmentId);
        }, () => AddLocationCoreAsync(location, responsible, activeResponsible));
    }

    private async Task AddLocationCoreAsync(
        EquipmentLocationPeriod location,
        EquipmentResponsiblePeriod? responsible,
        bool expectedActiveResponsible)
    {
        var command = EquipmentLocationSaveBuilder.BuildAdd(location, responsible, expectedActiveResponsible);
        await _db.ExecuteAsync(command.Sql, command.Parameters, commandType: CommandType.Text);
    }

    private static void ValidateLocationAddition(
        EquipmentLocationPeriod location,
        EquipmentResponsiblePeriod? responsible)
    {
        if (location.EquipmentId <= 0 || location.LocationId <= 0 || location.FromUtc == default)
            throw new InvalidOperationException("Не указаны оборудование, расположение или дата начала периода.");
        if (responsible is not null &&
            (responsible.EquipmentId != location.EquipmentId || responsible.EmployeeId <= 0 || responsible.FromUtc == default))
            throw new InvalidOperationException("Ответственный не соответствует оборудованию или имеет недопустимую дату выдачи.");
    }

    /// <summary>Проверяет роль 43 и обновляет срок возврата и примечания расположения.</summary>
    public virtual Task UpdateLocationAsync(EquipmentLocationPeriod location) =>
        RunPeriodWriteAsync(EnsureInventoryEditAsync, () => location.UpdateAsync(_db));

    /// <summary>Загружает историю ответственных сотрудников.</summary>
    public virtual async Task<IReadOnlyList<EquipmentResponsiblePeriod>> GetResponsiblesAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentResponsiblePeriod>(
            SQLQueries.SELECT_ОборудованиеСотрудники, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Проверяет роль 43 и добавляет нового ответственного.</summary>
    public virtual Task IssueResponsibleAsync(EquipmentResponsiblePeriod responsible) =>
        RunPeriodWriteAsync(EnsureInventoryEditAsync, () => responsible.InsertAsync(_db));

    /// <summary>Проверяет роль 43 и обновляет срок возврата, примечания и окончание ответственного.</summary>
    public virtual Task UpdateResponsibleAsync(EquipmentResponsiblePeriod responsible) =>
        RunPeriodWriteAsync(EnsureInventoryEditAsync, () => responsible.UpdateAsync(_db));

    /// <summary>Проверяет роль 43 и атомарно сохраняет изъятие и связанные записи.</summary>
    public virtual Task WithdrawResponsibleAsync(
        EquipmentResponsiblePeriod current,
        EquipmentResponsiblePeriod? next,
        EquipmentLocationPeriod? location) =>
        WithdrawResponsibleAsync(current, next, location, null);

    /// <summary>Проверяет срок возврата по локальному календарному дню начала следующего периода.</summary>
    public virtual Task WithdrawResponsibleAsync(
        EquipmentResponsiblePeriod current,
        EquipmentResponsiblePeriod? next,
        EquipmentLocationPeriod? location,
        DateTime? nextFromLocalDate) =>
        RunPeriodWriteAsync(
            () => PrepareWithdrawalAsync(current, next, location, nextFromLocalDate),
            () => current.WithdrawAsync(_db, next, location));

    private async Task PrepareWithdrawalAsync(
        EquipmentResponsiblePeriod current,
        EquipmentResponsiblePeriod? next,
        EquipmentLocationPeriod? location,
        DateTime? nextFromLocalDate)
    {
        await EnsureInventoryEditAsync();
        ValidateWithdrawal(current, next, location);
        var responsibles = await GetResponsiblesAsync(current.EquipmentId);
        var active = responsibles.Where(item => item.ToUtc is null).Take(2).ToList();
        if (active.Count != 1 || active[0].FromUtc != current.FromUtc || active[0].EmployeeId != current.EmployeeId ||
            active[0].ChangedAtUtc != current.ChangedAtUtc)
            throw new InvalidOperationException(
                "Действующий ответственный изменился. Обновите данные и повторите операцию.");
        if (next is not null && next.EmployeeId == active[0].EmployeeId)
            throw new InvalidOperationException("Следующий ответственный должен отличаться от действующего.");
        if (next?.ReturnDate is not null)
        {
            if (nextFromLocalDate is null)
                throw new InvalidOperationException(
                    "Локальная календарная дата начала следующего периода не задана. Запись отменена.");
            if (next.ReturnDate.Value.Date < nextFromLocalDate.Value.Date)
                throw new InvalidOperationException("Срок возврата следующего ответственного не может быть раньше его выдачи.");
        }
    }

    private static void ValidateWithdrawal(
        EquipmentResponsiblePeriod current,
        EquipmentResponsiblePeriod? next,
        EquipmentLocationPeriod? location)
    {
        if (current.EquipmentId <= 0 || current.EmployeeId <= 0 || current.FromUtc == default ||
            current.ToUtc is null || current.ToUtc == default)
            throw new InvalidOperationException("Для изъятия укажите оборудование, текущий период и фактическую дату окончания.");
        if (next is not null &&
            (next.EquipmentId != current.EquipmentId || next.EmployeeId <= 0 || next.FromUtc == default))
            throw new InvalidOperationException("Следующий ответственный не соответствует оборудованию или имеет недопустимую дату выдачи.");
        if (location is not null &&
            (location.EquipmentId != current.EquipmentId || location.LocationId <= 0 || location.FromUtc == default))
            throw new InvalidOperationException("Новое расположение не соответствует оборудованию или имеет недопустимую дату.");
    }

    private static async Task RunPeriodWriteAsync(Func<Task> preflight, Func<Task> write)
    {
        try { await preflight(); }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            throw new EquipmentOperationException(EquipmentWritePhase.NotSent,
                "Не удалось проверить исходные данные периода. Команда записи не отправлена.", ex);
        }

        try { await write(); }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            // RAISERROR 50000 и текст исключения не подтверждают rollback полученной команды.
            throw new EquipmentOperationException(EquipmentWritePhase.Unknown,
                "Результат записи периода неизвестен. Перед повторной записью требуется сверка.", ex);
        }
    }

    /// <summary>Загружает учётные записи без паролей после проверки роли 102.</summary>
    public virtual async Task<IReadOnlyList<EquipmentAccount>> GetAccountsAsync(int equipmentId)
    {
        await EnsureAccountAccessAsync(false);
        return (await _db.QueryRequiredAsync<EquipmentAccount>(
            SQLQueries.SELECT_ОборудованиеУчётныеЗаписи, new { EquipmentId = equipmentId })).ToList();
    }

    /// <summary>Загружает пароль одной записи после серверной проверки роли 102.</summary>
    public virtual async Task<string> GetAccountPasswordAsync(int equipmentId, string login)
    {
        await EnsureAccountAccessAsync(false);
        return await _db.RunDbAsync(async connection =>
        {
            var rows = (await connection.QueryAsync<string>(
                SQLQueries.SELECT_ОборудованиеУчётнаяЗаписьПароль,
                new { EquipmentId = equipmentId, Login = login }, commandType: CommandType.Text)).ToList();
            if (rows.Count == 0)
                throw new InvalidOperationException("Учётная запись не найдена или была удалена. Обновите список.");
            if (rows.Count > 1)
                throw new InvalidOperationException("Не удалось однозначно определить учётную запись. Обновите список.");
            return rows[0] ?? string.Empty;
        });
    }

    /// <summary>Читает защищённый снимок одной учётной записи с непрозрачной исходной версией.</summary>
    public virtual async Task<EquipmentAccount> GetAccountEditSnapshotAsync(int equipmentId, string login)
    {
        await EnsureAccountAccessAsync(false);
        return await EquipmentAccount.ReadEditorSnapshotAsync(_db, equipmentId, login)
            ?? throw new InvalidOperationException("Учётная запись не найдена или была удалена. Сверьте актуальные данные.");
    }

    /// <summary>Добавляет учётную запись после проверки ролей 102 и 43 и фактического типа.</summary>
    public virtual async Task AddAccountAsync(EquipmentAccount account)
    {
        await EnsureAccountWriteAccessAsync(account.EquipmentId);
        await ExecuteAccountWriteAsync(() => account.InsertAsync(_db));
    }

    /// <summary>Обновляет учётную запись после проверки ролей 102 и 43 и фактического типа.</summary>
    public virtual async Task UpdateAccountAsync(EquipmentAccount account)
    {
        await EnsureAccountWriteAccessAsync(account.EquipmentId);
        await ExecuteAccountWriteAsync(() => account.UpdateAsync(_db));
    }

    /// <summary>Удаляет учётную запись после проверки ролей 102 и 43 и фактического типа.</summary>
    public virtual async Task DeleteAccountAsync(EquipmentAccount account)
    {
        await EnsureAccountWriteAccessAsync(account.EquipmentId);
        await ExecuteAccountWriteAsync(() => account.DeleteAsync(_db));
    }

    private static async Task ExecuteAccountWriteAsync(Func<Task> write)
    {
        try { await write(); }
        catch (EquipmentAccountStorageException ex)
        {
            var phase = ex.MayHaveWritten ? EquipmentWritePhase.Unknown
                : ex.WasDispatched ? EquipmentWritePhase.Rejected : EquipmentWritePhase.NotSent;
            throw new EquipmentOperationException(phase, ex.Message, ex);
        }
    }

    private async Task EnsureAccountWriteAccessAsync(int equipmentId)
    {
        try
        {
            await EnsureAccountAccessAsync(true);
            if ((await GetRequiredChildWriteTypeAsync(equipmentId)).MacAddressCount == 0)
                throw new InvalidOperationException("Учётные записи недоступны для фактического типа оборудования.");
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            throw new EquipmentOperationException(EquipmentWritePhase.NotSent,
                "Не удалось проверить права или тип оборудования. Команда не отправлена.", ex);
        }
    }

    /// <summary>Загружает сведения SIM без PIN/PUK.</summary>
    public virtual async Task<IReadOnlyList<EquipmentSimCard>> GetSimCardsAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentSimCard>(
            SQLQueries.SELECT_SIMКарты_КодОборудования, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Строит URL детализации только для доступной SIM и актуального ответственного.</summary>
    public virtual async Task<string?> GetSimDetailUrlAsync(int equipmentId, string baseUri)
    {
        await EnsureTrustedIdentityAsync();
        var responsibles = (await _db.QueryRequiredAsync<EquipmentResponsiblePeriod>(
            SQLQueries.SELECT_ОборудованиеСотрудники, new { EquipmentId = equipmentId })).ToList();
        var sims = (await _db.QueryRequiredAsync<EquipmentSimCard>(
            SQLQueries.SELECT_SIMКарта_Детализация, new { EquipmentId = equipmentId })).ToList();
        if (sims.Count != 1)
            return null;
        var sim = sims[0];
        if (sim.ExecutorPersonId != (int)Лица.Билайн)
            return null;
        if (sim.IsSubordinate != 1)
        {
            var tarifficationAvailable = await ExecuteRequiredScalarAsync<int>(
                SQLQueries.SELECT_SIMДетализация_ЕстьТарификация, null, CommandType.Text);
            if (tarifficationAvailable != 1)
                return null;
            var availableByEmployee = await ExecuteRequiredScalarAsync<int>(
                SQLQueries.SELECT_SIMДетализация_Подчинённый, new { ContractId = sim.ContractId }, CommandType.Text);
            if (availableByEmployee != 1)
                return null;
        }
        var responsible = responsibles.Count == 1
            ? responsibles[0]
            : responsibles.SingleOrDefault(item => item.ToUtc is null);
        return responsible is null
            ? null
            : sim.BuildAccountingPhoneUri(baseUri, responsible.EmployeeName);
    }

    /// <summary>Загружает PIN/PUK после серверной проверки роли.</summary>
    public virtual async Task<EquipmentSimSecrets> GetSimSecretsAsync(int equipmentId)
    {
        if (!await HasRoleAsync(Роли.Администратор_тарифов_связи))
            throw new UnauthorizedAccessException("Недостаточно прав для просмотра кодов SIM.");
        return await _db.RunDbAsync(async connection =>
        {
            var secrets = (await connection.QueryAsync<EquipmentSimSecrets>(
                SQLQueries.SELECT_SIMКарта_Секреты, new { EquipmentId = equipmentId })).SingleOrDefault();
            if (secrets is null)
                throw new InvalidOperationException("Сведения SIM не найдены или были удалены. Обновите список.");
            return secrets;
        });
    }

    /// <summary>Читает защищённый исходный снимок только после проверки права просмотра кодов SIM.</summary>
    public virtual async Task<EquipmentSimCard?> GetSimEditorSnapshotAsync(int equipmentId)
    {
        if (!await HasRoleAsync(Роли.Администратор_тарифов_связи))
            throw new UnauthorizedAccessException("Недостаточно прав для просмотра кодов SIM.");
        return await EquipmentSimCard.ReadEditorSnapshotAsync(_db, equipmentId);
    }

    /// <summary>Ищет другую активную SIM с тем же номером.</summary>
    public virtual async Task<int?> GetDuplicateSimIdAsync(int equipmentId, string phoneNumber) =>
        await ExecuteRequiredScalarAsync<int?>(SQLQueries.SELECT_SimКарты_Другая_С_Тем_Же_Номером,
            new { EquipmentId = equipmentId, PhoneNumber = phoneNumber }, commandType: CommandType.Text);

    /// <summary>Сохраняет SIM в явном режиме после свежей проверки права и фактического типа.</summary>
    public virtual async Task SaveSimAsync(EquipmentSimCard sim, EquipmentSimWriteMode mode, string? expectedVersion,
        int? duplicateEquipmentId = null)
    {
        await EnsureSimWriteAccessAsync(sim.EquipmentId);
        var validation = sim.Validate();
        if (validation is not null)
            throw new InvalidOperationException(validation);
        await ExecuteSimWriteAsync(() => sim.SaveAsync(_db, mode, expectedVersion, duplicateEquipmentId));
    }

    /// <summary>Удаляет проверенную исходную строку после свежей проверки права и фактического типа.</summary>
    public virtual async Task DeleteSimAsync(int equipmentId, string? expectedVersion)
    {
        await EnsureSimWriteAccessAsync(equipmentId);
        await ExecuteSimWriteAsync(() => EquipmentSimCard.DeleteAsync(_db, equipmentId, expectedVersion));
    }

    private async Task EnsureSimWriteAccessAsync(int equipmentId)
    {
        try
        {
            if (!await HasRoleAsync(Роли.Администратор_тарифов_связи))
                throw new UnauthorizedAccessException("Недостаточно прав для изменения SIM.");
            if ((await GetRequiredChildWriteTypeAsync(equipmentId)).HasSimCharacteristics == 0)
                throw new InvalidOperationException("Сведения SIM недоступны для фактического типа оборудования.");
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            var error = new EquipmentSimStorageException(false, false);
            throw new EquipmentOperationException(EquipmentWritePhase.NotSent, error.Message, error);
        }
    }

    private static async Task ExecuteSimWriteAsync(Func<Task> write)
    {
        try { await write(); }
        catch (EquipmentSimStorageException error)
        {
            var phase = error.MayHaveWritten ? EquipmentWritePhase.Unknown
                : error.WasDispatched ? EquipmentWritePhase.Rejected : EquipmentWritePhase.NotSent;
            throw new EquipmentOperationException(phase, error.Message, error);
        }
    }

    /// <summary>Возвращает лицо сотрудника для заполнения ПДн.</summary>
    public virtual async Task<int?> GetEmployeePersonIdAsync(int employeeId) =>
        await ExecuteRequiredScalarAsync<int?>(SQLQueries.SELECT_ЛицоСотрудника,
            new { EmployeeId = employeeId }, commandType: CommandType.Text);

    /// <summary>Проверяет ПДн; SQL тарифов требует явный business Today через перегрузку.</summary>
    public virtual Task<(bool IsEligible, string Message)> GetPersonalDataAdvisoryAsync(
        int equipmentId, int? contractId, int? personId, string technicalTariffs) =>
        GetPersonalDataAdvisoryAsync(equipmentId, contractId, personId, technicalTariffs, null);

    /// <summary>Проверяет выбранное лицо ПДн по тому же business Today, что и список услуг.</summary>
    public virtual async Task<(bool IsEligible, string Message)> GetPersonalDataAdvisoryAsync(
        int equipmentId, int? contractId, int? personId, string technicalTariffs, DateTime? businessToday)
    {
        if (personId is null)
            return (true, string.Empty);
        if (contractId is null)
            return (false, "Сначала выберите договор связи; соответствие лица ПДн не проверено.");
        if (businessToday is null)
            throw new InvalidOperationException("Календарная дата клиента не задана. Проверка технического тарифа отменена.");
        var eligibility = await ExecuteRequiredScalarAsync<int>(
            SQLQueries.SELECT_ЛицоПерсональныхДанныхСоответствие,
            new
            {
                EquipmentId = equipmentId,
                ContractId = contractId,
                PersonId = personId,
                TechnicalTariffs = technicalTariffs,
                BusinessToday = businessToday.Value.Date,
            },
            commandType: CommandType.Text);
        return eligibility switch
        {
            1 => (true, string.Empty),
            -1 => (false, "Договор связи не найден. Сохранённое лицо ПДн не удалено; проверьте выбор."),
            _ => (false, "Лицо ПДн не соответствует договору, заказчику или активному техническому тарифу."),
        };
    }

    /// <summary>Загружает услуги SIM; активный фильтр требует явный business Today через перегрузку.</summary>
    public virtual Task<IReadOnlyList<EquipmentSimService>> GetSimServicesAsync(
        int equipmentId, bool showHistory, bool showRequired) =>
        GetSimServicesAsync(equipmentId, showHistory, showRequired, null);

    /// <summary>Загружает дополнительные услуги текущей SIM по согласованному business Today.</summary>
    public virtual async Task<IReadOnlyList<EquipmentSimService>> GetSimServicesAsync(
        int equipmentId, bool showHistory, bool showRequired, DateTime? businessToday)
    {
        if (businessToday is null)
            throw new InvalidOperationException("Календарная дата клиента не задана. Чтение услуг SIM отменено.");
        return (await _db.QueryRequiredAsync<EquipmentSimService>(SQLQueries.SELECT_SIMКартыДопУслугиСотОператора_КодОборудования,
            new
            {
                EquipmentId = equipmentId,
                ShowHistory = showHistory ? 1 : 0,
                ShowRequired = showRequired ? 1 : 0,
                BusinessToday = businessToday.Value.Date,
            })).ToList();
    }

    /// <summary>Загружает типы дополнительных услуг.</summary>
    public virtual async Task<IReadOnlyList<SimServiceType>> GetSimServiceTypesAsync() =>
        (await _db.QueryRequiredAsync<SimServiceType>(SQLQueries.SELECT_ТипыДопУслугСотОператора)).ToList();

    /// <summary>Сохраняет услугу только после передачи календарной даты через согласованный контракт.</summary>
    public virtual Task SaveSimServiceAsync(EquipmentSimService service) =>
        SaveSimServiceAsync(service, null);

    /// <summary>Проверяет роль 42, фактический тип и даты услуги по календарному дню клиента.</summary>
    public virtual async Task SaveSimServiceAsync(EquipmentSimService service, DateTime? businessToday)
    {
        if (!await HasRoleAsync(Роли.Администратор_тарифов_связи))
            throw new UnauthorizedAccessException("Недостаточно прав для изменения дополнительной услуги SIM.");
        if ((await GetRequiredChildWriteTypeAsync(service.EquipmentId)).HasSimCharacteristics == 0)
            throw new InvalidOperationException("Дополнительные услуги SIM недоступны для фактического типа оборудования.");
        if (businessToday is null)
            throw new InvalidOperationException("Календарная дата клиента не задана. Сохранение дополнительной услуги SIM отменено.");
        var validation = service.Validate(businessToday.Value);
        if (validation is not null)
            throw new InvalidOperationException(validation);
        await service.SaveAsync(_db);
    }

    /// <summary>Возвращает конфигурацию IP-телефона после проверки роли 102.</summary>
    public virtual async Task<string> GetPhoneConfigAsync(string mac)
    {
        if (!await HasRoleAsync(Роли.Дежурный_инженер))
            throw new UnauthorizedAccessException("Недостаточно прав для получения конфигурации телефона.");
        var parameters = new Dapper.DynamicParameters();
        parameters.Add("@MACадрес", mac, System.Data.DbType.String, size: 12);
        parameters.Add("@КонфигурацияIpТелефона", dbType: System.Data.DbType.String,
            direction: System.Data.ParameterDirection.Output, size: -1);
        await _db.ExecuteAsync("dbo.sp_КонфигурацияIpТелефона", parameters);
        return parameters.Get<string>("@КонфигурацияIpТелефона") ?? string.Empty;
    }

    /// <summary>Возвращает счётчик подключений рабочей сети.</summary>
    public virtual async Task<int> GetNetworkAttachmentCountAsync(int equipmentId) =>
        await ExecuteRequiredScalarAsync<int>(SQLQueries.SELECT_ID_ConnectedWorkNet,
            new { EquipmentId = equipmentId }, commandType: CommandType.Text);

    /// <summary>Проверяет компьютерный тип и роль 103, затем подключает оборудование к рабочей сети.</summary>
    public virtual async Task AttachNetworkAsync(int equipmentId)
    {
        await EnsureNetworkAccessAsync(equipmentId);
        await _db.ExecuteAsync(
            SQLQueries.INSERT_Net, new { КодОборудования = equipmentId }, commandType: CommandType.Text);
    }

    /// <summary>Проверяет компьютерный тип и роль 103, затем отключает оборудование от рабочей сети.</summary>
    public virtual async Task DetachNetworkAsync(int equipmentId)
    {
        await EnsureNetworkAccessAsync(equipmentId);
        await _db.ExecuteAsync(
            SQLQueries.UPDATE_Net, new { КодОборудования = equipmentId }, commandType: CommandType.Text);
    }

    /// <summary>Загружает UPS, подключённые к серверу.</summary>
    public virtual async Task<IReadOnlyList<UpsServerLink>> GetUpsLinksAsync(int serverEquipmentId) =>
        (await _db.QueryRequiredAsync<UpsServerLink>(
            SQLQueries.SELECT_ID_vwПодключенияUPS, new { ServerEquipmentId = serverEquipmentId })).ToList();

    /// <summary>Проверяет роль по владельцу и быстро подключает UPS к серверу.</summary>
    public virtual async Task AddUpsLinkAsync(int upsEquipmentId, int serverEquipmentId)
    {
        await EnsureUpsAccessAsync(serverEquipmentId);
        await _db.ExecuteAsync(SQLQueries.INSERT_СерверПодключенияUPSОборудование,
            new { КодОборудованияUPS = upsEquipmentId, КодОборудованияСервера = serverEquipmentId },
            commandType: CommandType.Text);
    }

    /// <summary>Проверяет роль по владельцу и удаляет связь UPS и сервера.</summary>
    public virtual async Task DeleteUpsLinkAsync(int upsEquipmentId, int serverEquipmentId)
    {
        await EnsureUpsAccessAsync(serverEquipmentId);
        await _db.ExecuteAsync(SQLQueries.DELETE_СерверПодключенияUPS,
            new { КодОборудованияUPS = upsEquipmentId, КодОборудованияСервера = serverEquipmentId },
            commandType: CommandType.Text);
    }

    /// <summary>Загружает серверы выбранного UPS.</summary>
    public virtual async Task<IReadOnlyList<UpsServerLink>> GetUpsServersAsync(int upsEquipmentId) =>
        (await _db.QueryRequiredAsync<UpsServerLink>(
            SQLQueries.SELECT_ID_СервераПодключенияUPS, new { UpsEquipmentId = upsEquipmentId })).ToList();

    /// <summary>Загружает тесты выбранного UPS.</summary>
    public virtual async Task<IReadOnlyList<UpsTest>> GetUpsTestsAsync(int upsEquipmentId) =>
        (await _db.QueryRequiredAsync<UpsTest>(
            SQLQueries.SELECT_ID_ТестыПодключенияUPS, new { UpsEquipmentId = upsEquipmentId })).ToList();

    /// <summary>Проверяет актуальные права на исходное оборудование и каждый изменяемый сервер.</summary>
    public virtual async Task SaveUpsDetailsAsync(
        int upsEquipmentId,
        int sourceServerEquipmentId,
        IReadOnlyCollection<UpsServerLink> originalServers,
        IReadOnlyCollection<UpsServerLink> currentServers,
        IReadOnlyCollection<UpsTest> originalTests,
        IReadOnlyCollection<UpsTest> currentTests)
    {
        if (upsEquipmentId <= 0 || sourceServerEquipmentId <= 0)
            throw new InvalidOperationException("Подключение UPS отсутствует в БД.");
        await EnsureTrustedIdentityAsync();
        await UpsServerLink.SaveDetailsAsync(
            _db, upsEquipmentId, sourceServerEquipmentId, originalServers, currentServers, originalTests, currentTests);
    }

    /// <summary>Загружает последнюю инвентаризацию оборудования.</summary>
    public virtual async Task<EquipmentActualInventory?> GetLastInventoryAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentActualInventory>(
            SQLQueries.SELECT_ID_РеальноеРасположениеОборудования_ПоследнееРасположение,
            new { EquipmentId = equipmentId })).SingleOrDefault();

    /// <summary>Загружает основные сведения маршрутизатора.</summary>
    public virtual async Task<EquipmentRouterInfo?> GetRouterInfoAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentRouterInfo>(
            SQLQueries.SELECT_Routers_КодОборудования, new { EquipmentId = equipmentId })).SingleOrDefault();

    /// <summary>Загружает инвентаризацию маршрутизатора.</summary>
    public virtual async Task<IReadOnlyList<EquipmentRouterInventoryItem>> GetRouterInventoryAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentRouterInventoryItem>(
            SQLQueries.SELECT_КодОборудования_RouterInventory, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Проверяет роли 66/68 и ведение бухгалтерского учёта владельцем.</summary>
    public virtual async Task<bool> CanUseAccountingAsync(int? personId)
    {
        if (personId is null or <= 0)
            return false;
        var hasRole = await HasRoleForPersonAsync(Роли.Бухгалтер, personId) ||
                      await HasRoleForPersonAsync(Роли.Главный_бухгалтер_1С, personId);
        if (!hasRole)
            return false;
        return await ExecuteRequiredScalarAsync<int>(SQLQueries.SELECT_ЛицоВедетБухгалтерскийУчет,
            new { PersonId = personId.Value }, commandType: CommandType.Text) == 1;
    }

    /// <summary>Загружает историю бухгалтерского учёта.</summary>
    public virtual async Task<IReadOnlyList<EquipmentAccounting>> GetAccountingAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentAccounting>(
            SQLQueries.SELECT_ОборудованиеБухУчета_КодОборудования, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Проверяет права и сохраняет запись бухгалтерского учёта.</summary>
    public virtual async Task SaveAccountingAsync(EquipmentAccounting accounting)
    {
        if (!await CanUseAccountingAsync(accounting.OwnerId))
            throw new UnauthorizedAccessException("Недостаточно прав для изменения бухгалтерского учёта.");
        var validation = accounting.Validate();
        if (validation is not null)
            throw new InvalidOperationException(validation);
        await accounting.SaveAsync(_db);
    }

    /// <summary>Атомарно удаляет подтверждённую запись, если она всё ещё последняя.</summary>
    public virtual async Task DeleteAccountingAsync(int equipmentId, int accountingId)
    {
        await EnsureTrustedIdentityAsync();
        await EquipmentAccounting.DeleteLatestAsync(_db, equipmentId, accountingId);
    }

    /// <summary>Загружает установленное ПО оборудования.</summary>
    public virtual async Task<IReadOnlyList<EquipmentSoftwareInstallation>> GetSoftwareInstallationsAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentSoftwareInstallation>(
            SQLQueries.SELECT_ПО_КодОборудования, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Проверяет роль 43 и фактический тип, валидацию и сохраняет установку ПО.</summary>
    public virtual async Task SaveSoftwareInstallationAsync(EquipmentSoftwareInstallation installation)
    {
        await EnsureInventoryEditAsync();
        if ((await GetRequiredChildWriteTypeAsync(installation.EquipmentId)).HasComputerCharacteristics == 0)
            throw new InvalidOperationException("Установка ПО недоступна для фактического типа оборудования.");
        var validation = installation.Validate();
        if (validation is not null)
            throw new InvalidOperationException(validation);
        await installation.SaveAsync(_db);
    }

    /// <summary>Загружает лицензию оборудования.</summary>
    public virtual async Task<IReadOnlyList<EquipmentLicense>> GetLicensesAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentLicense>(
            SQLQueries.SELECT_Soft_КодОборудования, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Загружает типы лицензирования.</summary>
    public virtual async Task<IReadOnlyList<LicenseType>> GetLicenseTypesAsync() =>
        (await _db.QueryRequiredAsync<LicenseType>(SQLQueries.SELECT_ТипыЛицензирования)).ToList();

    /// <summary>Проверяет роль 44 по владельцу/арендатору и сохраняет лицензию.</summary>
    public virtual async Task SaveLicenseAsync(EquipmentLicense license, LicenseType? type)
    {
        await EnsureLicenseAccessAsync(license.EquipmentId);
        var validation = license.Validate(type);
        if (validation is not null)
            throw new InvalidOperationException(validation);
        await license.SaveAsync(_db);
    }

    /// <summary>Проверяет роль текущего пользователя по конкретному лицу через Employee.</summary>
    public virtual async Task<bool> HasRoleForPersonAsync(int roleId, int? personId) =>
        await Employee.HasRoleAsync(_db, await GetWindowsPrincipalAsync(), (Роли)roleId, personId);

    /// <summary>Загружает историю сетевых имён от новых записей к старым.</summary>
    public virtual async Task<IReadOnlyList<EquipmentNetworkNameHistory>> GetNetworkNameHistoryAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentNetworkNameHistory>(
            SQLQueries.SELECT_ОборудованиеСетевыеИмена, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Загружает историю дневных конфигураций компьютера.</summary>
    public virtual async Task<IReadOnlyList<EquipmentComputerSnapshot>> GetComputerHistoryAsync(int equipmentId)
    {
        var history = (await _db.QueryRequiredAsync<EquipmentComputerSnapshot>(
            SQLQueries.SELECT_Компьютеры_КодОборудования, new { EquipmentId = equipmentId })).ToList();
        foreach (var snapshot in history)
            snapshot.ParseDetails();
        return history;
    }

    /// <summary>Загружает стандартные конфигурации.</summary>
    public virtual async Task<IReadOnlyList<StandardConfiguration>> GetStandardConfigurationsAsync() =>
        (await _db.QueryRequiredAsync<StandardConfiguration>(SQLQueries.SELECT_СтандартныеКонфигурации)).ToList();

    /// <summary>Проверяет роль 43, фактическую модель и business Today до dispatch дневного снимка.</summary>
    public virtual Task SaveComputerAsync(EquipmentComputerSnapshot snapshot, DateTime? today = null) =>
        SaveComputerCoreAsync(snapshot, today);

    /// <summary>Сохраняет дневной снимок с phase-aware ошибкой до или после DML dispatch.</summary>
    public virtual async Task SaveComputerPhasedAsync(EquipmentComputerSnapshot snapshot, DateTime? businessToday) =>
        _ = await RunPhasedAsync(async dispatch =>
        {
            await SaveComputerCoreAsync(snapshot, businessToday, dispatch);
            return true;
        });

    private async Task SaveComputerCoreAsync(
        EquipmentComputerSnapshot snapshot,
        DateTime? businessToday,
        PhasedDispatch? dispatch = null)
    {
        _logger?.LogInformation("ComputerSave start EquipmentId={EquipmentId}, Date={Date}, ChangedAt={ChangedAt}, Configuration1Date={Configuration1Date}, Configuration2Date={Configuration2Date}, Ram={RamCount}, Hd={HdCount}, Ssd={SsdCount}, ExpectedExisting={ExpectedExisting}",
            snapshot.EquipmentId, snapshot.Date, snapshot.ChangedAt, snapshot.Configuration1Date, snapshot.Configuration2Date,
            snapshot.RamComponents.Count, snapshot.HdComponents.Count, snapshot.SsdComponents.Count, snapshot.ExpectedExisting);
        await EnsureInventoryEditAsync();
        var context = (await _db.QueryRequiredAsync<ComputerModelContext>(
            SQLQueries.SELECT_КомпьютерКонтекстСохранения, new { snapshot.EquipmentId })).SingleOrDefault()
            ?? throw new InvalidOperationException("Оборудование или его модель не найдены. Обновите данные.");
        if (context.HasComputerCharacteristics == 0)
            throw new InvalidOperationException("Конфигурация компьютера недоступна для фактического типа оборудования.");
        if (businessToday is null)
            throw new InvalidOperationException("Актуальная календарная дата клиента не задана. Сохранение конфигурации отменено.");
        var validation = snapshot.ValidateConfiguration(businessToday.Value, context.MemoryBanks);
        if (validation is not null)
            throw new InvalidOperationException(validation);
        _logger?.LogInformation("ComputerSave validated EquipmentId={EquipmentId}, Dispatch={Dispatch}", snapshot.EquipmentId, dispatch is not null);
        if (dispatch is null)
            await snapshot.SaveAsync(_db);
        else
            await dispatch(() => snapshot.SaveAsync(_db));
        _logger?.LogInformation("ComputerSave completed EquipmentId={EquipmentId}", snapshot.EquipmentId);
    }

    /// <summary>Совместимый вход сохранения; новый phased вход — SaveWithPhonesAsync.</summary>
    public virtual async Task UpdateWithPhonesAsync(
        Equipment equipment,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current) =>
        _ = await SaveWithPhonesCoreAsync(equipment, original, current);

    /// <summary>Возвращает trusted intent и phase-aware ошибку до или после фактического DML dispatch.</summary>
    public virtual async Task<EquipmentPhoneSyncIntent> SaveWithPhonesAsync(
        Equipment equipment,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current) =>
        await RunPhasedAsync(async dispatch =>
        {
            await EnsureInventoryEditAsync();
            await ValidateEquipmentForSaveAsync(equipment);
            await dispatch(() => equipment.UpdateWithPhonesAsync(_db, original, current));
            var intent = CreatePhoneIntent(equipment.Id, EquipmentPhoneSyncOrigin.EquipmentSave,
                equipment.ChangedAtUtc, equipment.OwnerId, equipment.EquipmentTypeId, original, current);
            _confirmedPhoneIntents[intent.AttemptId] = intent;
            return intent;
        });

    private async Task<EquipmentPhoneSyncIntent> SaveWithPhonesCoreAsync(
        Equipment equipment,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current)
    {
        await EnsureInventoryEditAsync();
        await ValidateEquipmentForSaveAsync(equipment);
        await equipment.UpdateWithPhonesAsync(_db, original, current);
        var intent = CreatePhoneIntent(equipment.Id, EquipmentPhoneSyncOrigin.EquipmentSave,
            equipment.ChangedAtUtc, equipment.OwnerId, equipment.EquipmentTypeId, original, current);
        _confirmedPhoneIntents[intent.AttemptId] = intent;
        return intent;
    }

    /// <summary>Настраивает сохранённый IP-телефон по актуальным связям без повторного SQL UPDATE.</summary>
    public virtual async Task<IReadOnlyList<PhoneSyncResult>> ConfigurePhoneEquipmentAsync(int equipmentId)
    {
        try
        {
            return ToLegacyResults(await ConfigurePhoneEquipmentAttemptAsync(equipmentId));
        }
        catch (EquipmentOperationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    /// <summary>Создаёт либо продолжает explicit Configure attempt с owner/global43 и IP-phone scope.</summary>
    public virtual async Task<PhoneSyncAttempt> ConfigurePhoneEquipmentAttemptAsync(int equipmentId,
        Func<PhoneSyncAttempt, Task>? checkpoint = null)
    {
        var pending = _phoneSyncAttempts.Values.LastOrDefault(item =>
            item.Intent.EquipmentId == equipmentId && item.Intent.Origin == EquipmentPhoneSyncOrigin.ExplicitConfigure &&
            (item.PlanningError is not null || item.Operations.Any(operation => operation.IsUnknown || operation.CanRetry)));
        if (pending is not null)
            return await RetryPhoneSyncAttemptAsync(pending);

        var intent = await RunPhasedAsync(async _ =>
        {
            var equipment = await GetAsync(equipmentId) ??
                            throw new InvalidOperationException("Оборудование не найдено.");
            EnsureExplicitConfigureScope(equipment);
            if (!await HasRoleForPersonAsync(Роли.Специалист_по_инвентаризации_оборудования, equipment.OwnerId))
                throw new UnauthorizedAccessException("Недостаточно прав для настройки оборудования владельца.");
            var current = await GetPhonesAsync(equipmentId);
            return CreatePhoneIntent(equipmentId, EquipmentPhoneSyncOrigin.ExplicitConfigure,
                equipment.ChangedAtUtc, equipment.OwnerId, equipment.EquipmentTypeId, [], current);
        });
        _confirmedPhoneIntents[intent.AttemptId] = intent;
        return await SynchronizePhoneAttemptCoreAsync(intent, validateContext: false, checkpoint);
    }

    /// <summary>Синхронизирует изменённые номера и оборудование с подтверждёнными АТС.</summary>
    public virtual async Task<IReadOnlyList<string>> SynchronizePhonesAsync(
        int equipmentId,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current)
    {
        var results = await SynchronizePhoneOperationsAsync(equipmentId, original, current);
        return results.Where(result => !result.Success).Select(result => result.Error!).ToList();
    }

    /// <summary>Использует только diff, подтверждённый атомарным Save в этом серверном контексте.</summary>
    public virtual async Task<IReadOnlyList<PhoneSyncResult>> SynchronizePhoneOperationsAsync(
        int equipmentId,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current)
    {
        try
        {
            var intent = GetConfirmedIntent(equipmentId, EquipmentPhoneSyncOrigin.EquipmentSave, original, current);
            var attempt = _phoneSyncAttempts.TryGetValue(intent.AttemptId, out var pending)
                ? await RetryPhoneSyncAttemptAsync(pending)
                : await SynchronizePhoneAttemptAsync(intent);
            return ToLegacyResults(attempt);
        }
        catch (EquipmentOperationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    /// <summary>Планирует и выполняет ATS operations для подтверждённого trusted intent.</summary>
    public virtual async Task<PhoneSyncAttempt> SynchronizePhoneAttemptAsync(EquipmentPhoneSyncIntent intent,
        Func<PhoneSyncAttempt, Task>? checkpoint = null) =>
        await SynchronizePhoneAttemptCoreAsync(intent, validateContext: true, checkpoint);

    private async Task<PhoneSyncAttempt> SynchronizePhoneAttemptCoreAsync(
        EquipmentPhoneSyncIntent intent,
        bool validateContext,
        Func<PhoneSyncAttempt, Task>? checkpoint)
    {
        if (validateContext)
            _ = await RunPhasedAsync(async _ =>
            {
                await ValidateTrustedIntentAsync(intent);
                return true;
            });
        if (_phoneSyncAttempts.TryGetValue(intent.AttemptId, out var existing))
            return await RetryPhoneSyncAttemptAsync(existing, checkpoint);

        return await PlanPhoneSyncAttemptAsync(intent, previous: null, checkpoint);
    }

    private async Task<PhoneSyncAttempt> PlanPhoneSyncAttemptAsync(
        EquipmentPhoneSyncIntent intent, PhoneSyncAttempt? previous,
        Func<PhoneSyncAttempt, Task>? checkpoint)
    {
        if (!_phoneSyncPlanningGate.Wait(0))
            throw new InvalidOperationException("Дождитесь завершения планирования синхронизации АТС.");
        try
        {
            if (_phoneSyncAttempts.TryGetValue(intent.AttemptId, out var current) && !ReferenceEquals(current, previous))
                throw new InvalidOperationException("Состояние планирования АТС изменилось. Повтор не отправлен.");
            return await PlanAndDispatchPhoneSyncAsync(intent, checkpoint);
        }
        finally { _phoneSyncPlanningGate.Release(); }
    }

    private async Task<PhoneSyncAttempt> PlanAndDispatchPhoneSyncAsync(EquipmentPhoneSyncIntent intent,
        Func<PhoneSyncAttempt, Task>? checkpoint)
    {
        PhoneSyncAttempt Complete(IReadOnlyCollection<PhoneSyncOperationResult> results, string? planningError = null)
        {
            var attempt = new PhoneSyncAttempt(intent, results) { PlanningError = planningError };
            _phoneSyncAttempts[intent.AttemptId] = attempt;
            return attempt;
        }

        var pendingAttempt = Complete([]);
        var currentIds = intent.Current.Select(item => item.PhoneId).ToHashSet();
        var originalIds = intent.Original.Select(item => item.PhoneId).ToHashSet();
        var originalById = intent.Original.ToDictionary(item => item.PhoneId);
        var added = intent.Current.Where(item => !originalIds.Contains(item.PhoneId)).ToList();
        var deleted = intent.Original.Where(item => !currentIds.Contains(item.PhoneId)).ToList();
        var changed = intent.Current.Where(item => originalById.TryGetValue(item.PhoneId, out var source) &&
            HasSyncChanges(source, item)).ToList();
        if (added.Count == 0 && deleted.Count == 0 && changed.Count == 0)
            return pendingAttempt;

        var phones = deleted.Select(item => (item.PhoneId, Deleted: true))
            .Concat(added.Concat(changed).Select(item => (item.PhoneId, Deleted: false)))
            .OrderBy(item => item.PhoneId);
        var changedIds = changed.Select(item => item.PhoneId).ToHashSet();
        var equipmentIds = deleted.Concat(intent.Original.Where(item => changedIds.Contains(item.PhoneId)))
            .Select(item => item.ConnectionEquipmentId)
            .Concat(added.Concat(changed).Select(item => item.ConnectionEquipmentId))
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (equipmentIds.Count == 0)
            equipmentIds.Add(intent.EquipmentId);
        var operations = new List<PhoneSyncOperation>();
        var needsCcm = false;
        var hasXorcom = false;
        foreach (var phone in phones)
        {
            PhoneNumberLookup? lookup;
            try
            {
                lookup = (await _db.QueryRequiredAsync<PhoneNumberLookup>(
                    SQLQueries.SELECT_ТелефонныйНомерПоИдентификатору, new { Id = phone.PhoneId })).SingleOrDefault();
            }
            catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
            {
                return Complete([], $"Не удалось определить АТС номера #{phone.PhoneId}. Команды АТС не отправлены.");
            }

            if (lookup is null)
                return Complete([], $"Телефонный номер #{phone.PhoneId} не найден для планирования. Команды АТС не отправлены.");

            needsCcm |= lookup.StationId == 0;
            if (string.IsNullOrWhiteSpace(lookup.XorcomName))
                continue;
            hasXorcom = true;
            operations.Add(new PhoneSyncOperation(PhoneSyncKind.Xorcom, phone.PhoneId.ToString(), phone.Deleted));
        }
        if (needsCcm && !hasXorcom)
            operations.AddRange(equipmentIds.Select(id => new PhoneSyncOperation(PhoneSyncKind.Ccm, id.ToString())));
        EnsureOperationsBelongToDiff(intent.EquipmentId, intent.Original, intent.Current, operations);
        return await ExecutePhoneSyncOperationsCoreAsync(intent, operations, checkpoint);
    }

    /// <summary>Перепланирует попытку без dispatch либо повторяет подтверждённые Failed/NotSent операции без основного UPDATE.</summary>
    public virtual async Task<PhoneSyncAttempt> RetryPhoneSyncAttemptAsync(PhoneSyncAttempt attempt,
        Func<PhoneSyncAttempt, Task>? checkpoint = null)
    {
        if (_phoneSyncPlanningGate.CurrentCount == 0)
            throw new InvalidOperationException("Дождитесь завершения планирования синхронизации АТС.");
        if (!_phoneSyncAttempts.TryGetValue(attempt.Intent.AttemptId, out var current) ||
            !IntentMatches(current.Intent, attempt.Intent) || current.PlanningError != attempt.PlanningError ||
            !OperationResultsMatch(current.Operations, attempt.Operations))
            throw new UnauthorizedAccessException("Контекст синхронизации не подтверждён текущим серверным контекстом.");
        _ = await RunPhasedAsync(async _ =>
        {
            await ValidateTrustedIntentAsync(current.Intent);
            return true;
        });
        if (current.PlanningError is not null)
            return await PlanPhoneSyncAttemptAsync(current.Intent, current, checkpoint);

        var retryOperations = current.Operations.Where(item => item.CanRetry).Select(item => item.Operation).ToList();
        if (retryOperations.Count == 0)
            return current;
        EnsureOperationsBelongToDiff(current.Intent.EquipmentId, current.Intent.Original, current.Intent.Current, retryOperations);
        if (!_phoneSyncPlanningGate.Wait(0))
            throw new InvalidOperationException("Подождите завершения синхронизации АТС.");
        try
        {
            if (!_phoneSyncAttempts.TryGetValue(current.AttemptId, out var live) || !ReferenceEquals(live, current))
                throw new InvalidOperationException("Состояние операции АТС изменилось. Команда не отправлена.");
            return await ExecutePhoneSyncOperationsCoreAsync(current.Intent, retryOperations, checkpoint, current.Operations);
        }
        finally { _phoneSyncPlanningGate.Release(); }
    }

    /// <summary>Только читает фактическое состояние; не изменяет outcomes и не выполняет DML/ATS replay.</summary>
    public virtual async Task<PhoneSyncReconciliation> ReconcilePhoneSyncAttemptAsync(PhoneSyncAttempt attempt)
    {
        if (!_phoneSyncAttempts.TryGetValue(attempt.Intent.AttemptId, out var current) ||
            !IntentMatches(current.Intent, attempt.Intent) || current.PlanningError != attempt.PlanningError ||
            !OperationResultsMatch(current.Operations, attempt.Operations))
            throw new UnauthorizedAccessException("Контекст сверки не подтверждён текущим серверным контекстом.");
        var equipment = await GetAsync(current.Intent.EquipmentId);
        if (equipment is null)
            return new PhoneSyncReconciliation(current, null, [], false);
        if (current.Intent.Origin == EquipmentPhoneSyncOrigin.ExplicitConfigure)
        {
            EnsureExplicitConfigureScope(equipment);
            if (!await HasRoleForPersonAsync(Роли.Специалист_по_инвентаризации_оборудования, equipment.OwnerId))
                throw new UnauthorizedAccessException("Недостаточно прав для сверки оборудования владельца.");
        }
        else
        {
            await EnsureInventoryEditAsync();
        }
        var actual = await GetPhonesAsync(current.Intent.EquipmentId);
        var actualById = actual.ToDictionary(item => item.PhoneId);
        var currentIds = current.Intent.Current.Select(item => item.PhoneId).ToHashSet();
        var matches = actual.Count == current.Intent.Current.Count && current.Intent.Current.All(expected =>
            actualById.TryGetValue(expected.PhoneId, out var persisted) && !HasSyncChanges(expected, persisted));
        foreach (var deleted in current.Intent.Original.Where(item => !currentIds.Contains(item.PhoneId)))
        {
            var assignments = await _db.QueryRequiredAsync<int>(
                SQLQueries.SELECT_ТелефонныйНомерАктуальныеПривязки, new { PhoneId = deleted.PhoneId });
            matches &= !assignments.Any();
        }
        if (current.Intent.Origin == EquipmentPhoneSyncOrigin.ExplicitConfigure)
            matches &= equipment.OwnerId == current.Intent.OwnerId &&
                       equipment.EquipmentTypeId == current.Intent.EquipmentTypeId &&
                       equipment.ChangedAtUtc == current.Intent.ExpectedChangedAtUtc;
        return new PhoneSyncReconciliation(current, equipment, actual, matches);
    }

    /// <summary>Совместимый ready-plan retry: операции должны принадлежать сохранённому attempt и быть retryable.</summary>
    public virtual async Task<IReadOnlyList<PhoneSyncResult>> ExecutePhoneSyncOperationsAsync(
        int equipmentId,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current,
        IEnumerable<PhoneSyncOperation> operations)
    {
        try
        {
            var intent = GetConfirmedIntent(equipmentId, EquipmentPhoneSyncOrigin.EquipmentSave, original, current);
            if (!_phoneSyncAttempts.TryGetValue(intent.AttemptId, out var attempt))
                throw new UnauthorizedAccessException("Повтор возможен только для зарегистрированного ATS attempt.");
            if (attempt.PlanningError is { } planningError)
                throw new InvalidOperationException(planningError);
            var requested = operations.Distinct().ToHashSet();
            var allowed = attempt.Operations.Where(item => item.CanRetry).Select(item => item.Operation).ToHashSet();
            if (!requested.SetEquals(allowed))
                throw new UnauthorizedAccessException("Операции не соответствуют подтверждённым неуспешным операциям attempt.");
            var completed = await RetryPhoneSyncAttemptAsync(attempt);
            return ToLegacyResults(completed.Operations.Where(item => requested.Contains(item.Operation)));
        }
        catch (EquipmentOperationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private async Task ValidateTrustedIntentAsync(EquipmentPhoneSyncIntent intent)
    {
        if (!_confirmedPhoneIntents.TryGetValue(intent.AttemptId, out var confirmed) || !IntentMatches(confirmed, intent))
            throw new UnauthorizedAccessException("ATS intent не подтверждён сохранением в текущем серверном контексте.");
        var equipment = await GetAsync(intent.EquipmentId) ??
                        throw new InvalidOperationException("Оборудование не найдено. Повтор синхронизации отменён.");
        if (intent.Origin == EquipmentPhoneSyncOrigin.ExplicitConfigure)
        {
            EnsureExplicitConfigureScope(equipment);
            if (equipment.OwnerId != intent.OwnerId || equipment.ChangedAtUtc != intent.ExpectedChangedAtUtc)
                throw new InvalidOperationException("Владелец, тип или версия оборудования изменились. Повтор настройки отменён.");
            if (!await HasRoleForPersonAsync(Роли.Специалист_по_инвентаризации_оборудования, equipment.OwnerId))
                throw new UnauthorizedAccessException("Недостаточно прав для настройки оборудования владельца.");
        }
        else
        {
            await EnsureInventoryEditAsync();
        }
        await EnsurePhoneSyncContextCurrentAsync(intent);
    }

    private static void EnsureExplicitConfigureScope(Equipment equipment)
    {
        if (equipment.EquipmentTypeId != (int)ТипыОборудования.IP_телефон)
            throw new InvalidOperationException("Команда доступна только для сохранённого IP-телефона.");
    }

    private async Task EnsurePhoneSyncContextCurrentAsync(EquipmentPhoneSyncIntent intent)
    {
        var actual = (await GetPhonesAsync(intent.EquipmentId)).ToDictionary(item => item.PhoneId);
        var currentIds = intent.Current.Select(item => item.PhoneId).ToHashSet();
        if (intent.Original.Any(item => !currentIds.Contains(item.PhoneId) && actual.ContainsKey(item.PhoneId)))
            throw new InvalidOperationException("Удалённая телефонная связь была создана повторно. Повтор синхронизации отменён.");
        foreach (var deleted in intent.Original.Where(item => !currentIds.Contains(item.PhoneId) && !actual.ContainsKey(item.PhoneId)))
        {
            var assignments = await _db.QueryRequiredAsync<int>(
                SQLQueries.SELECT_ТелефонныйНомерАктуальныеПривязки, new { PhoneId = deleted.PhoneId });
            if (assignments.Any())
                throw new InvalidOperationException("Удалённый номер уже привязан к другому оборудованию. Повтор синхронизации отменён.");
        }
        foreach (var expected in intent.Current)
        {
            if (!actual.TryGetValue(expected.PhoneId, out var persisted) || HasSyncChanges(expected, persisted))
                throw new InvalidOperationException("Телефонные связи изменились в другой сессии. Повтор синхронизации отменён.");
        }
    }

    private EquipmentPhoneSyncIntent GetConfirmedIntent(
        int equipmentId,
        EquipmentPhoneSyncOrigin origin,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current)
    {
        var originalFingerprint = PhoneSyncFingerprint(original);
        var currentFingerprint = PhoneSyncFingerprint(current);
        var intent = _confirmedPhoneIntents.Values.LastOrDefault(item =>
            item.EquipmentId == equipmentId && item.Origin == origin &&
            PhoneSyncFingerprint(item.Original) == originalFingerprint &&
            PhoneSyncFingerprint(item.Current) == currentFingerprint);
        return intent ?? throw new UnauthorizedAccessException(
            "Синхронизация допускается только для diff, подтверждённого атомарным сохранением в текущем серверном контексте.");
    }

    private static EquipmentPhoneSyncIntent CreatePhoneIntent(
        int equipmentId,
        EquipmentPhoneSyncOrigin origin,
        DateTime expectedChangedAtUtc,
        int? ownerId,
        int? equipmentTypeId,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current) =>
        new(Guid.NewGuid().ToString("N"), equipmentId, origin, expectedChangedAtUtc, ownerId, equipmentTypeId,
            original.Select(ClonePhone).ToArray(), current.Select(ClonePhone).ToArray());

    private static EquipmentPhoneLink ClonePhone(EquipmentPhoneLink item) => new()
    {
        PhoneId = item.PhoneId,
        EquipmentId = item.EquipmentId,
        Description = item.Description,
        IsDefault = item.IsDefault,
        ConnectionEquipmentId = item.ConnectionEquipmentId,
        AutoDialNumber = item.AutoDialNumber,
        AutoDialDelay = item.AutoDialDelay,
        ReturnDate = item.ReturnDate,
        Notes = item.Notes,
        ChangedByName = item.ChangedByName,
        ChangedAt = item.ChangedAt,
    };

    private static bool IntentMatches(EquipmentPhoneSyncIntent left, EquipmentPhoneSyncIntent right) =>
        left.AttemptId == right.AttemptId && left.EquipmentId == right.EquipmentId && left.Origin == right.Origin &&
        left.ExpectedChangedAtUtc == right.ExpectedChangedAtUtc && left.OwnerId == right.OwnerId &&
        left.EquipmentTypeId == right.EquipmentTypeId &&
        PhoneSyncFingerprint(left.Original) == PhoneSyncFingerprint(right.Original) &&
        PhoneSyncFingerprint(left.Current) == PhoneSyncFingerprint(right.Current);

    private static bool OperationResultsMatch(
        IReadOnlyCollection<PhoneSyncOperationResult> left,
        IReadOnlyCollection<PhoneSyncOperationResult> right) =>
        left.Count == right.Count && left.OrderBy(item => item.Operation.Kind).ThenBy(item => item.Operation.Key)
            .ThenBy(item => item.Operation.Deleting).SequenceEqual(
                right.OrderBy(item => item.Operation.Kind).ThenBy(item => item.Operation.Key)
                    .ThenBy(item => item.Operation.Deleting));

    private static List<PhoneSyncOperationResult> OrderOperationResults(IEnumerable<PhoneSyncOperationResult> results) =>
        results.OrderBy(item => item.Operation.Kind).ThenBy(item => item.Operation.Key, StringComparer.Ordinal)
            .ThenBy(item => item.Operation.Deleting).ToList();

    private static void EnsureOperationsBelongToDiff(
        int equipmentId,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current,
        IEnumerable<PhoneSyncOperation> operations)
    {
        var originalById = original.ToDictionary(item => item.PhoneId);
        var currentById = current.ToDictionary(item => item.PhoneId);
        var affectedIds = originalById.Keys.Concat(currentById.Keys).Where(phoneId =>
        {
            var hasOriginal = originalById.TryGetValue(phoneId, out var before);
            var hasCurrent = currentById.TryGetValue(phoneId, out var after);
            return hasOriginal != hasCurrent || hasOriginal && hasCurrent && HasSyncChanges(before!, after!);
        }).ToHashSet();
        var affected = original.Concat(current).Where(item => affectedIds.Contains(item.PhoneId)).ToList();
        var equipmentIds = affected.Select(item => item.ConnectionEquipmentId).Where(id => id is > 0)
            .Select(id => id!.Value).Append(equipmentId).ToHashSet();
        foreach (var operation in operations.Distinct())
        {
            if (operation.Kind == PhoneSyncKind.Ccm &&
                int.TryParse(operation.Key, out var targetId) && equipmentIds.Contains(targetId))
                continue;
            if (operation.Kind == PhoneSyncKind.Xorcom && int.TryParse(operation.Key, out var phoneId) &&
                affectedIds.Contains(phoneId) &&
                operation.Deleting == (originalById.ContainsKey(phoneId) && !currentById.ContainsKey(phoneId)))
                continue;
            throw new UnauthorizedAccessException("Операция АТС не соответствует подтверждённому изменению оборудования.");
        }
    }

    private static bool HasSyncChanges(EquipmentPhoneLink source, EquipmentPhoneLink target) =>
        source.ConnectionEquipmentId != target.ConnectionEquipmentId ||
        source.AutoDialNumber != target.AutoDialNumber ||
        source.AutoDialDelay != target.AutoDialDelay ||
        source.IsDefault != target.IsDefault;

    private static string PhoneSyncFingerprint(IEnumerable<EquipmentPhoneLink> links) =>
        string.Join(";", links.OrderBy(item => item.PhoneId).Select(item =>
            $"{item.EquipmentId}:{item.PhoneId}:{item.ConnectionEquipmentId}:{item.AutoDialNumber}:{item.AutoDialDelay}:" +
            $"{item.IsDefault}:{item.ReturnDate?.Ticks}:{item.Notes}"));

    private async Task<PhoneSyncAttempt> ExecutePhoneSyncOperationsCoreAsync(
        EquipmentPhoneSyncIntent intent,
        IEnumerable<PhoneSyncOperation> operations,
        Func<PhoneSyncAttempt, Task>? checkpoint,
        IReadOnlyCollection<PhoneSyncOperationResult>? previous = null)
    {
        var plan = operations.Distinct().ToArray();
        var results = previous?.ToDictionary(item => item.Operation) ?? [];
        foreach (var operation in plan)
            results.TryAdd(operation, new PhoneSyncOperationResult(intent.AttemptId, operation, PhoneSyncPhase.NotSent));

        PhoneSyncAttempt Snapshot() => new(intent, OrderOperationResults(results.Values));
        var service = new PhoneFormService(_db);
        foreach (var operation in plan)
        {
            // Until a response is stored, the durable plan cannot attest that dispatch did not happen.
            results[operation] = new PhoneSyncOperationResult(intent.AttemptId, operation, PhoneSyncPhase.Unknown,
                "Команда АТС подготовлена к отправке. Подтверждённого ответа пока нет.");
            var sending = Snapshot();
            if (checkpoint is not null)
                await checkpoint(sending);
            _phoneSyncAttempts[intent.AttemptId] = sending;

            var phoneId = operation.Kind == PhoneSyncKind.Xorcom ? int.Parse(operation.Key) : 0;
            var outcome = await service.SynchronizePhasedAsync(intent.AttemptId, phoneId, [operation]);
            results[operation] = outcome.Single();
            var completed = Snapshot();
            _phoneSyncAttempts[intent.AttemptId] = completed;
            if (checkpoint is not null)
                await checkpoint(completed);
        }
        var final = Snapshot();
        _phoneSyncAttempts[intent.AttemptId] = final;
        return final;
    }

    private static IReadOnlyList<PhoneSyncResult> ToLegacyResults(PhoneSyncAttempt attempt)
    {
        if (attempt.PlanningError is { } error)
            throw new InvalidOperationException(error);
        return ToLegacyResults(attempt.Operations);
    }

    private static IReadOnlyList<PhoneSyncResult> ToLegacyResults(IEnumerable<PhoneSyncOperationResult> results) =>
        results.Select(item => new PhoneSyncResult(item.Operation, item.Error,
            item.Phase == PhoneSyncPhase.Unknown ? PhoneSyncOutcome.Unknown :
            item.Phase == PhoneSyncPhase.Succeeded ? PhoneSyncOutcome.Succeeded : PhoneSyncOutcome.Failed)).ToList();

    private sealed class ComputerModelContext
    {
        /// <summary>Фактическая модель оборудования.</summary>
        public int ModelId { get; set; }
        /// <summary>Фактический тип модели.</summary>
        public int EquipmentTypeId { get; set; }
        /// <summary>Признак характеристик компьютера.</summary>
        public byte HasComputerCharacteristics { get; set; }
        /// <summary>Число банков памяти фактической модели.</summary>
        public int? MemoryBanks { get; set; }
    }

    private async Task ValidateEquipmentForSaveAsync(Equipment equipment)
    {
        var errors = new List<string>();
        if (equipment.Id <= 0)
            errors.Add("Не указан идентификатор редактируемого оборудования.");
        if (equipment.EquipmentTypeId is null || equipment.ModelId is null)
            errors.Add("Выберите тип и модель оборудования.");
        if (equipment.OwnerId is null)
            errors.Add("Укажите владельца оборудования.");
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));

        var type = (await _db.QueryRequiredAsync<EquipmentType>(
            SQLQueries.SELECT_МодельОборудованияТипДляСохранения,
            new { ModelId = equipment.ModelId.GetValueOrDefault() })).SingleOrDefault()
            ?? throw new InvalidOperationException("Модель оборудования не найдена.");
        if (type.Id != equipment.EquipmentTypeId)
            throw new InvalidOperationException("Выбранная модель не соответствует типу оборудования. Обновите модель и повторите операцию.");

        var hasWriteOff = equipment.WriteOffAtUtc is not null && equipment.WriteOffAtUtc != DateTime.MinValue;
        if (equipment.DocumentWriteOffId is not null && !hasWriteOff)
            errors.Add("Укажите дату списания.");
        if (type.Id != (int)ТипыОборудования.SIM_карта &&
            hasWriteOff && equipment.DocumentWriteOffId is null)
            errors.Add("Укажите документ списания.");
        if (type.IsSerialNumberRequired > 0 && string.IsNullOrWhiteSpace(equipment.SerialNumber))
            errors.Add("Укажите серийный номер.");
        if (type.MacAddressCount > 0 &&
            (!Equipment.IsValidMacAddress(equipment.MacAddress) ||
             !Equipment.IsValidMacAddress(equipment.MacAddress2) ||
             !Equipment.IsValidMacAddress(equipment.MacAddressIlo)))
            errors.Add("MAC-адрес должен содержать 12 hex-символов; допустимы разделители пробел, двоеточие, дефис и точка.");
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));

        if (type.MacAddressCount > 0)
        {
            equipment.MacAddress = Equipment.NormalizeMacAddress(equipment.MacAddress);
            equipment.MacAddress2 = Equipment.NormalizeMacAddress(equipment.MacAddress2);
            equipment.MacAddressIlo = Equipment.NormalizeMacAddress(equipment.MacAddressIlo);
        }
    }

    private async Task EnsureInventoryEditAsync()
    {
        if (!await HasRoleAsync(Роли.Специалист_по_инвентаризации_оборудования))
            throw new UnauthorizedAccessException("Недостаточно прав для изменения оборудования.");
    }

    private async Task EnsureAccountAccessAsync(bool write)
    {
        if (!await HasGlobalRoleAsync(Роли.Дежурный_инженер) ||
            write && !await HasRoleAsync(Роли.Специалист_по_инвентаризации_оборудования))
            throw new UnauthorizedAccessException("Недостаточно прав для работы с учётными записями оборудования.");
    }

    private async Task<EquipmentType> GetRequiredChildWriteTypeAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentType>(
            SQLQueries.SELECT_ОборудованиеТипДляДочернихЗаписей, new { EquipmentId = equipmentId })).SingleOrDefault()
        ?? throw new InvalidOperationException("Оборудование или его модель не найдены. Обновите данные.");

    private async Task<EquipmentType> GetRequiredTypeAsync(Equipment equipment)
    {
        if (equipment.EquipmentTypeId is null)
            throw new InvalidOperationException("Для оборудования не указан тип.");
        return (await _db.QueryRequiredAsync<EquipmentType>(SQLQueries.SELECT_ТипОборудованияПоИдентификатору,
            new { Id = equipment.EquipmentTypeId.Value })).SingleOrDefault()
            ?? throw new InvalidOperationException("Тип оборудования не найден.");
    }

    private async Task EnsureNetworkAccessAsync(int equipmentId)
    {
        var equipment = await GetAsync(equipmentId) ??
                        throw new InvalidOperationException("Оборудование не найдено.");
        var type = await GetRequiredTypeAsync(equipment);
        if (type.HasComputerCharacteristics == 0 ||
            !await HasGlobalRoleAsync(Роли.Исполнитель_заявок_службы_поддержки_пользователей))
            throw new UnauthorizedAccessException("Недостаточно прав для изменения подключения рабочей сети.");
    }

    private async Task EnsureUpsAccessAsync(int serverEquipmentId)
    {
        var equipment = await GetAsync(serverEquipmentId) ??
                        throw new InvalidOperationException("Оборудование не найдено.");
        var type = await GetRequiredTypeAsync(equipment);
        if (equipment.EquipmentTypeId is not < 100 || type.HasSimCharacteristics != 0 ||
            !await HasRoleForPersonAsync(Роли.Специалист_по_инвентаризации_оборудования, equipment.OwnerId))
            throw new UnauthorizedAccessException("Недостаточно прав для изменения подключений UPS.");
    }

    private async Task EnsureLicenseAccessAsync(int equipmentId)
    {
        var equipment = await GetAsync(equipmentId) ??
                        throw new InvalidOperationException("Оборудование не найдено.");
        var type = await GetRequiredTypeAsync(equipment);
        var hasRole = await HasRoleForPersonAsync(Роли.Специалист_по_инвентаризации_по, equipment.OwnerId) ||
                      equipment.TenantId is not null && equipment.TenantId != equipment.OwnerId &&
                      await HasRoleForPersonAsync(Роли.Специалист_по_инвентаризации_по, equipment.TenantId);
        if (type.HasSoftwareCharacteristics == 0 || !hasRole)
            throw new UnauthorizedAccessException("Недостаточно прав для изменения лицензии.");
    }

    private async Task<T?> ExecuteRequiredScalarAsync<T>(string sql, object? parameters, CommandType commandType) =>
        (await _db.QueryRequiredAsync<T>(sql, parameters)).SingleOrDefault();

    private delegate Task PhasedDispatch(Func<Task> write);

    private static async Task<T> RunPhasedAsync<T>(Func<PhasedDispatch, Task<T>> operation)
    {
        var dispatched = false;
        async Task Dispatch(Func<Task> write)
        {
            dispatched = true;
            await write();
        }
        try
        {
            return await operation(Dispatch);
        }
        catch (EquipmentOperationException)
        {
            throw;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            throw new EquipmentOperationException(
                dispatched ? EquipmentWritePhase.Unknown : EquipmentWritePhase.NotSent, ex.Message, ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            throw new EquipmentOperationException(
                dispatched ? EquipmentWritePhase.Unknown : EquipmentWritePhase.Rejected, ex.Message, ex);
        }
    }

    /// <summary>Ожидает post-commit refresh и не превращает его сбой в разрешение повторной записи.</summary>
    public static async Task<EquipmentWriteResult<T>> CompleteCommittedAsync<T>(T value, Func<Task> refresh)
    {
        try
        {
            await refresh();
            return new EquipmentWriteResult<T>(value, EquipmentWritePhase.Committed);
        }
        catch (Exception ex)
        {
            return new EquipmentWriteResult<T>(value, EquipmentWritePhase.RefreshFailed, ex.Message);
        }
    }

    /// <summary>Получает текущего сотрудника после подтверждения Windows/SQL principal.</summary>
    public virtual async Task<Employee?> GetCurrentUserAsync() =>
        await Employee.GetCurrentAsync(_db, await GetWindowsPrincipalAsync());

    /// <summary>Получает текущего сотрудника со снимком effective roles для отображения формы.</summary>
    public virtual async Task<Employee?> GetCurrentUserWithRolesAsync() =>
        await Employee.GetCurrentWithRolesAsync(_db, await GetWindowsPrincipalAsync());

    private async Task EnsureTrustedIdentityAsync()
    {
        if (await GetCurrentUserAsync() is null)
            throw new UnauthorizedAccessException("Не удалось подтвердить текущего сотрудника.");
    }

    private Task<string?> GetWindowsPrincipalAsync() =>
        _principalProvider?.GetWindowsPrincipalAsync() ?? Task.FromResult<string?>(null);

    /// <summary>Проверяет роль текущего пользователя с учётом замещений через Employee.</summary>
    public virtual async Task<bool> HasRoleAsync(Роли role) =>
        await Employee.HasRoleAsync(_db, await GetWindowsPrincipalAsync(), role);

    /// <summary>Проверяет роль по фактическому лицу; при отсутствии лица допускается только global.</summary>
    public virtual async Task<bool> HasRoleForPersonAsync(Роли role, int? personId) =>
        await Employee.HasRoleAsync(_db, await GetWindowsPrincipalAsync(), role,
            EmployeeRoleScope.PersonOrGlobal, personId);

    private async Task<bool> HasGlobalRoleAsync(Роли role) =>
        await Employee.HasRoleAsync(_db, await GetWindowsPrincipalAsync(), role, EmployeeRoleScope.Global);
}

/// <summary>Сериализуемый ATS attempt; retry принимается только при совпадении с текущим серверным контекстом.</summary>
public sealed record PhoneSyncAttempt(
    EquipmentPhoneSyncIntent Intent,
    IReadOnlyCollection<PhoneSyncOperationResult> Operations)
{
    /// <summary>Причина незавершённого планирования до отправки какой-либо операции АТС.</summary>
    public string? PlanningError { get; init; }

    /// <summary>Стабильный идентификатор попытки и всех её операций.</summary>
    public string AttemptId => Intent.AttemptId;
}

/// <summary>Результат read-only сверки; Unknown operations остаются неизменными.</summary>
public sealed record PhoneSyncReconciliation(
    PhoneSyncAttempt Attempt,
    Equipment? ActualEquipment,
    IReadOnlyCollection<EquipmentPhoneLink> ActualPhones,
    bool PersistedStateMatches);

/// <summary>Фаза отдельной внешней операции относительно фактического dispatch.</summary>
public enum PhoneSyncPhase
{
    /// <summary>Preflight завершился до dispatch; внешняя команда не отправлялась.</summary>
    NotSent,

    /// <summary>Операция отклонена до dispatch.</summary>
    Rejected,

    /// <summary>Внешний успех подтверждён.</summary>
    Succeeded,

    /// <summary>Подтверждённый отказ без доказанного внешнего эффекта.</summary>
    Failed,

    /// <summary>Ответ потерян после dispatch; blind replay запрещён.</summary>
    Unknown,
}

/// <summary>Результат одной ATS operation со стабильной попыткой, целью и deleting flag.</summary>
public sealed record PhoneSyncOperationResult(
    string AttemptId,
    PhoneSyncOperation Operation,
    PhoneSyncPhase Phase,
    string? Error = null)
{
    /// <summary>Исход неизвестен и автоматический повтор запрещён.</summary>
    public bool IsUnknown => Phase == PhoneSyncPhase.Unknown;

    /// <summary>Повтор разрешён только для доказанно неотправленной либо подтверждённо failed операции.</summary>
    public bool CanRetry => Phase is PhoneSyncPhase.NotSent or PhoneSyncPhase.Failed;
}

/// <summary>Фаза серверной записи и последующего обновления прочитанного состояния.</summary>
public enum EquipmentWritePhase
{
    /// <summary>Ошибка preflight до DML; команда не отправлялась.</summary>
    NotSent,

    /// <summary>Ввод, контекст или права отклонены до DML.</summary>
    Rejected,

    /// <summary>Ответ потерян после DML dispatch; повтор запрещён до сверки.</summary>
    Unknown,

    /// <summary>Запись и последующее чтение подтверждены.</summary>
    Committed,

    /// <summary>Запись подтверждена, но refresh завершился ошибкой; повторять только чтение.</summary>
    RefreshFailed,
}

/// <summary>Подтверждённый результат записи и отдельный исход post-commit refresh.</summary>
public sealed record EquipmentWriteResult<T>(T Value, EquipmentWritePhase Phase, string? Error = null);

/// <summary>Ошибка серверной операции с доказанной фазой вместо классификации по SqlException.Number.</summary>
public sealed class EquipmentOperationException : InvalidOperationException
{
    /// <summary>Создаёт ошибку с фазой и исходным исключением.</summary>
    public EquipmentOperationException(EquipmentWritePhase phase, string message, Exception innerException)
        : base(message, innerException) => Phase = phase;

    /// <summary>Фактическая достигнутая фаза операции.</summary>
    public EquipmentWritePhase Phase { get; }
}
