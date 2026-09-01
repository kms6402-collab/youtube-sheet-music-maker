using System.IO;
using System.Text.Json;

namespace ScoreCap.Services.Project;

/// <summary>Serializes/deserializes .scap project files (JSON) so a capture session can be reopened later.</summary>
public class ProjectService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public void Save(string path, ProjectFile project)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(project, JsonOptions);
        File.WriteAllText(path, json);
    }

    public ProjectFile Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ProjectFile>(json, JsonOptions)
               ?? throw new InvalidOperationException("프로젝트 파일을 읽을 수 없습니다.");
    }
}
