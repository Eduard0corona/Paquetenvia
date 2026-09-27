using Dispatch.Application.Assignments;
using Paqueteria.ContractTests.Support;
using YamlDotNet.RepresentationModel;

namespace Paqueteria.ContractTests.Normative;

/// <summary>
/// D8: the Dispatch closure policy covers exactly the AI-04 order state machine, so a new or removed
/// AI-04 transition fails here until the D8 map is decided for it.
/// </summary>
public sealed class AssignmentLifecycleAi04CoverageTests
{
    [Fact]
    public void Closure_policy_covers_every_ai04_order_transition_and_nothing_else()
    {
        var domain = YamlNodes.LoadMapping(RepositoryPaths.Normative("specs", "AI-04_DOMAIN_MODEL.yaml"));
        var transitions = domain.Mapping("order_state_machine").Mapping("transitions");
        var ai04 = transitions.Children
            .SelectMany(pair => ((YamlSequenceNode)pair.Value).Children
                .Cast<YamlScalarNode>()
                .Select(target => (((YamlScalarNode)pair.Key).Value!, target.Value!)))
            .ToHashSet();

        Assert.Equal(30, ai04.Count);
        Assert.True(
            ai04.SetEquals(AssignmentLifecyclePolicy.Transitions),
            $"AI-04 only: {string.Join(", ", ai04.Except(AssignmentLifecyclePolicy.Transitions))}; " +
            $"policy only: {string.Join(", ", AssignmentLifecyclePolicy.Transitions.Except(ai04))}");
    }
}
