using FluentAssertions;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class ToolResultTests
{

    [Fact]
    public void LegacySuccess_ContentProperty_ReturnsModelPreview()
    {
        var result = ToolResult.Success("hello world");

        result.Content.Should().Be("hello world");
        result.ModelPreview.Should().Be("hello world");
        result.IsError.Should().BeFalse();
        result.ErrorMessage.Should().BeNull();
        result.Class.Should().Be(ResultClass.Success);
    }

    [Fact]
    public void LegacyError_ContentProperty_ReturnsModelPreview()
    {
        var result = ToolResult.Error("something went wrong");

        result.Content.Should().Be("something went wrong");
        result.ModelPreview.Should().Be("something went wrong");
        result.IsError.Should().BeTrue();
        result.ErrorMessage.Should().Be("something went wrong");
        result.Class.Should().Be(ResultClass.InvalidInput);
    }

    [Fact]
    public void LegacySuccess_WithMetadata_PreservesMetadata()
    {
        var metadata = new Dictionary<string, object> { ["key"] = "value", ["count"] = 42 };
        var result = ToolResult.Success("content", metadata);

        result.Metadata.Should().NotBeNull();
        result.Metadata!["key"].Should().Be("value");
        result.Metadata!["count"].Should().Be(42);
    }

    [Theory]
    [InlineData(ResultClass.Success, false)]
    [InlineData(ResultClass.InvalidInput, true)]
    [InlineData(ResultClass.PermissionDenied, true)]
    [InlineData(ResultClass.StateConflict, true)]
    [InlineData(ResultClass.Crash, true)]
    [InlineData(ResultClass.Empty, true)]
    [InlineData(ResultClass.Cancelled, true)]
    public void IsError_ReflectsResultClass(ResultClass resultClass, bool expectedIsError)
    {
        var result = new ToolResult { ModelPreview = "test", Class = resultClass };

        result.IsError.Should().Be(expectedIsError);
    }

    [Fact]
    public void ErrorMessage_OnlySetWhenIsError()
    {
        var success = ToolResult.Success("good");
        var error = ToolResult.Error("bad");

        success.ErrorMessage.Should().BeNull();
        error.ErrorMessage.Should().Be("bad");
    }

    [Fact]
    public void InvalidInput_IncludesRetryHint()
    {
        var result = ToolResult.InvalidInput(
            "File not found: /nonexistent",
            "Check if the file path is correct. Did you mean '/existing/file.txt'?");

        result.Class.Should().Be(ResultClass.InvalidInput);
        result.RetryHint.Should().Contain("Did you mean");
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public void StateConflict_IncludesRetryHint()
    {
        var result = ToolResult.StateConflict(
            "File was modified since last read",
            "Re-read the file before editing to get the latest content.");

        result.Class.Should().Be(ResultClass.StateConflict);
        result.RetryHint.Should().Contain("Re-read");
    }

    [Fact]
    public void Crash_IncludesRetryHint()
    {
        var result = ToolResult.Crash(
            "Tool crashed: NullReferenceException",
            "Try with different parameters or report this as a bug.");

        result.Class.Should().Be(ResultClass.Crash);
        result.RetryHint.Should().Contain("report this as a bug");
    }

    [Fact]
    public void PermissionDenied_OptionalRetryHint()
    {
        var withHint = ToolResult.PermissionDenied("Access denied", "Request elevated permissions.");
        var withoutHint = ToolResult.PermissionDenied("Access denied");

        withHint.RetryHint.Should().NotBeNullOrEmpty();
        withoutHint.RetryHint.Should().BeNull();
    }

    [Fact]
    public void SuccessWithPayload_SeparatesModelViewFromMachineData()
    {
        var payload = new { matches = new[] { "file1.cs", "file2.cs" }, total = 2 };
        var result = ToolResult.SuccessWithPayload(
            "Found 2 matches in src/",
            payload);

        result.ModelPreview.Should().Be("Found 2 matches in src/");
        result.Content.Should().Be("Found 2 matches in src/");
        result.MachinePayload.Should().NotBeNull();

        var typed = (dynamic)result.MachinePayload!;
        ((int)typed.total).Should().Be(2);
    }

    [Fact]
    public void MachinePayload_CanBeComplexObject()
    {
        var payload = new GrepPayload(
            Matches: [
                new GrepMatch("src/a.cs", 10, "public class Foo"),
                new GrepMatch("src/b.cs", 20, "public class Bar")
            ],
            TotalFiles: 50,
            SearchTimeMs: 123
        );

        var result = ToolResult.SuccessWithPayload("2 matches", payload);

        var typed = result.MachinePayload as GrepPayload;
        typed.Should().NotBeNull();
        typed!.Matches.Should().HaveCount(2);
        typed.TotalFiles.Should().Be(50);
    }

    [Fact]
    public void WithArtifacts_AddsReferencesToResult()
    {
        var result = ToolResult.Success("Large output truncated")
            .WithArtifacts(
                new ArtifactRef("art-001", "build_log", 1024 * 1024, "/tmp/build.log"),
                new ArtifactRef("art-002", "test_output", 512 * 1024, "/tmp/tests.txt"));

        result.Artifacts.Should().HaveCount(2);
        result.Artifacts[0].Id.Should().Be("art-001");
        result.Artifacts[0].Kind.Should().Be("build_log");
        result.Artifacts[0].Bytes.Should().Be(1024 * 1024);
    }

    [Fact]
    public void WithArtifacts_ChainedCalls_Accumulate()
    {
        var result = ToolResult.Success("output")
            .WithArtifacts(new ArtifactRef("a", "log", 100, "/a"))
            .WithArtifacts(new ArtifactRef("b", "log", 200, "/b"));

        result.Artifacts.Should().HaveCount(2);
    }

    [Fact]
    public void Artifacts_DefaultsToEmptyList()
    {
        var result = ToolResult.Success("no artifacts");

        result.Artifacts.Should().NotBeNull();
        result.Artifacts.Should().BeEmpty();
    }

    [Fact]
    public void WithSideEffects_TracksFileWrites()
    {
        var result = ToolResult.Success("File created")
            .WithSideEffects(SideEffect.FileWrite("/src/new-file.cs", 1234));

        result.SideEffects.Should().HaveCount(1);
        result.SideEffects[0].Kind.Should().Be("file_write");
        result.SideEffects[0].Target.Should().Be("/src/new-file.cs");
        result.SideEffects[0].Meta["bytes"].Should().Be("1234");
    }

    [Fact]
    public void WithSideEffects_TracksProcessSpawn()
    {
        var result = ToolResult.Success("Process started")
            .WithSideEffects(SideEffect.ProcessSpawn("npm install", pid: 12345));

        result.SideEffects[0].Kind.Should().Be("process_spawn");
        result.SideEffects[0].Target.Should().Be("npm install");
        result.SideEffects[0].Meta["pid"].Should().Be("12345");
    }

    [Fact]
    public void WithSideEffects_TracksFileDelete()
    {
        var result = ToolResult.Success("Cleaned up")
            .WithSideEffects(SideEffect.FileDelete("/tmp/old-file.txt"));

        result.SideEffects[0].Kind.Should().Be("file_delete");
        result.SideEffects[0].Target.Should().Be("/tmp/old-file.txt");
    }

    [Fact]
    public void SideEffects_DefaultsToEmptyList()
    {
        var result = ToolResult.Success("read only");

        result.SideEffects.Should().NotBeNull();
        result.SideEffects.Should().BeEmpty();
    }

    [Fact]
    public void WithWarnings_AddsNonFatalIssues()
    {
        var result = ToolResult.Success("File read successfully")
            .WithWarnings(
                "File contains non-UTF8 characters, showing lossy decode",
                "File exceeds 10MB, only first 1MB shown");

        result.Warnings.Should().HaveCount(2);
        result.IsError.Should().BeFalse();
    }

    [Fact]
    public void Warnings_DefaultsToEmptyList()
    {
        var result = ToolResult.Success("clean");

        result.Warnings.Should().NotBeNull();
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void WithCacheKey_EnablesDedup()
    {
        var result = ToolResult.Success("file contents...")
            .WithCacheKey("file:/src/app.cs|mtime:12345|offset:0|limit:100");

        result.CacheKey.Should().Contain("file:/src/app.cs");
    }

    [Fact]
    public void CacheKey_DefaultsToNull()
    {
        var result = ToolResult.Success("no cache");

        result.CacheKey.Should().BeNull();
    }

    [Fact]
    public void Empty_IndicatesNoMeaningfulOutput()
    {
        var result = ToolResult.Empty("Grep returned no matches");

        result.Class.Should().Be(ResultClass.Empty);
        result.IsError.Should().BeTrue();
        result.ModelPreview.Should().Contain("no matches");
    }

    [Fact]
    public void Cancelled_IndicatesCancellationToken()
    {
        var result = ToolResult.Cancelled();

        result.Class.Should().Be(ResultClass.Cancelled);
        result.IsError.Should().BeTrue();
        result.ModelPreview.Should().Contain("cancelled");
    }

    [Fact]
    public void Cancelled_WithCustomMessage()
    {
        var result = ToolResult.Cancelled("User interrupted the operation");

        result.ModelPreview.Should().Be("User interrupted the operation");
    }

    [Fact]
    public void WithMethods_ReturnNewInstances_DoNotMutate()
    {
        var original = ToolResult.Success("original");
        var withWarning = original.WithWarnings("warning");
        var withArtifact = original.WithArtifacts(new ArtifactRef("a", "b", 0, "/"));

        original.Warnings.Should().BeEmpty();
        original.Artifacts.Should().BeEmpty();

        withWarning.Warnings.Should().HaveCount(1);
        withArtifact.Artifacts.Should().HaveCount(1);
    }

    [Fact]
    public void EmptyString_ModelPreview_IsValid()
    {
        var result = ToolResult.Success("");

        result.Content.Should().BeEmpty();
        result.ModelPreview.Should().BeEmpty();
        result.IsError.Should().BeFalse();
    }

    [Fact]
    public void VeryLongContent_IsPreserved()
    {
        var longContent = new string('x', 100_000);
        var result = ToolResult.Success(longContent);

        result.Content.Length.Should().Be(100_000);
    }

    [Fact]
    public void UnicodeContent_IsPreserved()
    {
        var unicode = "Hello 世界! Привет мир! 🚀💻";
        var result = ToolResult.Success(unicode);

        result.Content.Should().Be(unicode);
    }

    [Fact]
    public void NullMachinePayload_IsAllowed()
    {
        var result = ToolResult.Success("no payload");

        result.MachinePayload.Should().BeNull();
    }

    [Fact]
    public void FullyPopulatedResult_AllFieldsAccessible()
    {
        var result = ToolResult.SuccessWithPayload("Build completed", new { exitCode = 0 })
            .WithWarnings("Deprecated API used", "Missing type annotations")
            .WithSideEffects(
                SideEffect.FileWrite("/out/app.dll", 50000),
                SideEffect.ProcessSpawn("dotnet build", 9999))
            .WithArtifacts(new ArtifactRef("log-001", "build_log", 10000, "/tmp/build.log"))
            .WithCacheKey("build:hash:abc123");

        result.ModelPreview.Should().Be("Build completed");
        result.Content.Should().Be("Build completed");
        result.IsError.Should().BeFalse();
        result.Class.Should().Be(ResultClass.Success);
        result.MachinePayload.Should().NotBeNull();
        result.Warnings.Should().HaveCount(2);
        result.SideEffects.Should().HaveCount(2);
        result.Artifacts.Should().HaveCount(1);
        result.CacheKey.Should().Be("build:hash:abc123");
    }

    private sealed record GrepPayload(
        IReadOnlyList<GrepMatch> Matches,
        int TotalFiles,
        int SearchTimeMs);

    private sealed record GrepMatch(string File, int Line, string Snippet);
}
