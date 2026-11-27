using System.Data.SqlTypes;
using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    private enum EquipmentLegacyFamily
    {
        Main, Network, Accounting, Software, Sim, SimService, License, Computer, Account, Period, Ups, Configure
    }

    private sealed record EquipmentLegacyIntent(Guid AttemptId, string Kind, EquipmentLegacyFamily Family,
        int? RelatedId = null, DateTime? When = null, bool? Existing = null, string? Binding = null);

    private static EquipmentLegacyIntent ParseEquipmentLegacyIntent(ClayLegacyDraftOperationEvidence item, int equipmentId)
    {
        if (equipmentId <= 0 || item is null || item.AttemptId is not { Length: 32 } ||
            !Guid.TryParseExact(item.AttemptId, "N", out var attemptId) || attemptId == Guid.Empty ||
            item.Data is not { Length: >= 2 and <= 256 })
            throw InvalidEquipmentLegacyIntent();

        try
        {
            using var document = JsonDocument.Parse(item.Data);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw InvalidEquipmentLegacyIntent();

            var fields = document.RootElement.EnumerateObject();
            JsonProperty? field = fields.MoveNext() ? fields.Current : null;
            if (fields.MoveNext())
                throw InvalidEquipmentLegacyIntent();

            JsonElement Value(string name) =>
                field is { } property && property.NameEquals(name)
                    ? property.Value : throw InvalidEquipmentLegacyIntent();

            int Id(string name, bool inserting)
            {
                var value = Value(name);
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var id) ||
                    value.GetRawText() != id.ToString(CultureInfo.InvariantCulture) ||
                    (inserting ? id != 0 : id <= 0))
                    throw InvalidEquipmentLegacyIntent();
                return id;
            }

            EquipmentLegacyIntent Empty(EquipmentLegacyFamily family) =>
                field is null ? new(attemptId, item.Kind, family) : throw InvalidEquipmentLegacyIntent();

            switch (item.Kind)
            {
                case "equipment-save":
                case "equipment-phone-save":
                    return Empty(EquipmentLegacyFamily.Main);
                case AttachNetworkCommand:
                case DetachNetworkCommand:
                case "Подключение к локальной сети":
                case "Отключение от локальной сети":
                    return Empty(EquipmentLegacyFamily.Network);
                case InsertAccountingCommand:
                case UpdateAccountingCommand:
                case DeleteAccountingCommand:
                    return new(attemptId, item.Kind, EquipmentLegacyFamily.Accounting,
                        RelatedId: Id("AccountingId", item.Kind == InsertAccountingCommand));
                case InsertSoftwareCommand:
                case UpdateSoftwareCommand:
                    return new(attemptId, item.Kind, EquipmentLegacyFamily.Software,
                        RelatedId: Id("InstallationId", item.Kind == InsertSoftwareCommand));
                case InsertSimServiceCommand:
                case UpdateSimServiceCommand:
                    return new(attemptId, item.Kind, EquipmentLegacyFamily.SimService,
                        RelatedId: Id("ServiceId", item.Kind == InsertSimServiceCommand));
                case InsertSimCommand:
                case UpdateSimCommand:
                case DeleteSimCommand:
                {
                    var value = Value("DuplicateEquipmentId");
                    if (value.ValueKind == JsonValueKind.Null)
                        return new(attemptId, item.Kind, EquipmentLegacyFamily.Sim);
                    var related = Id("DuplicateEquipmentId", inserting: false);
                    if (related == equipmentId || item.Kind == DeleteSimCommand)
                        throw InvalidEquipmentLegacyIntent();
                    return new(attemptId, item.Kind, EquipmentLegacyFamily.Sim, RelatedId: related);
                }
                case InsertLicenseCommand:
                case UpdateLicenseCommand:
                {
                    var existing = item.Kind == UpdateLicenseCommand;
                    if (Value("ExpectedExisting").ValueKind != (existing ? JsonValueKind.True : JsonValueKind.False))
                        throw InvalidEquipmentLegacyIntent();
                    return new(attemptId, item.Kind, EquipmentLegacyFamily.License, Existing: existing);
                }
                case InsertComputerCommand:
                case UpdateComputerCommand:
                {
                    var date = Value("Date");
                    if (date.ValueKind != JsonValueKind.String || date.GetString() is not { Length: 8 } text ||
                        !DateTime.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ||
                        day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) != text)
                        throw InvalidEquipmentLegacyIntent();
                    return new(attemptId, item.Kind, EquipmentLegacyFamily.Computer, When: day,
                        Existing: item.Kind == UpdateComputerCommand);
                }
                case InsertAccountCommand:
                case UpdateAccountCommand:
                case DeleteAccountCommand:
                {
                    var value = Value("Binding");
                    if (value.ValueKind != JsonValueKind.String || !IsAccountBinding(value.GetString()))
                        throw InvalidEquipmentLegacyIntent();
                    return new(attemptId, item.Kind, EquipmentLegacyFamily.Account, Binding: value.GetString());
                }
                case AddLocationPeriodCommand:
                case UpdateLocationPeriodCommand:
                case IssueResponsiblePeriodCommand:
                case UpdateResponsiblePeriodCommand:
                case WithdrawResponsiblePeriodCommand:
                {
                    var value = Value("FromUtcTicks");
                    if (value.ValueKind != JsonValueKind.String ||
                        !long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                        ticks <= 0 || ticks > DateTime.MaxValue.Ticks ||
                        ticks.ToString(CultureInfo.InvariantCulture) != value.GetString())
                        throw InvalidEquipmentLegacyIntent();
                    var start = new DateTime(ticks, DateTimeKind.Utc);
                    if (EquipmentPeriodJournalWrite.NormalizeStartUtc(start).Ticks != ticks)
                        throw InvalidEquipmentLegacyIntent();
                    return new(attemptId, item.Kind, EquipmentLegacyFamily.Period, When: start);
                }
                case AddUpsLinkCommand:
                case DeleteUpsLinkCommand:
                case SaveUpsDetailsCommand:
                {
                    var related = Id("UpsEquipmentId", inserting: false);
                    if (related == equipmentId)
                        throw InvalidEquipmentLegacyIntent();
                    return new(attemptId, item.Kind, EquipmentLegacyFamily.Ups, RelatedId: related);
                }
                case "configure-phone-equipment":
                case "retry-phone-sync":
                    return Empty(EquipmentLegacyFamily.Configure);
                case "Сохранение бухгалтерского учёта":
                case "Удаление бухгалтерского учёта":
                    return Empty(EquipmentLegacyFamily.Accounting);
                case "Сохранение установленного ПО":
                    return Empty(EquipmentLegacyFamily.Software);
                case "Сохранение SIM":
                case "Удаление сведений SIM":
                    return Empty(EquipmentLegacyFamily.Sim);
                case "Сохранение дополнительной услуги SIM":
                    return Empty(EquipmentLegacyFamily.SimService);
                case "Сохранение лицензии":
                    return Empty(EquipmentLegacyFamily.License);
                case "Сохранение конфигурации компьютера":
                    return Empty(EquipmentLegacyFamily.Computer);
                case "Добавление учётной записи":
                case "Изменение учётной записи":
                case "Удаление учётной записи":
                    return Empty(EquipmentLegacyFamily.Account);
                case "Добавление расположения":
                case "Изменение расположения":
                case "Сохранение ответственного":
                    return Empty(EquipmentLegacyFamily.Period);
                case "Подключение UPS":
                case "Сохранение подключения UPS":
                    return Empty(EquipmentLegacyFamily.Ups);
                default:
                    throw InvalidEquipmentLegacyIntent();
            }
        }
        catch (Exception error) when (error is JsonException or SqlTypeException)
        {
            throw InvalidEquipmentLegacyIntent();
        }
    }

    private static InvalidOperationException InvalidEquipmentLegacyIntent() =>
        new("Не удалось подтвердить сведения о прежней операции оборудования.");
}
