using PROCTOR.Server.Services;
using PROCTOR.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace PROCTOR.Tests;

/// <summary>Version comparison for update offers, and what the server is willing to hand agents.</summary>
public class UpdateTests
{
    [Theory]
    [InlineData("v0.4.1", 0, 4, 1)]
    [InlineData("0.4.1", 0, 4, 1)]
    [InlineData("V1.2", 1, 2, 0)]
    [InlineData("1.2.3-beta.1", 1, 2, 3)]
    [InlineData("1.2.3+abc123", 1, 2, 3)]
    [InlineData(" 2.0.10 ", 2, 0, 10)]
    public void Release_tags_parse_to_three_part_versions(string text, int major, int minor, int build) =>
        Assert.Equal(new Version(major, minor, build), ProductVersion.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    public void Non_versions_are_rejected(string? text) => Assert.Null(ProductVersion.Parse(text));

    [Fact]
    public void Only_a_higher_version_counts_as_an_update()
    {
        var current = ProductVersion.Current;
        Assert.True(ProductVersion.IsNewer(new Version(current.Major, current.Minor, current.Build + 1)));
        Assert.False(ProductVersion.IsNewer(current));
        Assert.False(ProductVersion.IsNewer(new Version(0, 0, 1)));
        Assert.False(ProductVersion.IsNewer(null));
    }

    [Fact]
    public void All_parts_share_the_version_from_Directory_Build_props()
    {
        // ProctorVersion flows into every assembly; 0.0.0 would mean it didn't.
        Assert.True(ProductVersion.Current >= new Version(0, 4, 0));
        Assert.Equal(ProductVersion.Text, PROCTOR.Agent.AgentWorker.AgentVersion);
    }

    [Fact]
    public void Nothing_is_offered_when_no_agent_package_is_installed()
    {
        var package = Package(Path.Combine(Path.GetTempPath(), "proctor-no-such-dir-" + Guid.NewGuid().ToString("N")));
        Assert.Null(package.Info);
        Assert.Null(package.FilePath);
    }

    [Fact]
    public void A_package_in_a_user_writable_folder_is_never_offered()
    {
        // Agents run this MSI as SYSTEM: one sitting in a folder the current (non-admin) user can
        // write, such as a build output or %TEMP%, must not be handed out.
        var dir = Path.Combine(Path.GetTempPath(), "proctor-agent-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "PROCTOR.Agent.msi"), [1, 2, 3, 4]);
            File.WriteAllText(Path.Combine(dir, "version.txt"), "99.0.0");
            var package = Package(dir);
            if (OperatingSystem.IsWindows()) Assert.Null(package.Info);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static AgentUpdatePackage Package(string dir) =>
        new(NullLogger<AgentUpdatePackage>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["PROCTOR:AgentUpdateDir"] = dir }).Build());
}
