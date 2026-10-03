using Commerce.Application.Time;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>Reads the business day (see <see cref="IBusinessClock"/>) for the request being served.</summary>
internal static class BusinessDay
{
    internal static DateOnly Today(this HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IBusinessClock>().Today;
}
