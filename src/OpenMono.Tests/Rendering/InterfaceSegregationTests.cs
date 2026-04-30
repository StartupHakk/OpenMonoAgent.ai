using System.Reflection;
using FluentAssertions;
using OpenMono.Permissions;
using OpenMono.Rendering;

namespace OpenMono.Tests.Rendering;

public sealed class InterfaceSegregationTests
{
    [Fact]
    public void IRenderer_IsZeroMemberComposite()
    {
        typeof(IRenderer)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Should().BeEmpty("IRenderer is a composite alias — it must not declare new members");
    }

    [Fact]
    public void TerminalRenderer_HasNoDefaultInterfaceMethodFallthrough()
    {
        var declaredMethods = typeof(TerminalRenderer)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet();

        var requiredMethods = typeof(IOutputSink)
            .GetMethods()
            .Concat(typeof(IInputReader).GetMethods())
            .Select(m => m.Name);

        foreach (var name in requiredMethods)
            declaredMethods.Should().Contain(name,
                $"TerminalRenderer must explicitly implement {name}");
    }

    [Fact]
    public void PermissionEngine_DoesNotDependOnIRenderer()
    {
        var ctors = typeof(PermissionEngine).GetConstructors();
        foreach (var ctor in ctors)
        {
            ctor.GetParameters()
                .Should().NotContain(p => p.ParameterType == typeof(IRenderer),
                    "PermissionEngine must depend on IOutputSink+IInputReader, not IRenderer");
        }
    }
}
