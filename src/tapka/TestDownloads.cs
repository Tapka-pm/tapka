#if TAPKA_TEST_BUILD
using System.Net;

namespace Tapka;

internal sealed class TestDownloads(string directory) : HttpMessageHandler
{
    private readonly string root = Path.GetFullPath(directory);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.RequestUri?.AbsoluteUri == "https://github.com/Tapka-pm/packages/archive/refs/heads/master.zip")
        {
            return Task.FromResult(Respond(Path.Combine(root, "official-repository.zip")));
        }
        if (request.RequestUri is not { Scheme: "https", Host: "fixtures.tapka.invalid", IsDefaultPort: true } uri)
        {
            throw new HttpRequestException($"Unmapped integration test URL '{request.RequestUri}'.");
        }
        if (uri.LocalPath == "/repository.zip")
        {
            return Task.FromResult(Respond(Path.Combine(root, "repository.zip")));
        }
        var path = uri.LocalPath.Split('/');
        if (path is not ["", var package, var filename] ||
            !PackageRepository.IsPathSegment(package) || !PackageRepository.IsPathSegment(filename))
        {
            throw new HttpRequestException($"Invalid integration test URL '{uri}'.");
        }

        return Task.FromResult(Respond(Path.Combine(root, package, filename)));
    }

    private static HttpResponseMessage Respond(string file) => File.Exists(file)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(file)) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
}
#endif
