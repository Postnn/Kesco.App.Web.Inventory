namespace Kesco.App.Web.Inventory.Services;

/// <summary>Состояние поля формы телефона по правилам legacy.</summary>
public enum PhoneFieldState
{
    /// <summary>Поле поддерживается и доступно для редактирования при наличии прав.</summary>
    Available,

    /// <summary>Поле не поддерживается и не содержит значения.</summary>
    Hidden,

    /// <summary>Поле не поддерживается, но содержит прежнее значение и показывается read-only.</summary>
    IncompatibleExistingValue
}

/// <summary>Вычисленное состояние одного поля телефона.</summary>
/// <param name="State">Видимость, редактируемость и предупреждение поля.</param>
public readonly record struct PhoneFieldAvailability(PhoneFieldState State)
{
    /// <summary>Поле присутствует в форме.</summary>
    public bool IsVisible => State != PhoneFieldState.Hidden;

    /// <summary>Поле поддерживается текущей конфигурацией.</summary>
    public bool IsEditable => State == PhoneFieldState.Available;

    /// <summary>Требуется предупреждение о несовместимом существующем значении.</summary>
    public bool HasIncompatibleValue => State == PhoneFieldState.IncompatibleExistingValue;

    internal static PhoneFieldAvailability Resolve(bool available, bool hasValue) => new(
        available ? PhoneFieldState.Available :
        hasValue ? PhoneFieldState.IncompatibleExistingValue : PhoneFieldState.Hidden);
}

/// <summary>Входной снимок типа, станции, её возможностей и текущих значений формы.</summary>
public sealed record PhoneFormRuleInput
{
    /// <summary>Тип телефонного номера.</summary>
    public int? PhoneTypeId { get; init; }

    /// <summary>Телефонная станция.</summary>
    public int? PhoneStationId { get; init; }

    /// <summary>Для выбранных типа и станции существуют порты АТС.</summary>
    public bool StationHasPorts { get; init; }

    /// <summary>Для выбранных типа и станции доступен автонабор.</summary>
    public bool AutoDialAvailable { get; init; }

    /// <summary>Станция поддерживает ведомые номера.</summary>
    public bool StationSupportsDrivenBy { get; init; }

    /// <summary>Станция поддерживает отображаемые номера.</summary>
    public bool StationSupportsDisplayedFrom { get; init; }

    /// <summary>Станция поддерживает параллельные вызовы.</summary>
    public bool StationSupportsParallelCalls { get; init; }

    /// <summary>Выбрана розетка.</summary>
    public bool HasSocket { get; init; }

    /// <summary>Заполнен порт АТС.</summary>
    public bool HasPort { get; init; }

    /// <summary>Заполнена группа перехвата, включая допустимое значение 0.</summary>
    public bool HasPickupGroup { get; init; }

    /// <summary>Заполнен номер автонабора.</summary>
    public bool HasAutoDial { get; init; }

    /// <summary>Выбран ведомый номер.</summary>
    public bool HasDrivenBy { get; init; }

    /// <summary>Выбран отображаемый номер.</summary>
    public bool HasDisplayedFrom { get; init; }

    /// <summary>Есть параллельные вызовы.</summary>
    public bool HasParallelCalls { get; init; }

    /// <summary>Есть члены PBX-группы.</summary>
    public bool HasGroupMembers { get; init; }

    /// <summary>Есть связи с оборудованием.</summary>
    public bool HasEquipment { get; init; }

    /// <summary>Установлен признак основного номера.</summary>
    public bool HasDefaultPhone { get; init; }

    /// <summary>Выбран сотрудник.</summary>
    public bool HasEmployee { get; init; }
}

/// <summary>Матрица доступности всех зависимых элементов формы телефона.</summary>
public sealed class PhoneFormAvailability
{
    internal PhoneFormAvailability(PhoneFormRuleInput input)
    {
        var dependentType = input.PhoneTypeId is (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.Цифровой
            or (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.Аналоговый
            or (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.ISDN
            or (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.DECT
            or (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.IP;
        var dependenciesSelected = dependentType && input.PhoneStationId is not null;
        Socket = PhoneFieldAvailability.Resolve(input.PhoneTypeId is (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.Цифровой
            or (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.Аналоговый, input.HasSocket);
        Port = PhoneFieldAvailability.Resolve(
            input.PhoneTypeId is not null && input.PhoneStationId is not null && input.StationHasPorts,
            input.HasPort);
        PickupGroup = PhoneFieldAvailability.Resolve(input.PhoneStationId is >= 0 and <= 99, input.HasPickupGroup);
        AutoDial = PhoneFieldAvailability.Resolve(
            input.PhoneTypeId is not null && input.PhoneStationId is not null && input.AutoDialAvailable,
            input.HasAutoDial);
        DrivenBy = PhoneFieldAvailability.Resolve(dependenciesSelected && input.StationSupportsDrivenBy, input.HasDrivenBy);
        DisplayedFrom = PhoneFieldAvailability.Resolve(
            dependenciesSelected && input.StationSupportsDisplayedFrom, input.HasDisplayedFrom);
        ParallelCalls = PhoneFieldAvailability.Resolve(
            input.PhoneStationId is not null && input.StationSupportsParallelCalls, input.HasParallelCalls);
        GroupMembers = PhoneFieldAvailability.Resolve(
            input.PhoneTypeId == (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.ГруппаИлиФиктивный && input.PhoneStationId is not null,
            input.HasGroupMembers);
        Equipment = PhoneFieldAvailability.Resolve(
            input.PhoneTypeId is (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.Цифровой
                or (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.Аналоговый
                or (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.DECT
            || input.PhoneTypeId >= (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.IP, input.HasEquipment);
        DefaultPhone = PhoneFieldAvailability.Resolve(input.HasEmployee, input.HasDefaultPhone);
    }

    /// <summary>Розетка.</summary>
    public PhoneFieldAvailability Socket { get; }

    /// <summary>Порт АТС.</summary>
    public PhoneFieldAvailability Port { get; }

    /// <summary>Группа перехвата.</summary>
    public PhoneFieldAvailability PickupGroup { get; }

    /// <summary>Номер автонабора.</summary>
    public PhoneFieldAvailability AutoDial { get; }

    /// <summary>Ведомый номер.</summary>
    public PhoneFieldAvailability DrivenBy { get; }

    /// <summary>Отображаемый номер.</summary>
    public PhoneFieldAvailability DisplayedFrom { get; }

    /// <summary>Параллельные вызовы.</summary>
    public PhoneFieldAvailability ParallelCalls { get; }

    /// <summary>Члены PBX-группы.</summary>
    public PhoneFieldAvailability GroupMembers { get; }

    /// <summary>Связи с оборудованием.</summary>
    public PhoneFieldAvailability Equipment { get; }

    /// <summary>Основной номер сотрудника.</summary>
    public PhoneFieldAvailability DefaultPhone { get; }
}

/// <summary>Чистые правила доступности формы телефона без SQL и Razor.</summary>
public static class PhoneFormRules
{
    /// <summary>Вычисляет видимость, read-only и предупреждения по снимку состояния.</summary>
    public static PhoneFormAvailability Evaluate(PhoneFormRuleInput input) => new(input);
}
