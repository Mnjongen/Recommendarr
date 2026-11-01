using System.Net.Http;
using System.Security.Authentication;
using Recommendarr.Web.Jobs;

namespace Recommendarr.Web.Tests;

public sealed class SyncJobTests
{
    [Fact]
    public void Failures_are_described_with_their_cause()
    {
        var ex = new HttpRequestException("The SSL connection could not be established, see inner exception.",
            new AuthenticationException("Authentication failed because the remote party has closed the transport stream."));

        Assert.Equal(
            "The SSL connection could not be established → Authentication failed because the remote party has closed the transport stream.",
            SyncJob.Describe(ex));
    }
}
