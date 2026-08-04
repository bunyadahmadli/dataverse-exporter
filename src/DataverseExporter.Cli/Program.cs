using System.Collections.Concurrent;
using System.Diagnostics;
using DataverseExporter.Core.Dataverse;
using DataverseExporter.Core.Model;
using DataverseExporter.Core.Services;
using DataverseExporter.Core.Settings;
using DataverseExporter.Core.Targets;
using DataverseExporter.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.PowerPlatform.Dataverse.Client;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .AddEnvironmentVariables(prefix: "DVEXPORT_")
    .Build();

var settings = configuration.Get<ExporterSettings>()
    ?? throw new InvalidOperationException("Could not read appsettings.json.");

// Built-in target providers. To add a database, implement ITargetProvider and
// register it here — see docs/adding-a-provider.md.
var registry = new ProviderRegistry();
registry.Register("SqlServer", s => new SqlServerProvider(s));

if (string.IsNullOrWhiteSpace(settings.Dataverse.ConnectionString) ||
    string.IsNullOrWhiteSpace(settings.Target.ConnectionString))
{
    Console.Error.WriteLine("ERROR: Dataverse:ConnectionString and Target:ConnectionString must be set " +
                            "(appsettings.json, appsettings.local.json or DVEXPORT_* environment variables).");
    return 1;
}

ITargetProvider provider;
try
{
    provider = registry.Create(settings.Target);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

// --test: only tries both connections, reports the result and exits; no export is started.
var testMode = args.Contains("--test");

Console.WriteLine("Connecting to Dataverse...");
ServiceClient? crmClientMaybe = null;
try
{
    crmClientMaybe = new ServiceClient(settings.Dataverse.ConnectionString);
    if (!crmClientMaybe.IsReady)
    {
        Console.Error.WriteLine($"ERROR: could not connect to Dataverse: {crmClientMaybe.LastError}");
        if (!testMode)
            return 1;
        crmClientMaybe = null;
    }
    else
    {
        // First access to these properties calls the server and can throw (e.g. when the
        // app is not added as an application user) — it must stay inside the try.
        Console.WriteLine($"Connected: {crmClientMaybe.ConnectedOrgFriendlyName} ({crmClientMaybe.ConnectedOrgUriActual})");
    }
}
catch (Exception ex)
{
    // ServiceClient can throw on authentication failures instead of returning
    // IsReady=false; show the actual cause (outer + innermost message) instead of a stack trace.
    var root = ex;
    while (root.InnerException != null)
        root = root.InnerException;
    var message = ReferenceEquals(root, ex) ? ex.Message : $"{ex.Message} ({root.Message})";
    Console.Error.WriteLine($"ERROR: could not connect to Dataverse: {message}");
    crmClientMaybe?.Dispose();
    crmClientMaybe = null;
    if (!testMode)
        return 1;
}
using var _crmDisposer = crmClientMaybe;

Console.WriteLine($"Connecting to the target database ({provider.Name})...");
ITargetConnection setupConn;
try
{
    setupConn = provider.Connect();
    Console.WriteLine($"Connected: {setupConn.Description}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: could not connect to the target database: {ex.Message}");
    return 1;
}

if (testMode)
{
    setupConn.Dispose();
    Console.WriteLine(crmClientMaybe != null
        ? "Connection test: both Dataverse and the target database connected SUCCESSFULLY."
        : "Connection test: target database OK, Dataverse FAILED (see the error above).");
    return crmClientMaybe != null ? 0 : 1;
}

if (crmClientMaybe == null)
    return 1;
var crmClient = crmClientMaybe;
// With the affinity cookie disabled, requests are spread across Dataverse web servers;
// significantly improves parallel throughput (Microsoft's recommendation for bulk reads).
crmClient.EnableAffinityCookie = false;

using (setupConn)
{
    Console.WriteLine("Retrieving entity metadata (this can take a few minutes)...");
    var metadataService = new MetadataService(crmClient, settings.Export);
    var models = metadataService.GetExportableEntities()
        .Select(EntityTableModel.Build)
        .Where(m => m != null)
        .Select(m => m!)
        .ToList();
    Console.WriteLine($"{models.Count} entities found.");

    if (settings.Models.Generate)
    {
        var modelDir = new ModelGenerator(settings.Models, settings.Target.Schema, provider).Generate(models);
        Console.WriteLine($"C# model classes generated: {modelDir}");
    }

    setupConn.Initialize();

    // RecreateTables drops the tables, which invalidates any previous progress.
    if (settings.Export.ResetProgress || settings.Export.RecreateTables)
    {
        Console.WriteLine("Previous progress cleared, the export will start from scratch.");
        setupConn.ResetProgress();
    }

    var progress = setupConn.LoadProgress();
    var completedCount = progress.Values.Count(p => p.Status == EntityProgress.StatusCompleted);
    if (completedCount > 0)
        Console.WriteLine($"{completedCount} entities were already completed in a previous run and will be skipped.");

    // A table referenced by an FK cannot be TRUNCATEd/DROPped; whatever the settings,
    // drop our own constraints possibly left behind by a previous run.
    setupConn.DropManagedForeignKeys();

    // Record counts: putting the biggest tables at the front of the queue keeps parallel
    // workers busy until the end; the same counts are reused by verification.
    var crmCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    if (!settings.Export.SchemaOnly)
    {
        Console.WriteLine("Retrieving Dataverse record counts...");
        crmCounts = VerificationService.GetRecordCountSnapshot(
            crmClient, models.Select(m => m.Entity.LogicalName).ToList());
        Console.WriteLine($"~{crmCounts.Values.Sum():N0} records to copy in total.");
    }

    var workItems = settings.Export.SchemaOnly
        ? models
        : models.OrderByDescending(m => crmCounts.GetValueOrDefault(m.Entity.LogicalName, 0L)).ToList();

    var dop = settings.Export.MaxDegreeOfParallelism > 0
        ? settings.Export.MaxDegreeOfParallelism
        : Math.Clamp(crmClient.RecommendedDegreesOfParallelism, 2, 16);
    Console.WriteLine($"{workItems.Count} tables to process, parallelism: {dop}.");

    var failed = new ConcurrentBag<(string Entity, string Error)>();
    long grandTotal = 0;
    var started = 0;
    var stopwatch = Stopwatch.StartNew();

    // One Dataverse client clone PER WORKER (localInit/localFinally), not per entity:
    // thousands of rapid clones can exhaust sockets/ports ("No route to host" errors).
    Parallel.ForEach(workItems, new ParallelOptions { MaxDegreeOfParallelism = dop },
        () => CreateWorkerClient(),
        (model, _, client) =>
        {
            var entityName = model.Entity.LogicalName;

            progress.TryGetValue(entityName, out var entityProgress);
            var resume = !settings.Export.SchemaOnly && entityProgress?.Status == EntityProgress.StatusInProgress
                ? entityProgress
                : null;

            if (!settings.Export.SchemaOnly && entityProgress?.Status == EntityProgress.StatusCompleted)
            {
                Interlocked.Add(ref grandTotal, entityProgress.RowsCopied);
                return client;
            }

            var n = Interlocked.Increment(ref started);
            Console.WriteLine($"[{n}/{workItems.Count}] {entityName} started " +
                              $"({model.Table.Columns.Count} columns, " +
                              $"Dataverse ~{crmCounts.GetValueOrDefault(entityName, 0):N0} records)");

            try
            {
                // Each worker needs its own target connection; transactions are bound to it.
                using var target = provider.Connect();

                // When resuming a half-finished entity, dropping/truncating the table would
                // delete already-copied data — these steps only run on a fresh start.
                if (resume == null)
                {
                    target.CreateTable(model.Table, settings.Export.RecreateTables);

                    if (!settings.Export.SchemaOnly && settings.Export.TruncateBeforeInsert
                        && !settings.Export.RecreateTables)
                        target.TruncateTable(model.Table);
                }

                // If the table predates this run, complete the fields added to Dataverse
                // since (or columns missing from tables created by an older version).
                target.EnsureColumns(model.Table);

                if (settings.Export.SchemaOnly)
                    return client;

                var entityStopwatch = Stopwatch.StartNew();
                var pump = new DataPump(client, settings.Export.PageSize);
                var rows = pump.Run(target, model, resume);
                Interlocked.Add(ref grandTotal, rows);

                var rate = rows / Math.Max(1.0, entityStopwatch.Elapsed.TotalSeconds);
                Console.WriteLine($"  [{entityName}] completed: {rows:N0} rows ({rate:N0} rows/s).");
            }
            catch (Exception ex)
            {
                // Some system entities cannot be queried (no RetrieveMultiple support etc.)
                // — record and continue.
                failed.Add((entityName, ex.Message));
                Console.WriteLine($"  [{entityName}] SKIPPED (error): {ex.Message}");
            }
            return client;
        },
        client => DisposeWorkerClient(client));

    Console.WriteLine();
    Console.WriteLine($"Data phase finished: {grandTotal:N0} rows in {stopwatch.Elapsed:hh\\:mm\\:ss}.");

    if (settings.Export.CreateForeignKeys)
    {
        Console.WriteLine();
        Console.WriteLine("Creating foreign key relationships...");

        // In schema-only mode all tables are empty so FKs can be created right away;
        // in data mode only between fully exported tables (half tables get no FK).
        using var fkConn = provider.Connect();
        Func<string, bool> eligible;
        if (settings.Export.SchemaOnly)
        {
            eligible = _ => true;
        }
        else
        {
            var fkProgress = fkConn.LoadProgress();
            eligible = name => fkProgress.TryGetValue(name, out var p)
                               && p.Status == EntityProgress.StatusCompleted;
        }

        var plan = ForeignKeyPlanner.Plan(models, eligible);
        var (fkCreated, fkFailed) = fkConn.CreateForeignKeys(plan);
        Console.WriteLine($"{fkCreated} foreign keys created, {fkFailed.Count} failed.");
        foreach (var (fk, error) in fkFailed)
            Console.WriteLine($"  - {fk}: {error}");
    }

    var verificationResults = new List<VerificationResult>();
    if (!settings.Export.SchemaOnly && settings.Verification.Enabled)
    {
        Console.WriteLine();
        Console.WriteLine("Verification phase starting...");
        var verifier = new VerificationService(settings.Verification);

        IReadOnlyDictionary<string, EntityProgress> finalProgress;
        using (var progressConn = provider.Connect())
            finalProgress = progressConn.LoadProgress();

        var toVerify = workItems
            .Where(m => finalProgress.TryGetValue(m.Entity.LogicalName, out var p)
                        && p.Status == EntityProgress.StatusCompleted)
            .ToList();

        var resultBag = new ConcurrentBag<VerificationResult>();
        Parallel.ForEach(toVerify, new ParallelOptions { MaxDegreeOfParallelism = dop },
            () => CreateWorkerClient(),
            (model, _, client) =>
            {
                var entityName = model.Entity.LogicalName;
                VerificationResult? result = null;

                // Transient network errors must not void the verification (a previous run
                // once lost the check for 453 entities): 3 attempts with growing backoff.
                for (var attempt = 1; attempt <= 3 && result == null; attempt++)
                {
                    try
                    {
                        using var target = provider.Connect();

                        var copied = finalProgress[entityName].RowsCopied;
                        long? snapshot = crmCounts.TryGetValue(entityName, out var c) ? c : null;

                        result = verifier.VerifyEntity(client, target, model, copied, snapshot);
                        target.SaveVerification(result);
                    }
                    catch (Exception ex) when (attempt < 3)
                    {
                        Console.WriteLine($"  [{entityName}] verification attempt {attempt} failed ({ex.Message}), retrying...");
                        Thread.Sleep(TimeSpan.FromSeconds(10 * attempt));
                    }
                    catch (Exception ex)
                    {
                        result = new VerificationResult(entityName, -1, -1, null, 0, 0, 0,
                            VerificationResult.StatusError, $"verification could not run: {ex.Message}");
                        Console.WriteLine($"  [{entityName}] verification error: {ex.Message}");
                    }
                }

                resultBag.Add(result!);
                if (result!.Status != VerificationResult.StatusOk
                    && result.Details?.StartsWith("verification could not run") != true)
                    Console.WriteLine($"  [{entityName}] {result.Status}: {result.Details}");
                return client;
            },
            client => DisposeWorkerClient(client));

        verificationResults = resultBag.ToList();
        var okCount = verificationResults.Count(r => r.Status == VerificationResult.StatusOk);
        var warningCount = verificationResults.Count(r => r.Status == VerificationResult.StatusWarning);
        var errorCount = verificationResults.Count(r => r.Status == VerificationResult.StatusError);
        Console.WriteLine($"Verification finished: {okCount} OK, {warningCount} warnings, {errorCount} errors. " +
                          $"Details: {settings.Target.Schema}._MigrationVerification");
    }

    Console.WriteLine();
    Console.WriteLine($"Done. {grandTotal:N0} rows in total, total time {stopwatch.Elapsed:hh\\:mm\\:ss}, " +
                      $"{failed.Count} entities skipped due to errors.");
    foreach (var (name, error) in failed)
        Console.WriteLine($"  - {name}: {error}");

    if (verificationResults.Any(r => r.Status == VerificationResult.StatusError))
        return 3;
    return failed.IsEmpty ? 0 : 2;

    // One Dataverse client clone per worker in the parallel phases (token shared, channel separate).
    ServiceClient CreateWorkerClient()
    {
        if (dop <= 1)
            return crmClient;
        var clone = crmClient.Clone();
        clone.EnableAffinityCookie = false;
        return clone;
    }

    void DisposeWorkerClient(ServiceClient client)
    {
        if (!ReferenceEquals(client, crmClient))
            client.Dispose();
    }
}
