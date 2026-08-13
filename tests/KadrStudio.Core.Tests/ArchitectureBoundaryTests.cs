using System.Reflection;
using KadrStudio.Application.Editing;
using KadrStudio.Core.Domain;
using KadrStudio.Infrastructure.Storage;

namespace KadrStudio.Core.Tests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void Core_has_no_dependency_on_outer_layers_or_desktop_frameworks()
    {
        var references = ReferencedAssemblies(typeof(ProjectState).Assembly);

        Assert.DoesNotContain("KadrStudio.Application", references);
        Assert.DoesNotContain("KadrStudio.Infrastructure", references);
        Assert.DoesNotContain("KadrStudio", references);
        Assert.DoesNotContain("PresentationFramework", references);
        Assert.DoesNotContain("PresentationCore", references);
        Assert.DoesNotContain("WindowsBase", references);
    }

    [Fact]
    public void Application_depends_on_core_but_not_infrastructure_or_wpf()
    {
        var references = ReferencedAssemblies(typeof(EditorSession).Assembly);

        Assert.Contains("KadrStudio.Core", references);
        Assert.DoesNotContain("KadrStudio.Infrastructure", references);
        Assert.DoesNotContain("KadrStudio", references);
        Assert.DoesNotContain("PresentationFramework", references);
    }

    [Fact]
    public void Infrastructure_depends_inward_and_never_on_desktop_ui()
    {
        var references = ReferencedAssemblies(typeof(SqliteProjectStore).Assembly);

        Assert.Contains("KadrStudio.Core", references);
        Assert.Contains("KadrStudio.Application", references);
        Assert.DoesNotContain("KadrStudio", references);
        Assert.DoesNotContain("PresentationFramework", references);
        Assert.DoesNotContain("PresentationCore", references);
    }

    private static HashSet<string> ReferencedAssemblies(Assembly assembly)
        => assembly.GetReferencedAssemblies()
            .Select(item => item.Name)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
}
