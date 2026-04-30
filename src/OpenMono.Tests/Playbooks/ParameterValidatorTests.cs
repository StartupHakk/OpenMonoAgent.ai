using FluentAssertions;
using OpenMono.Playbooks;

namespace OpenMono.Tests.Playbooks;

public class ParameterValidatorTests
{
    [Fact]
    public void Validate_AllRequiredPresent_ReturnsNull()
    {
        var playbook = new PlaybookDefinition
        {
            Name = "test",
            Description = "test",
            Parameters = new()
            {
                ["name"] = new() { Type = ParameterType.String, Required = true },
            },
        };
        var parameters = new Dictionary<string, object> { ["name"] = "hello" };

        ParameterValidator.Validate(playbook, parameters).Should().BeNull();
    }

    [Fact]
    public void Validate_MissingRequired_ReturnsError()
    {
        var playbook = new PlaybookDefinition
        {
            Name = "test",
            Description = "test",
            Parameters = new()
            {
                ["name"] = new() { Type = ParameterType.String, Required = true },
            },
        };
        var parameters = new Dictionary<string, object>();

        ParameterValidator.Validate(playbook, parameters).Should().Contain("name");
    }

    [Fact]
    public void Validate_AppliesDefault_WhenMissing()
    {
        var playbook = new PlaybookDefinition
        {
            Name = "test",
            Description = "test",
            Parameters = new()
            {
                ["scope"] = new() { Type = ParameterType.String, Required = false, Default = "auto" },
            },
        };
        var parameters = new Dictionary<string, object>();

        ParameterValidator.Validate(playbook, parameters).Should().BeNull();
        parameters["scope"].Should().Be("auto");
    }

    [Fact]
    public void Validate_EnumViolation_ReturnsError()
    {
        var playbook = new PlaybookDefinition
        {
            Name = "test",
            Description = "test",
            Parameters = new()
            {
                ["level"] = new() { Type = ParameterType.String, Required = true, Enum = ["low", "high"] },
            },
        };
        var parameters = new Dictionary<string, object> { ["level"] = "extreme" };

        ParameterValidator.Validate(playbook, parameters).Should().Contain("one of");
    }

    [Fact]
    public void Validate_NumberRange_ReturnsError()
    {
        var playbook = new PlaybookDefinition
        {
            Name = "test",
            Description = "test",
            Parameters = new()
            {
                ["coverage"] = new() { Type = ParameterType.Number, Required = true, Min = 0, Max = 100 },
            },
        };
        var parameters = new Dictionary<string, object> { ["coverage"] = 150.0 };

        ParameterValidator.Validate(playbook, parameters).Should().Contain("<=");
    }

    [Fact]
    public void Validate_WrongType_ReturnsError()
    {
        var playbook = new PlaybookDefinition
        {
            Name = "test",
            Description = "test",
            Parameters = new()
            {
                ["count"] = new() { Type = ParameterType.Number, Required = true },
            },
        };
        var parameters = new Dictionary<string, object> { ["count"] = "not-a-number" };

        ParameterValidator.Validate(playbook, parameters).Should().Contain("number");
    }

    [Fact]
    public void Validate_NoParameters_AlwaysValid()
    {
        var playbook = new PlaybookDefinition { Name = "test", Description = "test" };
        var parameters = new Dictionary<string, object>();

        ParameterValidator.Validate(playbook, parameters).Should().BeNull();
    }
}
