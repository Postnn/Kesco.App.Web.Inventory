using Clayzor.Lib.DALC;
using Kesco.App.Web.Inventory.Services;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>HTTP-контракт выдачи конфигурации сохранённого IP-телефона.</summary>
public static class EquipmentPhoneConfigEndpoint
{
    /// <summary>Регистрирует authenticated endpoint с идентичностью обычного HTTP-запроса.</summary>
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/Equipment/PhoneConfigHandler.ashx", HandleAsync);

    private static async Task<IResult> HandleAsync(string? mac, HttpContext context, DbManager db)
    {
        if (string.IsNullOrWhiteSpace(mac) || mac.Length != 12)
            return Results.BadRequest("Укажите MAC-адрес из 12 символов.");
        try
        {
            var service = new EquipmentFormService(db, new HttpCurrentUserPrincipalProvider(context));
            var config = await service.GetPhoneConfigAsync(mac);
            var contentType = config.Contains("<?") && config.Contains("?>") ? "text/xml" : "text/plain";
            return Results.Text(config, contentType);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
    }
}
