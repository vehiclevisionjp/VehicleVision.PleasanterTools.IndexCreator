using System.Data.Common;
using System.Diagnostics;
using VehicleVision.PleasanterTools.IndexCreator;

internal static class Benchmark
{
    public static async Task Run(Configuration config, Database database, SqlDialect d)
    {
        var digits = "(" + string.Join(" UNION ALL ", Enumerable.Range(0, 10).Select(n => "SELECT " + n + " AS n")) + ")";
        var number = "a.n+10*b.n+100*c.n+1000*d.n+10000*e.n";
        var cast = config.Dbms == Dbms.MySQL ? "CHAR" : "varchar(32)";
        await database.Execute($"INSERT INTO {d.Table("Results")} ({d.Quote("SiteId")},{d.Quote("ResultId")},{d.Quote("UpdatedTime")},{d.Quote("ClassA")},{d.Quote("Status")},{d.Quote("Manager")},{d.Quote("Owner")}) SELECT 1,100+({number}),'2026-01-01',CAST(({number})%100 AS {cast}),100,({number})%100,(({number})+1)%100 FROM {digits} a CROSS JOIN {digits} b CROSS JOIN {digits} c CROSS JOIN {digits} d CROSS JOIN {digits} e");
        await using DbConnection query = config.Dbms switch { Dbms.SQLServer => new Microsoft.Data.SqlClient.SqlConnection(config.ConnectionString), Dbms.PostgreSQL => new Npgsql.NpgsqlConnection(config.ConnectionString), _ => new MySqlConnector.MySqlConnection(config.ConnectionString) };
        await query.OpenAsync();
        async Task<double> Measure(string sql, long expected)
        {
            await using var cmd = query.CreateCommand();
            cmd.CommandText = sql;
            var times = new List<double>();
            for (var i = 0; i < 6; i++)
            {
                var watch = Stopwatch.StartNew();
                var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                watch.Stop();
                if (count != expected) throw new Exception("Benchmark result mismatch.");
                if (i > 0) times.Add(watch.Elapsed.TotalMilliseconds);
            }
            return times.Order().ElementAt(2);
        }
        var classSql = $"SELECT COUNT(*) FROM {d.Table("Results")} WHERE {d.Quote("SiteId")}=1 AND {d.Quote("ClassA")}='42'";
        var before = await Measure(classSql, 1000);
        var spec = new IndexSpec("Results", [new("SiteId"), new("ClassA", Prefix: config.Dbms == Dbms.MySQL ? 100 : 0)]);
        await database.Apply(Reconciler.Plan([spec], await database.ReadIndexes(default), false), default);
        var after = await Measure(classSql, 1000);
        Console.WriteLine(FormattableString.Invariant($"Benchmark ({config.Dbms}): rows=100000, matches=1000, median before={before:F3}ms, after={after:F3}ms. Synthetic data only; no latency threshold asserted."));
        var ownSql = $"SELECT COUNT(*) FROM {d.Table("Results")} WHERE {d.Quote("SiteId")}=1 AND ({d.Quote("Manager")}=42 OR {d.Quote("Owner")}=42)";
        var ownBefore = await Measure(ownSql, 2000);
        var ownSite = new Site(1, "Results", Json.Parse("""{"Views":[{"Own":true}]}"""), 100000);
        var ownIndexes = new Planner(config.Dbms).Analyze([ownSite]).Indexes.Where(i => i.Keys.Any(k => k.Column is "Manager" or "Owner")).ToArray();
        await database.Apply(Reconciler.Plan(ownIndexes, await database.ReadIndexes(default), false), default);
        var ownAfter = await Measure(ownSql, 2000);
        Console.WriteLine(FormattableString.Invariant($"Own benchmark ({config.Dbms}): rows=100000, matches=2000, median before={ownBefore:F3}ms, after={ownAfter:F3}ms. Synthetic data only; no latency threshold asserted."));
        if (config.Dbms == Dbms.SQLServer)
        {
            await using var planCommand = query.CreateCommand();
            planCommand.CommandText = "SET SHOWPLAN_XML ON";
            await planCommand.ExecuteNonQueryAsync();
            try
            {
                planCommand.CommandText = ownSql;
                var plan = System.Xml.Linq.XDocument.Parse((string)(await planCommand.ExecuteScalarAsync())!);
                var operators = plan.Descendants().Where(e => e.Name.LocalName == "RelOp").Select(e => e.Attribute("PhysicalOp")?.Value).Where(x => x != null).Distinct();
                Console.WriteLine("Own query plan (SQLServer): " + string.Join(", ", operators));
            }
            finally
            {
                planCommand.CommandText = "SET SHOWPLAN_XML OFF";
                await planCommand.ExecuteNonQueryAsync();
            }
        }
    }
}
