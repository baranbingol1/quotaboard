// SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text;
using AiLimits.Infrastructure.Providers.Copilot;

namespace AiLimits.Tests;

public sealed class GitHubDeviceFlowClientTests
{
    [Fact]
    public async Task ErrorPayloadOnHttp200ThrowsInvalidOperationException()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"error\":\"unsupported_client_type\",\"error_description\":\"Client type not supported.\"}",
                Encoding.UTF8,
                "application/json"
            ),
        });

        var client = new GitHubDeviceFlowClient(new HttpClient(handler), "test-client-id");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync(default));

        Assert.Contains("unsupported_client_type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRequiredFieldThrowsInvalidOperationException()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"error\":\"bad_verification_code\"}", Encoding.UTF8, "application/json"),
        });

        var client = new GitHubDeviceFlowClient(new HttpClient(handler), "test-client-id");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync(default));
    }

    [Fact]
    public async Task ValidPayloadReturnsDeviceAuthorization()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"device_code\":\"dc123\",\"user_code\":\"UC-CODE\",\"verification_uri\":\"https://github.com/login/device\",\"expires_in\":900,\"interval\":5}",
                Encoding.UTF8,
                "application/json"
            ),
        });

        var client = new GitHubDeviceFlowClient(new HttpClient(handler), "test-client-id");

        var auth = await client.StartAsync(default);

        Assert.Equal("dc123", auth.DeviceCode);
        Assert.Equal("UC-CODE", auth.UserCode);
        Assert.Equal(new Uri("https://github.com/login/device"), auth.VerificationUri);
    }

    [Fact]
    public async Task Oversized_device_code_response_throws()
    {
        string oversized = "{\"device_code\":\"" + new string('x', 3_000_000) + "\"}";
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(oversized, Encoding.UTF8, "application/json"),
        });

        var client = new GitHubDeviceFlowClient(new HttpClient(handler), "test-client-id");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartAsync(default));
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }
}
