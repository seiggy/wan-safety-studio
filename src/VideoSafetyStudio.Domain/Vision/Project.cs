using System.Text.Json.Serialization;

namespace VideoSafetyStudio.Domain.Vision;

public sealed record Project(string Name, IReadOnlyList<string> Classes)
{
    public static Project Create(string? name, IReadOnlyList<string>? classes)
    {
        Names.Check(name);
        if (classes is null || classes.Count is < 1 or > 64)
        {
            throw new ValidationException("Define between 1 and 64 classes.");
        }

        foreach (var item in classes)
        {
            Names.Check(item, "class");
        }

        if (classes.Distinct().Count() != classes.Count)
        {
            throw new ValidationException("Class names must be unique.");
        }

        return new Project(name!, classes);
    }
}

/// <summary>A labelled box in pixel coordinates of the source frame.</summary>
public sealed record Box(
    [property: JsonPropertyName("cls")] string Cls,
    double X,
    double Y,
    double W,
    double H,
    string State = "reviewed",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Score = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Track = null)
{
    private static readonly string[] States = ["auto", "reviewed"];

    public Box Checked(IReadOnlyCollection<string> classes, double width, double height)
    {
        if (!classes.Contains(Cls))
        {
            throw new ValidationException($"Unknown class '{Cls}'.");
        }

        if (!States.Contains(State))
        {
            throw new ValidationException("Box state must be auto or reviewed.");
        }

        if (!(W > 0 && H > 0 && X >= 0 && Y >= 0 && X + W <= width + 0.5 && Y + H <= height + 0.5))
        {
            throw new ValidationException("Box must have positive size and lie inside the frame.");
        }

        return this;
    }
}

public sealed record FrameLabels(bool Reviewed, IReadOnlyList<Box> Boxes)
{
    public static FrameLabels Empty { get; } = new(false, []);
}
