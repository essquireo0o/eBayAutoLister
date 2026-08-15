using System.Xml.Linq;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// credentials.json holds the build machine's live secrets — the eBay app client secret, the
/// seller's OAuth refresh token, the Anthropic key, the Stripe secret key and webhook secret — and
/// it lives in the project folder next to the .csproj. The Web SDK's default Content glob picks up
/// every .json under the project, so with no exclusion `dotnet publish` copies that file into the
/// output folder, and a publish is exactly how the hosted build gets onto a server somebody else
/// operates.
///
/// Nothing at runtime fails when the exclusion is deleted — the app reads credentials.json from
/// beside the exe either way, so every test still passes and every build still works, and the only
/// visible difference is one extra file in a directory nobody lists. That is why this is asserted
/// against the project file itself rather than against behaviour, in the manner of
/// DealBoardLayoutTests: it is the only place the loss shows up.
/// </summary>
public class PublishSecretsTests
{
    private static readonly XDocument Csproj = ReadCsproj();

    /// <summary>
    /// Every item type that would otherwise carry the file into publish output. Content is the one
    /// the Web SDK's glob actually assigns it; None is removed alongside so the same file cannot
    /// come back through the other default glob.
    /// </summary>
    [Theory]
    [InlineData("Content")]
    [InlineData("None")]
    public void TheBuildMachinesSecretsAreNotCopiedIntoPublishOutput(string itemType)
    {
        var removals = Removals(itemType);

        Assert.True(removals.Any(IsCredentialsJson),
            $"""
             <{itemType} Remove="credentials.json" /> is gone from the app's .csproj, so publish
             output carries the live credentials.json again — the eBay client secret, the OAuth
             refresh token, the Anthropic key and the Stripe secret key, in a folder that gets
             uploaded to a server. Removals still present for {itemType}: {string.Join(", ", removals)}
             """);
    }

    /// <summary>
    /// The desktop build publishes into "ING eBay AutoLister\dist" — inside the project folder, so
    /// inside the default glob. That folder gets a full copy of the unredacted credentials.json, and
    /// a hosted publish was picking it up as dist\credentials.json even after the project-root file
    /// was excluded: the top of the output directory looked clean while the secrets rode along one
    /// level down. Excluding the bare filename is not enough; the removal has to be recursive.
    /// </summary>
    [Theory]
    [InlineData("Content")]
    [InlineData("None")]
    public void NorAreTheCopiesOfThemLeftInNestedBuildFolders(string itemType)
    {
        var removals = Removals(itemType);

        Assert.True(removals.Any(CoversNestedCopies),
            $"""
             the {itemType} removal for credentials.json no longer covers subdirectories, so a copy
             of it in a nested build folder — "ING eBay AutoLister\dist", which build-installer.ps1
             publishes into and which holds the file with every secret still in it — is published
             again. Expected a recursive spec such as **\credentials.json. Removals still present
             for {itemType}: {string.Join(", ", removals)}
             """);
    }

    [Fact]
    public void TheExclusionAppliesToEveryConfigurationAndNotJustTheHostedOne()
    {
        // A secret that only stays out of one of the two builds is a leaked secret: the desktop
        // publish output is copied wholesale into the installer's dist folder. So the removal must
        // sit in an unconditional ItemGroup — no Condition on the group and none on the item.
        var conditioned = Csproj.Root!.Elements("ItemGroup")
            .SelectMany(group => group.Elements("Content").Concat(group.Elements("None"))
                .Where(item => IsCredentialsJson((string?)item.Attribute("Remove") ?? ""))
                .Select(item => (Group: group, Item: item)))
            .Where(x => x.Group.Attribute("Condition") is not null || x.Item.Attribute("Condition") is not null)
            .Select(x => $"<{x.Item.Name.LocalName}> under condition " +
                         $"'{(string?)x.Item.Attribute("Condition") ?? (string?)x.Group.Attribute("Condition")}'")
            .ToList();

        Assert.True(conditioned.Count == 0,
            "the credentials.json exclusion is conditional, so some configuration still publishes " +
            $"the file: {string.Join("; ", conditioned)}");
    }

    /// <summary>
    /// Matches the attribute against the file it names rather than against the literal string, so a
    /// future edit may write it as <c>.\credentials.json</c>, use forward slashes, or fold it into a
    /// semicolon-separated list without this failing for a reason that isn't a real one.
    /// </summary>
    private static bool IsCredentialsJson(string removeAttribute) =>
        Specs(removeAttribute).Any(spec =>
            spec.Equals("credentials.json", StringComparison.OrdinalIgnoreCase));

    /// <summary>A spec that also reaches the copies of it sitting in subdirectories.</summary>
    private static bool CoversNestedCopies(string removeAttribute) =>
        Specs(removeAttribute).Any(spec =>
            spec.EndsWith("/credentials.json", StringComparison.OrdinalIgnoreCase)
            && spec.Contains("**", StringComparison.Ordinal));

    /// <summary>The individual globs in one Remove attribute, path separators normalised.</summary>
    private static IEnumerable<string> Specs(string removeAttribute) =>
        removeAttribute.Split(';')
            .Select(spec => spec.Trim().Replace('\\', '/'))
            .Select(spec => spec.StartsWith("./", StringComparison.Ordinal) ? spec[2..] : spec)
            .Where(spec => spec.Length > 0);

    private static List<string> Removals(string itemType) =>
        Csproj.Root!.Elements("ItemGroup")
            .Elements(itemType)
            .Select(item => (string?)item.Attribute("Remove"))
            .Where(remove => remove is not null)
            .Select(remove => remove!)
            .ToList();

    private static XDocument ReadCsproj()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, "ING eBay AutoLister", "ING eBay AutoLister.csproj");
        Assert.True(File.Exists(path), "missing project file: " + path);
        return XDocument.Load(path);
    }
}
