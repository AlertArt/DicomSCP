using System.Net;
using Xunit;

namespace DicomSCP.IntegrationTests;

[Collection("E2E")]
public class AuthProtectionTests
{
    private readonly E2EFixture _fx;

    public AuthProtectionTests(E2EFixture fixture)
    {
        _fx = fixture;
    }

    [Theory]
    [InlineData("/dicomweb/studies")]
    [InlineData("/wado?requestType=WADO&studyUID=x&seriesUID=x&objectUID=x")]
    [InlineData("/viewer/ohif/1.2.3")]
    [InlineData("/viewer/weasis/1.2.3")]
    public async Task AnonymousRequests_ToPhiEndpoints_AreRejected(string path)
    {
        using var anonHttp = new HttpClient();
        var resp = await anonHttp.GetAsync($"http://127.0.0.1:{E2EFixture.HttpPort}{path}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task AnonymousRequests_ToApiEndpoints_AreRejected()
    {
        using var anonHttp = new HttpClient();
        var resp = await anonHttp.GetAsync($"http://127.0.0.1:{E2EFixture.HttpPort}/api/dicom/stats");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}