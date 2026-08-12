using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace OrderDeck.LicenseServer.Services.Auth;

/// <summary>
/// Varsayılan-kapalı yetki kapısı: <c>stock</c> rolündeki operatör yalnız
/// <see cref="AllowStockStaffAttribute"/> ile işaretli uçlara girebilir.
/// Global filtre olarak kayıtlı (<c>Program.cs</c>), böylece yarın eklenen bir
/// uç kendiliğinden açık gelmez.
/// </summary>
public sealed class StockStaffScopeFilter : IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.User.GetOperatorRole() != OperatorRoles.Stock)
            return next();

        var allowed = context.ActionDescriptor.EndpointMetadata
            .Any(m => m is AllowStockStaffAttribute);

        if (allowed) return next();

        context.Result = new ObjectResult(new ProblemDetails
        {
            Title = "stock-staff-forbidden",
            Detail = "Stok elemanı bu bölüme erişemez.",
            Status = StatusCodes.Status403Forbidden,
        })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
        return Task.CompletedTask;
    }
}
