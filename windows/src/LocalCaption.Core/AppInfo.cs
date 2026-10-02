using System.Reflection;

namespace LocalCaption.Core;

/// <summary>Facts about the running build, shared by Settings ▸ System and the Codex client info.</summary>
public static class AppInfo
{
    /// <summary>
    /// The app's version: the entry assembly's informational version without the <c>+commit</c>
    /// suffix, else its assembly version, else <c>unknown</c>.
    /// </summary>
    public static string Version()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                   is { Length: > 0 } informational
            ? informational
            : assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}
