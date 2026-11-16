using System.Text.Json.Serialization;

namespace Tapka;

[JsonSerializable(typeof(PackageManifest))]
[JsonSerializable(typeof(ShimGenerationPackage[]))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class PackageJsonContext : JsonSerializerContext;
