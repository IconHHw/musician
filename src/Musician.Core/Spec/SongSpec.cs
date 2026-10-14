using System.Text.Json;
using System.Text.Json.Serialization;

// 命名空间刻意不叫 SongSpec：否则会和同名类型 SongSpec 撞车，
// 编译器会把 SongSpec 解析成命名空间而不是类。
namespace Musician.Core.Spec;

/// <summary>
/// 乐谱规格 —— **整个项目的稳定接口**。
///
/// 这份 JSON 由「作曲的一方」撰写（当前是 Claude Code 会话，将来也可以换成
/// 直接调 API 的程序），下游的校验、编译、渲染全都是确定性的代码。
/// 换句话说：换掉「谁来写 JSON」，下游一行都不用改。
///
/// 设计上刻意把职责分开：JSON 里只写**和声进行、动机种子、曲式意图**这些
/// 属于乐理知识的东西；织体实现、声部连接、动机发展、量化换算全部交给代码。
/// 让 LLM 逐音符写钢琴伴奏是一定会失败的。
/// </summary>
public sealed class SongSpec
{
    public string Title { get; set; } = "未命名";

    /// <summary>每分钟拍数。</summary>
    public double Tempo { get; set; } = 80;

    /// <summary>调号，如 "D minor" / "Bb major"。</summary>
    public string Key { get; set; } = "C major";

    /// <summary>拍号，如 "4/4" / "3/4" / "6/8"。</summary>
    public string TimeSignature { get; set; } = "4/4";

    /// <summary>可复用的音乐材料，键是材料名（"A"、"B"…）。</summary>
    public Dictionary<string, Material> Material { get; set; } = [];

    /// <summary>曲式：段落列表，按顺序排布。</summary>
    public List<SectionSpec> Form { get; set; } = [];

    public int TotalBars => Form.Sum(section => section.Bars);

    public static SongSpec Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"乐谱文件不存在：{full}");

        SongSpec? spec;
        try
        {
            spec = JsonSerializer.Deserialize<SongSpec>(File.ReadAllText(full), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{Path.GetFileName(full)} 不是合法的 JSON：{ex.Message}", ex);
        }

        if (spec is null)
            throw new FormatException($"{Path.GetFileName(full)} 解析结果为空");

        spec.Validate();
        return spec;
    }

    public void Save(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(full, JsonSerializer.Serialize(this, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 结构层面的自检。乐理层面的问题（音域、和弦音）交给 Validate 模块，
    /// 这里只管「字段缺没缺、引用的材料存不存在」这类会直接让编译崩掉的问题。
    /// </summary>
    public void Validate()
    {
        if (Form.Count == 0)
            throw new FormatException("form 为空：至少要有一个段落");

        if (Tempo is <= 0 or > 400)
            throw new FormatException($"tempo 为 {Tempo}，应在 0–400 之间（常规是 40–200）");

        foreach (var section in Form)
        {
            if (section.Bars <= 0)
                throw new FormatException($"段落 '{section.Section}' 的 bars 为 {section.Bars}，必须为正");

            if (string.IsNullOrWhiteSpace(section.Material))
                throw new FormatException($"段落 '{section.Section}' 没有指定 material");

            if (!Material.ContainsKey(section.Material))
            {
                var available = Material.Count == 0 ? "（没有任何材料）" : string.Join("、", Material.Keys);
                throw new FormatException(
                    $"段落 '{section.Section}' 引用了不存在的材料 '{section.Material}'。可用材料：{available}");
            }
        }

        foreach (var (name, material) in Material)
        {
            if (material.Chords.Count == 0)
                throw new FormatException($"材料 '{name}' 的 chords 为空");

            if (string.IsNullOrWhiteSpace(material.Motif))
                throw new FormatException($"材料 '{name}' 的 motif 为空");
        }
    }

    public Material GetMaterial(string name) => Material[name];
}

/// <summary>一段可复用的音乐材料：和声进行 + 旋律动机。</summary>
public sealed class Material
{
    /// <summary>每小节一个和弦，循环使用。</summary>
    public List<string> Chords { get; set; } = [];

    /// <summary>紧凑记谱写的旋律动机，长度应与 chords 的小节数一致。</summary>
    public string Motif { get; set; } = "";
}

/// <summary>曲式里的一个段落。</summary>
public sealed class SectionSpec
{
    public string Section { get; set; } = "";

    /// <summary>小节数。</summary>
    public int Bars { get; set; }

    /// <summary>引用 material 里的哪个材料。</summary>
    public string Material { get; set; } = "";

    /// <summary>小提琴这一段的角色：motif（奏动机）/ rest（休止）。</summary>
    public string Violin { get; set; } = "motif";

    /// <summary>钢琴织体：block / broken-8th / alberti / arpeggio-up / sustain-pad。</summary>
    public string Piano { get; set; } = "block";

    /// <summary>动机发展手法，如 sequence+2、octave-1、fragment。</summary>
    public List<string> Develop { get; set; } = [];
}
