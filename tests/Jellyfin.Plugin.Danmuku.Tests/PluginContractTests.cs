using System.Reflection;
using System.Text.Json;
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
        Assert.False(new PluginConfiguration().EnableWebSupport);
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new PluginConfiguration { InstanceLabel = "测试 & <label>", EnableWebSupport = true });
        using var reader = new StringReader(writer.ToString());
        var config = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));
        Assert.Equal("测试 & <label>", config.InstanceLabel);
        Assert.True(config.EnableWebSupport);
    }

    [Fact]
    public void WebResourcesAreEmbeddedInPluginAssembly()
    {
        var resourceNames = new[]
        {
            WebResourceController.BootstrapResourceName,
            WebResourceController.ScriptResourceName,
            WebResourceController.LayoutResourceName,
            WebResourceController.StylesheetResourceName
        };

        foreach (var resourceName in resourceNames)
        {
            using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            Assert.True(stream!.Length > 0);
        }
    }

    [Fact]
    public void WebStatusExposesOnlyEnabledAndResourceVersion()
    {
        var controller = new WebResourceController(new PluginConfiguration { EnableWebSupport = true });
        var status = controller.GetStatus().Value;
        Assert.NotNull(status);
        Assert.True(status.Enabled);
        Assert.Equal(WebResourceController.ResourceVersion, status.ResourceVersion);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(status));
        var properties = document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();
        Assert.Equal(new[] { "enabled", "resourceVersion" }, properties);
        Assert.True(document.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(WebResourceController.ResourceVersion, document.RootElement.GetProperty("resourceVersion").GetString());
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
