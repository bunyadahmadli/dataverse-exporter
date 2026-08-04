using System.Text;
using DataverseExporter.Core.Dataverse;
using DataverseExporter.Core.Settings;
using DataverseExporter.Core.Targets;

namespace DataverseExporter.Core.Services;

/// <summary>
/// Generates an EF Core compatible (DataAnnotations) POCO class per entity plus a
/// DbContext that collects them all. The files are written outside the tool itself so
/// a downstream API/frontend project can copy the folder and use it directly with the
/// matching EF Core database package.
/// </summary>
public class ModelGenerator
{
    private readonly ModelGenerationSettings _settings;
    private readonly string _schema;
    private readonly ITargetProvider _provider;

    public ModelGenerator(ModelGenerationSettings settings, string schema, ITargetProvider provider)
    {
        _settings = settings;
        _schema = schema;
        _provider = provider;
    }

    /// <summary>Generates the class files; returns the absolute output directory.</summary>
    public string Generate(IReadOnlyList<EntityTableModel> models)
    {
        var dir = Path.GetFullPath(_settings.OutputDirectory);
        Directory.CreateDirectory(dir);

        var classNames = new Dictionary<string, string>(); // logical name -> class name
        var usedNames = new HashSet<string>(StringComparer.Ordinal) { "CrmDbContext" };

        foreach (var model in models)
        {
            var className = ToIdentifier(model.Entity.SchemaName ?? model.Entity.LogicalName);
            while (!usedNames.Add(className))
                className += "_";
            classNames[model.Entity.LogicalName] = className;
        }

        foreach (var model in models)
        {
            var source = GenerateClass(model, classNames[model.Entity.LogicalName]);
            File.WriteAllText(Path.Combine(dir, classNames[model.Entity.LogicalName] + ".cs"),
                source, Encoding.UTF8);
        }

        File.WriteAllText(Path.Combine(dir, "CrmDbContext.cs"),
            GenerateDbContext(models, classNames), Encoding.UTF8);

        return dir;
    }

    private string GenerateClass(EntityTableModel model, string className)
    {
        var entity = model.Entity;
        var sb = new StringBuilder();
        // The file must carry its own nullable context: if the consuming project has
        // nullable disabled, "string?" members would produce CS8632.
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System.ComponentModel.DataAnnotations;");
        sb.AppendLine("using System.ComponentModel.DataAnnotations.Schema;");
        sb.AppendLine();
        sb.AppendLine($"namespace {_settings.Namespace};");
        sb.AppendLine();

        var displayName = entity.DisplayName?.UserLocalizedLabel?.Label;
        if (!string.IsNullOrEmpty(displayName))
            sb.AppendLine($"/// <summary>{XmlEscape(displayName)}</summary>");

        sb.AppendLine($"[Table(\"{entity.LogicalName}\", Schema = \"{_schema}\")]");
        sb.AppendLine($"public class {className}");
        sb.AppendLine("{");

        var usedNames = new HashSet<string>(StringComparer.Ordinal) { className };
        var first = true;

        foreach (var col in model.Columns)
        {
            if (!first)
                sb.AppendLine();
            first = false;

            var attr = col.Attribute;
            var isPk = col.Column.IsPrimaryKey;

            var propName = ToIdentifier(attr.SchemaName ?? attr.LogicalName);
            if (col.IsEntityTypeColumn)
                propName += "_EntityType";
            while (!usedNames.Add(propName))
                propName += "_";

            var attrDisplay = attr.DisplayName?.UserLocalizedLabel?.Label;
            if (!col.IsEntityTypeColumn && !string.IsNullOrEmpty(attrDisplay))
                sb.AppendLine($"    /// <summary>{XmlEscape(attrDisplay)}</summary>");

            if (isPk)
                sb.AppendLine("    [Key]");

            // Without TypeName, EF falls back to its default decimal(18,2) and warns
            // about possible precision loss.
            var typeName = col.Column.ClrType == typeof(decimal)
                ? $", TypeName = \"{_provider.ToStoreType(col.Column).ToLowerInvariant()}\""
                : "";
            sb.AppendLine($"    [Column(\"{col.Column.Name}\"{typeName})]");
            sb.AppendLine($"    public {ToCSharpType(col.Column.ClrType, nullable: !isPk)} {propName} {{ get; set; }}");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private string GenerateDbContext(IReadOnlyList<EntityTableModel> models,
        Dictionary<string, string> classNames)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using Microsoft.EntityFrameworkCore;");
        sb.AppendLine();
        sb.AppendLine($"namespace {_settings.Namespace};");
        sb.AppendLine();
        sb.AppendLine("public class CrmDbContext : DbContext");
        sb.AppendLine("{");
        sb.AppendLine("    public CrmDbContext(DbContextOptions<CrmDbContext> options) : base(options) { }");
        sb.AppendLine();

        foreach (var model in models)
        {
            var className = classNames[model.Entity.LogicalName];
            sb.AppendLine($"    public DbSet<{className}> {className} => Set<{className}>();");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string ToCSharpType(Type clrType, bool nullable)
    {
        var name = clrType switch
        {
            _ when clrType == typeof(string) => "string",
            _ when clrType == typeof(int) => "int",
            _ when clrType == typeof(long) => "long",
            _ when clrType == typeof(bool) => "bool",
            _ when clrType == typeof(DateTime) => "DateTime",
            _ when clrType == typeof(decimal) => "decimal",
            _ when clrType == typeof(double) => "double",
            _ when clrType == typeof(Guid) => "Guid",
            _ => "object"
        };
        return nullable ? name + "?" : name;
    }

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
        "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
        "virtual", "void", "volatile", "while",
        // contextual keywords that are problematic as member names
        "record", "required"
    };

    /// <summary>Turns a logical/schema name into a valid C# identifier.</summary>
    private static string ToIdentifier(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        if (sb.Length == 0 || char.IsDigit(sb[0]))
            sb.Insert(0, '_');

        var result = sb.ToString();
        return CSharpKeywords.Contains(result) ? result + "_" : result;
    }

    /// <summary>
    /// Besides XML escaping, strips line breaks: a newline inside a display name would
    /// split the /// comment and the generated file would not compile.
    /// </summary>
    private static string XmlEscape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
             .Replace("\r", " ").Replace("\n", " ");
}
