using System.Reflection;
using HeadsUp.Models;
using HeadsUp.Services;
using NetArchTest.Rules;
using Xunit;

namespace HeadsUp.Tests;

/// <summary>
/// Locks the layer boundaries so they can't silently rot. The project references already
/// stop a layer referencing one it shouldn't at compile time (Domain references no other
/// HeadsUp project); these assertions add the framework-purity guarantees on top — that the
/// domain stays free of infrastructure/UI frameworks and the application never reaches into
/// Slack infrastructure.
/// </summary>
public class ArchitectureTests
{
    // A representative type pins each layer's assembly (namespaces are shared across layers,
    // so we resolve by assembly, not namespace).
    private static readonly Assembly Domain = typeof(Alert).Assembly;               // HeadsUp.Domain
    private static readonly Assembly Application = typeof(AlertLinks).Assembly;      // HeadsUp.Application

    [Fact]
    public void Domain_is_free_of_infrastructure_and_ui_frameworks()
    {
        var result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny("SlackNet", "Microsoft.Extensions", "CommunityToolkit")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Application_does_not_depend_on_slack_infrastructure()
    {
        var result = Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOn("SlackNet")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        result.IsSuccessful
            ? string.Empty
            : "Offending types: " + string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>());
}
