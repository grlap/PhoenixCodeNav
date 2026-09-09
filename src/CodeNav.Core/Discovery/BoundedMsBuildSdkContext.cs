using System.Xml.Linq;

namespace CodeNav.Core.Discovery;

/// <summary>The implicit context of the bounded evaluator's admitted root SDK declaration.</summary>
internal readonly record struct BoundedMsBuildSdkContext(bool UsesMicrosoftNetSdk)
{
    public static bool TryRead(XElement root, CancellationToken cancellationToken,
        out BoundedMsBuildSdkContext context)
    {
        context = default;
        cancellationToken.ThrowIfCancellationRequested();
        XAttribute[] attributes = root.Attributes()
            .Where(attribute => attribute.Name.LocalName.Equals("Sdk", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (attributes.Length > 1) return false;
        if (attributes.Length == 1 &&
            !attributes[0].Value.Trim().Equals("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (XElement element in root.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Child declarations and explicit SDK imports retain their existing caller refusals.
            // In particular, a mid-document SDK import cannot supply a root-time context.
            if (element.Name.LocalName.Equals("Sdk", StringComparison.OrdinalIgnoreCase)) return false;
        }
        context = new(attributes.Length == 1);
        return true;
    }

    public void SeedProperties(IDictionary<string, BoundedMsBuildProperty> properties)
    {
        if (!UsesMicrosoftNetSdk) return;
        // Microsoft.NET.Sdk/Sdk/Sdk.props sets these before Microsoft.Common.props so
        // Directory.Build.props and NuGet props can use them. Unlike the Configuration/
        // Platform toolchain defaults, these SDK flags precede the early props phase.
        // Later ordinary project/import assignments remain authoritative.
        properties["UsingMicrosoftNETSdk"] = new("true", true);
        properties["UsingNETSdkDefaults"] = new("true", true);
    }
}
