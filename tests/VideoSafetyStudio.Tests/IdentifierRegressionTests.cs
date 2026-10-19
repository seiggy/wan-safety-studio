using Microsoft.Extensions.Options;
using VideoSafetyStudio.Application;
using VideoSafetyStudio.Domain;
using VideoSafetyStudio.Domain.Training;
using VideoSafetyStudio.Domain.Vision;
using VideoSafetyStudio.Infrastructure.Trainers;

namespace VideoSafetyStudio.Tests;

public sealed class IdentifierRegressionTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\nmiddle")]
    [InlineData("\r\nmiddle")]
    public void All_identifiers_reject_trailing_or_embedded_line_breaks(string suffix)
    {
        Assert.Throws<ValidationException>(() => Names.Check("ab" + suffix));
        Assert.Throws<ValidationException>(() => Names.CheckVideo("v0123456789ab" + suffix));
        Assert.Throws<ValidationException>(() => Names.CheckFrame("000001" + suffix));
        using var workspace = new VisionRegressionWorkspace();
        var registry = Registry(workspace, "ab" + suffix);
        Assert.Throws<ValidationException>(() => registry.All());
    }

    [Theory]
    [InlineData("\nab")]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    public void Project_and_trainer_identifiers_reject_newlines_inside_the_grammar(string name)
    {
        Assert.Throws<ValidationException>(() => Names.Check(name));
        using var workspace = new VisionRegressionWorkspace();
        Assert.Throws<ValidationException>(() => Registry(workspace, name).All());
    }

    [Fact]
    public void Valid_boundary_lengths_are_preserved_without_trimming_or_aliasing()
    {
        Assert.Equal("a0", Names.Check("a0"));
        Assert.Equal(new string('a', 63), Names.Check(new string('a', 63)));
        Assert.Throws<ValidationException>(() => Names.Check("a"));
        Assert.Throws<ValidationException>(() => Names.Check(new string('a', 64)));
        Assert.Equal("v0123456789ab", Names.CheckVideo("v0123456789ab"));
        Assert.Equal("000001", Names.CheckFrame("000001"));
        using var workspace = new VisionRegressionWorkspace();
        Assert.Equal("a0", Assert.Single(Registry(workspace, "a0").All()).Name);
        Assert.Equal(new string('a', 41), Assert.Single(Registry(workspace, new string('a', 41)).All()).Name);
        Assert.Throws<ValidationException>(() => Registry(workspace, new string('a', 42)).All());
        Assert.Throws<ValidationException>(() => Registry(workspace, "a").All());
    }

    private static FileTrainerRegistry Registry(VisionRegressionWorkspace workspace, string name)
    {
        var folder = Path.Combine(workspace.Root, "trainers", "candidate");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "trainer.json"),
            Json.ToBytes(new TrainerManifest(name, "1", "test:1", "MIT", "MIT", ["nano"], "nano")));
        return new(Options.Create(new TrainerOptions { BuiltIn = Path.Combine(workspace.Root, "trainers") }));
    }
}
