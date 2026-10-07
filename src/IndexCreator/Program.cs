using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace VehicleVision.PleasanterTools.IndexCreator;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        try
        {
            var options = Options.Parse(args);
            if (options.Action == "help")
            {
                Console.WriteLine("""
                    IndexCreator 0.1.0 - Pleasanter index management
                    Usage: IndexCreator <plan|apply|_rds> [/p <Pleasanter folder>] [/y]
                      /p, -p <folder>             Pleasanter application folder (not Parameters)
                      /c, --check                 Inspect only; never modify the database
                      /f, --force                 Rebuild matching managed indexes
                      --sites <sites.json>          Offline plan only; requires --dbms
                      --dbms <SQLServer|PostgreSQL|MySQL>
                      --schema <name>              Override the database schema
                      --min-records <count>        Minimum records per site (default: 10000)
                      --mysql-prefix <1..191>      TEXT prefix length (default: 100)
                      --include-filter-columns     Include filter controls
                      --offline                    Use blocking index operations
                      --prune                      Remove obsolete IndexCreator indexes
                      --output <plan.sql>          Export the inspected operation plan
                      /y, -y, --yes                Apply without an interactive prompt
                    Default layout: IndexCreator beside Implem.CodeDefiner and Implem.Pleasanter.
                    Only vvic_v1_<table>_<16 hex digits> indexes are managed.
                    """);
                return 0;
            }
            var config = Configuration.Load(options);
            if (!config.DisableIndexChangeDetection)
            {
                Console.WriteLine("WARNING: DisableIndexChangeDetection is not true. Set it in Rds.json before applying indexes.");
                if (options.Action == "apply") throw new UserError("Apply requires DisableIndexChangeDetection=true in Rds.json.");
            }
            if (config.Dbms == Dbms.SQLServer && !options.Offline)
                Console.WriteLine("INFO: Online creation is used on supported editions; other editions build offline.");
            IReadOnlyList<Site> sites;
            IReadOnlyList<ExistingIndex> existing = [];
            await using var database = options.SitesFile == null ? new Database(config, options.Offline) : null;
            if (database != null)
            {
                await database.Open(cancel.Token);
                if (options.Action == "apply") await database.AcquireLock(cancel.Token);
                sites = await database.ReadSites(cancel.Token);
                existing = await database.ReadIndexes(cancel.Token);
            }
            else sites = Json.ReadSites(await File.ReadAllTextAsync(options.SitesFile!, cancel.Token));
            var analysis = new Planner(config.Dbms, options.MinRecords, options.IncludeFilters, options.MysqlPrefix).Analyze(sites);
            if (database != null) await database.ValidateColumns(analysis.Indexes, cancel.Token);
            foreach (var d in analysis.Diagnostics) Console.WriteLine($"WARNING: Site {d.SiteId}: {d.Message}");
            var changes = Reconciler.Plan(analysis.Indexes, existing, options.Prune, options.Force);
            foreach (var change in changes) Console.WriteLine($"{change.Kind,-7} {change.Spec.Table,-7} {change.Name}");
            var pending = changes.Count(c => c.Kind != ChangeKind.Keep);
            Console.WriteLine($"Sites: {sites.Count}. Desired indexes: {analysis.Indexes.Count}. Pending operations: {pending}.");
            var dialect = new SqlDialect(config.Dbms, config.Schema, options.Offline);
            if (options.Output != null)
            {
                var statements = new List<string> { "-- IndexCreator inspected plan. Re-run plan before execution; database state may have changed.", "-- PostgreSQL concurrent operations must run outside a transaction.", "-- Run IndexCreator apply again after CodeDefiner rebuilds tables." };
                foreach (var c in changes.Where(c => c.Kind != ChangeKind.Keep))
                {
                    if (c.Kind is ChangeKind.Drop or ChangeKind.Repair) statements.Add(dialect.Drop(c.Spec.Table, c.Name));
                    if (c.Kind is ChangeKind.Create or ChangeKind.Repair) statements.Add(dialect.Create(c.Spec));
                }
                await File.WriteAllTextAsync(options.Output, string.Join(Environment.NewLine, statements), cancel.Token);
                Console.WriteLine("SQL plan exported.");
            }
            if (options.Action == "plan" || pending == 0) return 0;
            if (!options.Yes)
            {
                if (Console.IsInputRedirected) throw new UserError("Non-interactive apply requires -y.");
                Console.Write("Apply the listed changes? Type yes: ");
                if (Console.ReadLine() != "yes") { Console.WriteLine("Cancelled. No changes were applied."); return 2; }
            }
            await database!.Apply(changes, cancel.Token, sites);
            Console.WriteLine("Completed. Re-run plan to verify the current site configuration.");
            return 0;
        }
        catch (UserError e) { Console.Error.WriteLine("ERROR: " + e.Message); return 2; }
        catch (OperationCanceledException) { Console.Error.WriteLine("ERROR: Operation cancelled. Re-run plan to inspect partial progress."); return 130; }
        catch (DbException) { Console.Error.WriteLine("ERROR: Database operation failed. Check permissions, connectivity, TLS and index limits. Re-run plan to inspect partial progress. Connection details are not logged."); return 3; }
        catch (JsonException) { Console.Error.WriteLine("ERROR: Invalid JSON. No further operations were applied."); return 2; }
        catch (Exception) { Console.Error.WriteLine("ERROR: Operation failed. Check configuration and file access. No exception details are logged."); return 1; }
    }
}
