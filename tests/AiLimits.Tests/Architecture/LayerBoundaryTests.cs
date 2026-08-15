// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Refresh;
using AiLimits.Domain;
using AiLimits.Infrastructure.Persistence;
using AiLimits.Platform.Windows.Security;
using AiLimits.Presentation.WinUI.ViewModels;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using ArchitectureModel = ArchUnitNET.Domain.Architecture;

namespace AiLimits.Tests.Architecture;

/// <summary>
/// Fitness functions for the layered solution. Domain stays ignorant of
/// everything else; Application talks only to Domain; Infrastructure and
/// Platform implement Application abstractions; Presentation binds view
/// models to Application and Domain without reaching into SQLite or
/// provider adapters.
/// </summary>
public sealed class LayerBoundaryTests
{
    private static readonly ArchitectureModel Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(ProviderId).Assembly,
            typeof(RefreshCoordinator).Assembly,
            typeof(SqliteDatabase).Assembly,
            typeof(WindowsCredentialSecretStore).Assembly,
            typeof(LiveDashboardViewModel).Assembly
        )
        .Build();

    [Fact]
    public void Domain_does_not_depend_on_outer_layers()
    {
        Types()
            .That()
            .ResideInNamespace(@"^AiLimits\.Domain($|\.)", true)
            .Should()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.Application($|\.)", true))
            .AndShould()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.Infrastructure($|\.)", true))
            .AndShould()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.Presentation($|\.)", true))
            .AndShould()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.Platform($|\.)", true))
            .AndShould()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.App($|\.)", true))
            .Because("Domain models are the inner circle and must compile alone.")
            .Check(Architecture);
    }

    [Fact]
    public void Application_depends_only_on_domain()
    {
        Types()
            .That()
            .ResideInNamespace(@"^AiLimits\.Application($|\.)", true)
            .Should()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.Infrastructure($|\.)", true))
            .AndShould()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.Presentation($|\.)", true))
            .AndShould()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.Platform($|\.)", true))
            .AndShould()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.App($|\.)", true))
            .Because("Application orchestrates through abstractions; adapters live outside it.")
            .Check(Architecture);
    }

    [Fact]
    public void Presentation_does_not_reach_into_infrastructure_or_platform()
    {
        Types()
            .That()
            .ResideInNamespace(@"^AiLimits\.Presentation($|\.)", true)
            .Should()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.Infrastructure($|\.)", true))
            .AndShould()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.Platform($|\.)", true))
            .AndShould()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.App($|\.)", true))
            .Because("WinUI binds to Application view-model contracts, not SQLite or Win32 adapters.")
            .Check(Architecture);
    }

    [Fact]
    public void Platform_does_not_depend_on_infrastructure_or_ui()
    {
        Types()
            .That()
            .ResideInNamespace(@"^AiLimits\.Platform($|\.)", true)
            .Should()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.Infrastructure($|\.)", true))
            .AndShould()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.Presentation($|\.)", true))
            .AndShould()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.App($|\.)", true))
            .Because("Win32 adapters implement Application ports and stay UI-agnostic.")
            .Check(Architecture);
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_ui_or_app_host()
    {
        Types()
            .That()
            .ResideInNamespace(@"^AiLimits\.Infrastructure($|\.)", true)
            .Should()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.Presentation($|\.)", true))
            .AndShould()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.App($|\.)", true))
            .AndShould()
            .NotDependOnAny(Types().That().ResideInNamespace(@"^AiLimits\.Platform($|\.)", true))
            .Because("Provider adapters and SQLite repositories must stay hostable from tests.")
            .Check(Architecture);
    }
}
