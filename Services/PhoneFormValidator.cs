using System.Text.RegularExpressions;
using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Чистые правила формата, полного номера и зависимых коллекций формы телефона.</summary>
public static partial class PhoneFormValidator
{
    /// <summary>Проверяет все правила, не требующие SQL.</summary>
    public static IReadOnlyList<PhoneValidationIssue> Validate(PhoneSaveRequest request)
    {
        var phone = request.Phone;
        var issues = new List<PhoneValidationIssue>();
        if (string.IsNullOrWhiteSpace(phone.Number)) Add(issues, "Number", "Укажите номер телефона.");
        if (phone.Number.Length > 4 || !NumberRegex().IsMatch(phone.Number)) Add(issues, "Number", "Номер должен содержать до 4 цифр, # или *.");
        if (phone.PhoneTypeId is null) Add(issues, "PhoneTypeId", "Выберите тип номера.");
        if (phone.StationId is null) Add(issues, "StationId", "Выберите телефонную станцию.");
        if (phone.CustomerId is null) Add(issues, "CustomerId", "Выберите компанию заказчика.");
        ValidateOptionalNumber(issues, "QuickCall", "Переадресация", phone.QuickCall);
        ValidateOptionalNumber(issues, "AutoDialNumber", "Номер автонабора", phone.AutoDialNumber);
        if (phone.Connected.Length > 100) Add(issues, "Connected", "Поле «Подключено» не может превышать 100 символов.");
        if (phone.AtsDescription.Length > 100) Add(issues, "AtsDescription", "Описание АТС не может превышать 100 символов.");
        if (RussianRegex().IsMatch(phone.AtsDescription)) Add(issues, "AtsDescription", "Описание АТС не должно содержать кириллицу.");
        if (string.IsNullOrWhiteSpace(phone.AtsDescription) && phone.DrivenById is null &&
            (string.IsNullOrWhiteSpace(phone.Connected) || !phone.Connected.StartsWith('№')))
            Add(issues, "AtsDescription", "Укажите описание АТС.");
        if (phone.EmployeeId is null && string.IsNullOrWhiteSpace(phone.Connected))
            Add(issues, "EmployeeId", "Заполните сотрудника или поле «Подключено».");
        if (phone.PickupGroup.Length > 20) Add(issues, "PickupGroup", "Группа перехвата не может превышать 20 символов.");
        if (phone.StationId is not null && !string.IsNullOrEmpty(phone.PickupGroup) &&
            !request.AvailablePickupGroups.Contains(phone.PickupGroup, StringComparer.Ordinal))
            Add(issues, "PickupGroup", "Выбранная группа перехвата недоступна на этой станции.");
        if (phone.Notes.Length > 300) Add(issues, "Notes", "Примечания не могут превышать 300 символов.");
        if (phone.ReturnDate?.Date < request.Today.Date) Add(issues, "ReturnDate", "Срок возврата не может быть в прошлом.");
        if (phone.ServicesKind is not (0 or 1 or 3 or 4 or 5)) Add(issues, "ServicesKind", "Выберите допустимый вид услуг связи.");
        if (phone.ViewInDirectory is not (0 or 1 or 2 or 4 or 6 or 8)) Add(issues, "ViewInDirectory", "Выберите допустимый режим отображения в справочнике.");

        var numbers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var call in request.Relations.ParallelCalls)
        {
            ValidateOptionalNumber(issues, "ParallelCalls", "Номер параллельного вызова", call.CallNumber, required: true);
            if (call.DelayBeforeCall is < 0 or > 255 || call.DelayTooQuickResponse is < 0 or > 255)
                Add(issues, "ParallelCalls", "Задержки должны быть целыми числами от 0 до 255.");
            if (string.Equals(call.CallNumber, phone.Number, StringComparison.Ordinal))
                Add(issues, "ParallelCalls", "Номер параллельного вызова не должен совпадать с текущим номером.");
            if (!numbers.Add(call.CallNumber)) Add(issues, "ParallelCalls", $"Номер «{call.CallNumber}» добавлен повторно.");
        }
        if (request.Relations.ParallelCalls.Count > 0 && phone.ViewInDirectory == 0)
            Add(issues, "ViewInDirectory", "При наличии параллельных вызовов номер должен отображаться в справочнике.");

        var equipment = new HashSet<int>();
        foreach (var link in request.Relations.Equipment)
        {
            if (!equipment.Add(link.EquipmentId)) Add(issues, "Equipment", $"Оборудование #{link.EquipmentId} добавлено повторно: это запрещено первичным ключом таблицы.");
            if (link.ReturnDate?.Date < request.Today.Date) Add(issues, "Equipment", "Срок возврата оборудования не может быть в прошлом.");
            if (link.Notes.Length > 300) Add(issues, "Equipment", "Примечание связи оборудования не может превышать 300 символов.");
        }
        var members = new HashSet<int>();
        foreach (var member in request.Relations.GroupMembers)
        {
            if (member.PhoneId == phone.Id && phone.Id > 0) Add(issues, "GroupMembers", "Группа не может включать саму себя.");
            if (!members.Add(member.PhoneId)) Add(issues, "GroupMembers", $"Номер #{member.PhoneId} уже добавлен в группу.");
        }
        return issues;
    }

    /// <summary>Проверяет черновик параллельного вызова до закрытия диалога.</summary>
    public static IReadOnlyList<string> ValidateParallelCall(PhoneParallelCall call, string currentNumber,
        IEnumerable<string> otherNumbers)
    {
        var issues = new List<PhoneValidationIssue>();
        ValidateOptionalNumber(issues, "ParallelCalls", "Номер параллельного вызова", call.CallNumber, required: true);
        if (call.DelayBeforeCall is < 0 or > 255 || call.DelayTooQuickResponse is < 0 or > 255)
            Add(issues, "ParallelCalls", "Задержки должны быть целыми числами от 0 до 255.");
        if (string.Equals(call.CallNumber, currentNumber, StringComparison.Ordinal))
            Add(issues, "ParallelCalls", "Номер параллельного вызова не должен совпадать с текущим номером.");
        if (otherNumbers.Contains(call.CallNumber, StringComparer.Ordinal))
            Add(issues, "ParallelCalls", $"Номер «{call.CallNumber}» добавлен повторно.");
        return issues.Select(item => item.Message).ToList();
    }

    /// <summary>Вычисляет предварительный полный номер по контракту Phone.CalculateFullNumber.</summary>
    public static string CalculateFullNumber(string? number, string? displayedFromFullNumber,
        string? drivenByFullNumber, PhoneStationLookup? station)
    {
        if (number is null) return string.Empty;
        if (!string.IsNullOrEmpty(displayedFromFullNumber)) return displayedFromFullNumber;
        if (!string.IsNullOrEmpty(drivenByFullNumber)) return drivenByFullNumber;
        var prefix = string.Empty;
        if (station is not null && !string.IsNullOrEmpty(station.PrefixUsed) &&
            number.StartsWith(station.PrefixUsed.Replace("%", string.Empty), StringComparison.Ordinal))
            prefix = station.Prefix;
        return prefix + number;
    }

    /// <summary>Экранирует специальные символы LIKE для параметризованной проверки префиксов.</summary>
    public static string EscapeLike(string value) => value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    private static void ValidateOptionalNumber(List<PhoneValidationIssue> issues, string field, string displayName,
        string? value, bool required = false)
    {
        if (string.IsNullOrEmpty(value))
        {
            if (required) Add(issues, field, $"Укажите: {displayName}.");
            return;
        }
        if (value.Length > 50 || !OptionalNumberRegex().IsMatch(value))
            Add(issues, field, $"{displayName} должен содержать до 50 символов: необязательный +, цифры, # или *.");
    }

    private static void Add(List<PhoneValidationIssue> issues, string field, string message,
        PhoneValidationSeverity severity = PhoneValidationSeverity.Blocking) =>
        issues.Add(new PhoneValidationIssue(field, message, severity));

    [GeneratedRegex("^[0-9#*]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();

    [GeneratedRegex("^\\+?[0-9#*]+$", RegexOptions.CultureInvariant)]
    private static partial Regex OptionalNumberRegex();

    [GeneratedRegex("[А-Яа-яЁё]", RegexOptions.CultureInvariant)]
    private static partial Regex RussianRegex();
}
