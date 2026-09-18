using System.Reflection;
using System.Xml.Serialization;
using Jellyfin.Plugin.Danmuku.Api;
using Jellyfin.Plugin.Danmuku.Configuration;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Jellyfin.Plugin.Danmuku.Tests;

public sealed class PluginContractTests
{
    [Fact]
    public void DashboardResourceUsesOwnPluginIdentity()
    {
        Assert.NotEqual(Guid.Empty, Guid.Parse(Plugin.PluginId));
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(
            "Jellyfin.Plugin.Danmuku.Configuration.configPage.html");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();
        Assert.Contains(Plugin.PluginId, html);
        Assert.DoesNotContain("AgentBridge", html);
    }

    [Fact]
    public void ConfigurationRoundTripsThroughXml()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new PluginConfiguration { InstanceLabel = "测试 & <label>" });
        using var reader = new StringReader(writer.ToString());
        var config = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));
        Assert.Equal("测试 & <label>", config.InstanceLabel);
    }

    [Fact]
    public void HealthRequiresElevationAndReportsOwnIdentity()
    {
        var auth = typeof(HealthController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal(Policies.RequiresElevation, auth?.Policy);
        var health = new HealthController().Get().Value;
        Assert.NotNull(health);
        Assert.Equal("Danmuku", health.Plugin);
        Assert.Equal("ok", health.Status);
    }

    [Fact]
    public void AssemblyDoesNotReferenceSiblingPlugin()
    {
        Assert.DoesNotContain(typeof(Plugin).Assembly.GetReferencedAssemblies(),
            reference => reference.Name == "Jellyfin.Plugin.AgentBridge");
    }
}
