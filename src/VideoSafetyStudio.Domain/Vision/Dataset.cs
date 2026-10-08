using System.Text.Json.Serialization;

namespace VideoSafetyStudio.Domain.Vision;

/// <summary>Where a source video came from (kind = upload | wan).</summary>
public sealed record VideoOrigin(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Filename = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Blob = null);

public sealed record VideoInfo(int Width, int Height, double Duration, double Fps);

public sealed record VideoSource(string Id, VideoOrigin Origin, int Width, int Height, double Duration, double Fps, int Frames = 0);

public sealed record FrameIndex(int Width, int Height, double Fps, IReadOnlyList<string> Frames);

public sealed record CocoImage(
    int Id,
    [property: JsonPropertyName("file_name")] string FileName,
    int Width,
    int Height,
    string Video);

public sealed record CocoAnnotation(
    int Id,
    [property: JsonPropertyName("image_id")] int ImageId,
    [property: JsonPropertyName("category_id")] int CategoryId,
    double[] Bbox,
    double Area,
    int Iscrowd);

public sealed record CocoCategory(int Id, string Name);

public sealed record CocoDataset(
    IReadOnlyList<CocoImage> Images,
    IReadOnlyList<CocoAnnotation> Annotations,
    IReadOnlyList<CocoCategory> Categories);

public sealed record SplitCounts(int Images, int Boxes);

public sealed record DatasetVideos(IReadOnlyList<string> Train, IReadOnlyList<string> Val);

public sealed record DatasetManifest(
    string Version,
    IReadOnlyList<string> Classes,
    int Seed,
    double ValFraction,
    DatasetVideos Videos,
    IReadOnlyDictionary<string, SplitCounts> Counts,
    IReadOnlyList<string> Warnings,
    string CreatedAt);
