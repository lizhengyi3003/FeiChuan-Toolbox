using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TunHotspot.Core;

public static class Contract
{
    public const int Version = 1;
    public static string ProductVersion => typeof(Contract).Assembly.GetName().Version!.ToString(3);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = false, MaxDepth = 48, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
      RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true };
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Decode<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new InvalidDataException("数据为空。");
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static string Sid => WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法读取账户 SID。");
    public static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static string Privilege => WindowsIdentity.GetCurrent().IsSystem ? "LocalSystem" : Elevated ? "管理员" : "普通用户";
    private static readonly Lazy<string> MachineIdentity = new(() =>
    {
        using var machine = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var crypto = machine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        string guid = crypto?.GetValue("MachineGuid") as string ?? throw new InvalidOperationException("无法读取本机标识，未建立跨账户分析关联。");
        return Hash(guid.ToLowerInvariant());
    });
    public static string DeviceId => MachineIdentity.Value;
}
