namespace Tapka;

internal static class GitPackage
{
    public const string Version = "2.56.1.windows.1";
    public static readonly Uri DownloadUrl = new(
        "https://github.com/git-for-windows/git/releases/download/v2.56.1.windows.1/PortableGit-2.56.1-64-bit.7z.exe");
    public const string Sha256 = "9dd04a608ceb5f6ab32a73709e0ecb159d211da71b697c45ff81f6dd0c6b49c7";
    public const string Executable = @"cmd\git.exe";
}
