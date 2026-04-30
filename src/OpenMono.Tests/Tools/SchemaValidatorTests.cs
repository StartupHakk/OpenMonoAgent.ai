using System.Text.Json;
using FluentAssertions;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class SchemaValidatorTests
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement;

    private static readonly JsonElement BashSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "command":    { "type": "string", "minLength": 1 },
            "timeout_ms": { "type": "integer", "minimum": 1, "maximum": 600000 }
        },
        "required": ["command"]
    }
    """);

    [Fact]
    public void ValidInput_ReturnsNull()
    {
        var input = Schema("""{"command": "ls", "timeout_ms": 5000}""");
        SchemaValidator.Validate("Bash", BashSchema, input).Should().BeNull();
    }

    [Fact]
    public void MissingRequiredField_ReturnsError()
    {
        var input = Schema("""{"timeout_ms": 5000}""");
        var result = SchemaValidator.Validate("Bash", BashSchema, input);
        result.Should().Contain("missing required field 'command'");
    }

    [Fact]
    public void NullRequiredField_ReturnsError()
    {
        var input = Schema("""{"command": null}""");
        var result = SchemaValidator.Validate("Bash", BashSchema, input);
        result.Should().Contain("missing required field 'command'");
    }

    [Fact]
    public void WrongType_ReturnsError()
    {
        var input = Schema("""{"command": "ls", "timeout_ms": "fast"}""");
        var result = SchemaValidator.Validate("Bash", BashSchema, input);
        result.Should().Contain("expected type 'integer'");
    }

    [Fact]
    public void NumberWhereIntegerExpected_ReturnsError()
    {
        var input = Schema("""{"command": "ls", "timeout_ms": 1.5}""");
        var result = SchemaValidator.Validate("Bash", BashSchema, input);
        result.Should().Contain("expected type 'integer'");
    }

    [Fact]
    public void BelowMinimum_ReturnsError()
    {
        var input = Schema("""{"command": "ls", "timeout_ms": 0}""");
        var result = SchemaValidator.Validate("Bash", BashSchema, input);
        result.Should().Contain("below minimum");
    }

    [Fact]
    public void AboveMaximum_ReturnsError()
    {
        var input = Schema("""{"command": "ls", "timeout_ms": 999999}""");
        var result = SchemaValidator.Validate("Bash", BashSchema, input);
        result.Should().Contain("above maximum");
    }

    [Fact]
    public void StringBelowMinLength_ReturnsError()
    {
        var input = Schema("""{"command": ""}""");
        var result = SchemaValidator.Validate("Bash", BashSchema, input);
        result.Should().Contain("shorter than minLength");
    }

    [Fact]
    public void EnumViolation_ReturnsError()
    {
        var schema = Schema("""
        {
            "type": "object",
            "properties": { "mode": { "type": "string", "enum": ["fast", "safe"] } },
            "required": ["mode"]
        }
        """);
        var input = Schema("""{"mode": "yolo"}""");
        var result = SchemaValidator.Validate("Test", schema, input);
        result.Should().Contain("must be one of");
    }

    [Fact]
    public void EnumValid_ReturnsNull()
    {
        var schema = Schema("""
        {
            "type": "object",
            "properties": { "mode": { "type": "string", "enum": ["fast", "safe"] } },
            "required": ["mode"]
        }
        """);
        var input = Schema("""{"mode": "fast"}""");
        SchemaValidator.Validate("Test", schema, input).Should().BeNull();
    }

    [Fact]
    public void PatternMismatch_ReturnsError()
    {
        var schema = Schema("""
        {
            "type": "object",
            "properties": { "id": { "type": "string", "pattern": "^[a-z]+$" } }
        }
        """);
        var input = Schema("""{"id": "ABC123"}""");
        var result = SchemaValidator.Validate("Test", schema, input);
        result.Should().Contain("does not match pattern");
    }

    [Fact]
    public void ArrayItemTypeViolation_ReturnsError()
    {
        var schema = Schema("""
        {
            "type": "object",
            "properties": { "tags": { "type": "array", "items": { "type": "string" } } }
        }
        """);
        var input = Schema("""{"tags": ["ok", 42]}""");
        var result = SchemaValidator.Validate("Test", schema, input);
        result.Should().Contain("tags[1]");
        result.Should().Contain("expected type 'string'");
    }

    [Fact]
    public void NonObjectInput_ReturnsError()
    {
        var input = Schema("""[1,2,3]""");
        var result = SchemaValidator.Validate("Test", BashSchema, input);
        result.Should().Contain("expected JSON object");
    }

    [Fact]
    public void UnknownExtraFields_AreIgnored()
    {

        var input = Schema("""{"command": "ls", "made_up": true}""");
        SchemaValidator.Validate("Bash", BashSchema, input).Should().BeNull();
    }
}
