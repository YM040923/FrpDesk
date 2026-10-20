using System.IO;

namespace FrpDesk.Services;

public static class RuntimeAssets
{
    public const string FrpcVersion = "0.71.0";

    public static string FrpcPath => Path.Combine(AppContext.BaseDirectory, "assets", "frpc.exe");

    public static bool FrpcExists => File.Exists(FrpcPath);
}
