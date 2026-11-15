using System.Text.Json.Serialization;

namespace Tapka;

[JsonSerializable(typeof(PackageManifest))]
internal sealed partial class PackageJsonContext : JsonSerializerContext;
