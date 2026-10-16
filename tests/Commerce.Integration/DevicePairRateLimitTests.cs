using System.Net;
using System.Net.Http.Json;
using Commerce.Cloud.Api.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Commerce.Integration;

/// <summary>
/// `POST /device/pair` is anonymous and every NEW installation id burns one of a
/// branch's 999 register numbers for good, so the endpoint has its own per-IP
/// limiter (DeviceRateLimitPolicies.Pair). Failed attempts count too: the
/// limiter sits in front of the credential check and also slows password guessing.
/// The limiter runs before the handler, so these tests need no database.
/// </summary>
public sealed class DevicePairRateLimitTests
{
    private static WebApplicationFactory<Program> FactoryWithLimit(int limit) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Commerce", PostgresTestFixture.DirectConnectionString);
            builder.UseSetting("RateLimits:DevicePairPermitLimit", limit.ToString());
        });

    private static Task<HttpResponseMessage> PairAsync(HttpClient client) =>
        client.PostAsJsonAsync("/device/pair", new DevicePairRequest("", "", Guid.NewGuid(), null));

    [Fact]
    public async Task Pairing_BeyondThePermitLimit_Returns429_WithRetryAfter()
    {
        using var factory = FactoryWithLimit(3);
        var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PairAsync(client)).StatusCode);
        }
        var limited = await PairAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task TheLimiter_DoesNotCoverTheIdentityEndpoint()
    {
        using var factory = FactoryWithLimit(1);
        var client = factory.CreateClient();
        await PairAsync(client);
        await PairAsync(client);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/device/identity")).StatusCode);
        }
    }

    [Fact]
    public void ThePolicy_DefaultsToAGenerousPerQuarterHourCeiling()
    {
        Assert.Equal("device-pair", DeviceRateLimitPolicies.Pair);
        Assert.Equal(TimeSpan.FromMinutes(15), DeviceRateLimitPolicies.PairWindow);
        Assert.InRange(DeviceRateLimitPolicies.DefaultPairPermitLimit, 10, 100);
    }
}
