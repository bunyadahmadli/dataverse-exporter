namespace DataverseExporter.Core.Settings;

public class ExporterSettings
{
    public DataverseSettings Dataverse { get; set; } = new();
    public TargetSettings Target { get; set; } = new();
    public ExportSettings Export { get; set; } = new();
    public ModelGenerationSettings Models { get; set; } = new();
    public VerificationSettings Verification { get; set; } = new();
}

public class DataverseSettings
{
    /// <summary>Dataverse connection string (AuthType=ClientSecret; Url=...; ClientId=...; ClientSecret=...).</summary>
    public string ConnectionString { get; set; } = "";
}

public class TargetSettings
{
    /// <summary>Name of the registered target provider (e.g. "SqlServer").</summary>
    public string Provider { get; set; } = "SqlServer";

    /// <summary>Connection string understood by the selected provider.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Target schema/namespace inside the database (default: dbo).</summary>
    public string Schema { get; set; } = "dbo";
}

public class ExportSettings
{
    /// <summary>If non-empty, only these entities (logical names) are exported.</summary>
    public List<string> IncludeEntities { get; set; } = new();

    /// <summary>These entities (logical names) are skipped.</summary>
    public List<string> ExcludeEntities { get; set; } = new();

    /// <summary>Whether N:N relationship (intersect) tables are exported too.</summary>
    public bool IncludeIntersectTables { get; set; } = true;

    /// <summary>True drops and recreates existing tables (resume progress is reset too).</summary>
    public bool RecreateTables { get; set; } = false;

    /// <summary>True truncates tables before inserting (only when starting an entity from scratch).</summary>
    public bool TruncateBeforeInsert { get; set; } = true;

    /// <summary>True clears the resume checkpoints so the export starts over.</summary>
    public bool ResetProgress { get; set; } = false;

    /// <summary>Records fetched from Dataverse per page (max 5000).</summary>
    public int PageSize { get; set; } = 5000;

    /// <summary>True creates tables only, no data is copied.</summary>
    public bool SchemaOnly { get; set; } = false;

    /// <summary>True creates FOREIGN KEY constraints for single-target lookup columns.</summary>
    public bool CreateForeignKeys { get; set; } = true;

    /// <summary>Entities exported concurrently. 0 = use the value recommended by the Dataverse server.</summary>
    public int MaxDegreeOfParallelism { get; set; } = 0;
}

public class ModelGenerationSettings
{
    /// <summary>True generates EF Core compatible C# POCO classes for every entity.</summary>
    public bool Generate { get; set; } = true;

    /// <summary>Directory the generated classes are written to.</summary>
    public string OutputDirectory { get; set; } = "./generated-models";

    /// <summary>Namespace of the generated classes.</summary>
    public string Namespace { get; set; } = "Crm.Models";
}

public class VerificationSettings
{
    /// <summary>True compares row counts and sample records against Dataverse after the export.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Number of random records per entity compared field-by-field.</summary>
    public int SampleSize { get; set; } = 10;
}
